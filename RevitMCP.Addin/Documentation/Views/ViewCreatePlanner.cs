using System.Globalization;

namespace RevitMCP.Addin.Documentation.Views;

/// <summary>A level as the planner sees it: id, name and elevation (Level.ProjectElevation) in mm.</summary>
public sealed class LevelInfo
{
    public LevelInfo(long id, string name, double elevationMm)
    {
        Id = id;
        Name = name;
        ElevationMm = elevationMm;
    }

    public long Id { get; }
    public string Name { get; }
    public double ElevationMm { get; }
}

/// <summary>One view the request asks for, after fan-out, naming and the Revit-free checks.</summary>
public sealed class PlannedView
{
    public int SpecIndex { get; init; }
    public ViewCreateSpec Spec { get; init; } = null!;
    public string Kind { get; init; } = string.Empty;

    /// <summary>1-based position within the spec's fan-out ({n}); 1 when the spec does not fan out.</summary>
    public int FanOutIndex { get; init; } = 1;

    public LevelInfo? Level { get; init; }

    /// <summary>The name asked for, placeholders filled in; null = Revit's default name.</summary>
    public string? RequestedName { get; set; }

    /// <summary>The name the view will get (unique); null = Revit's default name.</summary>
    public string? Name { get; set; }

    /// <summary>Sheet number, name or id the view goes on, placeholders filled in.</summary>
    public string? Sheet { get; set; }

    public SectionBoxMath.Result? Section { get; set; }

    public string Status { get; set; } = ViewCreateStatus.WillCreate;
    public string? Code { get; set; }
    public string? Reason { get; set; }
    public List<string> Notes { get; } = new();

    public bool IsReady => Status == ViewCreateStatus.WillCreate;

    /// <summary>A reference callout points at an existing view and creates none of its own.</summary>
    public bool IsReferenceCallout => Kind == ViewKinds.Callout && !string.IsNullOrWhiteSpace(Spec.ReferenceView);

    public void Invalidate(string code, string reason)
    {
        Status = ViewCreateStatus.Invalid;
        Code = code;
        Reason = reason;
    }
}

/// <summary>
/// The Revit-free planning of revit_create_views (#104): per-kind validation, fan-out over levels with {level} and
/// {n} in names and sheets, unique names under <see cref="NameConflictMode"/>, and the section box. References that
/// need the model (types, templates, scope boxes, sheets, parent views, legends) are checked afterwards by the
/// add-in's resolver, which may still mark a planned view invalid.
/// </summary>
public static class ViewCreatePlanner
{
    /// <summary>Characters Revit refuses in view names.</summary>
    public static readonly char[] ForbiddenNameChars = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

    public static List<PlannedView> Plan(
        ViewCreateRequest request, IReadOnlyList<LevelInfo> levels, IEnumerable<string> existingNames, out string? error)
    {
        error = null;
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var planned = new List<PlannedView>();
        var sortedLevels = levels.OrderBy(l => l.ElevationMm).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var elevations = sortedLevels.Select(l => l.ElevationMm).ToList();

        foreach (var spec in request.Views)
            planned.AddRange(Expand(spec, sortedLevels, elevations));

        if (planned.Count > ViewCreateSpecParser.MaxViews)
        {
            error = $"The specs expand to {planned.Count} views; the limit is {ViewCreateSpecParser.MaxViews} per call. " +
                    "Split the request (for example fewer levels per call).";
            return planned;
        }

        foreach (var view in planned.Where(v => v.IsReady))
            AssignName(view, taken, request.IfNameExists);
        return planned;
    }

    // ── Fan-out and per-kind checks ───────────────────────────────────────────

