namespace RevitMCP.Addin.Datums;

/// <summary>A level, grid or reference plane already in the model, as the checks need it.</summary>
public sealed class ExistingDatum
{
    public long Id;
    public string Kind = string.Empty;
    public string Name = string.Empty;
    public double? ElevationMm;
    /// <summary>Grids and reference planes: the two ends (mm).</summary>
    public MmPoint? Start;
    public MmPoint? End;
}

/// <summary>A view a reference plane would be drawn in: whether it can hold one, and where it looks.</summary>
public sealed class DatumViewInfo
{
    public long Id;
    /// <summary>Null when the view can hold a reference plane; otherwise why not.</summary>
    public string? Problem;
    public double DirX, DirY, DirZ;
}

/// <summary>What depends on a datum: counts by kind and a few examples.</summary>
public sealed class DatumDependents
{
    /// <summary>Elements hosted on or constrained to the datum (for a level: elements with that level).</summary>
    public int Hosted;
    /// <summary>Views associated with the datum (plan views generated from a level).</summary>
    public int Views;
    /// <summary>Other dependent elements (dimensions, tags, hosted sketches, ...).</summary>
    public int Other;
    public int Total => Hosted + Views + Other;
    public List<string> Examples = new();
}

/// <summary>A datum an edit points at, with its live status.</summary>
public sealed class DatumTarget
{
    public long Id;
    public string Kind = string.Empty;
    public string Name = string.Empty;
    public double? ElevationMm;
    public bool Pinned;
    public bool OwnedByOther;
    public string? Owner;
    public DatumDependents Dependents = new();
}

public sealed class DatumItemResult
{
    public string Kind = string.Empty;
    public int Index;
    public string Name = string.Empty;
    /// <summary>ok | unchanged | refused.</summary>
    public string Status = "ok";
    /// <summary>none | low | high — what the change does to dependent elements and views.</summary>
    public string Impact = "none";
    public bool NeedsConfirmation;
    public long? ElementId;
    public string Summary = string.Empty;
    public List<DatumProblem> Problems = new();
    public List<string> Warnings = new();

    public bool Refused => Status == "refused";
}

