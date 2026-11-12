using System.IO;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.FileSystem;

namespace RevitMCP.Addin.Tools.Families;

/// <summary>A fill pattern a filled region type gets: none, the solid fill, or a named pattern copied from the project.</summary>
internal sealed class RegionPatternChoice
{
    public bool None { get; set; }
    public bool Solid { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>The project's pattern, copied into the family when it has none of that name.</summary>
    public FillPattern? Pattern { get; set; }

    public string Label => None ? "none" : Solid ? "solid" : Name;
}

/// <summary>What happens to one filled region type the spec uses or defines.</summary>
internal sealed class RegionTypePlan
{
    public string Name { get; set; } = string.Empty;
    /// <summary>defined (filledRegionTypes) | project (copied from the project's type of that name) | missing.</summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>The project type the values start from; null when there is none.</summary>
    public string? CopiedFrom { get; set; }
    /// <summary>Null = left as the family's base type has it.</summary>
    public RegionPatternChoice? Foreground { get; set; }
    public RegionPatternChoice? Background { get; set; }
    public string? ForegroundColor { get; set; }
    public string? BackgroundColor { get; set; }
    public int? LineWeight { get; set; }
    public bool? IsMasking { get; set; }
    public List<string> UsedBy { get; set; } = new();
}

/// <summary>Where a parameter label's family comes from.</summary>
internal sealed class LabelFamilySource
{
    public string Reference { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>file (a .rfa path) | project (a family loaded in the project).</summary>
    public string Kind { get; set; } = string.Empty;
    public string? Path { get; set; }
    public Family? Family { get; set; }
    public List<string> Types { get; set; } = new();
}

/// <summary>Something whose Visible parameter is linked to a yes/no family parameter once the parameters exist.</summary>
internal sealed class VisibilityLink
{
    public ElementId ElementId { get; set; } = ElementId.InvalidElementId;
    public string Parameter { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// Filled regions and their types, parameter labels and visibility parameters of revit_create_detail_family.
/// The pure checks are in <see cref="DetailFamilySpecParser"/>; this is the part that talks to Revit.
/// </summary>
internal static class DetailFamilyRegionsRevit
{
    private const double MmPerFoot = 304.8;

    // ── Filled region types: plan (project side) ─────────────────────────────

    /// <summary>
    /// Resolves every filled region type the spec uses or defines against the project: a name in filledRegionTypes is
    /// built from its fields (starting from copyFrom, or the project's type of the same name), any other name must be
    /// a filled region type of the project and is copied. Patterns are fill patterns of the project.
    /// </summary>
    public static List<RegionTypePlan> PlanTypes(Document doc, DetailFamilySpec spec, List<string> problems)
    {
        var plans = new List<RegionTypePlan>();
        var names = spec.FilledRegionTypes.Select(t => t.Name)
            .Concat(spec.FilledRegions.Select(r => r.Type).Where(n => n.Length > 0))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0) return plans;

        var projectTypes = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var projectPatterns = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().ToList();

        foreach (var name in names)
        {
            var definition = spec.FilledRegionTypes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            var plan = new RegionTypePlan
            {
                Name = definition?.Name ?? name,
                UsedBy = spec.FilledRegions.Where(r => string.Equals(r.Type, name, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Source).Take(10).ToList()
            };
            plans.Add(plan);

            FilledRegionType? source = null;
            var copyFrom = definition != null && definition.CopyFrom.Length > 0 ? definition.CopyFrom : name;
            projectTypes.TryGetValue(copyFrom, out source);

            if (definition == null && source == null)
            {
                plan.Status = "missing";
                var close = Suggest(name, projectTypes.Keys);
                problems.Add($"Filled region type '{name}' (used by {string.Join(", ", plan.UsedBy)}) is not a filled region type of '{doc.Title}' " +
                             $"and is not defined in filledRegionTypes{(close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(s => $"'{s}'"))}?" : ".")}");
                continue;
            }
            if (definition != null && definition.CopyFrom.Length > 0 && source == null)
            {
                plan.Status = "missing";
                var close = Suggest(definition.CopyFrom, projectTypes.Keys);
                problems.Add($"{definition.Source}.copyFrom '{definition.CopyFrom}' is not a filled region type of '{doc.Title}'" +
                             $"{(close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(s => $"'{s}'"))}?" : ".")}");
                continue;
            }

            plan.Status = definition != null ? "defined" : "project";
            if (source != null)
            {
                plan.CopiedFrom = source.Name;
                plan.Foreground = FromProject(doc, source.ForegroundPatternId);
                plan.Background = FromProject(doc, source.BackgroundPatternId);
                plan.ForegroundColor = Hex(source.ForegroundPatternColor);
                plan.BackgroundColor = Hex(source.BackgroundPatternColor);
                plan.LineWeight = source.LineWeight;
                plan.IsMasking = source.IsMasking;
            }
            if (definition == null) continue;

            if (definition.ForegroundPattern != null)
                plan.Foreground = Named(definition.ForegroundPattern, projectPatterns, background: false, $"{definition.Source}.foregroundPattern", problems) ?? plan.Foreground;
            if (definition.BackgroundPattern != null)
                plan.Background = Named(definition.BackgroundPattern, projectPatterns, background: true, $"{definition.Source}.backgroundPattern", problems) ?? plan.Background;
            plan.ForegroundColor = definition.ForegroundColor ?? plan.ForegroundColor;
            plan.BackgroundColor = definition.BackgroundColor ?? plan.BackgroundColor;
            plan.LineWeight = definition.LineWeight ?? plan.LineWeight;
            plan.IsMasking = definition.IsMasking ?? plan.IsMasking;
        }
        return plans;
    }

