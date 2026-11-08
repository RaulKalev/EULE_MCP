using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Families;

/// <summary>revit_preview_create_detail_family (#147): checks a detail item family spec without creating anything.</summary>
public sealed class PreviewCreateDetailFamilyTool : IRevitMcpTool
{
    public string Name => "revit_preview_create_detail_family";

    public string Description =>
        "Previews creating a Detail Item family, without changing the model or any file. Same arguments as " +
        "revit_create_detail_family. Checks the save path (required, never defaulted), the file access policy and whether " +
        "the file exists, finds the template (Metric Detail Item.rft), validates the geometry, matches every requested " +
        "line style to the project's line styles (project | missing), resolves each parameter (shared ones against the " +
        "shared parameter file), checks label text types and lists the annotation families the labels need, and states " +
        "whether the family would load.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(DetailFamilyExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_create_detail_family (#147): creates, purges and saves a Detail Item family, and optionally loads it.</summary>
public sealed class CreateDetailFamilyTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_create_detail_family";

    public string Description =>
        "Creates a Detail Item family (.rfa) from Metric Detail Item.rft and draws it. Requires approval. savePath is " +
        "required (a folder with familyName, or the full .rfa path): ask the user where the family goes, there is no " +
        "default. Geometry in mm on the reference level, origin = insertion point: lines [{start:[x,y], end, lineStyle?}], " +
        "rectangles [{min, max, lineStyle?}], arcs [{center, radius, startAngle, endAngle (degrees, counter-clockwise)} " +
        "or {start, mid, end}], circles [{center, radius}]. lineStyle (per curve, or a default for all) must be a line " +
        "style of the project: the family gets a line style of the same name, weight, colour and pattern, so it merges " +
        "with the project's on load. purge (default true) removes unused elements before saving. overwrite replaces an " +
        "existing file. loadIntoProject loads it into the document (ifExists: skip | overwrite). parameters [{name, " +
        "dataType, group?, instance?, value?, formula?}] adds family parameters; {name or guid, shared:true} assigns an " +
        "existing shared parameter from the shared parameter file (never creates one). labels [{text, position:[x,y], " +
        "size? (paper mm), textType?, alignment?, rotation?}] places text through nested Generic Annotation families. Run " +
        "revit_preview_create_detail_family first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(DetailFamilyExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => DetailFamilyExecutor.Summary(request);
}

internal static class DetailFamilyExecutor
{
    private const double MmPerFoot = 304.8;
    private const int MaxPurgePasses = 5;

