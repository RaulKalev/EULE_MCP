using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Geometry;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Returns detailed geometry and parameter information for the currently selected Revit elements.
/// All coordinates are returned in millimetres.
/// </summary>
public class InspectSelectedElementsTool : IRevitMcpTool
{
    public string Name => "revit_inspect_selected_elements";
    public string Description => "Returns detailed geometry, bounding box (mm), location (mm), and parameter preview for the selected Revit elements. " +
                                 "All coordinates in millimetres (0.1 mm). The bounding box is bbox {min, max, sizeMm, centerMm}; the older " +
                                 "boundingBoxMm {min, max, size, center} is kept alongside it while legacy=true (the default, for one release).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Selection;

    private const int DefaultLimit = 50;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;

        if (uidoc?.Document == null)
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = "No active document."
            });

        var doc = uidoc.Document;
        var ids = uidoc.Selection.GetElementIds();

        if (ids.Count == 0)
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = true,
                Message    = "No elements selected.",
                Data       = new { selectedCount = 0, elements = Array.Empty<object>() },
                DurationMs = sw.ElapsedMilliseconds
            });

        var includeParameters    = ToolArguments.GetBool(request.Arguments, "includeParameters", true);
        var includeGeomSummary   = ToolArguments.GetBool(request.Arguments, "includeGeometrySummary", true);
        var legacy               = GeometryArgs.Legacy(request.Arguments);
        var limit                = ToolArguments.GetInt(request.Arguments, "limit", DefaultLimit);
        var parameterNamesRaw    = ToolArguments.GetStringArray(request.Arguments, "parameterNames");
        var paramNames           = new HashSet<string>(parameterNamesRaw ?? Array.Empty<string>(),
                                       StringComparer.OrdinalIgnoreCase);

        if (limit <= 0) limit = DefaultLimit;

        var elements = new List<object>();
        var warnings = new List<string>();
        int processed = 0;

        foreach (var id in ids)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (processed >= limit)
            {
                warnings.Add($"Output capped at {limit} elements. {ids.Count - limit} more selected.");
                break;
            }

            var elem = doc.GetElement(id);
            if (elem == null) continue;

            elements.Add(BuildDetail(doc, elem, includeParameters, paramNames, includeGeomSummary, legacy));
            processed++;
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"{ids.Count} element(s) selected. Returned {processed} detailed.",
            Data       = new { selectedCount = ids.Count, elements },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    // ── Conversion helpers ────────────────────────────────────────────────────

    // Coordinates use the shared geometry DTOs (#107): mm, 0.1 mm rounding, same {x,y,z} field names as before.
    private static double ToMm(double internalFeet) => GeometryUnits.FtToMm(internalFeet);

    private static PointMmDto PtToMm(XYZ p) => GeometryConvert.Point(p);

    // ── Main builder ──────────────────────────────────────────────────────────

    /// <param name="legacy">
    /// Also emit the pre-#107 <c>boundingBoxMm {min, max, size, center}</c> next to <c>bbox</c> (BoxMmDto). On by
    /// default for one release (argument <c>legacy</c>, docs/geometry-queries.md).
    /// </param>
    private static object BuildDetail(
        Document doc,
        Element elem,
        bool includeParameters,
        HashSet<string> paramNames,
        bool includeGeomSummary,
        bool legacy)
    {
        // ── Identity ──────────────────────────────────────────────────────────
        var categoryName = elem.Category?.Name ?? string.Empty;
        var familyName   = string.Empty;
        var typeName     = string.Empty;

        if (elem is FamilyInstance fi)
        {
            familyName = fi.Symbol?.Family?.Name ?? string.Empty;
            typeName   = fi.Symbol?.Name ?? string.Empty;
        }
        else
        {
            var typeEl = doc.GetElement(elem.GetTypeId());
            typeName = typeEl?.Name ?? string.Empty;
        }

        var levelName = string.Empty;
        if (elem.LevelId != ElementId.InvalidElementId)
            levelName = (doc.GetElement(elem.LevelId) as Level)?.Name ?? string.Empty;

        // ── Location ─────────────────────────────────────────────────────────
        object? location = null;
        if (elem.Location is LocationPoint lp)
        {
            location = new
            {
                kind     = "point",
                pointMm  = PtToMm(lp.Point)
            };
        }
        else if (elem.Location is LocationCurve lc)
        {
            var start = lc.Curve.GetEndPoint(0);
            var end   = lc.Curve.GetEndPoint(1);
            var len   = GeometryUnits.RoundMm(ToMm(lc.Curve.Length));
            location = new
            {
                kind          = "curve",
                curveStartMm  = PtToMm(start),
                curveEndMm    = PtToMm(end),
                lengthMm      = len
            };
        }

        // ── Bounding box (mm) ─────────────────────────────────────────────────
        BoxMmDto? bbox = null;
        object? boundingBoxMm = null;
        try
        {
            var bb = elem.get_BoundingBox(null);
            if (bb != null)
            {
                bbox = GeometryConvert.Box(bb);
                if (legacy)
                    boundingBoxMm = new { min = bbox.Min, max = bbox.Max, size = bbox.SizeMm, center = bbox.CenterMm };
            }
        }
        catch { }

        // ── Parameters preview ────────────────────────────────────────────────
        Dictionary<string, string?>? parametersPreview = null;
        if (includeParameters)
        {
            parametersPreview = new Dictionary<string, string?>();
            foreach (Parameter param in elem.Parameters)
            {
                if (param == null || !param.HasValue) continue;
                if (paramNames.Count > 0 && !paramNames.Contains(param.Definition?.Name ?? string.Empty))
                    continue;
                var pName = param.Definition?.Name ?? string.Empty;
                if (string.IsNullOrEmpty(pName)) continue;
                try
                {
                    parametersPreview[pName] = param.AsValueString()
                        ?? param.AsString()
                        ?? param.AsDouble().ToString("G6");
                }
                catch { /* skip unreadable parameter */ }
            }
        }

        // ── Geometry summary ──────────────────────────────────────────────────
        object? geometrySummary = null;
        if (includeGeomSummary)
        {
            geometrySummary = BuildGeometrySummary(elem);
        }

        return new
        {
            elementId         = elem.Id.Value,
            uniqueId          = elem.UniqueId,
            category          = categoryName,
            familyName,
            typeName,
            name              = elem.Name,
            level             = levelName,
            location,
            bbox,
            boundingBoxMm,
            parametersPreview,
            geometrySummary
        };
    }

    private static object BuildGeometrySummary(Element elem)
    {
        int solidCount = 0, meshCount = 0, curveCount = 0;
        double totalVolumeFeet3 = 0.0;
        bool hasGeometry = false;

        try
        {
            var opts = new Options { DetailLevel = ViewDetailLevel.Medium };
            var geom = elem.get_Geometry(opts);
            if (geom != null)
            {
                CountGeometry(geom, ref solidCount, ref meshCount, ref curveCount, ref totalVolumeFeet3);
                hasGeometry = solidCount > 0 || meshCount > 0 || curveCount > 0;
            }
        }
        catch { }

        // Convert cubic feet → cubic millimetres (1 ft^3 = 28_316_846.6 mm^3)
        double volumeMm3 = Math.Round(totalVolumeFeet3 * 28_316_846.592, 0);

        return new
        {
            hasGeometry,
            solidCount,
            meshCount,
            curveCount,
            estimatedVolumeMm3 = volumeMm3
        };
    }

    private static void CountGeometry(
        GeometryElement geom,
        ref int solids, ref int meshes, ref int curves, ref double volumeFt3)
    {
        foreach (var obj in geom)
        {
            switch (obj)
            {
                case Solid solid when solid.Volume > 0:
                    solids++;
                    volumeFt3 += solid.Volume;
                    break;
                case Mesh:
                    meshes++;
                    break;
                case Curve:
                    curves++;
                    break;
                case GeometryInstance gi:
                    CountGeometry(gi.GetInstanceGeometry(), ref solids, ref meshes, ref curves, ref volumeFt3);
                    break;
                case GeometryElement nested:
                    CountGeometry(nested, ref solids, ref meshes, ref curves, ref volumeFt3);
                    break;
            }
        }
    }
}