    public static object DescribePlan(RegionTypePlan plan) => new
    {
        name = plan.Name,
        status = plan.Status,
        copiedFrom = plan.CopiedFrom,
        foregroundPattern = plan.Foreground?.Label,
        foregroundColor = plan.ForegroundColor,
        backgroundPattern = plan.Background?.Label,
        backgroundColor = plan.BackgroundColor,
        lineWeight = plan.LineWeight,
        isMasking = plan.IsMasking,
        usedBy = plan.UsedBy
    };

    private static RegionPatternChoice FromProject(Document doc, ElementId id)
    {
        if (id == null || id == ElementId.InvalidElementId || doc.GetElement(id) is not FillPatternElement element)
            return new RegionPatternChoice { None = true };
        var pattern = element.GetFillPattern();
        return pattern.IsSolidFill
            ? new RegionPatternChoice { Solid = true }
            : new RegionPatternChoice { Name = element.Name, Pattern = pattern };
    }

    private static RegionPatternChoice? Named(string reference, List<FillPatternElement> projectPatterns, bool background, string where, List<string> problems)
    {
        var text = reference.Trim();
        if (text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase)) return new RegionPatternChoice { None = true };
        if (text.Equals("solid", StringComparison.OrdinalIgnoreCase)) return new RegionPatternChoice { Solid = true };

        var named = projectPatterns.Where(p => string.Equals(p.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
        var match = named.FirstOrDefault(p => p.GetFillPattern().Target == FillPatternTarget.Drafting) ?? named.FirstOrDefault();
        if (match == null)
        {
            var close = Suggest(text, projectPatterns.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase));
            problems.Add($"{where} '{text}' is not a fill pattern of the project (use a pattern name, solid or none)" +
                         $"{(close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(s => $"'{s}'"))}?" : ".")}");
            return null;
        }
        var pattern = match.GetFillPattern();
        if (pattern.IsSolidFill) return new RegionPatternChoice { Solid = true };
        if (background && pattern.Target != FillPatternTarget.Drafting)
        {
            problems.Add($"{where} '{match.Name}' is a model pattern; a background pattern must be a drafting pattern.");
            return null;
        }
        return new RegionPatternChoice { Name = match.Name, Pattern = pattern };
    }

    private static List<string> Suggest(string requested, IEnumerable<string> names) =>
        names.Where(n => n.IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0
                         || requested.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

    private static string? Hex(Color? color) =>
        color == null || !color.IsValid ? null : $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    private static Color ToColor(string hex) => new(
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));

    // ── Filled region types and regions: in the family ───────────────────────