    public static string Summary(McpToolRequest request)
    {
        var errors = new List<string>();
        var spec = DetailFamilySpecParser.Parse(request.Arguments, errors);
        var counts = string.Join(", ", spec.CountsByKind().Select(c => $"{c.Value} {c.Key}{(c.Value == 1 ? string.Empty : "s")}"));
        var styles = spec.LineStyleNames();
        var document = ToolArguments.GetString(request.Arguments, "document").Trim();
        var shared = spec.Parameters.Count(p => p.Shared);
        var parameters = spec.Parameters.Count == 0 ? string.Empty
            : $"; {spec.Parameters.Count} parameter(s){(shared > 0 ? $", {shared} of them shared (existing definitions only)" : string.Empty)}";
        var labels = spec.Labels.Count == 0 ? string.Empty
            : $"; {spec.Labels.Count} label(s) as nested Generic Annotation families";
        return $"Create Detail Item family '{spec.FamilyName}' ({(counts.Length > 0 ? counts : "no geometry")}" +
               $"{(styles.Count > 0 ? $"; line styles {string.Join(", ", styles)}" : string.Empty)}{parameters}{labels}) and save it to " +
               $"'{spec.SavePath}'{(spec.Overwrite ? ", replacing an existing file" : string.Empty)}" +
               $"{(spec.Purge ? ", purged" : string.Empty)}" +
               $"{(spec.LoadIntoProject ? $"; then load it into {(document.Length > 0 ? $"'{document}'" : "the active document")} (ifExists={spec.IfExists})" : string.Empty)}.";
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;
        if (doc.IsFamilyDocument)
            return Fail(request, "The target is a family document: run this from a project, whose line styles the family copies and into which it can load.");

        var problems = new List<string>();
        var warnings = new List<string>();
        var spec = DetailFamilySpecParser.Parse(request.Arguments, problems);

        // The save path: policy, existing file.
        var fileExists = false;
        if (spec.SavePath.Length > 0)
        {
            var policyError = new FilePathPolicy().ValidateWrite(spec.SavePath);
            if (policyError != null) problems.Add($"savePath: {policyError}");
            fileExists = File.Exists(spec.SavePath);
            if (fileExists && !spec.Overwrite)
                problems.Add($"'{spec.SavePath}' already exists: pass overwrite=true to replace it, or choose another name or folder.");
            var folder = Path.GetDirectoryName(spec.SavePath);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                warnings.Add($"The folder '{folder}' does not exist yet; it will be created.");
        }

        var (templatePath, templateError) = DetailFamilySpecParser.ResolveTemplate(spec.TemplatePath, SafeTemplateRoot(uiapp),
            File.Exists, root => SafeFind(root));
        if (templateError != null) problems.Add(templateError);

        var projectStyles = ReadProjectLineStyles(doc);
        var stylePlan = DetailFamilyLineStylePlanner.Plan(spec, projectStyles.Values.Select(s => s.Info).ToList());
        foreach (var missing in stylePlan.Where(s => s.Status == "missing"))
        {
            var suggestions = DetailFamilyLineStylePlanner.Suggest(missing.Requested, projectStyles.Values.Select(s => s.Info));
            problems.Add($"Line style '{missing.Requested}' (used by {string.Join(", ", missing.UsedBy)}) is not a line style of " +
                         $"'{doc.Title}'{(suggestions.Count > 0 ? $"; did you mean {string.Join(", ", suggestions.Select(s => $"'{s}'"))}?" : ".")}");
        }

        // Phase 2: parameters, against the running Revit and the shared parameter file.
        var parameters = DetailFamilyExtrasRevit.ResolveParameters(uiapp.Application, doc, spec, problems, warnings);

        // Phase 3: labels, as nested Generic Annotation families.
        var labelGroups = DetailFamilySpecParser.GroupLabels(spec, problems);
        string? annotationTemplate = null;
        var projectTextTypes = new Dictionary<string, ProjectTextTypeInfo>(StringComparer.OrdinalIgnoreCase);
        if (spec.Labels.Count > 0)
        {
            string? annotationError;
            (annotationTemplate, annotationError) = DetailFamilySpecParser.ResolveTemplate(spec.AnnotationTemplatePath, SafeTemplateRoot(uiapp),
                File.Exists, root => SafeFind(root, DetailFamilySpecParser.DefaultAnnotationTemplateFileName),
                DetailFamilySpecParser.DefaultAnnotationTemplateFileName);
            if (annotationError != null) problems.Add(annotationError);

            projectTextTypes = DetailFamilyExtrasRevit.ReadProjectTextTypes(doc);
            foreach (var name in DetailFamilySpecParser.LabelTextTypeNames(spec).Where(n => !projectTextTypes.ContainsKey(n)))
            {
                var suggestions = DetailFamilyExtrasRevit.SuggestTextTypes(name, projectTextTypes.Keys);
                problems.Add($"Text type '{name}' is not a text type of '{doc.Title}'" +
                             $"{(suggestions.Count > 0 ? $"; did you mean {string.Join(", ", suggestions.Select(s => $"'{s}'"))}?" : ".")}");
            }
        }

        var loadedFamilies = FamilyInventory.LoadedByName(doc);
        var alreadyLoaded = spec.FamilyName.Length > 0 && loadedFamilies.ContainsKey(spec.FamilyName);
        var overwriteLoaded = string.Equals(spec.IfExists, "overwrite", StringComparison.OrdinalIgnoreCase);
        var loadStatus = !spec.LoadIntoProject ? "notRequested" : !alreadyLoaded ? "willLoad" : overwriteLoaded ? "willOverwrite" : "willSkip";

        if (!apply)
        {
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = problems.Count == 0
                    ? $"Preview: ready to create '{spec.FamilyName}' with {spec.Curves.Count} curve(s)" +
                      $"{(spec.Parameters.Count > 0 ? $", {spec.Parameters.Count} parameter(s)" : string.Empty)}" +
                      $"{(spec.Labels.Count > 0 ? $", {spec.Labels.Count} label(s) in {labelGroups.Count} annotation famil{(labelGroups.Count == 1 ? "y" : "ies")}" : string.Empty)}" +
                      $" at '{spec.SavePath}'."
                    : $"Preview: {problems.Count} problem(s) to fix before creating the family.",
                Data = new
                {
                    ready = problems.Count == 0,
                    problems,
                    familyName = spec.FamilyName,
                    savePath = spec.SavePath,
                    fileExists,
                    willReplaceFile = fileExists && spec.Overwrite,
                    template = templatePath,
                    curves = spec.CountsByKind(),
                    lineStyles = stylePlan.Select(s => new
                    {
                        name = s.Requested,
                        status = s.Status,
                        weight = s.Project?.Weight,
                        color = s.Project?.Color,
                        pattern = s.Project == null ? null : s.Project.Pattern.Length > 0 ? s.Project.Pattern : "Solid",
                        usedBy = s.UsedBy
                    }).ToList(),
                    parameters = parameters.Items.Select(DetailFamilyExtrasRevit.DescribePlan).ToList(),
                    sharedParameterFile = parameters.SharedFilePath,
                    sharedParameterFileSource = parameters.SharedFileSource,
                    labels = spec.Labels.Count == 0 ? null : new
                    {
                        count = spec.Labels.Count,
                        annotationTemplate,
                        families = labelGroups.Select(g => new
                        {
                            name = g.FamilyName,
                            text = g.Template.Text,
                            textType = g.Template.TextType.Length > 0 ? g.Template.TextType : null,
                            sizeMm = g.Template.SizeMm
                                     ?? (g.Template.TextType.Length > 0 && projectTextTypes.TryGetValue(g.Template.TextType, out var t) ? t.SizeMm : null)
                                     ?? DetailFamilySpecParser.DefaultLabelSizeMm,
                            alignment = g.Template.Alignment,
                            instances = g.Instances.Count
                        }).ToList()
                    },
                    purge = spec.Purge,
                    load = new { status = loadStatus, documentTitle = doc.Title, ifExists = spec.IfExists }
                },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (problems.Count > 0)
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "invalid_arguments",
                Message = $"Nothing was created: {problems.Count} problem(s). {string.Join(" ", problems.Take(3))}",
                Errors = problems,
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };

        var tempFolder = Path.Combine(Path.GetTempPath(), "RevitMCP", "DetailFamily", Guid.NewGuid().ToString("N"));
        var tempPath = Path.Combine(tempFolder, Path.GetFileName(spec.SavePath));
        Document? familyDoc = null;
        using var dialogs = new ModalDialogGuard(uiapp);
        try
        {
            Directory.CreateDirectory(tempFolder);
            familyDoc = uiapp.Application.NewFamilyDocument(templatePath!);
            if (familyDoc == null) return Fail(request, $"Revit could not create a family from '{templatePath}'.");

            var category = familyDoc.OwnerFamily?.FamilyCategory;
            if (category == null || category.Id.Value != (long)BuiltInCategory.OST_DetailComponents)
                warnings.Add($"The template's category is '{category?.Name ?? "none"}', not Detail Items.");

            var view = FindSketchView(familyDoc);
            if (view == null) return Fail(request, $"The template '{templatePath}' has no plan or drafting view to draw in.");

            // 1. Line styles and geometry: one transaction.
            var styleResults = new List<object>();
            var curveFailures = new List<string>();
            var drawn = 0;
            var protectedIds = new HashSet<ElementId>();
            using (var tx = new Transaction(familyDoc, "Revit MCP - Draw detail family"))
            {
                tx.Start();
                var styles = new Dictionary<string, FamilyStyle>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in stylePlan)
                {
                    var style = EnsureLinesStyle(familyDoc, projectStyles[item.Requested], warnings);
                    styles[item.Requested] = style;
                    if (style.Category != null)
                    {
                        protectedIds.Add(style.Category.Id);
                        var gs = style.Category.GetGraphicsStyle(GraphicsStyleType.Projection);
                        if (gs != null) protectedIds.Add(gs.Id);
                        var patternId = style.Category.GetLinePatternId(GraphicsStyleType.Projection);
                        if (patternId != ElementId.InvalidElementId) protectedIds.Add(patternId);
                    }
                }

                foreach (var curveSpec in spec.Curves)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var curve = familyDoc.FamilyCreate.NewDetailCurve(view, BuildCurve(curveSpec));
                        drawn++;
                        if (curveSpec.LineStyle.Length == 0) continue;
                        var style = styles[curveSpec.LineStyle];
                        var applied = ApplyStyle(familyDoc, curve, style, projectStyles[curveSpec.LineStyle], warnings);
                        if (applied != null) protectedIds.UnionWith(applied);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        curveFailures.Add($"{curveSpec.Source}: {ex.Message}");
                    }
                }