    private static IEnumerable<PlannedView> Expand(ViewCreateSpec spec, List<LevelInfo> levels, List<double> elevations)
    {
        var kind = spec.Kind ?? spec.RawKind;
        if (spec.Errors.Count > 0 || spec.Kind == null)
        {
            var view = new PlannedView { SpecIndex = spec.Index, Spec = spec, Kind = kind };
            view.Invalidate(ViewCreateCodes.InvalidSpec, string.Join(" ", spec.Errors));
            yield return view;
            yield break;
        }

        if (ViewKinds.IsPlan(spec.Kind))
        {
            foreach (var view in ExpandPlan(spec, levels))
                yield return view;
            yield break;
        }

        var single = new PlannedView { SpecIndex = spec.Index, Spec = spec, Kind = spec.Kind };
        if (spec.Level != null || spec.Levels.Count > 0 || spec.AllLevels)
            single.Notes.Add($"level/levels apply to plans only; ignored for a {ViewKinds.Label(spec.Kind, 1)}.");
        if (spec.CropTo != null)
            single.Notes.Add("cropTo applies to plans only; set the extent of other views with their own fields or revit_set_view_extent.");
        if (spec.ViewRange != null)
            single.Notes.Add("viewRange applies to plans only; ignored.");

        var reason = CheckKind(spec, single, elevations);
        if (reason != null)
            single.Invalidate(reason.Value.Code, reason.Value.Message);
        else
        {
            single.RequestedName = Fill(spec.Name, null, 1, out var placeholderError);
            single.Sheet = SheetFor(spec, 0, null, 1, out var sheetError);
            if (placeholderError != null) single.Invalidate(ViewCreateCodes.InvalidSpec, placeholderError);
            else if (sheetError != null) single.Invalidate(ViewCreateCodes.InvalidSpec, sheetError);
        }
        yield return single;
    }

    private static IEnumerable<PlannedView> ExpandPlan(ViewCreateSpec spec, List<LevelInfo> levels)
    {
        if (spec.Level != null && (spec.Levels.Count > 0 || spec.AllLevels))
        {
            var view = new PlannedView { SpecIndex = spec.Index, Spec = spec, Kind = spec.Kind! };
            view.Invalidate(ViewCreateCodes.InvalidSpec, "Give either 'level' or 'levels', not both.");
            yield return view;
            yield break;
        }

        var wanted = new List<(string Ref, LevelInfo? Level)>();
        if (spec.AllLevels)
            wanted.AddRange(levels.Select(l => (l.Name, (LevelInfo?)l)));
        else if (spec.Levels.Count > 0)
            wanted.AddRange(spec.Levels.Distinct(StringComparer.OrdinalIgnoreCase).Select(r => (r, FindLevel(levels, r))));
        else if (spec.Level != null)
            wanted.Add((spec.Level, FindLevel(levels, spec.Level)));

        if (wanted.Count == 0)
        {
            var view = new PlannedView { SpecIndex = spec.Index, Spec = spec, Kind = spec.Kind! };
            view.Invalidate(levels.Count == 0 ? ViewCreateCodes.LevelNotFound : ViewCreateCodes.InvalidSpec,
                levels.Count == 0
                    ? "The model has no levels, so no plan can be created."
                    : $"A {ViewKinds.Label(spec.Kind!, 1)} needs 'level' (name or id) or 'levels' (names, ids or \"all\"). " +
                      $"Levels: {DescribeLevels(levels)}.");
            yield return view;
            yield break;
        }

        var fanOut = wanted.Count > 1;
        var appendLevel = fanOut && spec.Name != null &&
                          spec.Name.IndexOf("{level}", StringComparison.OrdinalIgnoreCase) < 0 &&
                          spec.Name.IndexOf("{n}", StringComparison.OrdinalIgnoreCase) < 0;

        for (var i = 0; i < wanted.Count; i++)
        {
            var (reference, level) = wanted[i];
            var view = new PlannedView { SpecIndex = spec.Index, Spec = spec, Kind = spec.Kind!, FanOutIndex = i + 1, Level = level };
            if (level == null)
            {
                view.Invalidate(ViewCreateCodes.LevelNotFound, $"No level '{reference}'. Levels: {DescribeLevels(levels)}.");
                yield return view;
                continue;
            }

            if (spec.Kind == ViewKinds.AreaPlan && !string.IsNullOrWhiteSpace(spec.ViewFamilyType))
                view.Notes.Add("An area plan takes its type from the area scheme; viewFamilyType is ignored.");
            if (spec.CropTo != null && !string.IsNullOrWhiteSpace(spec.ScopeBox))
                view.Notes.Add("scopeBox controls the crop region, so cropTo is ignored.");

            var name = appendLevel ? spec.Name + " {level}" : spec.Name;
            if (appendLevel && i == 0)
                view.Notes.Add("The name has no {level} or {n} while the spec fans out over levels; the level name is appended.");
            view.RequestedName = Fill(name, level, i + 1, out var placeholderError);
            view.Sheet = SheetFor(spec, i, level, i + 1, out var sheetError);
            if (placeholderError != null) view.Invalidate(ViewCreateCodes.InvalidSpec, placeholderError);
            else if (sheetError != null) view.Invalidate(ViewCreateCodes.InvalidSpec, sheetError);
            yield return view;
        }
    }