    /// <summary>
    /// Creates (or edits) the planned types in the family and draws the regions. Runs inside the caller's transaction.
    /// A region Revit refuses is reported and skipped; the others are still drawn.
    /// </summary>
    public static (List<object> Types, List<object> Regions, int Drawn) DrawRegions(Document familyDoc, View view, DetailFamilySpec spec,
        List<RegionTypePlan> plans, HashSet<ElementId> protectedIds, List<VisibilityLink> visibility, List<string> warnings,
        CancellationToken cancellationToken)
    {
        var typeResults = new List<object>();
        var regionResults = new List<object>();
        var types = new Dictionary<string, FilledRegionType>(StringComparer.OrdinalIgnoreCase);

        foreach (var plan in plans)
        {
            try
            {
                var (type, action) = EnsureType(familyDoc, plan, protectedIds, warnings);
                types[plan.Name] = type;
                typeResults.Add(new
                {
                    name = type.Name,
                    action,
                    copiedFrom = plan.CopiedFrom,
                    foregroundPattern = PatternName(familyDoc, type.ForegroundPatternId),
                    foregroundColor = Hex(type.ForegroundPatternColor),
                    backgroundPattern = PatternName(familyDoc, type.BackgroundPatternId),
                    backgroundColor = Hex(type.BackgroundPatternColor),
                    lineWeight = type.LineWeight,
                    isMasking = type.IsMasking
                });
            }
            catch (Exception ex)
            {
                typeResults.Add(new { name = plan.Name, action = "failed", reason = ex.Message });
                warnings.Add($"Filled region type '{plan.Name}' failed: {ex.Message}");
            }
        }

        FilledRegionType? defaultType = null;
        var drawn = 0;
        foreach (var region in spec.FilledRegions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FilledRegionType? type;
                if (region.Type.Length > 0)
                {
                    if (!types.TryGetValue(region.Type, out type))
                    {
                        regionResults.Add(new { source = region.Source, status = "failed", reason = $"The filled region type '{region.Type}' was not created." });
                        continue;
                    }
                }
                else
                {
                    defaultType ??= DefaultType(familyDoc)
                                    ?? throw new InvalidOperationException("The family has no filled region type; give the region a type.");
                    type = defaultType;
                    protectedIds.Add(type.Id);
                }

                var created = FilledRegion.Create(familyDoc, type.Id, view.Id, region.Loops.Select(BuildLoop).ToList());
                drawn++;
                string? styleNote = null;
                if (region.LineStyle.Length > 0)
                {
                    var styleId = FilledRegion.GetValidLineStyleIdsForFilledRegion(familyDoc)
                        .Select(id => familyDoc.GetElement(id) as GraphicsStyle)
                        .FirstOrDefault(gs => gs != null && string.Equals(gs.GraphicsStyleCategory?.Name ?? gs.Name, region.LineStyle, StringComparison.OrdinalIgnoreCase))?.Id;
                    if (styleId != null)
                    {
                        created.SetLineStyleId(styleId);
                        protectedIds.Add(styleId);
                    }
                    else
                    {
                        styleNote = $"Line style '{region.LineStyle}' is not offered to filled regions in this family; the boundary keeps the type's line.";
                        warnings.Add($"{region.Source}: {styleNote}");
                    }
                }
                if (region.VisibleParameter.Length > 0)
                    visibility.Add(new VisibilityLink { ElementId = created.Id, Parameter = region.VisibleParameter, Source = region.Source });

                regionResults.Add(new
                {
                    source = region.Source,
                    status = "drawn",
                    type = type.Name,
                    lineStyle = region.LineStyle.Length > 0 && styleNote == null ? region.LineStyle : null,
                    loops = region.Loops.Count,
                    note = styleNote
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                regionResults.Add(new { source = region.Source, status = "failed", reason = ex.Message });
                warnings.Add($"{region.Source} was not drawn: {ex.Message}");
            }
        }
        return (typeResults, regionResults, drawn);
    }

    private static (FilledRegionType Type, string Action) EnsureType(Document familyDoc, RegionTypePlan plan,
        HashSet<ElementId> protectedIds, List<string> warnings)
    {
        var existing = new FilteredElementCollector(familyDoc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().ToList();
        var type = existing.FirstOrDefault(t => string.Equals(t.Name, plan.Name, StringComparison.OrdinalIgnoreCase));
        var action = "edited";
        if (type == null)
        {
            var baseType = DefaultType(familyDoc)
                           ?? throw new InvalidOperationException("The family has no filled region type to duplicate.");
            type = (FilledRegionType)baseType.Duplicate(plan.Name);
            action = "created";
        }
        protectedIds.Add(type.Id);

        void Try(string what, Action set)
        {
            try { set(); }
            catch (Exception ex) { warnings.Add($"Filled region type '{plan.Name}': {what} was not set ({ex.Message})."); }
        }

        // Masking first: a masking type takes patterns differently, so the patterns are applied to the final state.
        if (plan.IsMasking is { } masking) Try("isMasking", () => type.IsMasking = masking);
        if (plan.Foreground != null)
            Try("foregroundPattern", () =>
            {
                var id = EnsurePattern(familyDoc, plan.Foreground, plan.Name, warnings);
                if (id != null) { type.ForegroundPatternId = id; if (id != ElementId.InvalidElementId) protectedIds.Add(id); }
            });
        if (plan.Background != null)
            Try("backgroundPattern", () =>
            {
                var id = EnsurePattern(familyDoc, plan.Background, plan.Name, warnings);
                if (id != null) { type.BackgroundPatternId = id; if (id != ElementId.InvalidElementId) protectedIds.Add(id); }
            });
        if (plan.ForegroundColor != null) Try("foregroundColor", () => type.ForegroundPatternColor = ToColor(plan.ForegroundColor));
        if (plan.BackgroundColor != null) Try("backgroundColor", () => type.BackgroundPatternColor = ToColor(plan.BackgroundColor));
        if (plan.LineWeight is { } weight)
            Try("lineWeight", () =>
            {
                if (!FilledRegionType.IsValidLineWeight(weight)) throw new InvalidOperationException($"Revit refuses line weight {weight}");
                type.LineWeight = weight;
            });
        return (type, action);
    }

    /// <summary>The family's pattern for a choice: none, its solid fill, or the named pattern (copied from the project when absent).</summary>
    private static ElementId? EnsurePattern(Document familyDoc, RegionPatternChoice choice, string typeName, List<string> warnings)
    {
        if (choice.None) return ElementId.InvalidElementId;
        var patterns = new FilteredElementCollector(familyDoc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().ToList();
        if (choice.Solid)
        {
            var solid = patterns.FirstOrDefault(p => p.GetFillPattern().IsSolidFill);
            if (solid == null) warnings.Add($"Filled region type '{typeName}': the family has no solid fill pattern; the pattern was left as it was.");
            return solid?.Id;
        }

        var target = choice.Pattern?.Target ?? FillPatternTarget.Drafting;
        var existing = patterns.FirstOrDefault(p => string.Equals(p.Name, choice.Name, StringComparison.OrdinalIgnoreCase) && p.GetFillPattern().Target == target);
        if (existing != null) return existing.Id;
        if (choice.Pattern == null)
        {
            warnings.Add($"Filled region type '{typeName}': fill pattern '{choice.Name}' could not be read from the project; the pattern was left as it was.");
            return null;
        }
        return FillPatternElement.Create(familyDoc, choice.Pattern).Id;
    }

    private static FilledRegionType? DefaultType(Document familyDoc)
    {
        try
        {
            var id = familyDoc.GetDefaultElementTypeId(ElementTypeGroup.FilledRegionType);
            if (id != null && id != ElementId.InvalidElementId && familyDoc.GetElement(id) is FilledRegionType type) return type;
        }
        catch
        {
            // Not every document reports a default: fall back to the first type.
        }
        return new FilteredElementCollector(familyDoc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static string PatternName(Document doc, ElementId id) =>
        id == null || id == ElementId.InvalidElementId ? "none"
        : doc.GetElement(id) is FillPatternElement element ? (element.GetFillPattern().IsSolidFill ? "solid" : element.Name)
        : "none";

    private static XYZ Xyz(DetailPoint p) => new(p.X / MmPerFoot, p.Y / MmPerFoot, 0);

    private static CurveLoop BuildLoop(DetailRegionLoop loop)
    {
        var result = new CurveLoop();
        if (loop.IsCircle)
        {
            var center = Xyz(loop.Center);
            var radius = loop.Radius / MmPerFoot;
            result.Append(Arc.Create(center, radius, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
            result.Append(Arc.Create(center, radius, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
            return result;
        }
        for (var i = 0; i < loop.Points.Count; i++)
            result.Append(Line.CreateBound(Xyz(loop.Points[i]), Xyz(loop.Points[(i + 1) % loop.Points.Count])));
        return result;
    }

    // ── Parameter labels ─────────────────────────────────────────────────────

    /// <summary>
    /// Finds the label families the parameter labels name: a .rfa file, or a Generic Annotation family loaded in the
    /// project. Checks what can be checked without opening them; the label's own parameter is matched when it is placed.
    /// </summary>
    public static Dictionary<string, LabelFamilySource> ResolveLabelFamilies(Document doc, DetailFamilySpec spec, List<string> problems)
    {
        var result = new Dictionary<string, LabelFamilySource>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Family>? loaded = null;
        foreach (var label in spec.ParameterLabels.Where(l => l.LabelFamily.Length > 0))
        {
            if (!result.TryGetValue(label.LabelFamily, out var source))
            {
                source = new LabelFamilySource { Reference = label.LabelFamily, Name = DetailFamilySpecParser.LabelFamilyName(label.LabelFamily) };
                result[label.LabelFamily] = source;
                if (DetailFamilySpecParser.IsLabelFamilyPath(label.LabelFamily))
                {
                    source.Kind = "file";
                    var policyError = new FilePathPolicy().ValidateRead(label.LabelFamily);
                    if (policyError != null) problems.Add($"{label.Source}.labelFamily: {policyError}");
                    else if (!File.Exists(label.LabelFamily)) problems.Add($"{label.Source}.labelFamily '{label.LabelFamily}' does not exist.");
                    else source.Path = label.LabelFamily;
                }
                else
                {
                    source.Kind = "project";
                    loaded ??= FamilyInventory.LoadedByName(doc);
                    if (!loaded.TryGetValue(label.LabelFamily, out var family))
                    {
                        var close = Suggest(label.LabelFamily, loaded.Where(f => IsGenericAnnotation(f.Value)).Select(f => f.Key));
                        problems.Add($"{label.Source}.labelFamily '{label.LabelFamily}' is not a family loaded in '{doc.Title}' (or pass a .rfa path)" +
                                     $"{(close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(s => $"'{s}'"))}?" : ".")}");
                    }
                    else if (!IsGenericAnnotation(family))
                        problems.Add($"{label.Source}.labelFamily '{family.Name}' is a {FamilyInventory.CategoryOf(family)} family; a label family must be a Generic Annotation.");
                    else if (!family.IsEditable)
                        problems.Add($"{label.Source}.labelFamily '{family.Name}' cannot be opened for editing, so it cannot be nested.");
                    else
                    {
                        source.Family = family;
                        source.Types = FamilyInventory.Symbols(doc, family).Select(FamilyTypeSupport.SafeName).ToList();
                    }
                }
            }

            if (label.LabelType.Length > 0 && source.Family != null &&
                !source.Types.Contains(label.LabelType, StringComparer.OrdinalIgnoreCase))
                problems.Add($"{label.Source}.labelType '{label.LabelType}' is not a type of '{source.Name}' (types: {string.Join(", ", source.Types.Take(15))}).");
        }
        return result;
    }

    private static bool IsGenericAnnotation(Family family)
    {
        try { return family.FamilyCategory != null && family.FamilyCategory.Id.Value == (long)BuiltInCategory.OST_GenericAnnotation; }
        catch { return false; }
    }

    /// <summary>
    /// The .rfa to nest for a label family: the file itself, or a copy of the project's loaded family saved to
    /// <paramref name="folder"/>. Must run with no transaction open in the project.
    /// </summary>
    public static string LabelFamilyFile(Document projectDoc, LabelFamilySource source, string folder)
    {
        if (source.Path != null) return source.Path;
        Document? opened = null;
        try
        {
            opened = projectDoc.EditFamily(source.Family!)
                     ?? throw new InvalidOperationException($"Revit could not open the family '{source.Name}'.");
            var path = Path.Combine(folder, source.Name + ".rfa");
            opened.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            return path;
        }
        finally
        {
            try { opened?.Close(false); } catch { /* already closed */ }
        }
    }

    /// <summary>
    /// Nests each label family, places one instance per parameter label and links the instance's label parameter to
    /// the detail family's parameter, so the label shows that parameter. Runs inside the caller's transaction, after
    /// the parameters were added.
    /// </summary>
    public static List<object> PlaceParameterLabels(Document familyDoc, View view, DetailFamilySpec spec,
        IReadOnlyDictionary<string, string> familyFiles, HashSet<ElementId> protectedIds, List<VisibilityLink> visibility, List<string> warnings)
    {
        var results = new List<object>();
        var manager = familyDoc.FamilyManager;
        var families = new Dictionary<string, Family?>(StringComparer.OrdinalIgnoreCase);
        // A type parameter of the label family is shared by every instance of that type: remember what it was linked to.
        var typeLinks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var label in spec.ParameterLabels)
        {
            object Failed(string reason)
            {
                warnings.Add($"{label.Source} (label for '{label.Parameter}') failed: {reason}");
                return new { source = label.Source, parameter = label.Parameter, family = DetailFamilySpecParser.LabelFamilyName(label.LabelFamily), status = "failed", reason };
            }

            try
            {
                if (!families.TryGetValue(label.LabelFamily, out var family))
                {
                    family = null;
                    if (familyFiles.TryGetValue(label.LabelFamily, out var path) &&
                        familyDoc.LoadFamily(path, new FamilyLoadOptions(FamilyIfExists.Overwrite), out var loadedFamily))
                        family = loadedFamily;
                    families[label.LabelFamily] = family;
                    if (family != null) protectedIds.Add(family.Id);
                }
                if (family == null) { results.Add(Failed("the label family could not be loaded into the detail family.")); continue; }

                var symbols = family.GetFamilySymbolIds().Select(id => familyDoc.GetElement(id)).OfType<FamilySymbol>().ToList();
                var symbol = label.LabelType.Length > 0
                    ? symbols.FirstOrDefault(s => string.Equals(s.Name, label.LabelType, StringComparison.OrdinalIgnoreCase))
                    : symbols.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (symbol == null)
                {
                    results.Add(Failed(label.LabelType.Length > 0
                        ? $"'{family.Name}' has no type '{label.LabelType}' (types: {string.Join(", ", symbols.Select(s => s.Name).Take(15))})."
                        : $"'{family.Name}' has no type."));
                    continue;
                }
                if (!symbol.IsActive) symbol.Activate();
                protectedIds.Add(symbol.Id);

                var host = manager.get_Parameter(label.Parameter);
                if (host == null) { results.Add(Failed($"the family has no parameter '{label.Parameter}'.")); continue; }

                var point = new XYZ(label.Position.X / MmPerFoot, label.Position.Y / MmPerFoot, 0);
                var instance = familyDoc.FamilyCreate.NewFamilyInstance(point, symbol, view);
                if (Math.Abs(label.RotationDeg) > 1e-9)
                    ElementTransformUtils.RotateElement(familyDoc, instance.Id, Line.CreateBound(point, point + XYZ.BasisZ),
                        label.RotationDeg * Math.PI / 180);
                familyDoc.Regenerate();

                var (target, onType, reason) = FindLabelParameter(manager, instance, symbol, label.LabelParameter, host);
                if (target == null)
                {
                    familyDoc.Delete(instance.Id);
                    results.Add(Failed(reason!));
                    continue;
                }
                if (onType)
                {
                    var key = symbol.Id.Value + "|" + target.Definition.Name;
                    if (typeLinks.TryGetValue(key, out var linked) && !string.Equals(linked, host.Definition.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        familyDoc.Delete(instance.Id);
                        results.Add(Failed($"'{target.Definition.Name}' is a type parameter of '{family.Name}', and type '{symbol.Name}' already shows '{linked}'. " +
                                           "Use another labelType, or make it an instance parameter in the label family."));
                        continue;
                    }
                    typeLinks[key] = host.Definition.Name;
                }

                manager.AssociateElementParameterToFamilyParameter(target, host);
                if (label.VisibleParameter.Length > 0)
                    visibility.Add(new VisibilityLink { ElementId = instance.Id, Parameter = label.VisibleParameter, Source = label.Source });

                results.Add(new
                {
                    source = label.Source,
                    parameter = host.Definition.Name,
                    family = family.Name,
                    type = symbol.Name,
                    labelParameter = target.Definition.Name,
                    labelParameterScope = onType ? "type" : "instance",
                    status = "placed"
                });
            }
            catch (Exception ex)
            {
                results.Add(Failed(ex.Message));
            }
        }
        return results;
    }

    /// <summary>
    /// The label family's parameter to link: the named one (on the instance, else on its type), or its only editable,
    /// user-defined parameter of the host parameter's data type.
    /// </summary>
    private static (Parameter? Target, bool OnType, string? Reason) FindLabelParameter(FamilyManager manager, FamilyInstance instance,
        FamilySymbol symbol, string name, FamilyParameter host)
    {
        var hostType = host.Definition.GetDataType();
        bool Matches(Parameter p) => Equals(p.Definition.GetDataType(), hostType);
        bool Linkable(Parameter p)
        {
            try { return !p.IsReadOnly && manager.CanElementParameterBeAssociated(p); }
            catch { return false; }
        }

        if (name.Length > 0)
        {
            var onInstance = instance.LookupParameter(name);
            var found = onInstance ?? symbol.LookupParameter(name);
            if (found == null)
                return (null, false, $"'{symbol.FamilyName}' has no parameter '{name}' (parameters: {string.Join(", ", UserParameters(instance, symbol).Take(15))}).");
            if (!Matches(found))
                return (null, false, $"'{name}' of '{symbol.FamilyName}' and '{host.Definition.Name}' have different data types; a label parameter is linked to a parameter of the same type.");
            if (!Linkable(found))
                return (null, false, $"Revit does not let '{name}' of '{symbol.FamilyName}' be linked to a family parameter.");
            return (found, onInstance == null, null);
        }

        var candidates = instance.Parameters.Cast<Parameter>().Where(p => IsUserParameter(p) && Matches(p) && Linkable(p)).ToList();
        var onTypeCandidates = false;
        if (candidates.Count == 0)
        {
            candidates = symbol.Parameters.Cast<Parameter>().Where(p => IsUserParameter(p) && Matches(p) && Linkable(p)).ToList();
            onTypeCandidates = true;
        }
        if (candidates.Count == 1) return (candidates[0], onTypeCandidates, null);
        if (candidates.Count == 0)
            return (null, false, $"'{symbol.FamilyName}' has no parameter of the same data type as '{host.Definition.Name}' to link " +
                                 $"(parameters: {string.Join(", ", UserParameters(instance, symbol).Take(15))}). Its label must show a parameter of that type.");
        return (null, false, $"'{symbol.FamilyName}' has several parameters that fit ({string.Join(", ", candidates.Select(p => p.Definition.Name))}); pass labelParameter.");
    }

    private static bool IsUserParameter(Parameter p) =>
        p.Definition is InternalDefinition definition && definition.BuiltInParameter == BuiltInParameter.INVALID;

    private static IEnumerable<string> UserParameters(FamilyInstance instance, FamilySymbol symbol) =>
        instance.Parameters.Cast<Parameter>().Concat(symbol.Parameters.Cast<Parameter>())
            .Where(IsUserParameter).Select(p => p.Definition.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    // ── Visibility parameters ────────────────────────────────────────────────

    /// <summary>
    /// Links each element's Visible parameter to its yes/no family parameter. Runs inside the caller's transaction,
    /// after the parameters were added. A link Revit refuses is a warning; the element stays always visible.
    /// </summary>
    public static List<object> LinkVisibility(Document familyDoc, List<VisibilityLink> links, List<string> warnings)
    {
        var manager = familyDoc.FamilyManager;
        return links.GroupBy(l => l.Parameter, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var linked = 0;
            var failures = new List<string>();
            var host = manager.get_Parameter(group.Key);
            foreach (var link in group)
            {
                try
                {
                    if (host == null) { failures.Add($"{link.Source}: the family has no parameter '{group.Key}'"); continue; }
                    var element = familyDoc.GetElement(link.ElementId);
                    var visible = element?.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
                    if (visible == null) { failures.Add($"{link.Source}: the element has no Visible parameter"); continue; }
                    if (!manager.CanElementParameterBeAssociated(visible)) { failures.Add($"{link.Source}: Revit does not let its Visible parameter be linked"); continue; }
                    manager.AssociateElementParameterToFamilyParameter(visible, host);
                    linked++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{link.Source}: {ex.Message}");
                }
            }
            if (failures.Count > 0)
                warnings.Add($"Visibility parameter '{group.Key}' was not linked for {failures.Count} element(s): {string.Join("; ", failures.Take(5))}");
            return (object)new { parameter = host?.Definition.Name ?? group.Key, linked, failed = failures.Count };
        }).ToList();
    }
}
