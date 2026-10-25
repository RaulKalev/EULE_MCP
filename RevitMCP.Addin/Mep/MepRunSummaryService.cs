using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace RevitMCP.Addin.Mep;

/// <summary>The filters of revit_get_mep_run_summary.</summary>
internal sealed class MepSummaryFilter
{
    /// <summary>cableTray, conduit, or null for both.</summary>
    public string? Kind { get; init; }
    public string[] Types { get; init; } = Array.Empty<string>();
    public string[] Levels { get; init; } = Array.Empty<string>();
    public long ViewId { get; init; }
    public string RunId { get; init; } = string.Empty;
    public int ScanLimit { get; init; } = 20_000;
}

internal sealed class MepSummaryScan
{
    public MepRunSummary Summary { get; init; } = null!;
    public int Scanned { get; init; }
    public bool ScanTruncated { get; init; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Reads trays, conduits and their fittings for revit_get_mep_run_summary (#106) and hands them to the pure
/// <see cref="MepRunSummaryMath"/>. Lengths are Revit's own Length parameter (CURVE_ELEM_LENGTH), which is what a
/// schedule sums; the location curve is the fallback.
/// </summary>
internal static class MepRunSummaryService
{
    public static MepSummaryScan Scan(Document doc, MepSummaryFilter filter)
    {
        var warnings = new List<string>();
        var kinds = filter.Kind == null ? new[] { MepKind.CableTray, MepKind.Conduit } : new[] { filter.Kind };
        var levelFilter = filter.Levels.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var typeFilter = filter.Types.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        var worksets = doc.IsWorkshared ? doc.GetWorksetTable() : null;

        var segments = new List<MepSegmentInfo>();
        var fittings = new List<MepFittingInfo>();
        var openEnds = new List<MepOpenEndInfo>();
        var scanned = 0;
        var truncated = false;

        foreach (var kind in kinds)
        {
            foreach (var element in Collect(doc, filter, kind == MepKind.CableTray ? typeof(CableTray) : typeof(Conduit)))
            {
                if (scanned >= filter.ScanLimit) { truncated = true; break; }
                if (element is not MEPCurve curve) continue;
                var typeName = TypeName(doc, curve);
                if (typeFilter.Count > 0 && !Matches(typeFilter, typeName, curve.GetTypeId())) continue;
                var level = curve.ReferenceLevel;
                if (levelFilter.Count > 0 && !Matches(levelFilter, level?.Name ?? string.Empty, level?.Id)) continue;
                if (!InRun(filter, curve)) continue;
                scanned++;

                var ends = MepConnectorFinder.EndConnectors(curve);
                var open = ends.Where(c => !MepConnectorFinder.IsConnected(c)).ToList();
                var size = MepConnectorFinder.SizeOf(curve);
                segments.Add(new MepSegmentInfo
                {
                    ElementId = curve.Id.Value,
                    Kind = kind,
                    TypeName = typeName,
                    Size = MepRunSummaryMath.SizeLabel(kind, size.WidthMm, size.HeightMm, size.DiameterMm),
                    Level = level?.Name ?? string.Empty,
                    Workset = WorksetName(worksets, curve),
                    LengthMm = LengthMm(curve),
                    EndConnectors = ends.Count,
                    OpenEnds = open.Count,
                    RunId = MepRunSchema.RunIdOf(curve)
                });
                foreach (var connector in open)
                    openEnds.Add(new MepOpenEndInfo
                    {
                        ElementId = curve.Id.Value, Kind = kind, ElementClass = "segment", TypeName = typeName, Level = level?.Name ?? string.Empty,
                        Position = MepConnectorFinder.ToMm(connector.Origin)
                    });
            }

            var fittingCategory = kind == MepKind.CableTray ? BuiltInCategory.OST_CableTrayFitting : BuiltInCategory.OST_ConduitFitting;
            foreach (var element in CollectCategory(doc, filter, fittingCategory))
            {
                if (scanned >= filter.ScanLimit) { truncated = true; break; }
                if (element is not FamilyInstance fitting) continue;
                var levelName = LevelName(doc, fitting);
                if (levelFilter.Count > 0 && !Matches(levelFilter, levelName, fitting.LevelId)) continue;
                if (!InRun(filter, fitting)) continue;
                scanned++;
                var typeName = fitting.Symbol == null ? fitting.Name : $"{fitting.Symbol.FamilyName}: {fitting.Symbol.Name}";
                var open = MepConnectorFinder.OpenEnds(fitting);
                fittings.Add(new MepFittingInfo { ElementId = fitting.Id.Value, Kind = kind, TypeName = typeName, Level = levelName, OpenEnds = open.Count });
                foreach (var connector in open)
                    openEnds.Add(new MepOpenEndInfo
                    {
                        ElementId = fitting.Id.Value, Kind = kind, ElementClass = "fitting", TypeName = typeName, Level = levelName,
                        Position = MepConnectorFinder.ToMm(connector.Origin)
                    });
            }
        }

        if (truncated)
            warnings.Add($"The scan stopped at {filter.ScanLimit} elements: the totals cover only those. Narrow it with kind, levels, types, viewId or runId.");
        if (typeFilter.Count > 0)
            warnings.Add("types filters the segments only; fittings are counted by their own family type.");

        var scan = new MepSummaryScan
        {
            Summary = MepRunSummaryMath.Summarize(segments, fittings, openEnds),
            Scanned = scanned,
            ScanTruncated = truncated
        };
        scan.Warnings.AddRange(warnings);
        return scan;
    }

    private static IEnumerable<Element> Collect(Document doc, MepSummaryFilter filter, Type type)
    {
        var collector = filter.ViewId > 0 ? new FilteredElementCollector(doc, new ElementId(filter.ViewId)) : new FilteredElementCollector(doc);
        collector = collector.OfClass(type).WhereElementIsNotElementType();
        var marker = filter.RunId.Length > 0 ? MepRunSchema.Filter() : null;
        if (filter.RunId.Length > 0 && marker == null) return Array.Empty<Element>();
        return marker == null ? collector : collector.WherePasses(marker);
    }

    private static IEnumerable<Element> CollectCategory(Document doc, MepSummaryFilter filter, BuiltInCategory category)
    {
        var collector = filter.ViewId > 0 ? new FilteredElementCollector(doc, new ElementId(filter.ViewId)) : new FilteredElementCollector(doc);
        collector = collector.OfCategory(category).WhereElementIsNotElementType();
        var marker = filter.RunId.Length > 0 ? MepRunSchema.Filter() : null;
        if (filter.RunId.Length > 0 && marker == null) return Array.Empty<Element>();
        return marker == null ? collector : collector.WherePasses(marker);
    }

    private static bool InRun(MepSummaryFilter filter, Element element) =>
        filter.RunId.Length == 0 || string.Equals(MepRunSchema.RunIdOf(element), filter.RunId, StringComparison.OrdinalIgnoreCase);

    /// <summary>A filter value matches a name (case-insensitive) or a numeric element id.</summary>
    private static bool Matches(List<string> values, string name, ElementId? id) =>
        values.Any(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase) ||
                        id != null && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n == id.Value);

    public static double LengthMm(MEPCurve curve)
    {
        try
        {
            var parameter = curve.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
            if (parameter is { StorageType: StorageType.Double, HasValue: true })
                return MepTypeCatalog.FtToMm(parameter.AsDouble());
        }
        catch
        {
            // Fall back to the location curve.
        }
        return (curve.Location as LocationCurve)?.Curve is { } line ? MepTypeCatalog.FtToMm(line.Length) : 0;
    }

    private static string TypeName(Document doc, Element element) => doc.GetElement(element.GetTypeId())?.Name ?? element.Name;

    private static string LevelName(Document doc, FamilyInstance instance)
    {
        var id = instance.LevelId;
        if (id == null || id == ElementId.InvalidElementId)
        {
            var parameter = instance.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ?? instance.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM);
            id = parameter?.AsElementId() ?? ElementId.InvalidElementId;
        }
        return id == ElementId.InvalidElementId ? string.Empty : doc.GetElement(id)?.Name ?? string.Empty;
    }

    private static string WorksetName(WorksetTable? worksets, Element element)
    {
        if (worksets == null) return string.Empty;
        try { return worksets.GetWorkset(element.WorksetId)?.Name ?? string.Empty; }
        catch { return string.Empty; }
    }
}