    private static (string Code, string Message)? CheckKind(ViewCreateSpec spec, PlannedView view, List<double> elevations)
    {
        switch (spec.Kind)
        {
            case ViewKinds.Section:
            {
                if (spec.From == null || spec.To == null)
                    return (ViewCreateCodes.InvalidSpec, "A section needs 'from' and 'to' ({x, y, z} in mm): the section line in plan.");
                var (bottom, top) = SectionBoxMath.VerticalRange(spec.From.Value, spec.To.Value, elevations, spec.BottomMm, spec.HeightMm);
                var box = SectionBoxMath.Compute(spec.From.Value, spec.To.Value, bottom, top, spec.DepthMm, spec.Flip, out var error);
                if (box == null) return (ViewCreateCodes.InvalidSpec, error!);
                view.Section = box;
                if (spec.Region != null)
                    view.Notes.Add("box/aroundElements/aroundRoom are ignored for a section: its extent is from, to, depthMm and the height.");
                return null;
            }
            case ViewKinds.Callout:
            {
                if (string.IsNullOrWhiteSpace(spec.ParentView))
                    return (ViewCreateCodes.InvalidSpec, "A callout needs 'parentView' (name or id of the plan or section it is drawn in).");
                var hasCorners = spec.From != null || spec.To != null;
                var hasRegion = spec.Region != null && !spec.Region.IsEmpty;
                if (hasCorners && hasRegion)
                    return (ViewCreateCodes.InvalidSpec, "Give the callout either from/to corners or aroundElements/aroundRoom/box, not both.");
                if (hasCorners && (spec.From == null || spec.To == null))
                    return (ViewCreateCodes.InvalidSpec, "A callout by corners needs both 'from' and 'to'.");
                if (!hasCorners && !hasRegion)
                    return (ViewCreateCodes.InvalidSpec, "A callout needs 'from' and 'to' corners (mm), or aroundElements/aroundRoom/box with paddingMm.");
                if (view.IsReferenceCallout)
                {
                    view.Notes.Add("A reference callout points at the existing referenceView and creates no view: name, template, " +
                                   "scale and sheet placement do not apply.");
                    if (spec.PlaceOnSheet != null)
                        return (ViewCreateCodes.InvalidSpec, "A reference callout creates no view, so it cannot be placed on a sheet.");
                }
                return null;
            }
            case ViewKinds.Legend:
                if (string.IsNullOrWhiteSpace(spec.SourceLegend))
                    return (ViewCreateCodes.LegendSourceRequired,
                        "Revit's API cannot create a legend view. Name an existing legend in 'sourceLegend' (name or id): it is " +
                        "duplicated with its detailing and renamed. Without any legend in the model, create one by hand first.");
                if (!string.IsNullOrWhiteSpace(spec.ViewFamilyType))
                    view.Notes.Add("A legend copies the type of its source legend; viewFamilyType is ignored.");
                return null;
            case ViewKinds.ThreeD:
                if (spec.Region != null && spec.Region.IsEmpty)
                    view.Notes.Add("paddingMm without box/aroundElements/aroundRoom: the view gets no section box.");
                if (spec.Orientation != null && spec.Orientation.EyeMm != null && spec.Isometric)
                    view.Notes.Add("An isometric view has no eye point: only the direction of 'eye' matters.");
                return null;
            default:
                return null;
        }
    }

