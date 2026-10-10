using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Placement;

/// <summary>The per-element outcome names shared by the copy preview and the write tool.</summary>
public static class CopyStatus
{
    /// <summary>Would be copied (preview) and nothing known stands in the way.</summary>
    public const string Ready = "Ready";

    public const string Copied = "Copied";
    public const string Missing = "Missing";

    /// <summary>
    /// Not an element Revit's Copy handles — a view, a sheet or a family type. Another tool does it,
    /// and the reason names that tool.
    /// </summary>
    public const string UseOtherTool = "UseOtherTool";

    /// <summary>The requested combination cannot work: incompatible views, a move off the view plane, no axes.</summary>
    public const string Unsupported = "Unsupported";

    /// <summary>Revit refused to copy the element.</summary>
    public const string Failed = "Failed";

    /// <summary>Copied during the transaction, then undone because atomic=true and something failed.</summary>
    public const string RolledBack = "RolledBack";

    /// <summary>Copyable, but never attempted — atomic=true and the batch was rejected up front.</summary>
    public const string NotAttempted = "NotAttempted";
}

/// <summary>How a batch of elements is copied.</summary>
public static class CopyOperation
{
    /// <summary>Model elements copied within the document by a translation (rehosting applies).</summary>
    public const string Document = "Document";

    /// <summary>Elements copied from a source view into a destination view, which may be the same view.</summary>
    public const string View = "View";
}

/// <summary>How sure the source→copy pairing is.</summary>
public static class CopyMapping
{
    /// <summary>The copy sits exactly where the source plus the translation is.</summary>
    public const string Position = "Position";

    /// <summary>Sources and copies of the same type were paired in id order.</summary>
    public const string Order = "Order";

    /// <summary>The element was copied on its own, so the copy of its kind is its copy.</summary>
    public const string Single = "Single";
}

/// <summary>The copy request as it arrived.</summary>
public sealed class CopyRequest
{
    public List<long> ElementIds { get; init; } = new();
    public bool UseSelection { get; init; }

    public double? DeltaXMm { get; init; }
    public double? DeltaYMm { get; init; }
    public double? DeltaZMm { get; init; }
    public double? DeltaRightMm { get; init; }
    public double? DeltaUpMm { get; init; }

    /// <summary>Destination view. 0 = same place: model elements stay model-wide, view-specific ones stay in their view.</summary>
    public long TargetViewId { get; init; }

    /// <summary>The view model elements are copied from when a targetViewId is given, and the axes for view deltas.</summary>
    public long SourceViewId { get; init; }

    public bool Atomic { get; init; } = true;

    public bool HasModelDelta => DeltaXMm.HasValue || DeltaYMm.HasValue || DeltaZMm.HasValue;
    public bool HasViewDelta => DeltaRightMm.HasValue || DeltaUpMm.HasValue;

    /// <summary>The displacement as a move entry, so the copy and move tools share one definition of the axes.</summary>
    public MoveRequest AsDelta() => new()
    {
        DeltaXMm = DeltaXMm,
        DeltaYMm = DeltaYMm,
        DeltaZMm = DeltaZMm,
        DeltaRightMm = DeltaRightMm,
        DeltaUpMm = DeltaUpMm
    };
}

/// <summary>What is known about one element before or after a copy, free of Revit types.</summary>
public sealed class ElementSnapshot
{
    public long Id { get; init; }
    public string ClassName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public long TypeId { get; init; }

    /// <summary>A point that identifies where the element is: insertion point, curve midpoint or box centre.</summary>
    public PointMm? Anchor { get; init; }

    public long? OwnerViewId { get; init; }
    public long? HostId { get; init; }
    public long? GroupId { get; init; }
    public bool Pinned { get; init; }

    /// <summary>Same class, category and type — the copy of an element always matches its source in these.</summary>
    public bool SameKindAs(ElementSnapshot other) =>
        ClassName == other.ClassName && TypeId == other.TypeId && Category == other.Category;
}

/// <summary>One source element and what happened to it.</summary>
public sealed class CopyItem
{
    public long ElementId { get; init; }
    public ElementSnapshot? Source { get; set; }
    public string? OwnerViewName { get; set; }

