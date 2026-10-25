namespace RevitMCP.Addin.Mep;

/// <summary>One tray or conduit as the summary sees it (read from the model by MepRunSummaryService).</summary>
public sealed class MepSegmentInfo
{
    public long ElementId { get; init; }
    public string Kind { get; init; } = MepKind.CableTray;
    public string TypeName { get; init; } = string.Empty;

    /// <summary>"300x60" for a tray, "Ø25" for a conduit.</summary>
    public string Size { get; init; } = string.Empty;

    public string Level { get; init; } = string.Empty;
    public string Workset { get; init; } = string.Empty;
    public double LengthMm { get; init; }

    /// <summary>End connectors of the segment and how many of them are not connected.</summary>
    public int EndConnectors { get; init; }
    public int OpenEnds { get; init; }

    public string? RunId { get; init; }
}

/// <summary>A tray or conduit fitting (elbow, tee, union, …).</summary>
public sealed class MepFittingInfo
{
    public long ElementId { get; init; }
    public string Kind { get; init; } = MepKind.CableTray;

    /// <summary>"Family: Type".</summary>
    public string TypeName { get; init; } = string.Empty;

    public string Level { get; init; } = string.Empty;
    public int OpenEnds { get; init; }
}

/// <summary>An unconnected end connector: where to look in the model.</summary>
public sealed class MepOpenEndInfo
{
    public long ElementId { get; init; }
    public string Kind { get; init; } = MepKind.CableTray;
    public string ElementClass { get; init; } = "segment";
    public string TypeName { get; init; } = string.Empty;
    public string Level { get; init; } = string.Empty;
    public MepPoint Position { get; init; }
}

/// <summary>Length and count of one group of segments.</summary>
public sealed class MepLengthGroup
{
    public string Kind { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public string? Type { get; init; }
    public string? Size { get; init; }
    public string? Level { get; init; }
    public string? Workset { get; init; }
    public int Count { get; init; }
    public double LengthMm { get; init; }
    public double LengthM => MepMath.ToMetres(LengthMm);
}

public sealed class MepRunSummary
{
    public int Segments { get; init; }
    public int Fittings { get; init; }
    public double TotalLengthMm { get; init; }
    public double TotalLengthM => MepMath.ToMetres(TotalLengthMm);

    /// <summary>Total metres per kind (cableTray, conduit).</summary>
    public Dictionary<string, double> TotalByKindM { get; init; } = new();

    public List<MepLengthGroup> ByType { get; init; } = new();
    public List<MepLengthGroup> BySize { get; init; } = new();
    public List<MepLengthGroup> ByLevel { get; init; } = new();
    public List<MepLengthGroup> ByWorkset { get; init; } = new();

    /// <summary>Type × size × level × workset: the rows a schedule would show.</summary>
    public List<MepLengthGroup> Groups { get; init; } = new();

    /// <summary>Fitting count per kind and "Family: Type".</summary>
    public List<MepLengthGroup> FittingsByType { get; init; } = new();

    public int OpenEndCount { get; init; }
    public List<MepOpenEndInfo> OpenEnds { get; init; } = new();
    public bool OpenEndsTruncated => OpenEndCount > OpenEnds.Count;

    /// <summary>Segments whose every end is open: connected to nothing.</summary>
    public int DisconnectedCount { get; init; }
    public List<long> DisconnectedSegmentIds { get; init; } = new();
    public bool DisconnectedTruncated => DisconnectedCount > DisconnectedSegmentIds.Count;
}

/// <summary>Pure grouping and totals of revit_get_mep_run_summary (#106). No Revit API.</summary>
public static class MepRunSummaryMath
{
    public const int MaxListed = 500;