    // ── Names ─────────────────────────────────────────────────────────────────

    private static void AssignName(PlannedView view, HashSet<string> taken, NameConflictMode mode)
    {
        if (view.IsReferenceCallout)
        {
            view.RequestedName = null;
            view.Name = null;
            return;
        }
        var requested = view.RequestedName;
        if (requested == null)
        {
            view.Name = null;
            return;
        }

        if (requested.IndexOfAny(ForbiddenNameChars) >= 0)
        {
            view.Invalidate(ViewCreateCodes.InvalidSpec,
                $"The name '{requested}' contains characters Revit does not allow in view names ({new string(ForbiddenNameChars)}). " +
                "Placeholders are {level} and {n}.");
            return;
        }

        if (!taken.Contains(requested))
        {
            view.Name = requested;
            taken.Add(requested);
            return;
        }

        switch (mode)
        {
            case NameConflictMode.Skip:
                view.Status = ViewCreateStatus.Skipped;
                view.Code = ViewCreateCodes.ViewNameExists;
                view.Reason = $"A view named '{requested}' already exists (ifNameExists=skip).";
                break;
            case NameConflictMode.Fail:
                view.Invalidate(ViewCreateCodes.ViewNameExists,
                    $"A view named '{requested}' already exists (ifNameExists=fail). Rename it, or pass ifNameExists=suffix or skip.");
                break;
            default:
                var unique = UniqueName(requested, taken);
                view.Name = unique;
                taken.Add(unique);
                view.Notes.Add($"A view named '{requested}' already exists; this one is named '{unique}'.");
                break;
        }
    }

    /// <summary>The candidate, or "candidate 1", "candidate 2", … — the rule of ViewManagerToolSupport.ResolveUniqueName.</summary>
    public static string UniqueName(string candidate, HashSet<string> taken)
    {
        if (!taken.Contains(candidate)) return candidate;
        for (var index = 1; ; index++)
        {
            var resolved = $"{candidate} {index}";
            if (!taken.Contains(resolved)) return resolved;
        }
    }

