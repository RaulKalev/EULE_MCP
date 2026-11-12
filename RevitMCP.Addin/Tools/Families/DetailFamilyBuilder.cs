using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Parameters;

namespace RevitMCP.Addin.Tools.Families;

/// <summary>A line style of the project (or one defined in lineStyles) as the family gets it.</summary>
internal sealed class ProjectStyle
{
    public ProjectLineStyleInfo Info { get; set; } = new();
    public LinePattern? Pattern { get; set; }
    /// <summary>From lineStyles: the family's style gets exactly these values, also when the template already has the name.</summary>
    public bool Defined { get; set; }
}

/// <summary>The family's subcategory a line style became.</summary>
internal sealed class FamilyStyle
{
    public Category? Category { get; set; }
    /// <summary>reused (the template had it) | edited (the template had it; lineStyles set its values) | created.</summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>lines (a Lines subcategory, merges with the project's style on load) | detailItems (fallback).</summary>
    public string Kind { get; set; } = "lines";
}

/// <summary>A spec resolved against the project: what the family's content needs before anything is drawn.</summary>
internal sealed class DetailFamilyPlan
{
    public Dictionary<string, ProjectStyle> ProjectStyles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LineStylePlanItem> StylePlan { get; set; } = new();
    public ResolvedFamilyParameters Parameters { get; set; } = new();
    public List<DetailLabelGroup> LabelGroups { get; set; } = new();
    public string? AnnotationTemplate { get; set; }
    public Dictionary<string, ProjectTextTypeInfo> ProjectTextTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<RegionTypePlan> RegionTypes { get; set; } = new();
    public Dictionary<string, LabelFamilySource> LabelFamilies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int TextLabelCount { get; set; }
    public int ParameterLabelCount { get; set; }
}

/// <summary>What drawing a spec into a family document did.</summary>
internal sealed class DetailFamilyDrawResult
{
    public List<object> LineStyles { get; } = new();
    public List<object> RegionTypes { get; set; } = new();
    public List<object> Regions { get; set; } = new();
    public List<object> Parameters { get; set; } = new();
    public List<object> Labels { get; set; } = new();
    public List<object> ParameterLabels { get; set; } = new();
    public List<object> Visibility { get; set; } = new();
    public List<object> Types { get; set; } = new();
    public List<string> CurveFailures { get; } = new();
    public int CurvesDrawn { get; set; }
    public int RegionsDrawn { get; set; }
    /// <summary>Elements the purge must keep: what the spec made or uses.</summary>
    public HashSet<ElementId> ProtectedIds { get; } = new();
    /// <summary>Curves were asked for and Revit drew none; the drawing transaction was rolled back.</summary>
    public bool NothingDrawn { get; set; }
}

/// <summary>
/// The steps revit_create_detail_family and revit_edit_detail_family share (#147): resolve a spec against the
/// project, then draw it into an open family document (line styles, curves, filled regions, parameters, types,
/// labels, parameter labels, visibility) and purge it. Each step is its own transaction in the family document.
/// </summary>
internal static class DetailFamilyBuilder
{
    private const double MmPerFoot = 304.8;
    private const int MaxPurgePasses = 5;

    // ── Plan (project side) ──────────────────────────────────────────────────

