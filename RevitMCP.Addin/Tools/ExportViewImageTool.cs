using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.RoomDevices;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Exports a view to PNG so the agent can check placement visually. Crop and highlight overrides
/// are applied inside a TransactionGroup that is rolled back after the export, so the model is
/// left exactly as it was.
/// </summary>
public sealed class ExportViewImageTool : IRevitMcpTool
{
    public string Name => "revit_export_view_image";
    public string Description =>
        "Exports a view (viewId, default the active view) as a PNG and returns its path; the MCP bridge also returns the " +
        "image itself. Optional cropToRoom (room number; with source/linkInstanceId/levelName) or bboxMm {minX,minY,maxX,maxY} " +
        "(host internal mm) crops a plan view, marginMm (default 1000). pixelSize (default 2000, 200-8000). " +
        "highlightElementIds[] are drawn red and thick. Crop and highlight are temporary — rolled back after the export.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (doc == null) return Task.FromResult(RoomToolSupport.Fail(request, "No active document."));
        var args = request.Arguments;
        var warnings = new List<string>();

        var viewId = ToolArguments.GetLong(args, "viewId");
        var view = viewId != 0 ? doc.GetElement(new ElementId(viewId)) as View : uidoc!.ActiveView;
        if (view == null || view.IsTemplate)
            return Task.FromResult(RoomToolSupport.Fail(request, viewId != 0 ? $"Element {viewId} is not a view." : "No active view."));

        // Crop region in host mm.
        Bounds2? crop = null;
        var margin = ToolArguments.GetDouble(args, "marginMm", 1000);
        var roomNumber = ToolArguments.GetString(args, "cropToRoom").Trim();
        if (roomNumber.Length > 0)
        {
            var service = RoomSourceService.FromArguments(doc, args, out var error);
            if (service == null) return Task.FromResult(RoomToolSupport.Fail(request, error!));
            var room = service.LoadRooms(new RoomFilter
            {
                RoomNumbers = [roomNumber],
                LevelName = ToolArguments.GetString(args, "levelName").Trim()
            }, warnings, 5).FirstOrDefault();
            if (room == null) return Task.FromResult(RoomToolSupport.Fail(request, $"Room '{roomNumber}' not found in the {service.Source.Label}.", warnings));
            crop = room.Bounds;
        }
        else if (args.TryGetValue("bboxMm", out var raw) && raw != null)
        {
            var obj = raw as JObject ?? (raw is string s ? ToolArguments.TryParseJObject(s) : JObject.FromObject(raw));
            var minX = obj?["minX"]?.Value<double?>();
            var minY = obj?["minY"]?.Value<double?>();
            var maxX = obj?["maxX"]?.Value<double?>();
            var maxY = obj?["maxY"]?.Value<double?>();
            if (minX == null || minY == null || maxX == null || maxY == null)
                return Task.FromResult(RoomToolSupport.Fail(request, "bboxMm needs minX, minY, maxX, maxY."));
            crop = new Bounds2(minX.Value, minY.Value, maxX.Value, maxY.Value);
        }

        if (crop != null && view is not ViewPlan)
        {
            warnings.Add("Cropping is only applied to plan views; the view is exported uncropped.");
            crop = null;
        }

        var highlight = ToolArguments.GetLongArray(args, "highlightElementIds");
        var pixelSize = Math.Max(200, Math.Min(8000, ToolArguments.GetInt(args, "pixelSize", 2000)));

        var folder = ToolArguments.GetString(args, "outputFolder").Trim();
        if (folder.Length == 0) folder = Path.Combine(Path.GetTempPath(), "RevitMCP", "images");
        Directory.CreateDirectory(folder);
        var baseName = Path.Combine(folder, $"view_{view.Id.Value}_{DateTime.Now:yyyyMMdd_HHmmss_fff}");

        string? exported = null;
        using (var group = new TransactionGroup(doc, "Revit MCP - Export View Image"))
        {
            group.Start();
            try
            {
                if (crop != null || highlight.Length > 0)
                {
                    using var t = new Transaction(doc, "Temporary view setup");
                    t.Start();
                    if (crop != null) ApplyCrop((ViewPlan)view, crop.Value, margin, warnings);
                    if (highlight.Length > 0) ApplyHighlight(doc, view, highlight, warnings);
                    t.Commit();
                }

                var options = new ImageExportOptions
                {
                    ExportRange = ExportRange.SetOfViews,
                    FilePath = baseName,
                    FitDirection = FitDirectionType.Horizontal,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_150,
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = pixelSize
                };
                options.SetViewsAndSheets(new List<ElementId> { view.Id });
                doc.ExportImage(options);

                // Revit appends the view name to FilePath; find the file it wrote.
                exported = Directory.GetFiles(folder, Path.GetFileName(baseName) + "*.png")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            finally
            {
                group.RollBack();
            }
        }

        if (exported == null)
            return Task.FromResult(RoomToolSupport.Fail(request, "Revit did not write an image file.", warnings));

        sw.Stop();
        return Task.FromResult(RoomToolSupport.Ok(request, sw,
            $"Exported view '{view.Name}' to {exported}.",
            new
            {
                imagePath = exported,
                viewId = view.Id.Value,
                viewName = view.Name,
                pixelSize,
                cropMm = crop is { } c ? new { minX = c.MinX - margin, minY = c.MinY - margin, maxX = c.MaxX + margin, maxY = c.MaxY + margin } : null,
                highlighted = highlight.Length
            },
            warnings));
    }

    private static void ApplyCrop(ViewPlan view, Bounds2 crop, double marginMm, List<string> warnings)
    {
        // The crop box lives in view coordinates; transform host points into it.
        var box = view.CropBox;
        var inverse = box.Transform.Inverse;
        var corners = new[]
        {
            new XYZ(crop.MinX - marginMm, crop.MinY - marginMm, 0),
            new XYZ(crop.MaxX + marginMm, crop.MinY - marginMm, 0),
            new XYZ(crop.MinX - marginMm, crop.MaxY + marginMm, 0),
            new XYZ(crop.MaxX + marginMm, crop.MaxY + marginMm, 0)
        }.Select(p => inverse.OfPoint(new XYZ(RoomUnits.MmToFt(p.X), RoomUnits.MmToFt(p.Y), 0))).ToList();

        box.Min = new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), box.Min.Z);
        box.Max = new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), box.Max.Z);
        try
        {
            view.CropBox = box;
            view.CropBoxActive = true;
            view.CropBoxVisible = false;
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not crop the view: {ex.Message}");
        }
    }

    private static void ApplyHighlight(Document doc, View view, long[] ids, List<string> warnings)
    {
        var settings = new OverrideGraphicSettings()
            .SetProjectionLineColor(new Color(255, 0, 0))
            .SetProjectionLineWeight(10)
            .SetCutLineColor(new Color(255, 0, 0))
            .SetCutLineWeight(10);

        foreach (var id in ids)
        {
            var elementId = new ElementId(id);
            if (doc.GetElement(elementId) == null)
            {
                warnings.Add($"Highlight: element {id} not found.");
                continue;
            }
            try { view.SetElementOverrides(elementId, settings); }
            catch (Exception ex) { warnings.Add($"Highlight {id}: {ex.Message}"); }
        }
    }
}