    public static MepRunSummary Summarize(
        IReadOnlyList<MepSegmentInfo> segments, IReadOnlyList<MepFittingInfo> fittings, IReadOnlyList<MepOpenEndInfo> openEnds,
        int maxListed = MaxListed)
    {
        var disconnected = segments.Where(s => s.EndConnectors > 0 && s.OpenEnds >= s.EndConnectors).Select(s => s.ElementId).ToList();
        return new MepRunSummary
        {
            Segments = segments.Count,
            Fittings = fittings.Count,
            TotalLengthMm = MepMath.Round(segments.Sum(s => s.LengthMm)),
            TotalByKindM = segments.GroupBy(s => s.Kind).OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => MepMath.ToMetres(g.Sum(s => s.LengthMm))),
            ByType = Group(segments, s => new { s.Kind, s.TypeName }, (k, g) => Make(k.Kind, k.TypeName, g, type: k.TypeName)),
            BySize = Group(segments, s => new { s.Kind, s.Size }, (k, g) => Make(k.Kind, k.Size, g, size: k.Size)),
            ByLevel = Group(segments, s => new { s.Kind, s.Level }, (k, g) => Make(k.Kind, k.Level, g, level: k.Level)),
            ByWorkset = Group(segments, s => new { s.Kind, s.Workset }, (k, g) => Make(k.Kind, k.Workset, g, workset: k.Workset)),
            Groups = Group(segments, s => new { s.Kind, s.TypeName, s.Size, s.Level, s.Workset },
                (k, g) => Make(k.Kind, $"{k.TypeName} | {k.Size} | {k.Level}{(k.Workset.Length > 0 ? " | " + k.Workset : string.Empty)}", g,
                    k.TypeName, k.Size, k.Level, k.Workset)),
            FittingsByType = fittings.GroupBy(f => new { f.Kind, f.TypeName })
                .OrderBy(g => g.Key.Kind, StringComparer.Ordinal).ThenBy(g => g.Key.TypeName, StringComparer.OrdinalIgnoreCase)
                .Select(g => new MepLengthGroup { Kind = g.Key.Kind, Key = g.Key.TypeName, Type = g.Key.TypeName, Count = g.Count() })
                .ToList(),
            OpenEndCount = openEnds.Count,
            OpenEnds = openEnds
                .OrderBy(o => o.Level, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.ElementId)
                .Take(maxListed)
                .Select(o => new MepOpenEndInfo
                {
                    ElementId = o.ElementId, Kind = o.Kind, ElementClass = o.ElementClass, TypeName = o.TypeName, Level = o.Level,
                    Position = o.Position.Rounded()
                })
                .ToList(),
            DisconnectedCount = disconnected.Count,
            DisconnectedSegmentIds = disconnected.OrderBy(id => id).Take(maxListed).ToList()
        };
    }

    /// <summary>Sizes are labelled "300x60" (tray width × height) and "Ø25" (conduit diameter), in mm.</summary>
    public static string SizeLabel(string kind, double? widthMm, double? heightMm, double? diameterMm) => kind == MepKind.CableTray
        ? widthMm.HasValue && heightMm.HasValue ? $"{MepMath.FormatMm(widthMm.Value)}x{MepMath.FormatMm(heightMm.Value)}" : "?"
        : diameterMm.HasValue ? $"Ø{MepMath.FormatMm(diameterMm.Value)}" : "?";

    private static List<MepLengthGroup> Group<TKey>(
        IReadOnlyList<MepSegmentInfo> segments, Func<MepSegmentInfo, TKey> key,
        Func<TKey, List<MepSegmentInfo>, MepLengthGroup> make) =>
        segments.GroupBy(key).Select(g => make(g.Key, g.ToList()))
            .OrderBy(g => g.Kind, StringComparer.Ordinal).ThenByDescending(g => g.LengthMm).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static MepLengthGroup Make(string kind, string key, List<MepSegmentInfo> group,
        string? type = null, string? size = null, string? level = null, string? workset = null) => new()
    {
        Kind = kind,
        Key = key,
        Type = type,
        Size = size,
        Level = level,
        Workset = workset,
        Count = group.Count,
        LengthMm = MepMath.Round(group.Sum(s => s.LengthMm))
    };
}