    /// <summary>
    /// Resolves the spec against the project: line styles, parameters (shared ones against the shared parameter file),
    /// text labels and their text types, filled region types, label families and visibility parameters. New label
    /// annotation families are named so they miss <paramref name="takenFamilyNames"/> (the families an edited family nests).
    /// </summary>
    public static DetailFamilyPlan Plan(UIApplication uiapp, Document doc, DetailFamilySpec spec, List<string> problems,
        List<string> warnings, IEnumerable<string>? takenFamilyNames = null)
    {
        var plan = new DetailFamilyPlan { ProjectStyles = ReadProjectLineStyles(doc) };
        var projectStyles = plan.ProjectStyles;
        plan.StylePlan = DetailFamilyLineStylePlanner.Plan(spec, projectStyles.Values.Select(s => s.Info).ToList());
        // A style defined in lineStyles is made from the request: its pattern is a line pattern of the project, copied by name.
        foreach (var defined in plan.StylePlan.Where(s => s.Status == "defined"))
        {
            LinePattern? pattern = null;
            if (defined.Project!.Pattern.Length > 0)
            {
                var element = LinePatternElement.GetLinePatternElementByName(doc, defined.Project.Pattern);
                if (element == null)
                    problems.Add($"lineStyles: '{defined.Project.Pattern}' (pattern of '{defined.Requested}') is not a line pattern of '{doc.Title}': use a line pattern name or solid.");
                else
                {
                    defined.Project.Pattern = element.Name;
                    pattern = element.GetLinePattern();
                }
            }
            projectStyles[defined.Requested] = new ProjectStyle { Info = defined.Project, Pattern = pattern, Defined = true };
        }
        foreach (var missing in plan.StylePlan.Where(s => s.Status == "missing"))
        {
            var suggestions = DetailFamilyLineStylePlanner.Suggest(missing.Requested, projectStyles.Values.Select(s => s.Info));
            problems.Add($"Line style '{missing.Requested}' (used by {string.Join(", ", missing.UsedBy)}) is not a line style of " +
                         $"'{doc.Title}'{(suggestions.Count > 0 ? $"; did you mean {string.Join(", ", suggestions.Select(s => $"'{s}'"))}?" : ".")} " +
                         "To make a new style in the family, define it in lineStyles.");
        }

        // Parameters, against the running Revit and the shared parameter file.
        plan.Parameters = DetailFamilyExtrasRevit.ResolveParameters(uiapp.Application, doc, spec, problems, warnings);
        CheckSharedTypeValues(spec, plan.Parameters, problems);

        // Labels, as nested Generic Annotation families.
        plan.LabelGroups = DetailFamilySpecParser.GroupLabels(spec, problems);
        if (takenFamilyNames != null)
            DetailFamilySpecParser.RenameLabelGroups(plan.LabelGroups, spec.FamilyName, takenFamilyNames);
        plan.TextLabelCount = spec.TextLabels.Count();
        plan.ParameterLabelCount = spec.ParameterLabels.Count();
        if (plan.TextLabelCount > 0)
        {
            string? annotationError;
            (plan.AnnotationTemplate, annotationError) = DetailFamilySpecParser.ResolveTemplate(spec.AnnotationTemplatePath, SafeTemplateRoot(uiapp),
                File.Exists, root => SafeFind(root, DetailFamilySpecParser.DefaultAnnotationTemplateFileName),
                DetailFamilySpecParser.DefaultAnnotationTemplateFileName);
            if (annotationError != null) problems.Add(annotationError);

            plan.ProjectTextTypes = DetailFamilyExtrasRevit.ReadProjectTextTypes(doc);
            foreach (var name in DetailFamilySpecParser.LabelTextTypeNames(spec).Where(n => !plan.ProjectTextTypes.ContainsKey(n)))
            {
                var suggestions = DetailFamilyExtrasRevit.SuggestTextTypes(name, plan.ProjectTextTypes.Keys);
                problems.Add($"Text type '{name}' is not a text type of '{doc.Title}'" +
                             $"{(suggestions.Count > 0 ? $"; did you mean {string.Join(", ", suggestions.Select(s => $"'{s}'"))}?" : ".")}");
            }
        }

        // Filled region types against the project, label families of parameter labels, and what visibility needs.
        plan.RegionTypes = DetailFamilyRegionsRevit.PlanTypes(doc, spec, problems);
        plan.LabelFamilies = DetailFamilyRegionsRevit.ResolveLabelFamilies(doc, spec, problems);
        foreach (var (source, name) in spec.VisibilityReferences())
        {
            // A family parameter's type was checked with the spec; a shared one is only known from the file.
            var resolved = plan.Parameters.Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (resolved is { Shared: not null } && resolved.Friendly != "yesNo")
                problems.Add($"{source}.visibleParameter '{name}' is a {resolved.DataTypeLabel} shared parameter; visibility needs a yes/no parameter.");
        }
        return plan;
    }