    /// <summary>Fills {level} and {n}; an unknown {placeholder} is an error so it never reaches Revit.</summary>
    public static string? Fill(string? template, LevelInfo? level, int n, out string? error)
    {
        error = null;
        if (template == null) return null;
        var text = template;
        if (text.IndexOf("{level}", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (level == null)
            {
                error = $"'{template}' uses {{level}}, but only plans have a level.";
                return null;
            }
            text = ReplaceIgnoreCase(text, "{level}", level.Name);
        }
        text = ReplaceIgnoreCase(text, "{n}", n.ToString(CultureInfo.InvariantCulture)).Trim();
        if (text.Length == 0)
        {
            error = $"The name '{template}' is empty after filling in the placeholders.";
            return null;
        }
        return text;
    }

    private static string? SheetFor(ViewCreateSpec spec, int position, LevelInfo? level, int n, out string? error)
    {
        error = null;
        var placement = spec.PlaceOnSheet;
        if (placement == null) return null;
        string? template;
        if (placement.Sheets.Count > 0)
        {
            if (position >= placement.Sheets.Count)
            {
                error = $"placeOnSheet.sheets has {placement.Sheets.Count} entr{(placement.Sheets.Count == 1 ? "y" : "ies")}, " +
                        $"fewer than the views this spec makes; give one sheet per level.";
                return null;
            }
            template = placement.Sheets[position];
        }
        else template = placement.Sheet;

        if (string.IsNullOrWhiteSpace(template))
        {
            error = "placeOnSheet names no sheet for this view.";
            return null;
        }
        return Fill(template, level, n, out error);
    }

    private static string ReplaceIgnoreCase(string text, string token, string value)
    {
        var index = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            text = text.Substring(0, index) + value + text.Substring(index + token.Length);
            index = text.IndexOf(token, index + value.Length, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    // ── Levels ────────────────────────────────────────────────────────────────

    /// <summary>A level by exact name (case-insensitive), then by element id.</summary>
    public static LevelInfo? FindLevel(IReadOnlyList<LevelInfo> levels, string reference)
    {
        var trimmed = reference.Trim();
        var byName = levels.FirstOrDefault(l => string.Equals(l.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName;
        return long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? levels.FirstOrDefault(l => l.Id == id)
            : null;
    }

    private static string DescribeLevels(IReadOnlyList<LevelInfo> levels) =>
        levels.Count == 0 ? "none" : string.Join(", ", levels.Take(30).Select(l => l.Name)) + (levels.Count > 30 ? ", …" : string.Empty);

    // ── Summary ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The approval summary from the plan (the model is known): "Create 5 view(s): 3 floor plans (levels 1–3, template
    /// EN_Plaan), 1 section and 1 3D view and place 3 on sheets." Views that are invalid or skipped are counted apart.
    /// </summary>
    public static string SummarizePlanned(IReadOnlyList<PlannedView> planned, ViewCreateRequest request)
    {
        var ready = planned.Where(p => p.IsReady).ToList();
        var parts = new List<string>();
        foreach (var group in ready.GroupBy(p => p.Kind))
        {
            var items = group.ToList();
            var details = new List<string>();
            var levelNames = items.Where(p => p.Level != null).Select(p => p.Level!.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (levelNames.Count == 1) details.Add($"level {levelNames[0]}");
            else if (levelNames.Count is > 1 and <= 3) details.Add($"levels {string.Join(", ", levelNames)}");
            else if (levelNames.Count > 3) details.Add($"levels {levelNames.First()}–{levelNames.Last()}");
            var templates = items.Select(p => p.Spec.Template).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
            if (templates.Count > 0) details.Add($"template {string.Join(", ", templates)}");
            parts.Add($"{items.Count} {ViewKinds.Label(group.Key, items.Count)}{(details.Count > 0 ? $" ({string.Join(", ", details)})" : string.Empty)}");
        }

        var onSheets = ready.Count(p => p.Sheet != null);
        var invalid = planned.Count(p => p.Status == ViewCreateStatus.Invalid);
        var skipped = planned.Count(p => p.Status == ViewCreateStatus.Skipped);
        var body = parts.Count == 0 ? "nothing" : parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts.Last();
        var text = $"Create {ready.Count} view(s): {body}{(onSheets > 0 ? $" and place {onSheets} on sheets" : string.Empty)}.";
        if (invalid > 0) text += $" {invalid} spec(s) are invalid{(request.Atomic ? " (atomic=true: nothing will be created)" : " and are left out")}.";
        if (skipped > 0) text += $" {skipped} skipped (name exists).";
        return text + " One transaction, one Undo.";
    }

    /// <summary>
    /// "Create N view(s): 3 floor plans (levels 1–3, template EN_Plaan), 1 section, 1 3D view and place 3 on sheets."
    /// From the request alone (no model): fan-out counts are level counts, "all levels" when levels="all".
    /// </summary>
    public static string Summarize(ViewCreateRequest request)
    {
        var parts = new List<string>();
        var total = 0;
        var totalKnown = true;
        var onSheets = 0;
        foreach (var group in request.Views.GroupBy(v => v.Kind ?? v.RawKind))
        {
            var count = 0;
            var allLevels = false;
            var levelNames = new List<string>();
            foreach (var spec in group)
            {
                if (spec.AllLevels) allLevels = true;
                var n = spec.Levels.Count > 0 ? spec.Levels.Count : 1;
                count += spec.AllLevels ? 0 : n;
                levelNames.AddRange(spec.Levels);
                if (spec.Level != null) levelNames.Add(spec.Level);
                if (spec.PlaceOnSheet != null) onSheets += spec.AllLevels ? 0 : n;
            }
            if (allLevels) totalKnown = false;
            total += count;

            var details = new List<string>();
            if (!allLevels && levelNames.Count > 0)
                details.Add(levelNames.Count == 1 ? $"level {levelNames[0]}" :
                    levelNames.Count <= 3 ? $"levels {string.Join(", ", levelNames)}" : $"levels {levelNames.First()}–{levelNames.Last()}");
            var templates = group.Select(v => v.Template).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
            if (templates.Count > 0) details.Add($"template {string.Join(", ", templates)}");
            var detailText = details.Count > 0 ? $" ({string.Join(", ", details)})" : string.Empty;

            var kind = ViewKinds.Normalize(group.Key);
            if (allLevels)
                parts.Add($"{(count > 0 ? $"{count} + " : string.Empty)}{(kind != null ? ViewKinds.Label(kind, 2) : group.Key)} for all levels{detailText}");
            else
                parts.Add($"{count} {(kind != null ? ViewKinds.Label(kind, count) : group.Key)}{detailText}");
        }

        var head = totalKnown ? $"Create {total} view(s): " : "Create views: ";
        var body = parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts.Last();
        var sheets = request.Views.Any(v => v.PlaceOnSheet != null)
            ? totalKnown ? $" and place {onSheets} on sheets" : " and place them on sheets"
            : string.Empty;
        return head + body + sheets + "." +
               (request.Atomic ? " atomic=true: any failure undoes all." : string.Empty) +
               " One transaction, one Undo.";
    }
}

/// <summary>
/// Which view family types may make a callout in which parent view (Revit API remarks of ViewSection.CreateCallout).
/// Families are ViewFamily enum names (FloorPlan, CeilingPlan, StructuralPlan, Section, Elevation, Detail, …).
/// </summary>
public static class CalloutRules
{
    private static readonly string[] SameFamilyTypes = { "FloorPlan", "CeilingPlan", "StructuralPlan", "Section", "Elevation" };
    private static readonly string[] ParentFamilies = { "FloorPlan", "CeilingPlan", "StructuralPlan", "Section", "Elevation", "Detail" };

    /// <summary>Parents a (non-reference) callout can be drawn in.</summary>
    public static bool CanHostCallout(string parentFamily) => ParentFamilies.Contains(parentFamily, StringComparer.OrdinalIgnoreCase);

    /// <summary>Detail types work in every parent except ceiling plans (and drafting views); plan, section and elevation types only in a parent of the same family.</summary>
    public static bool IsCompatible(string parentFamily, string typeFamily)
    {
        if (!CanHostCallout(parentFamily)) return false;
        if (string.Equals(typeFamily, "Detail", StringComparison.OrdinalIgnoreCase))
            return !string.Equals(parentFamily, "CeilingPlan", StringComparison.OrdinalIgnoreCase);
        return SameFamilyTypes.Contains(typeFamily, StringComparer.OrdinalIgnoreCase) &&
               string.Equals(parentFamily, typeFamily, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The family used when the spec names no type: Detail, or CeilingPlan in a ceiling plan.</summary>
    public static string DefaultTypeFamily(string parentFamily) =>
        string.Equals(parentFamily, "CeilingPlan", StringComparison.OrdinalIgnoreCase) ? "CeilingPlan" : "Detail";
}