/// <summary>
/// The safety rules of revit_create_datums and revit_edit_datums: names, elevation clashes, geometry, views and the
/// impact of moving or renaming a datum others depend on. Pure — no Revit types — so every decision is unit tested.
/// </summary>
public static class DatumPlanner
{
    /// <summary>Revit refuses these in element names.</summary>
    private static readonly char[] InvalidNameChars = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };
    public const int MaxNameLength = 255;
    public const double MinLengthMm = 5;
    /// <summary>A level or datum moved further than this counts as high impact even when nothing visibly depends on it.</summary>
    public const double LargeMoveMm = 1000;

    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "the name is empty";
        if (name!.Length > MaxNameLength) return $"the name is longer than {MaxNameLength} characters";
        if (name != name.Trim()) return "the name starts or ends with a space";
        var bad = name.IndexOfAny(InvalidNameChars);
        if (bad >= 0) return $"the name contains '{name[bad]}', which Revit does not allow";
        if (name.Any(char.IsControl)) return "the name contains a control character";
        return null;
    }

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool InBounds(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && Math.Abs(v) <= DatumSpecParser.MaxCoordinateMm;

    private static bool InBounds(MmPoint p) => InBounds(p.X) && InBounds(p.Y) && InBounds(p.Z);

    // ── Create ──

    public static List<DatumItemResult> CheckCreate(DatumCreateSpec spec, IReadOnlyList<ExistingDatum> existing,
        IReadOnlyDictionary<long, DatumViewInfo> views, DatumOptions options)
    {
        var results = new List<DatumItemResult>();
        var claimedNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [DatumKinds.Level] = new(StringComparer.OrdinalIgnoreCase),
            [DatumKinds.Grid] = new(StringComparer.OrdinalIgnoreCase),
            [DatumKinds.ReferencePlane] = new(StringComparer.OrdinalIgnoreCase)
        };
        foreach (var e in existing)
            if (e.Name.Length > 0 && claimedNames.TryGetValue(e.Kind, out var set)) set.Add(e.Name);
        var plannedElevations = new List<(double Mm, string Name)>();

        foreach (var l in spec.Levels)
        {
            var r = new DatumItemResult { Kind = DatumKinds.Level, Index = l.Index, Name = l.Name };
            results.Add(r);
            var nameProblem = ValidateName(l.Name);
            if (nameProblem != null) Refuse(r, "invalidName", $"Level '{l.Name}': {nameProblem}.");
            else CheckNameFree(r, claimedNames[DatumKinds.Level], l.Name, "level");
            if (!InBounds(l.ElevationMm))
                Refuse(r, "elevationOutOfRange", $"Level '{l.Name}': elevation {l.ElevationMm} mm is outside ±{DatumSpecParser.MaxCoordinateMm:0} mm (millimetres are expected).");
            else if (!options.AllowEquivalentLevels)
            {
                var clash = existing.Where(e => e.Kind == DatumKinds.Level && e.ElevationMm != null &&
                                                Math.Abs(e.ElevationMm.Value - l.ElevationMm) <= options.ElevationToleranceMm).ToList();
                foreach (var c in clash)
                    Refuse(r, "elevationClash", $"Level '{l.Name}' at {l.ElevationMm:0.#} mm is the same elevation (±{options.ElevationToleranceMm:0.#} mm) as level '{c.Name}' ({c.Id}). Pass allowEquivalentLevels=true to create it anyway.");
                var inBatch = plannedElevations.FirstOrDefault(p => Math.Abs(p.Mm - l.ElevationMm) <= options.ElevationToleranceMm);
                if (inBatch.Name != null)
                    Refuse(r, "elevationClash", $"Level '{l.Name}' repeats the elevation of '{inBatch.Name}' in the same call.");
            }
            if (!r.Refused)
            {
                claimedNames[DatumKinds.Level].Add(l.Name);
                plannedElevations.Add((l.ElevationMm, l.Name));
                r.Summary = $"create level '{l.Name}' at {l.ElevationMm:0.#} mm";
            }
        }

        foreach (var g in spec.Grids)
        {
            var r = new DatumItemResult { Kind = DatumKinds.Grid, Index = g.Index, Name = g.Name ?? string.Empty };
            results.Add(r);
            if (g.Name != null)
            {
                var nameProblem = ValidateName(g.Name);
                if (nameProblem != null) Refuse(r, "invalidName", $"Grid '{g.Name}': {nameProblem}.");
                else CheckNameFree(r, claimedNames[DatumKinds.Grid], g.Name, "grid");
            }
            if (!InBounds(g.Start) || !InBounds(g.End))
                Refuse(r, "coordinateOutOfRange", $"Grid {Label(g.Name)}: an endpoint is outside ±{DatumSpecParser.MaxCoordinateMm:0} mm (millimetres are expected).");
            else if (g.Start.DistanceXyTo(g.End) < MinLengthMm)
                Refuse(r, "badGeometry", $"Grid {Label(g.Name)}: the endpoints are {g.Start.DistanceXyTo(g.End):0.#} mm apart in plan; a grid needs at least {MinLengthMm:0} mm.");
            else if (Math.Abs(g.Start.Z - g.End.Z) > 0.5)
                Refuse(r, "badGeometry", $"Grid {Label(g.Name)}: the endpoints are at different heights ({g.Start.Z:0.#} and {g.End.Z:0.#} mm); a straight grid must be horizontal. Give [x, y] only.");
            else if (!options.AllowDuplicateGeometry)
            {
                var same = existing.FirstOrDefault(e => e.Kind == DatumKinds.Grid && e.Start != null && e.End != null && SameLineXy(e.Start.Value, e.End.Value, g.Start, g.End));
                if (same != null)
                    Refuse(r, "duplicateGeometry", $"Grid {Label(g.Name)} has the same endpoints as grid '{same.Name}' ({same.Id}). Pass allowDuplicateGeometry=true to create it anyway.");
            }
            if (!r.Refused)
            {
                if (g.Name != null) claimedNames[DatumKinds.Grid].Add(g.Name);
                r.Summary = $"create grid {Label(g.Name)} from ({g.Start.X:0.#}, {g.Start.Y:0.#}) to ({g.End.X:0.#}, {g.End.Y:0.#}) mm";
            }
        }

        foreach (var p in spec.ReferencePlanes)
        {
            var r = new DatumItemResult { Kind = DatumKinds.ReferencePlane, Index = p.Index, Name = p.Name ?? string.Empty };
            results.Add(r);
            if (p.Name != null)
            {
                var nameProblem = ValidateName(p.Name);
                if (nameProblem != null) Refuse(r, "invalidName", $"Reference plane '{p.Name}': {nameProblem}.");
                else CheckNameFree(r, claimedNames[DatumKinds.ReferencePlane], p.Name, "reference plane");
            }
            if (!views.TryGetValue(p.ViewId, out var view))
                Refuse(r, "viewNotFound", $"Reference plane {Label(p.Name)}: view {p.ViewId} is not in the document (a view of a linked model cannot be used).");
            else if (view.Problem != null)
                Refuse(r, "unsupportedView", $"Reference plane {Label(p.Name)}: view {p.ViewId} cannot hold one — {view.Problem}.");
            if (!InBounds(p.BubbleEnd) || !InBounds(p.FreeEnd))
                Refuse(r, "coordinateOutOfRange", $"Reference plane {Label(p.Name)}: an end is outside ±{DatumSpecParser.MaxCoordinateMm:0} mm (millimetres are expected).");
            else if (p.BubbleEnd.DistanceTo(p.FreeEnd) < MinLengthMm)
                Refuse(r, "badGeometry", $"Reference plane {Label(p.Name)}: the two ends are {p.BubbleEnd.DistanceTo(p.FreeEnd):0.#} mm apart; at least {MinLengthMm:0} mm is needed.");
            else if (view != null && view.Problem == null && ParallelToDirection(p.BubbleEnd, p.FreeEnd, view))
                Refuse(r, "badGeometry", $"Reference plane {Label(p.Name)}: the line runs along the direction view {p.ViewId} looks in, so it would be a point there. Draw it across the view.");
            else if (!options.AllowDuplicateGeometry)
            {
                var same = existing.FirstOrDefault(e => e.Kind == DatumKinds.ReferencePlane && e.Start != null && e.End != null &&
                                                        SameLine3(e.Start.Value, e.End.Value, p.BubbleEnd, p.FreeEnd));
                if (same != null)
                    Refuse(r, "duplicateGeometry", $"Reference plane {Label(p.Name)} has the same ends as reference plane '{same.Name}' ({same.Id}). Pass allowDuplicateGeometry=true to create it anyway.");
            }
            if (!r.Refused)
            {
                if (p.Name != null) claimedNames[DatumKinds.ReferencePlane].Add(p.Name);
                r.Summary = $"create reference plane {Label(p.Name)} in view {p.ViewId}";
            }
        }
        return results;
    }

    // ── Edit ──

    /// <param name="targets">The datum for each edited id; null when the id is not a datum of this document.</param>
    public static List<DatumItemResult> CheckEdits(IReadOnlyList<DatumEdit> edits, IReadOnlyDictionary<long, DatumTarget?> targets,
        IReadOnlyList<ExistingDatum> existing, DatumOptions options)
    {
        var results = new List<DatumItemResult>();
        var newNames = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal)
        {
            [DatumKinds.Level] = new(StringComparer.OrdinalIgnoreCase),
            [DatumKinds.Grid] = new(StringComparer.OrdinalIgnoreCase),
            [DatumKinds.ReferencePlane] = new(StringComparer.OrdinalIgnoreCase)
        };
        var seenIds = new HashSet<long>();
        // Levels as they will be after the whole call: ids → elevation.
        var finalElevations = existing.Where(e => e.Kind == DatumKinds.Level && e.ElevationMm != null).ToDictionary(e => e.Id, e => e.ElevationMm!.Value);
        foreach (var edit in edits)
            if (targets.TryGetValue(edit.Id, out var t) && t is { Kind: DatumKinds.Level } && edit.ElevationMm is { } mm && InBounds(mm))
                finalElevations[edit.Id] = mm;

        foreach (var edit in edits)
        {
            var r = new DatumItemResult { Index = edit.Index, ElementId = edit.Id };
            results.Add(r);
            targets.TryGetValue(edit.Id, out var target);
            if (target == null)
            {
                Refuse(r, "notFound", $"Element {edit.Id} is not a level, grid or reference plane of this document. Datums of linked models cannot be edited here.");
                continue;
            }
            r.Kind = target.Kind;
            r.Name = target.Name;
            if (!seenIds.Add(edit.Id)) { Refuse(r, "duplicateEdit", $"Element {edit.Id} appears in more than one edit; give one edit per datum."); continue; }
            if (target.OwnedByOther)
                Refuse(r, "ownedByOther", $"{Describe(target)} is checked out by {target.Owner ?? "another user"}. Ask them to relinquish it.");

            var geometric = false;
            double deltaMm = 0;

            if (edit.ElevationMm != null)
            {
                if (target.Kind != DatumKinds.Level) Refuse(r, "wrongField", $"elevationMm applies to levels only; {Describe(target)} is moved with moveByMm.");
                else if (!InBounds(edit.ElevationMm.Value)) Refuse(r, "elevationOutOfRange", $"{Describe(target)}: elevation {edit.ElevationMm} mm is outside ±{DatumSpecParser.MaxCoordinateMm:0} mm (millimetres are expected).");
                else
                {
                    deltaMm = edit.ElevationMm.Value - (target.ElevationMm ?? 0);
                    geometric = Math.Abs(deltaMm) > 0.05;
                }
            }
            if (edit.MoveByMm != null)
            {
                var m = edit.MoveByMm.Value;
                if (target.Kind == DatumKinds.Level) Refuse(r, "wrongField", $"moveByMm does not apply to levels; use elevationMm for {Describe(target)}.");
                else if (!InBounds(m)) Refuse(r, "coordinateOutOfRange", $"{Describe(target)}: moveByMm is outside ±{DatumSpecParser.MaxCoordinateMm:0} mm (millimetres are expected).");
                else
                {
                    deltaMm = m.DistanceTo(new MmPoint(0, 0, 0));
                    geometric = deltaMm > 0.05;
                }
            }
            if (geometric && target.Pinned)
                Refuse(r, "pinned", $"{Describe(target)} is pinned; unpin it first.");

            var renamed = false;
            if (edit.NewName != null)
            {
                var problem = ValidateName(edit.NewName);
                if (problem != null) Refuse(r, "invalidName", $"{Describe(target)}: {problem}.");
                else if (edit.NewName == target.Name) { /* unchanged */ }
                else
                {
                    var clash = existing.FirstOrDefault(e => e.Kind == target.Kind && e.Id != target.Id && SameName(e.Name, edit.NewName));
                    if (clash != null) Refuse(r, "nameConflict", $"{Describe(target)}: the name '{edit.NewName}' is already used by {DisplayKind(clash.Kind)} {clash.Id}.");
                    else if (newNames[target.Kind].TryGetValue(edit.NewName, out var other) && other != target.Id)
                        Refuse(r, "nameConflict", $"{Describe(target)}: the name '{edit.NewName}' is given to two datums in this call.");
                    else { newNames[target.Kind][edit.NewName] = target.Id; renamed = true; }
                }
            }

            if (target.Kind == DatumKinds.Level && geometric && !r.Refused && !options.AllowEquivalentLevels)
            {
                var finalMm = edit.ElevationMm!.Value;
                var clash = finalElevations.Where(kv => kv.Key != target.Id && Math.Abs(kv.Value - finalMm) <= options.ElevationToleranceMm).Select(kv => kv.Key).FirstOrDefault();
                if (clash != 0)
                {
                    var name = existing.FirstOrDefault(e => e.Id == clash)?.Name ?? clash.ToString();
                    Refuse(r, "elevationClash", $"{Describe(target)} would sit at {finalMm:0.#} mm, the same elevation (±{options.ElevationToleranceMm:0.#} mm) as level '{name}' ({clash}). Pass allowEquivalentLevels=true to allow it.");
                }
            }
            if (target.Kind == DatumKinds.Level && geometric && !r.Refused)
            {
                var before = target.ElevationMm ?? 0;
                var crossed = existing.Where(e => e.Kind == DatumKinds.Level && e.Id != target.Id && e.ElevationMm != null &&
                                                  Between(e.ElevationMm.Value, before, edit.ElevationMm!.Value)).Select(e => e.Name).ToList();
                if (crossed.Count > 0)
                    r.Warnings.Add($"The level moves past {string.Join(", ", crossed.Take(5).Select(n => $"'{n}'"))}, which changes the level order.");
            }

            if (!r.Refused && !geometric && !renamed)
            {
                r.Status = "unchanged";
                r.Summary = $"{Describe(target)}: nothing to change";
                continue;
            }
            if (r.Refused) continue;

            // Impact on what depends on it.
            var d = target.Dependents;
            r.Impact = ClassifyImpact(geometric, renamed, deltaMm, d);
            if (geometric && d.Total > 0)
                r.Warnings.Add($"{d.Total} dependent element(s) follow this datum: {d.Hosted} hosted, {d.Views} view(s), {d.Other} other" +
                               (d.Examples.Count > 0 ? $" (e.g. {string.Join("; ", d.Examples.Take(4))})" : string.Empty) + ".");
            if (renamed && d.Views > 0)
                r.Warnings.Add($"{d.Views} view(s) are associated with it; view names and schedule or filter rules that use the name are not renamed.");
            if (r.Impact == "high" && !options.ConfirmHighImpact)
            {
                r.NeedsConfirmation = true;
                Refuse(r, "confirmationRequired", $"{Describe(target)}: high-impact change ({ImpactReason(geometric, deltaMm, d)}). Pass confirmHighImpact=true to apply it.");
                continue;
            }
            r.Summary = SummarizeEdit(target, edit, geometric, renamed);
        }
        return results;
    }

    private static bool Between(double v, double a, double b) => v > Math.Min(a, b) && v < Math.Max(a, b);

    public static string ClassifyImpact(bool geometric, bool renamed, double deltaMm, DatumDependents dependents)
    {
        if (geometric) return dependents.Total > 0 || deltaMm >= LargeMoveMm ? "high" : "low";
        return renamed ? "low" : "none";
    }

    private static string ImpactReason(bool geometric, double deltaMm, DatumDependents d)
    {
        if (d.Total > 0) return $"{d.Total} dependent element(s), moved {deltaMm:0.#} mm";
        return $"moved {deltaMm:0.#} mm";
    }

    private static string SummarizeEdit(DatumTarget t, DatumEdit e, bool geometric, bool renamed)
    {
        var parts = new List<string>();
        if (renamed) parts.Add($"rename to '{e.NewName}'");
        if (geometric && e.ElevationMm != null) parts.Add($"elevation {t.ElevationMm:0.#} → {e.ElevationMm:0.#} mm");
        if (geometric && e.MoveByMm is { } m) parts.Add($"move by ({m.X:0.#}, {m.Y:0.#}, {m.Z:0.#}) mm");
        return $"{Describe(t)}: {string.Join(", ", parts)}";
    }

    private static string Describe(DatumTarget t) => $"{DisplayKind(t.Kind)} '{t.Name}' ({t.Id})";

    public static string DisplayKind(string kind) => kind == DatumKinds.ReferencePlane ? "reference plane" : kind;

    // ── Geometry ──

    private static bool SameLineXy(MmPoint a1, MmPoint a2, MmPoint b1, MmPoint b2, double tol = 1.0) =>
        (a1.DistanceXyTo(b1) <= tol && a2.DistanceXyTo(b2) <= tol) || (a1.DistanceXyTo(b2) <= tol && a2.DistanceXyTo(b1) <= tol);

    private static bool SameLine3(MmPoint a1, MmPoint a2, MmPoint b1, MmPoint b2, double tol = 1.0) =>
        (a1.DistanceTo(b1) <= tol && a2.DistanceTo(b2) <= tol) || (a1.DistanceTo(b2) <= tol && a2.DistanceTo(b1) <= tol);

    /// <summary>True when the line between the two points is parallel to the view direction (no plane can be cut).</summary>
    public static bool ParallelToDirection(MmPoint a, MmPoint b, DatumViewInfo view)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        var len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var dirLen = Math.Sqrt(view.DirX * view.DirX + view.DirY * view.DirY + view.DirZ * view.DirZ);
        if (len < 1e-9 || dirLen < 1e-9) return false;
        var cx = dy * view.DirZ - dz * view.DirY;
        var cy = dz * view.DirX - dx * view.DirZ;
        var cz = dx * view.DirY - dy * view.DirX;
        var sin = Math.Sqrt(cx * cx + cy * cy + cz * cz) / (len * dirLen);
        return sin < 1e-3;
    }

    // ── Helpers ──

    private static string Label(string? name) => string.IsNullOrEmpty(name) ? "(unnamed)" : $"'{name}'";

    private static void CheckNameFree(DatumItemResult r, HashSet<string> taken, string name, string what)
    {
        if (taken.Contains(name)) Refuse(r, "nameConflict", $"The name '{name}' is already used by a {what} (names are compared ignoring case) or repeated in this call.");
    }

    private static void Refuse(DatumItemResult r, string code, string message)
    {
        r.Status = "refused";
        r.Problems.Add(new DatumProblem { Kind = r.Kind, Index = r.Index, Code = code, Message = message });
    }

    // ── Outcome ──

    /// <summary>Counts for the result and the approval summary.</summary>
    public static (int Ok, int Unchanged, int Refused, int High) Count(IReadOnlyList<DatumItemResult> results) =>
        (results.Count(r => r.Status == "ok"), results.Count(r => r.Status == "unchanged"), results.Count(r => r.Refused),
         results.Count(r => r.Impact == "high" && r.Status == "ok"));

    /// <summary>Whether apply may run, and which items it runs.</summary>
    public static bool CanApply(IReadOnlyList<DatumItemResult> results, DatumOptions options, out string? reason)
    {
        reason = null;
        var (ok, _, refused, _) = Count(results);
        if (ok == 0) { reason = refused > 0 ? "every item was refused" : "nothing to change"; return false; }
        if (refused > 0 && options.AllOrNothing)
        {
            reason = $"{refused} item(s) were refused and mode is allOrNothing, so nothing was written. Fix them or pass mode=perItem.";
            return false;
        }
        return true;
    }

    public static string ApprovalText(string verb, string documentTitle, IReadOnlyList<DatumItemResult> results, DatumOptions options)
    {
        var (ok, unchanged, refused, high) = Count(results);
        var lines = new List<string>
        {
            $"{verb} {ok} datum(s) in '{documentTitle}'" + (unchanged > 0 ? $", {unchanged} unchanged" : string.Empty) + (refused > 0 ? $", {refused} refused" : string.Empty) + "."
        };
        if (high > 0) lines.Add($"HIGH IMPACT: {high} change(s) move or affect elements that depend on the datum (confirmHighImpact={options.ConfirmHighImpact.ToString().ToLowerInvariant()}).");
        foreach (var r in results.Where(x => x.Status == "ok").Take(6))
            lines.Add("  " + r.Summary + (r.Impact == "high" ? " [high impact]" : string.Empty));
        if (ok > 6) lines.Add($"  … and {ok - 6} more.");
        foreach (var r in results.Where(x => x.Refused).Take(3))
            lines.Add("  refused: " + (r.Problems.FirstOrDefault()?.Message ?? r.Summary));
        lines.Add(options.AllOrNothing ? "All or nothing, one transaction, one undo step." : "perItem: items that pass are written, one transaction.");
        return string.Join("\n", lines);
    }
}