    /// <summary>Type values of shared parameters given without dataType: their type is only known from the file.</summary>
    private static void CheckSharedTypeValues(DetailFamilySpec spec, ResolvedFamilyParameters parameters, List<string> problems)
    {
        foreach (var type in spec.Types)
        foreach (var value in type.Values)
        {
            var item = parameters.Items.FirstOrDefault(i => i.Spec.Spec == null && i.Spec.Shared &&
                                                            string.Equals(i.Name, value.Parameter, StringComparison.OrdinalIgnoreCase));
            if (item == null || item.Spec.Formula.Length > 0) continue;
            if (item.Friendly == null)
            {
                problems.Add($"{value.Source}: values of type {item.DataTypeLabel} cannot be set here.");
                continue;
            }
            var (_, error) = DetailFamilySpecParser.ConvertValue(item.Friendly, value.Value);
            if (error != null) problems.Add($"{value.Source}: {error}");
        }
    }

    // ── Plan descriptions (previews) ─────────────────────────────────────────

    public static List<object> DescribeLineStyles(DetailFamilyPlan plan) => plan.StylePlan.Select(s => (object)new
    {
        name = s.Requested,
        status = s.Status,
        note = s.Status == "defined" && s.UsedBy.Count == 0 ? "Defined but not used by any curve or region; it is created anyway." : null,
        weight = s.Project?.Weight,
        color = s.Project?.Color,
        pattern = s.Project == null ? null : s.Project.Pattern.Length > 0 ? s.Project.Pattern : "Solid",
        usedBy = s.UsedBy
    }).ToList();

    public static object? DescribeFilledRegions(DetailFamilySpec spec, DetailFamilyPlan plan) =>
        spec.FilledRegions.Count == 0 && plan.RegionTypes.Count == 0 ? null : new
        {
            count = spec.FilledRegions.Count,
            types = plan.RegionTypes.Select(DetailFamilyRegionsRevit.DescribePlan).ToList(),
            defaultType = spec.FilledRegions.Count(r => r.Type.Length == 0) is var untyped and > 0
                ? $"{untyped} region(s) without a type use the family's default filled region type."
                : null
        };

    public static object? DescribeParameterLabels(DetailFamilySpec spec, DetailFamilyPlan plan) =>
        plan.ParameterLabelCount == 0 ? null : new
        {
            count = plan.ParameterLabelCount,
            families = plan.LabelFamilies.Values.Select(f => new
            {
                name = f.Name,
                source = f.Kind,
                path = f.Path,
                types = f.Types.Count > 0 ? f.Types : null
            }).ToList(),
            labels = spec.ParameterLabels.Select(l => new
            {
                parameter = l.Parameter,
                family = DetailFamilySpecParser.LabelFamilyName(l.LabelFamily),
                type = l.LabelType.Length > 0 ? l.LabelType : null,
                labelParameter = l.LabelParameter.Length > 0 ? l.LabelParameter : null
            }).ToList(),
            note = "The label family's own label parameter is matched when the label is placed: it must have the same data type as the parameter it shows."
        };