    public string Status { get; set; } = CopyStatus.Ready;
    public string? Reason { get; set; }
    public bool IsFailure { get; set; }

    /// <summary>True for an item the write tool will hand to Revit.</summary>
    public bool CanCopy { get; set; }

    /// <summary>Index into the batches list; -1 when the item is in no batch.</summary>
    public int BatchIndex { get; set; } = -1;

    public ElementSnapshot? Copy { get; set; }
    public string? Mapping { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// The Revit-free part of the element copy tools (#91): request parsing and the pairing of each
/// source element with its copy. Revit's copy calls return only a flat list of new ids — which
/// include dependents such as tags' leaders or sketch lines — so the pairing is worked out here from
/// what the elements are and where they ended up.
/// </summary>
public static class CopyElementsMath
{
    /// <summary>Upper bound on one request, the same reasoning as <see cref="MoveElementsMath.MaxMoves"/>.</summary>
    public const int MaxElements = 2000;

    /// <summary>How close a copy must be to "source + translation" to be paired by position.</summary>
    public const double PositionMatchToleranceMm = 1.0;

    /// <summary>
    /// Reads the request. Returns null with <paramref name="error"/> set when it cannot be acted on.
    /// </summary>
    public static CopyRequest? Parse(Dictionary<string, object?> arguments, out string? error)
    {
        error = null;
        var ids = ReadIds(arguments.TryGetValue("elementIds", out var rawIds) ? rawIds : null);
        var useSelection = ReadBool(arguments, "useSelection", false);

        if (ids == null)
        {
            error = "'elementIds' must be an array of element ids.";
            return null;
        }

        if (ids.Count == 0 && !useSelection)
        {
            error = "Provide elementIds, or useSelection=true to copy the current selection.";
            return null;
        }

        if (ids.Count > 0 && useSelection)
        {
            error = "Give either elementIds or useSelection=true, not both — it would be unclear which set is copied.";
            return null;
        }

        if (ids.Count > MaxElements)
        {
            error = $"'elementIds' holds {ids.Count} entries; the limit is {MaxElements} per request. Split the batch.";
            return null;
        }

        var duplicate = ids.GroupBy(id => id).FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
        {
            error = $"Element {duplicate.Key} appears {duplicate.Count()} times in elementIds. List each element once — " +
                    "to make several copies, call the tool once per copy.";
            return null;
        }

        var request = new CopyRequest
        {
            ElementIds = ids,
            UseSelection = useSelection,
            DeltaXMm = ReadDouble(arguments, "deltaXmm"),
            DeltaYMm = ReadDouble(arguments, "deltaYmm"),
            DeltaZMm = ReadDouble(arguments, "deltaZmm"),
            DeltaRightMm = ReadDouble(arguments, "deltaRightMm"),
            DeltaUpMm = ReadDouble(arguments, "deltaUpMm"),
            TargetViewId = ReadLong(arguments, "targetViewId"),
            SourceViewId = ReadLong(arguments, "sourceViewId"),
            Atomic = ReadBool(arguments, "atomic", true)
        };

        if (request.HasModelDelta && request.HasViewDelta)
        {
            error = "Give the displacement in one set of axes: deltaXmm/deltaYmm/deltaZmm (model) or " +
                    "deltaRightMm/deltaUpMm (the view's right and up), not a mix.";
            return null;
        }

        return request;
    }

    /// <summary>
    /// Pairs each source with its copy among the elements a copy call created. A copy always has its
    /// source's class, category and type. Within that, position decides when the translation is
    /// known; otherwise equal-sized groups are paired in id order (Revit creates copies in the
    /// order it was given the sources). Anything left over in <paramref name="created"/> is a
    /// dependency Revit copied along.
    /// </summary>
    public static Dictionary<long, (ElementSnapshot Copy, string Mapping)> MatchCopies(
        IReadOnlyList<ElementSnapshot> sources,
        IReadOnlyList<ElementSnapshot> created,
        PointMm? expectedTranslation)
    {
        var result = new Dictionary<long, (ElementSnapshot, string)>();
        var used = new HashSet<long>();

        // 1. By position: the copy sits at source + translation.
        if (expectedTranslation.HasValue)
        {
            foreach (var source in sources)
            {
                if (!source.Anchor.HasValue) continue;
                var expected = source.Anchor.Value.Plus(expectedTranslation.Value);
                var hits = created
                    .Where(c => !used.Contains(c.Id) && c.SameKindAs(source) && c.Anchor.HasValue &&
                                c.Anchor.Value.Minus(expected).Length <= PositionMatchToleranceMm)
                    .ToList();
                if (hits.Count != 1) continue;
                result[source.Id] = (hits[0], CopyMapping.Position);
                used.Add(hits[0].Id);
            }
        }

        // 2. By order, kind by kind, when what is left pairs up one to one.
        var remaining = sources.Where(s => !result.ContainsKey(s.Id)).ToList();
        foreach (var kind in remaining.GroupBy(s => (s.ClassName, s.Category, s.TypeId)))
        {
            var kindSources = kind.OrderBy(s => s.Id).ToList();
            var kindCopies = created
                .Where(c => !used.Contains(c.Id) && c.SameKindAs(kindSources[0]))
                .OrderBy(c => c.Id)
                .ToList();
            if (kindCopies.Count != kindSources.Count) continue;

            var mapping = sources.Count == 1 ? CopyMapping.Single : CopyMapping.Order;
            for (var i = 0; i < kindSources.Count; i++)
            {
                result[kindSources[i].Id] = (kindCopies[i], mapping);
                used.Add(kindCopies[i].Id);
            }
        }

        return result;
    }

    /// <summary>The created elements no source was paired with: dependents Revit copied along.</summary>
    public static List<ElementSnapshot> Dependencies(
        IReadOnlyList<ElementSnapshot> created,
        Dictionary<long, (ElementSnapshot Copy, string Mapping)> matches)
    {
        var paired = new HashSet<long>(matches.Values.Select(match => match.Copy.Id));
        return created.Where(c => !paired.Contains(c.Id)).ToList();
    }

    /// <summary>Whether a batch that produced failures has to be undone. Same rule as the move tools.</summary>
    public static bool ShouldRollBack(bool atomic, int failureCount) => MoveElementsMath.ShouldRollBack(atomic, failureCount);

    // ── Argument reading ─────────────────────────────────────────────────────

    private static List<long>? ReadIds(object? raw)
    {
        switch (raw)
        {
            case null:
                return new List<long>();
            case long[] longs:
                return longs.ToList();
            case string text when string.IsNullOrWhiteSpace(text):
                return new List<long>();
        }

        JArray? array;
        try
        {
            array = raw switch
            {
                JArray existing => existing,
                string text => JToken.Parse(text) as JArray,
                _ => JArray.FromObject(raw)
            };
        }
        catch
        {
            return null;
        }

        if (array == null) return null;

        var ids = new List<long>(array.Count);
        foreach (var token in array)
        {
            // Straight to long: Revit 2026 issues element ids above int range.
            try { ids.Add(token.Value<long>()); }
            catch { return null; }
        }
        return ids;
    }

    private static double? ReadDouble(Dictionary<string, object?> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var raw) || raw == null) return null;
        try
        {
            var value = raw switch
            {
                double d => d,
                float f => f,
                int i => i,
                long l => l,
                JValue { Type: JTokenType.Null } => double.NaN,
                JValue jv => jv.Value<double>(),
                _ => double.NaN
            };
            return double.IsNaN(value) || double.IsInfinity(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static long ReadLong(Dictionary<string, object?> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var raw) || raw == null) return 0;
        try
        {
            return raw switch
            {
                long l => l,
                int i => i,
                JValue { Type: JTokenType.Null } => 0,
                JValue jv => jv.Value<long>(),
                _ => 0
            };
        }
        catch
        {
            return 0;
        }
    }

    private static bool ReadBool(Dictionary<string, object?> arguments, string name, bool fallback)
    {
        if (!arguments.TryGetValue(name, out var raw) || raw == null) return fallback;
        try
        {
            return raw switch
            {
                bool b => b,
                JValue { Type: JTokenType.Boolean } jv => jv.Value<bool>(),
                _ => fallback
            };
        }
        catch
        {
            return fallback;
        }
    }
}