                if (drawn == 0 && spec.Curves.Count > 0)
                {
                    tx.RollBack();
                    familyDoc.Close(false);
                    familyDoc = null;
                    return new McpToolResult
                    {
                        RequestId = request.RequestId,
                        Success = false,
                        Status = "transaction_failed",
                        Message = "Revit drew none of the curves; nothing was saved.",
                        Errors = curveFailures,
                        Warnings = warnings,
                        DurationMs = sw.ElapsedMilliseconds
                    };
                }
                TransactionCommitGuard.CommitOrThrow(tx);

                styleResults.AddRange(styles.Select(s => (object)new
                {
                    name = s.Key,
                    action = s.Value.Action,
                    kind = s.Value.Kind,
                    weight = projectStyles[s.Key].Info.Weight,
                    color = projectStyles[s.Key].Info.Color,
                    pattern = projectStyles[s.Key].Info.Pattern.Length > 0 ? projectStyles[s.Key].Info.Pattern : "Solid"
                }));
            }

            // 2. Parameters (phase 2): one transaction.
            var parameterResults = new List<object>();
            if (parameters.Items.Count > 0)
            {
                using var tx = new Transaction(familyDoc, "Revit MCP - Family parameters");
                tx.Start();
                parameterResults = DetailFamilyExtrasRevit.AddParameters(uiapp.Application, familyDoc, spec, parameters, protectedIds, warnings);
                TransactionCommitGuard.CommitOrThrow(tx);
            }

            // 3. Labels (phase 3): a Generic Annotation family per distinct text, loaded and placed in one transaction.
            var labelResults = new List<object>();
            if (labelGroups.Count > 0)
            {
                var annotationFolder = Path.Combine(tempFolder, "Labels");
                Directory.CreateDirectory(annotationFolder);
                var annotationFamilies = new List<(DetailLabelGroup Group, string Path)>();
                foreach (var group in labelGroups)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var annotationPath = DetailFamilyExtrasRevit.CreateAnnotationFamily(uiapp.Application, annotationTemplate!, group,
                        projectTextTypes, annotationFolder, spec.Purge,
                        (annotationDoc, keep) => Purge(annotationDoc, keep, warnings, cancellationToken), warnings);
                    annotationFamilies.Add((group, annotationPath));
                }

                using var tx = new Transaction(familyDoc, "Revit MCP - Place labels");
                tx.Start();
                labelResults = DetailFamilyExtrasRevit.PlaceLabels(familyDoc, view, annotationFamilies, protectedIds, warnings);
                TransactionCommitGuard.CommitOrThrow(tx);
            }

            // 4. Purge, before saving.
            var purged = spec.Purge ? Purge(familyDoc, protectedIds, warnings, cancellationToken) : new Dictionary<string, int>();

            // 3. Save to a temporary file of the final name (the file name is the family name), then copy into place.
            familyDoc.SaveAs(tempPath, new SaveAsOptions { OverwriteExistingFile = true });
            familyDoc.Close(false);
            familyDoc = null;

            var finalFolder = Path.GetDirectoryName(spec.SavePath)!;
            Directory.CreateDirectory(finalFolder);
            if (File.Exists(spec.SavePath) && !spec.Overwrite)
                return Fail(request, $"'{spec.SavePath}' appeared while the family was being created; nothing was replaced.", warnings);
            File.Copy(tempPath, spec.SavePath, overwrite: spec.Overwrite);

            // 4. Load into the project.
            object? loadResult = null;
            if (spec.LoadIntoProject)
                loadResult = Load(doc, spec, alreadyLoaded, overwriteLoaded, warnings);

            warnings.AddRange(dialogs.Dismissed.Distinct().Select(d => $"Closed a Revit dialog: {d}"));
            if (curveFailures.Count > 0)
                warnings.Add($"{curveFailures.Count} curve(s) were not drawn: {string.Join("; ", curveFailures.Take(5))}");

            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Created '{spec.FamilyName}' ({drawn} of {spec.Curves.Count} curve(s)" +
                          $"{(parameterResults.Count > 0 ? $", {parameterResults.Count} parameter(s)" : string.Empty)}" +
                          $"{(labelResults.Count > 0 ? $", {spec.Labels.Count} label(s)" : string.Empty)}) at '{spec.SavePath}'" +
                          $"{(spec.Purge ? $", purged {purged.Values.Sum()} unused element(s)" : string.Empty)}" +
                          $"{(spec.LoadIntoProject ? $"; load: {LoadStatusOf(loadResult)}" : string.Empty)}.",
                Data = new
                {
                    familyName = spec.FamilyName,
                    savePath = spec.SavePath,
                    replacedFile = fileExists,
                    template = templatePath,
                    curvesDrawn = drawn,
                    curvesFailed = curveFailures,
                    lineStyles = styleResults,
                    parameters = parameterResults,
                    sharedParameterFile = parameters.AnyShared ? parameters.SharedFilePath : null,
                    labels = labelResults,
                    purged,
                    load = loadResult
                },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            return Fail(request, "The request was cancelled; the family was not saved.", warnings);
        }
        catch (Exception ex)
        {
            return Fail(request, $"Creating the family failed: {ex.Message}", warnings);
        }
        finally
        {
            try { familyDoc?.Close(false); } catch { /* already closed */ }
            try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, recursive: true); } catch { /* temp only */ }
        }
    }

    // ── Line styles ──────────────────────────────────────────────────────────

    private sealed class ProjectStyle
    {
        public ProjectLineStyleInfo Info { get; set; } = new();
        public LinePattern? Pattern { get; set; }
    }

    private sealed class FamilyStyle
    {
        public Category? Category { get; set; }
        /// <summary>reused (the template had it) | created.</summary>
        public string Action { get; set; } = string.Empty;
        /// <summary>lines (a Lines subcategory, merges with the project's style on load) | detailItems (fallback).</summary>
        public string Kind { get; set; } = "lines";
    }

    private static Dictionary<string, ProjectStyle> ReadProjectLineStyles(Document doc)
    {
        var result = new Dictionary<string, ProjectStyle>(StringComparer.OrdinalIgnoreCase);
        var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        if (lines == null) return result;
        foreach (Category sub in lines.SubCategories)
        {
            var patternId = sub.GetLinePatternId(GraphicsStyleType.Projection);
            var patternElement = patternId == ElementId.InvalidElementId || patternId == LinePatternElement.GetSolidPatternId()
                ? null
                : doc.GetElement(patternId) as LinePatternElement;
            var color = sub.LineColor;
            result[sub.Name] = new ProjectStyle
            {
                Info = new ProjectLineStyleInfo
                {
                    Name = sub.Name,
                    Weight = sub.GetLineWeight(GraphicsStyleType.Projection),
                    Color = color != null && color.IsValid ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}" : string.Empty,
                    Pattern = patternElement?.Name ?? string.Empty
                },
                Pattern = patternElement?.GetLinePattern()
            };
        }
        return result;
    }

    /// <summary>The family's Lines subcategory of that name: the template's, else a new one copied from the project.</summary>
    private static FamilyStyle EnsureLinesStyle(Document familyDoc, ProjectStyle project, List<string> warnings)
    {
        var lines = familyDoc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        if (lines == null) return new FamilyStyle { Action = "unavailable", Kind = "none" };
        foreach (Category sub in lines.SubCategories)
        {
            if (!string.Equals(sub.Name, project.Info.Name, StringComparison.OrdinalIgnoreCase)) continue;
            var weight = sub.GetLineWeight(GraphicsStyleType.Projection);
            if (weight != project.Info.Weight)
                warnings.Add($"The template's line style '{sub.Name}' has weight {weight}, the project's {project.Info.Weight}; in the project the project's settings apply.");
            return new FamilyStyle { Category = sub, Action = "reused", Kind = "lines" };
        }
        if (!lines.CanAddSubcategory)
            return new FamilyStyle { Action = "unavailable", Kind = "none" };
        var created = familyDoc.Settings.Categories.NewSubcategory(lines, project.Info.Name);
        CopyStyle(familyDoc, created, project, warnings);
        return new FamilyStyle { Category = created, Action = "created", Kind = "lines" };
    }

    private static void CopyStyle(Document familyDoc, Category target, ProjectStyle project, List<string> warnings)
    {
        if (project.Info.Weight is int weight) target.SetLineWeight(weight, GraphicsStyleType.Projection);
        if (project.Info.Color.Length == 7)
        {
            target.LineColor = new Color(
                Convert.ToByte(project.Info.Color.Substring(1, 2), 16),
                Convert.ToByte(project.Info.Color.Substring(3, 2), 16),
                Convert.ToByte(project.Info.Color.Substring(5, 2), 16));
        }
        target.SetLinePatternId(EnsurePattern(familyDoc, project, warnings), GraphicsStyleType.Projection);
    }

    private static ElementId EnsurePattern(Document familyDoc, ProjectStyle project, List<string> warnings)
    {
        if (project.Pattern == null || project.Info.Pattern.Length == 0) return LinePatternElement.GetSolidPatternId();
        var existing = LinePatternElement.GetLinePatternElementByName(familyDoc, project.Info.Pattern);
        if (existing != null) return existing.Id;
        try
        {
            var copy = new LinePattern(project.Info.Pattern);
            copy.SetSegments(project.Pattern.GetSegments());
            return LinePatternElement.Create(familyDoc, copy).Id;
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not copy line pattern '{project.Info.Pattern}' ({ex.Message}); '{project.Info.Name}' is solid in the family.");
            return LinePatternElement.GetSolidPatternId();
        }
    }

    /// <summary>Sets the curve's style; when Revit does not offer the Lines style, falls back to a Detail Items subcategory.</summary>
    private static IEnumerable<ElementId>? ApplyStyle(Document familyDoc, DetailCurve curve, FamilyStyle style, ProjectStyle project, List<string> warnings)
    {
        var allowed = curve.GetLineStyleIds();
        GraphicsStyle? Find() => allowed
            .Select(id => familyDoc.GetElement(id) as GraphicsStyle)
            .FirstOrDefault(gs => gs != null && string.Equals(gs.GraphicsStyleCategory?.Name, project.Info.Name, StringComparison.OrdinalIgnoreCase));

        var match = Find();
        if (match == null && style.Kind != "detailItems")
        {
            // The template does not offer Lines subcategories to detail curves: use a subcategory of the family's category.
            var familyCategory = familyDoc.OwnerFamily?.FamilyCategory;
            if (familyCategory != null && familyCategory.CanAddSubcategory)
            {
                Category? sub = null;
                foreach (Category c in familyCategory.SubCategories)
                    if (string.Equals(c.Name, project.Info.Name, StringComparison.OrdinalIgnoreCase)) sub = c;
                if (sub == null)
                {
                    sub = familyDoc.Settings.Categories.NewSubcategory(familyCategory, project.Info.Name);
                    CopyStyle(familyDoc, sub, project, warnings);
                }
                style.Category = sub;
                style.Kind = "detailItems";
                style.Action = "created";
                warnings.Add($"Detail curves in this template cannot use Lines styles; '{project.Info.Name}' was made a Detail Items subcategory with the project style's weight, colour and pattern.");
                allowed = curve.GetLineStyleIds();
                match = Find();
            }
        }
        if (match == null)
        {
            warnings.Add($"Line style '{project.Info.Name}' could not be applied; the curve keeps the default style.");
            return null;
        }
        curve.LineStyle = match;
        return new[] { match.Id, match.GraphicsStyleCategory.Id };
    }

    // ── Geometry ─────────────────────────────────────────────────────────────

    private static XYZ Xyz(DetailPoint p) => new(p.X / MmPerFoot, p.Y / MmPerFoot, 0);

    private static Curve BuildCurve(DetailCurveSpec spec)
    {
        switch (spec.Kind)
        {
            case "arc" when spec.Mid != null:
                return Arc.Create(Xyz(spec.Start), Xyz(spec.End), Xyz(spec.Mid.Value));
            case "arc":
            {
                var start = spec.StartAngle * Math.PI / 180;
                var sweep = DetailFamilySpecParser.Sweep(spec.StartAngle, spec.EndAngle) * Math.PI / 180;
                return Arc.Create(Xyz(spec.Center), spec.Radius / MmPerFoot, start, start + sweep, XYZ.BasisX, XYZ.BasisY);
            }
            case "circle":
                return Arc.Create(Xyz(spec.Center), spec.Radius / MmPerFoot, 0, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY);
            default:
                return Line.CreateBound(Xyz(spec.Start), Xyz(spec.End));
        }
    }

    private static View? FindSketchView(Document familyDoc)
    {
        var views = new FilteredElementCollector(familyDoc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
        return views.FirstOrDefault(v => v.ViewType == ViewType.FloorPlan)
            ?? views.FirstOrDefault(v => v.ViewType == ViewType.DraftingView)
            ?? views.FirstOrDefault(v => v.ViewType is ViewType.CeilingPlan or ViewType.EngineeringPlan);
    }

    // ── Purge ────────────────────────────────────────────────────────────────

    /// <summary>Deletes what Revit lists as unused, pass after pass (deleting frees more), never the protected ids.</summary>
    private static Dictionary<string, int> Purge(Document familyDoc, HashSet<ElementId> protectedIds, List<string> warnings, CancellationToken cancellationToken)
    {
        var deletedByKind = new Dictionary<string, int>();
        var refused = new HashSet<ElementId>();
        for (var pass = 0; pass < MaxPurgePasses; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ICollection<ElementId> unused;
            try { unused = familyDoc.GetUnusedElements(new HashSet<ElementId>()); }
            catch (Exception ex)
            {
                warnings.Add($"Purge skipped: Revit could not list unused elements ({ex.Message}).");
                break;
            }
            var candidates = unused.Where(id => !protectedIds.Contains(id) && !refused.Contains(id)).ToList();
            if (candidates.Count == 0) break;

            var deletedThisPass = 0;
            using var tx = new Transaction(familyDoc, "Revit MCP - Purge detail family");
            tx.Start();
            foreach (var id in candidates)
            {
                var element = familyDoc.GetElement(id);
                if (element == null) continue;
                var kind = element.GetType().Name;
                using var sub = new SubTransaction(familyDoc);
                sub.Start();
                try
                {
                    familyDoc.Delete(id);
                    sub.Commit();
                    deletedByKind[kind] = deletedByKind.TryGetValue(kind, out var n) ? n + 1 : 1;
                    deletedThisPass++;
                }
                catch
                {
                    sub.RollBack();
                    refused.Add(id);
                }
            }
            TransactionCommitGuard.CommitOrThrow(tx);
            if (deletedThisPass == 0) break;
        }
        return deletedByKind;
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    private static object Load(Document doc, DetailFamilySpec spec, bool alreadyLoaded, bool overwrite, List<string> warnings)
    {
        if (alreadyLoaded && !overwrite)
            return new { status = "skipped", reason = $"A family named '{spec.FamilyName}' is already loaded (ifExists=skip)." };

        var options = new FamilyLoadOptions(overwrite ? FamilyIfExists.Overwrite : FamilyIfExists.Skip);
        Family? family = null;
        using (var tx = new Transaction(doc, $"Revit MCP - Load Family {spec.FamilyName}"))
        {
            tx.Start();
            var ok = doc.LoadFamily(spec.SavePath, options, out family);
            if (!ok || family == null)
            {
                tx.RollBack();
                return new { status = alreadyLoaded ? "skipped" : "failed", reason = alreadyLoaded ? "The loaded family already matches the file." : "Revit did not load the file." };
            }
            foreach (var symbol in FamilyInventory.Symbols(doc, family))
            {
                try { if (!symbol.IsActive) symbol.Activate(); }
                catch { /* nothing to activate */ }
            }
            TransactionCommitGuard.CommitOrThrow(tx);
        }

        var storeError = FamilyPathStore.Default().Remember(CheckpointChangeTracker.ModelIdOf(doc), new List<(string, string)> { (spec.FamilyName, spec.SavePath) });
        if (storeError != null) warnings.Add(storeError);
        return new
        {
            status = alreadyLoaded ? "overwritten" : "loaded",
            familyId = family.Id.Value,
            types = FamilyInventory.Symbols(doc, family).Select(s => new { id = s.Id.Value, name = FamilyTypeSupport.SafeName(s) }).ToList()
        };
    }

    private static string LoadStatusOf(object? load) =>
        load?.GetType().GetProperty("status")?.GetValue(load)?.ToString() ?? "none";

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string SafeTemplateRoot(UIApplication uiapp)
    {
        try { return uiapp.Application.FamilyTemplatePath ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static IEnumerable<string> SafeFind(string root, string fileName = DetailFamilySpecParser.DefaultTemplateFileName)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).Take(20).ToList()
                : Enumerable.Empty<string>();
        }
        catch { return Enumerable.Empty<string>(); }
    }

    private static McpToolResult Fail(McpToolRequest request, string message, List<string>? warnings = null) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Message = message,
        Errors = [message],
        Warnings = warnings ?? new List<string>()
    };
}