    public static object? DescribeVisibility(DetailFamilySpec spec) =>
        spec.VisibilityReferences().GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { parameter = g.Key, elements = g.Count() }).ToList() is { Count: > 0 } visibilityPlan ? visibilityPlan : null;

    public static object? DescribeLabels(DetailFamilyPlan plan) =>
        plan.TextLabelCount == 0 ? null : new
        {
            count = plan.TextLabelCount,
            annotationTemplate = plan.AnnotationTemplate,
            families = plan.LabelGroups.Select(g => new
            {
                name = g.FamilyName,
                text = g.Template.Text,
                textType = g.Template.TextType.Length > 0 ? g.Template.TextType : null,
                sizeMm = g.Template.SizeMm
                         ?? (g.Template.TextType.Length > 0 && plan.ProjectTextTypes.TryGetValue(g.Template.TextType, out var t) ? t.SizeMm : null)
                         ?? DetailFamilySpecParser.DefaultLabelSizeMm,
                alignment = g.Template.Alignment,
                instances = g.Instances.Count
            }).ToList()
        };

    /// <summary>The types a spec creates or updates, against the type names the family already has (empty for a new family).</summary>
    public static object? DescribeTypes(DetailFamilySpec spec, ICollection<string> existingTypeNames) =>
        spec.Types.Count == 0 ? null : spec.Types.Select(t => (object)new
        {
            name = t.Name,
            action = existingTypeNames.Contains(t.Name, StringComparer.OrdinalIgnoreCase) ? "update" : "create",
            values = t.Values.ToDictionary(v => v.Parameter, v => v.Value.ToString(Newtonsoft.Json.Formatting.None)),
            defaults = spec.Parameters.Where(p => p.Value != null && p.Formula.Length == 0 && !t.Values.Any(v => string.Equals(v.Parameter, p.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(p => p.Name).ToList() is { Count: > 0 } defaults ? defaults : null
        }).ToList();

    // ── Draw (family side) ───────────────────────────────────────────────────

    /// <summary>
    /// Draws the spec into <paramref name="familyDoc"/>: line styles and curves, filled region types and regions,
    /// parameters, types, text labels, parameter labels and visibility links, each step in its own transaction. Must
    /// run with no transaction open in the project (label families of the project are copied out with EditFamily).
    /// </summary>
    public static DetailFamilyDrawResult Draw(UIApplication uiapp, Document projectDoc, Document familyDoc, View view,
        DetailFamilySpec spec, DetailFamilyPlan plan, string tempFolder, List<string> warnings, CancellationToken cancellationToken)
    {
        var result = new DetailFamilyDrawResult();
        var protectedIds = result.ProtectedIds;
        var projectStyles = plan.ProjectStyles;
        var visibilityLinks = new List<VisibilityLink>();

        // 1. Line styles and geometry: one transaction.
        if (plan.StylePlan.Count > 0 || spec.Curves.Count > 0)
        {
            using var tx = new Transaction(familyDoc, "Revit MCP - Draw detail family");
            tx.Start();
            var styles = new Dictionary<string, FamilyStyle>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in plan.StylePlan)
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
                    result.CurvesDrawn++;
                    if (curveSpec.VisibleParameter.Length > 0)
                        visibilityLinks.Add(new VisibilityLink { ElementId = curve.Id, Parameter = curveSpec.VisibleParameter, Source = curveSpec.Source });
                    if (curveSpec.LineStyle.Length == 0) continue;
                    var style = styles[curveSpec.LineStyle];
                    var applied = ApplyStyle(familyDoc, curve, style, projectStyles[curveSpec.LineStyle], warnings);
                    if (applied != null) protectedIds.UnionWith(applied);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.CurveFailures.Add($"{curveSpec.Source}: {ex.Message}");
                }
            }

            if (result.CurvesDrawn == 0 && spec.Curves.Count > 0)
            {
                tx.RollBack();
                result.NothingDrawn = true;
                return result;
            }
            TransactionCommitGuard.CommitOrThrow(tx);

            result.LineStyles.AddRange(styles.Select(s => (object)new
            {
                name = s.Key,
                action = s.Value.Action,
                kind = s.Value.Kind,
                weight = projectStyles[s.Key].Info.Weight,
                color = projectStyles[s.Key].Info.Color,
                pattern = projectStyles[s.Key].Info.Pattern.Length > 0 ? projectStyles[s.Key].Info.Pattern : "Solid"
            }));
        }

        // 1b. Filled region types and filled regions: one transaction.
        if (plan.RegionTypes.Count > 0 || spec.FilledRegions.Count > 0)
        {
            using var tx = new Transaction(familyDoc, "Revit MCP - Filled regions");
            tx.Start();
            var (types, regions, drawn) = DetailFamilyRegionsRevit.DrawRegions(familyDoc, view, spec, plan.RegionTypes,
                protectedIds, visibilityLinks, warnings, cancellationToken);
            result.RegionTypes = types;
            result.Regions = regions;
            result.RegionsDrawn = drawn;
            TransactionCommitGuard.CommitOrThrow(tx);
        }

        // 2. Parameters: one transaction; then the types and their values, another.
        var typeNamesBefore = TypeNames(familyDoc);
        if (plan.Parameters.Items.Count > 0)
        {
            using var tx = new Transaction(familyDoc, "Revit MCP - Family parameters");
            tx.Start();
            result.Parameters = DetailFamilyExtrasRevit.AddParameters(uiapp.Application, familyDoc, spec, plan.Parameters, protectedIds, warnings);
            TransactionCommitGuard.CommitOrThrow(tx);
        }
        if (spec.Types.Count > 0)
        {
            using var tx = new Transaction(familyDoc, "Revit MCP - Family types");
            tx.Start();
            result.Types = ApplyTypes(familyDoc, spec, plan.Parameters, typeNamesBefore, warnings);
            TransactionCommitGuard.CommitOrThrow(tx);
        }

        // 3. Labels: a Generic Annotation family per distinct text, loaded and placed in one transaction.
        if (plan.LabelGroups.Count > 0)
        {
            var annotationFolder = Path.Combine(tempFolder, "Labels");
            Directory.CreateDirectory(annotationFolder);
            var annotationFamilies = new List<(DetailLabelGroup Group, string Path)>();
            foreach (var group in plan.LabelGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var annotationPath = DetailFamilyExtrasRevit.CreateAnnotationFamily(uiapp.Application, plan.AnnotationTemplate!, group,
                    plan.ProjectTextTypes, annotationFolder, spec.Purge,
                    (annotationDoc, keep) => Purge(annotationDoc, keep, warnings, cancellationToken), warnings);
                annotationFamilies.Add((group, annotationPath));
            }

            using var tx = new Transaction(familyDoc, "Revit MCP - Place labels");
            tx.Start();
            result.Labels = DetailFamilyExtrasRevit.PlaceLabels(familyDoc, view, annotationFamilies, protectedIds, visibilityLinks, warnings);
            TransactionCommitGuard.CommitOrThrow(tx);
        }

        // 3b. Parameter labels: nest the label families and link their label parameter to the family's parameter.
        if (plan.ParameterLabelCount > 0)
        {
            var labelFamilyFolder = Path.Combine(tempFolder, "LabelFamilies");
            Directory.CreateDirectory(labelFamilyFolder);
            var labelFamilyFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in plan.LabelFamilies.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { labelFamilyFiles[source.Reference] = DetailFamilyRegionsRevit.LabelFamilyFile(projectDoc, source, labelFamilyFolder); }
                catch (Exception ex) { warnings.Add($"The label family '{source.Name}' could not be prepared: {ex.Message}"); }
            }

            using var tx = new Transaction(familyDoc, "Revit MCP - Parameter labels");
            tx.Start();
            result.ParameterLabels = DetailFamilyRegionsRevit.PlaceParameterLabels(familyDoc, view, spec, labelFamilyFiles, protectedIds, visibilityLinks, warnings);
            TransactionCommitGuard.CommitOrThrow(tx);
        }

        // 3c. Visibility parameters: link each element's Visible parameter, now that the parameters exist.
        if (visibilityLinks.Count > 0)
        {
            using var tx = new Transaction(familyDoc, "Revit MCP - Visibility parameters");
            tx.Start();
            result.Visibility = DetailFamilyRegionsRevit.LinkVisibility(familyDoc, visibilityLinks, warnings);
            TransactionCommitGuard.CommitOrThrow(tx);
        }
        return result;
    }

    // ── Types ────────────────────────────────────────────────────────────────

    public static List<string> TypeNames(Document familyDoc)
    {
        var names = new List<string>();
        foreach (FamilyType type in familyDoc.FamilyManager.Types)
        {
            try { names.Add(type.Name); }
            catch { /* a type without a readable name is skipped */ }
        }
        return names;
    }

    /// <summary>
    /// Creates each type of the spec (or makes the family's type of that name current) and sets its values: first the
    /// parameters' own values (the default for every listed type), then the type's. Runs inside the caller's transaction.
    /// </summary>
    private static List<object> ApplyTypes(Document familyDoc, DetailFamilySpec spec, ResolvedFamilyParameters resolved,
        ICollection<string> typeNamesBefore, List<string> warnings)
    {
        var manager = familyDoc.FamilyManager;
        var results = new List<object>();
        var defaults = resolved.Items.Where(i => i.Value != null && i.Spec.Formula.Length == 0).ToList();
        foreach (var typeSpec in spec.Types)
        {
            FamilyType? type = null;
            foreach (FamilyType candidate in manager.Types)
                if (string.Equals(candidate.Name, typeSpec.Name, StringComparison.OrdinalIgnoreCase)) type = candidate;
            string action;
            try
            {
                if (type == null)
                {
                    type = manager.NewType(typeSpec.Name);
                    action = "created";
                }
                else
                {
                    action = typeNamesBefore.Contains(type.Name, StringComparer.OrdinalIgnoreCase) ? "updated" : "created";
                }
                manager.CurrentType = type;
            }
            catch (Exception ex)
            {
                results.Add(new { name = typeSpec.Name, action = "failed", reason = ex.Message });
                warnings.Add($"Type '{typeSpec.Name}' could not be made: {ex.Message}");
                continue;
            }
            if (type == null) continue;

            var values = new List<object>();
            foreach (var item in defaults.Where(d => !typeSpec.Values.Any(v => string.Equals(v.Parameter, d.Name, StringComparison.OrdinalIgnoreCase))))
            {
                var parameter = manager.get_Parameter(item.Name);
                if (parameter == null) continue;
                try { SetValue(manager, parameter, item.Value!); }
                catch (Exception ex) { warnings.Add($"Type '{type.Name}': the default of '{item.Name}' was not set ({ex.Message})."); }
            }
            foreach (var value in typeSpec.Values)
            {
                string status = "set";
                string? reason = null;
                try
                {
                    var parameter = manager.get_Parameter(value.Parameter)
                                    ?? throw new InvalidOperationException($"the family has no parameter '{value.Parameter}'");
                    if (!string.IsNullOrEmpty(parameter.Formula))
                        throw new InvalidOperationException($"'{value.Parameter}' has the formula '{parameter.Formula}'");
                    var friendly = resolved.Items.FirstOrDefault(i => string.Equals(i.Name, value.Parameter, StringComparison.OrdinalIgnoreCase))?.Friendly
                                   ?? ParameterRevitTypes.FriendlyOf(parameter.Definition.GetDataType())
                                   ?? throw new InvalidOperationException($"values of '{value.Parameter}''s data type cannot be set here");
                    var (converted, error) = DetailFamilySpecParser.ConvertValue(friendly, value.Value);
                    if (error != null) throw new InvalidOperationException(error);
                    SetValue(manager, parameter, converted!);
                }
                catch (Exception ex)
                {
                    status = "failed";
                    reason = ex.Message;
                    warnings.Add($"{value.Source} was not set: {ex.Message}");
                }
                values.Add(new { parameter = value.Parameter, value = value.Value.ToString(Newtonsoft.Json.Formatting.None), status, reason });
            }
            results.Add(new { name = type.Name, action, values });
        }
        return results;
    }

    private static void SetValue(FamilyManager manager, FamilyParameter parameter, object value)
    {
        switch (value)
        {
            case string text: manager.Set(parameter, text); break;
            case int number: manager.Set(parameter, number); break;
            case double number: manager.Set(parameter, number); break;
        }
    }

    // ── Line styles ──────────────────────────────────────────────────────────

    public static Dictionary<string, ProjectStyle> ReadProjectLineStyles(Document doc)
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
            if (project.Defined)
            {
                CopyStyle(familyDoc, sub, project, warnings);
                return new FamilyStyle { Category = sub, Action = "edited", Kind = "lines" };
            }
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

    public static View? FindSketchView(Document familyDoc)
    {
        var views = new FilteredElementCollector(familyDoc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
        return views.FirstOrDefault(v => v.ViewType == ViewType.FloorPlan)
            ?? views.FirstOrDefault(v => v.ViewType == ViewType.DraftingView)
            ?? views.FirstOrDefault(v => v.ViewType is ViewType.CeilingPlan or ViewType.EngineeringPlan);
    }

    // ── Purge ────────────────────────────────────────────────────────────────

    /// <summary>Deletes what Revit lists as unused, pass after pass (deleting frees more), never the protected ids.</summary>
    public static Dictionary<string, int> Purge(Document familyDoc, HashSet<ElementId> protectedIds, List<string> warnings, CancellationToken cancellationToken)
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    public static string? StatusOf(object? result) =>
        result?.GetType().GetProperty("status")?.GetValue(result)?.ToString();

    public static string SafeTemplateRoot(UIApplication uiapp)
    {
        try { return uiapp.Application.FamilyTemplatePath ?? string.Empty; }
        catch { return string.Empty; }
    }

    public static IEnumerable<string> SafeFind(string root, string fileName = DetailFamilySpecParser.DefaultTemplateFileName)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).Take(20).ToList()
                : Enumerable.Empty<string>();
        }
        catch { return Enumerable.Empty<string>(); }
    }
}
