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
        "shared parameter file), checks label text types and lists the annotation families the labels need, resolves " +
        "filled region types and their fill patterns against the project, finds the label families of parameter labels, " +
        "lists the types and their values, and states whether the family would load.";

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
        "existing file. lineStyles [{name, weight? (1-16), color? (#RRGGBB), pattern? (a line pattern of the project, or " +
        "solid)}] defines line styles of the family itself: such a name need not exist in the project. loadIntoProject " +
        "loads it into the document (ifExists: skip | overwrite). parameters [{name, " +
        "dataType, group?, instance?, value?, formula?}] adds family parameters; {name or guid, shared:true} assigns an " +
        "existing shared parameter from the shared parameter file (never creates one). labels [{text, position:[x,y], " +
        "size? (paper mm), textType?, alignment?, rotation?}] places text through nested Generic Annotation families. " +
        "filledRegions [{points:[[x,y],…] | min,max | center,radius, holes?, type?, lineStyle?}] draws filled regions; a type is " +
        "a filled region type of the project (copied into the family) or one defined in filledRegionTypes [{name, copyFrom?, " +
        "foregroundPattern?, foregroundColor?, backgroundPattern?, backgroundColor?, lineWeight?, isMasking?}] (patterns: a fill " +
        "pattern of the project, solid or none). A label that shows a parameter: labels [{parameter, position, labelFamily, " +
        "labelType?, labelParameter?}], where labelFamily is a Generic Annotation family that already holds a label (loaded in " +
        "the project, or a .rfa path): it is nested and its label parameter linked to the family's parameter. visibleParameter " +
        "on a curve, filled region or label names a yesNo parameter of parameters that switches it on and off. types " +
        "[{name, values:{\"<parameter>\": value}}] makes several types (values in the units of parameters[].value; a " +
        "parameter's own value is every type's default). Run revit_preview_create_detail_family first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(DetailFamilyExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => DetailFamilyExecutor.Summary(request);
}

internal static class DetailFamilyExecutor
{
    public static string Summary(McpToolRequest request)
    {
        var errors = new List<string>();
        var spec = DetailFamilySpecParser.Parse(request.Arguments, errors);
        var document = ToolArguments.GetString(request.Arguments, "document").Trim();
        return $"Create Detail Item family '{spec.FamilyName}' ({DescribeContent(spec)}) and save it to " +
               $"'{spec.SavePath}'{(spec.Overwrite ? ", replacing an existing file" : string.Empty)}" +
               $"{(spec.Purge ? ", purged" : string.Empty)}" +
               $"{(spec.LoadIntoProject ? $"; then load it into {(document.Length > 0 ? $"'{document}'" : "the active document")} (ifExists={spec.IfExists})" : string.Empty)}.";
    }

