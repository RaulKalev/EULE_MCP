using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Datums;

/// <summary>The three datum kinds revit_*_datums work with (#164).</summary>
public static class DatumKinds
{
    public const string Level = "level";
    public const string Grid = "grid";
    public const string ReferencePlane = "referencePlane";

    public static string? Normalize(string? value)
    {
        var v = (value ?? string.Empty).Trim().Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        return v switch
        {
            "level" or "levels" => Level,
            "grid" or "grids" => Grid,
            "referenceplane" or "referenceplanes" or "refplane" or "refplanes" => ReferencePlane,
            _ => null
        };
    }
}

/// <summary>A point in millimetres (host internal coordinates).</summary>
public readonly record struct MmPoint(double X, double Y, double Z)
{
    public double DistanceTo(MmPoint o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y) + (Z - o.Z) * (Z - o.Z));
    public double DistanceXyTo(MmPoint o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y));
}

public sealed class LevelCreate
{
    public int Index;
    public string Name = string.Empty;
    public double ElevationMm;
}

public sealed class GridCreate
{
    public int Index;
    /// <summary>Optional: Revit numbers an unnamed grid itself.</summary>
    public string? Name;
    public MmPoint Start;
    public MmPoint End;
}

public sealed class ReferencePlaneCreate
{
    public int Index;
    public string? Name;
    public long ViewId;
    public MmPoint BubbleEnd;
    public MmPoint FreeEnd;
}

public sealed class DatumCreateSpec
{
    public List<LevelCreate> Levels = new();
    public List<GridCreate> Grids = new();
    public List<ReferencePlaneCreate> ReferencePlanes = new();
    public int Count => Levels.Count + Grids.Count + ReferencePlanes.Count;
}

/// <summary>One change asked of an existing datum. Fields left null are not touched.</summary>
public sealed class DatumEdit
{
    public int Index;
    public long Id;
    public string? NewName;
    /// <summary>Levels only.</summary>
    public double? ElevationMm;
    /// <summary>Grids and reference planes only: a translation (dx, dy, dz) in millimetres.</summary>
    public MmPoint? MoveByMm;
}

public sealed class DatumOptions
{
    public const double DefaultElevationToleranceMm = 1.0;
    public double ElevationToleranceMm = DefaultElevationToleranceMm;
    public bool AllowEquivalentLevels;
    public bool AllowDuplicateGeometry;
    public bool ConfirmHighImpact;
    /// <summary>true = every item or none (default); false = apply the items that pass and report the rest.</summary>
    public bool AllOrNothing = true;
}

public sealed class DatumProblem
{
    public string Kind = string.Empty;
    public int Index;
    public string Code = string.Empty;
    public string Message = string.Empty;
}

/// <summary>Reads the arguments of revit_create_datums and revit_edit_datums. Pure: no Revit types, unit tested.</summary>
public static class DatumSpecParser
{
    public const int MaxItems = 500;
    /// <summary>Datum coordinates beyond this (mm, ±) are refused as a unit mistake.</summary>
    public const double MaxCoordinateMm = 1_000_000;

    public static DatumCreateSpec ParseCreate(object? levels, object? grids, object? referencePlanes, List<DatumProblem> problems)
    {
        var spec = new DatumCreateSpec();
        var i = 0;
        foreach (var o in Items(levels, "levels", problems))
        {
            i++;
            var elevation = Number(o, "elevationMm", "elevation");
            if (elevation == null)
            {
                problems.Add(Problem(DatumKinds.Level, i, "missingElevation", "levels[" + (i - 1) + "] needs elevationMm (millimetres)."));
                continue;
            }
            spec.Levels.Add(new LevelCreate { Index = i, Name = Text(o, "name") ?? string.Empty, ElevationMm = elevation.Value });
        }

        i = 0;
        foreach (var o in Items(grids, "grids", problems))
        {
            i++;
            var start = Point(o, "startMm", "start");
            var end = Point(o, "endMm", "end");
            if (start == null || end == null)
            {
                problems.Add(Problem(DatumKinds.Grid, i, "missingEndpoints", "grids[" + (i - 1) + "] needs startMm and endMm ([x, y] in millimetres)."));
                continue;
            }
            spec.Grids.Add(new GridCreate { Index = i, Name = Text(o, "name"), Start = start.Value, End = end.Value });
        }

        i = 0;
        foreach (var o in Items(referencePlanes, "referencePlanes", problems))
        {
            i++;
            var bubble = Point(o, "bubbleEndMm", "startMm", "start");
            var free = Point(o, "freeEndMm", "endMm", "end");
            var viewId = Number(o, "viewId");
            if (bubble == null || free == null || viewId == null)
            {
                problems.Add(Problem(DatumKinds.ReferencePlane, i, "missingGeometry",
                    "referencePlanes[" + (i - 1) + "] needs viewId, bubbleEndMm and freeEndMm ([x, y] or [x, y, z] in millimetres)."));
                continue;
            }
            spec.ReferencePlanes.Add(new ReferencePlaneCreate
            {
                Index = i, Name = Text(o, "name"), ViewId = (long)viewId.Value, BubbleEnd = bubble.Value, FreeEnd = free.Value
            });
        }

        if (spec.Count + problems.Count == 0)
            problems.Add(Problem(string.Empty, 0, "empty", "Give levels, grids or referencePlanes."));
        if (spec.Count > MaxItems)
            problems.Add(Problem(string.Empty, 0, "tooMany", $"{spec.Count} datums in one call; the limit is {MaxItems}."));
        return spec;
    }

    public static List<DatumEdit> ParseEdits(object? edits, List<DatumProblem> problems)
    {
        var list = new List<DatumEdit>();
        var i = 0;
        foreach (var o in Items(edits, "edits", problems))
        {
            i++;
            var id = Number(o, "id", "elementId");
            if (id == null)
            {
                problems.Add(Problem(string.Empty, i, "missingId", "edits[" + (i - 1) + "] needs id (the datum's element id)."));
                continue;
            }
            var edit = new DatumEdit
            {
                Index = i, Id = (long)id.Value, NewName = Text(o, "newName", "name"), ElevationMm = Number(o, "elevationMm", "elevation"),
                MoveByMm = Point(o, "moveByMm", "moveBy")
            };
            if (edit.NewName == null && edit.ElevationMm == null && edit.MoveByMm == null)
            {
                problems.Add(Problem(string.Empty, i, "nothingToChange", "edits[" + (i - 1) + "] has none of newName, elevationMm, moveByMm."));
                continue;
            }
            list.Add(edit);
        }
        if (list.Count + problems.Count == 0) problems.Add(Problem(string.Empty, 0, "empty", "Give edits [{id, newName?, elevationMm?, moveByMm?}]."));
        if (list.Count > MaxItems) problems.Add(Problem(string.Empty, 0, "tooMany", $"{list.Count} edits in one call; the limit is {MaxItems}."));
        return list;
    }

    public static DatumOptions ParseOptions(double? toleranceMm, bool allowEquivalentLevels, bool allowDuplicateGeometry, bool confirmHighImpact, string? mode)
    {
        return new DatumOptions
        {
            ElevationToleranceMm = toleranceMm is { } t && t >= 0 && t <= 1000 ? t : DatumOptions.DefaultElevationToleranceMm,
            AllowEquivalentLevels = allowEquivalentLevels,
            AllowDuplicateGeometry = allowDuplicateGeometry,
            ConfirmHighImpact = confirmHighImpact,
            AllOrNothing = !string.Equals((mode ?? string.Empty).Trim(), "perItem", StringComparison.OrdinalIgnoreCase)
        };
    }

    // ── Token helpers ──

    private static IEnumerable<JObject> Items(object? raw, string argument, List<DatumProblem> problems)
    {
        JToken? token = null;
        string? parseError = null;
        try
        {
            token = raw switch
            {
                null => null,
                JToken t => t,
                string s when string.IsNullOrWhiteSpace(s) => null,
                string s => JToken.Parse(s),
                _ => JToken.FromObject(raw)
            };
        }
        catch (Exception ex)
        {
            parseError = ex.Message;
        }
        if (parseError != null)
        {
            problems.Add(Problem(string.Empty, 0, "badJson", $"{argument} is not valid JSON: {parseError}"));
            return Array.Empty<JObject>();
        }
        if (token == null || token.Type == JTokenType.Null) return Array.Empty<JObject>();
        if (token is JObject single) token = new JArray(single);
        if (token is not JArray array)
        {
            problems.Add(Problem(string.Empty, 0, "notAnArray", $"{argument} must be an array of objects."));
            return Array.Empty<JObject>();
        }
        var result = new List<JObject>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JObject o) result.Add(o);
            else problems.Add(Problem(string.Empty, i + 1, "notAnObject", $"{argument}[{i}] is not an object."));
        }
        return result;
    }

    private static JToken? Get(JObject o, string[] names)
    {
        foreach (var n in names)
            if (o.TryGetValue(n, StringComparison.OrdinalIgnoreCase, out var v) && v.Type != JTokenType.Null) return v;
        return null;
    }

    private static string? Text(JObject o, params string[] names)
    {
        var v = Get(o, names);
        return v == null ? null : v.ToString().Trim();
    }

    private static double? Number(JObject o, params string[] names)
    {
        var v = Get(o, names);
        return v == null ? null : ToDouble(v);
    }

    private static double? ToDouble(JToken v)
    {
        if (v.Type is JTokenType.Float or JTokenType.Integer) return v.Value<double>();
        if (v.Type == JTokenType.String &&
            double.TryParse(v.Value<string>()!.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    private static MmPoint? Point(JObject o, params string[] names)
    {
        var v = Get(o, names);
        if (v == null) return null;
        double? x, y, z = 0;
        if (v is JArray a && (a.Count == 2 || a.Count == 3))
        {
            x = ToDouble(a[0]); y = ToDouble(a[1]);
            if (a.Count == 3) z = ToDouble(a[2]);
        }
        else if (v is JObject p)
        {
            x = Number(p, "x"); y = Number(p, "y"); z = Number(p, "z") ?? 0;
        }
        else return null;
        return x == null || y == null || z == null ? null : new MmPoint(x.Value, y.Value, z.Value);
    }

    private static DatumProblem Problem(string kind, int index, string code, string message) =>
        new() { Kind = kind, Index = index, Code = code, Message = message };
}