    /// <summary>What a spec adds, for approval summaries: curves, line styles, regions, parameters, labels, visibility and types.</summary>
    public static string DescribeContent(DetailFamilySpec spec)
    {
        var counts = string.Join(", ", spec.CountsByKind().Select(c => $"{c.Value} {c.Key}{(c.Value == 1 ? string.Empty : "s")}"));
        var styles = spec.LineStyleNames().Concat(spec.LineStyles.Select(d => d.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var shared = spec.Parameters.Count(p => p.Shared);
        var parameters = spec.Parameters.Count == 0 ? string.Empty
            : $"; {spec.Parameters.Count} parameter(s){(shared > 0 ? $", {shared} of them shared (existing definitions only)" : string.Empty)}";
        var textLabels = spec.TextLabels.Count();
        var parameterLabels = spec.ParameterLabels.ToList();
        var labels = (textLabels == 0 ? string.Empty : $"; {textLabels} text label(s) as nested Generic Annotation families") +
                     (parameterLabels.Count == 0 ? string.Empty
                         : $"; {parameterLabels.Count} parameter label(s) through {string.Join(", ", parameterLabels.Select(l => DetailFamilySpecParser.LabelFamilyName(l.LabelFamily)).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Select(n => $"'{n}'"))}");
        var regions = spec.FilledRegions.Count == 0 && spec.FilledRegionTypes.Count == 0 ? string.Empty
            : $"; {spec.FilledRegions.Count} filled region(s)" +
              $"{(spec.FilledRegionTypes.Count > 0 ? $", filled region types {string.Join(", ", spec.FilledRegionTypes.Select(t => t.Name))}" : string.Empty)}";
        var visibility = spec.VisibilityReferences().Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var visible = visibility.Count == 0 ? string.Empty : $"; visibility by {string.Join(", ", visibility)}";
        var types = spec.Types.Count == 0 ? string.Empty
            : $"; types {string.Join(", ", spec.Types.Take(10).Select(t => $"'{t.Name}'"))}{(spec.Types.Count > 10 ? $" and {spec.Types.Count - 10} more" : string.Empty)}";
        return $"{(counts.Length > 0 ? counts : "no curves")}" +
               $"{(styles.Count > 0 ? $"; line styles {string.Join(", ", styles)}" : string.Empty)}{regions}{parameters}{labels}{visible}{types}";
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

        var (templatePath, templateError) = DetailFamilySpecParser.ResolveTemplate(spec.TemplatePath, DetailFamilyBuilder.SafeTemplateRoot(uiapp),
            File.Exists, root => DetailFamilyBuilder.SafeFind(root));
        if (templateError != null) problems.Add(templateError);

        var plan = DetailFamilyBuilder.Plan(uiapp, doc, spec, problems, warnings);

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
                      $"{(spec.FilledRegions.Count > 0 ? $", {spec.FilledRegions.Count} filled region(s)" : string.Empty)}" +
                      $"{(plan.TextLabelCount > 0 ? $", {plan.TextLabelCount} text label(s) in {plan.LabelGroups.Count} annotation famil{(plan.LabelGroups.Count == 1 ? "y" : "ies")}" : string.Empty)}" +
                      $"{(plan.ParameterLabelCount > 0 ? $", {plan.ParameterLabelCount} parameter label(s)" : string.Empty)}" +
                      $"{(spec.Types.Count > 0 ? $", {spec.Types.Count} type(s)" : string.Empty)}" +
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
                    lineStyles = DetailFamilyBuilder.DescribeLineStyles(plan),
                    parameters = plan.Parameters.Items.Select(DetailFamilyExtrasRevit.DescribePlan).ToList(),
                    sharedParameterFile = plan.Parameters.SharedFilePath,
                    sharedParameterFileSource = plan.Parameters.SharedFileSource,
                    filledRegions = DetailFamilyBuilder.DescribeFilledRegions(spec, plan),
                    parameterLabels = DetailFamilyBuilder.DescribeParameterLabels(spec, plan),
                    visibility = DetailFamilyBuilder.DescribeVisibility(spec),
                    labels = DetailFamilyBuilder.DescribeLabels(plan),
                    types = DetailFamilyBuilder.DescribeTypes(spec, Array.Empty<string>()),
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

            var view = DetailFamilyBuilder.FindSketchView(familyDoc);
            if (view == null) return Fail(request, $"The template '{templatePath}' has no plan or drafting view to draw in.");

            // Line styles, curves, filled regions, parameters, types, labels and visibility: one transaction each.
            var drawn = DetailFamilyBuilder.Draw(uiapp, doc, familyDoc, view, spec, plan, tempFolder, warnings, cancellationToken);
            if (drawn.NothingDrawn)
            {
                familyDoc.Close(false);
                familyDoc = null;
                return new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Status = "transaction_failed",
                    Message = "Revit drew none of the curves; nothing was saved.",
                    Errors = drawn.CurveFailures,
                    Warnings = warnings,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            // Purge, before saving.
            var purged = spec.Purge ? DetailFamilyBuilder.Purge(familyDoc, drawn.ProtectedIds, warnings, cancellationToken) : new Dictionary<string, int>();

            // Save to a temporary file of the final name (the file name is the family name), then copy into place.
            familyDoc.SaveAs(tempPath, new SaveAsOptions { OverwriteExistingFile = true });
            familyDoc.Close(false);
            familyDoc = null;

            var finalFolder = Path.GetDirectoryName(spec.SavePath)!;
            Directory.CreateDirectory(finalFolder);
            if (File.Exists(spec.SavePath) && !spec.Overwrite)
                return Fail(request, $"'{spec.SavePath}' appeared while the family was being created; nothing was replaced.", warnings);
            File.Copy(tempPath, spec.SavePath, overwrite: spec.Overwrite);

            // Load into the project.
            object? loadResult = null;
            if (spec.LoadIntoProject)
                loadResult = Load(doc, spec, alreadyLoaded, overwriteLoaded, warnings);

            warnings.AddRange(dialogs.Dismissed.Distinct().Select(d => $"Closed a Revit dialog: {d}"));
            if (drawn.CurveFailures.Count > 0)
                warnings.Add($"{drawn.CurveFailures.Count} curve(s) were not drawn: {string.Join("; ", drawn.CurveFailures.Take(5))}");

            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Created '{spec.FamilyName}' ({drawn.CurvesDrawn} of {spec.Curves.Count} curve(s)" +
                          $"{(drawn.Parameters.Count > 0 ? $", {drawn.Parameters.Count} parameter(s)" : string.Empty)}" +
                          $"{(spec.FilledRegions.Count > 0 ? $", {drawn.RegionsDrawn} of {spec.FilledRegions.Count} filled region(s)" : string.Empty)}" +
                          $"{(drawn.Labels.Count > 0 ? $", {plan.TextLabelCount} text label(s)" : string.Empty)}" +
                          $"{(drawn.ParameterLabels.Count > 0 ? $", {drawn.ParameterLabels.Count(r => DetailFamilyBuilder.StatusOf(r) == "placed")} of {plan.ParameterLabelCount} parameter label(s)" : string.Empty)}" +
                          $"{(drawn.Types.Count > 0 ? $", {drawn.Types.Count} type(s)" : string.Empty)}) at '{spec.SavePath}'" +
                          $"{(spec.Purge ? $", purged {purged.Values.Sum()} unused element(s)" : string.Empty)}" +
                          $"{(spec.LoadIntoProject ? $"; load: {DetailFamilyBuilder.StatusOf(loadResult) ?? "none"}" : string.Empty)}.",
                Data = new
                {
                    familyName = spec.FamilyName,
                    savePath = spec.SavePath,
                    replacedFile = fileExists,
                    template = templatePath,
                    curvesDrawn = drawn.CurvesDrawn,
                    curvesFailed = drawn.CurveFailures,
                    lineStyles = drawn.LineStyles,
                    parameters = drawn.Parameters,
                    sharedParameterFile = plan.Parameters.AnyShared ? plan.Parameters.SharedFilePath : null,
                    filledRegionTypes = drawn.RegionTypes,
                    filledRegions = drawn.Regions,
                    labels = drawn.Labels,
                    parameterLabels = drawn.ParameterLabels,
                    visibility = drawn.Visibility,
                    types = drawn.Types.Count > 0 ? drawn.Types : null,
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    internal static McpToolResult Fail(McpToolRequest request, string message, List<string>? warnings = null) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Message = message,
        Errors = [message],
        Warnings = warnings ?? new List<string>()
    };
}
