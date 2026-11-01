using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Export;
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
        "highlightElementIds[] (or highlightIds[]) are drawn red and thick. Crop and highlight are temporary — rolled back " +
        "after the export. imageTransform maps pixels to coordinates, measured from a temporary line in a second export: " +
        "widthPx, heightPx, mmPerPx, originMm (the top-left pixel, in model mm, or sheet mm on a sheet), rightDirection, " +
        "upDirection, contentBoxPx (where the crop is; grids and annotation can make the image larger on any side). It is " +
        "null, with imageTransformNote, for a view without an active crop, a 3D view, or when it could not be measured.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var documentTarget = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (documentTarget == null) return Task.FromResult(documentFailure!);
        var doc = documentTarget.Document;
        var uidoc = ToolDocument.UiDocumentOf(uiapp, documentTarget);
        if (ToolArguments.GetBool(request.Arguments, "useSelection") && uidoc == null)
            return Task.FromResult(ToolDocument.ActiveRequired(request, "useSelection", documentTarget.Document.Title));
        var args = request.Arguments;
        var warnings = new List<string>();

        var viewId = ToolArguments.GetLong(args, "viewId");
        // A non-active document (#101) has no active view: it needs viewId.
        var view = viewId != 0 ? doc.GetElement(new ElementId(viewId)) as View : uidoc?.ActiveView;
        if (view == null || view.IsTemplate)
            return Task.FromResult(RoomToolSupport.Fail(request, viewId != 0 ? $"Element {viewId} is not a view." : uidoc == null ? $"'{doc.Title}' is not the active document: pass viewId." : "No active view."));

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

        var highlight = ToolArguments.GetLongArray(args, "highlightElementIds")
            .Concat(ToolArguments.GetLongArray(args, "highlightIds")).Distinct().ToArray();
        var pixelSize = Math.Max(200, Math.Min(8000, ToolArguments.GetInt(args, "pixelSize", 2000)));

        var folder = ToolArguments.GetString(args, "outputFolder").Trim();
        if (folder.Length == 0) folder = Path.Combine(Path.GetTempPath(), "RevitMCP", "images");
        Directory.CreateDirectory(folder);
        var baseName = Path.Combine(folder, $"view_{view.Id.Value}_{DateTime.Now:yyyyMMdd_HHmmss_fff}");

        string? exported = null;
        ImageFrame? frame = null;
        ViewImageTransform? transform = null;
        string? transformNote = null;
        // ExportImage needs a document without an open transaction, so the temporary setup is committed inside a group
        // that is always rolled back. The scope keeps that net-zero change out of checkpoints, plans and the graph.
        using (TemporaryModelChanges.Begin())
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

                // Never let the mapping cost the image: a view that cannot report its extent just has no transform.
                try { frame = ImageFrame.Of(view); }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException) { }
                if (frame == null) transformNote = view is View3D
                    ? "A 3D view has no flat mapping from pixels to the model."
                    : "The view has no active crop, so its image has no known extent. Pass bboxMm or cropToRoom, or crop the view.";

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

                if (frame != null && exported != null)
                {
                    try
                    {
                        transform = frame.Measure(doc, view, options, exported, folder, baseName);
                        if (transform == null) transformNote = "The temporary line did not show in the image, so it could not be measured (a template hiding Lines, for example).";
                    }
                    catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException or IOException or ArgumentException)
                    {
                        transformNote = $"It could not be measured: {ex.Message}";
                    }
                }
            }
            finally
            {
                group.RollBack();
            }
        }

        if (exported == null)
            return Task.FromResult(RoomToolSupport.Fail(request, "Revit did not write an image file.", warnings));

        if (transform == null && transformNote != null)
            warnings.Add("imageTransform is not available. " + transformNote);

        sw.Stop();
        return Task.FromResult(RoomToolSupport.Ok(request, sw,
            $"Exported view '{view.Name}' to {exported}.",
            new
            {
                imagePath = exported,
                viewId = view.Id.Value,
                viewName = view.Name,
                pixelSize,
                cropMm = crop is { } c
                    ? new
                    {
                        minX = Math.Round(c.MinX - margin, 1),
                        minY = Math.Round(c.MinY - margin, 1),
                        maxX = Math.Round(c.MaxX + margin, 1),
                        maxY = Math.Round(c.MaxY + margin, 1)
                    }
                    : null,
                highlighted = highlight.Length,
                imageTransform = transform == null ? null : Describe(transform, frame!.CoordinateSystem),
                imageTransformNote = transform == null ? transformNote : null
            },
            warnings));
    }

    private static object Describe(ViewImageTransform t, string coordinateSystem) => new
    {
        widthPx = t.WidthPx,
        heightPx = t.HeightPx,
        mmPerPx = Math.Round(t.MmPerPx, 4),
        coordinateSystem,
        originMm = new { x = Math.Round(t.OriginMm.X, 1), y = Math.Round(t.OriginMm.Y, 1), z = Math.Round(t.OriginMm.Z, 1) },
        rightDirection = new { x = Math.Round(t.RightDirection.X, 6), y = Math.Round(t.RightDirection.Y, 6), z = Math.Round(t.RightDirection.Z, 6) },
        upDirection = new { x = Math.Round(t.UpDirection.X, 6), y = Math.Round(t.UpDirection.Y, 6), z = Math.Round(t.UpDirection.Z, 6) },
        contentBoxPx = new { x = t.ContentBoxPx.X, y = t.ContentBoxPx.Y, width = t.ContentBoxPx.Width, height = t.ContentBoxPx.Height }
    };

    /// <summary>The extent the crop or sheet covers, read while the temporary crop is applied, and its measurement.</summary>
    private sealed class ImageFrame
    {
        private const double FeetToMm = 304.8;
        private const double MarkerInset = 0.1;
        private const int MaxMeasuredPixelSize = 4000;

        private double _widthMm;
        private double _heightMm;
        private ImageVector _topLeft;
        private ImageVector _right;
        private ImageVector _up;

        /// <summary>"model", or "sheet" for sheet mm.</summary>
        public string CoordinateSystem { get; private set; } = "model";

        public static ImageFrame? Of(View view)
        {
            if (view is ViewSheet)
            {
                var outline = view.Outline;
                return new ImageFrame
                {
                    CoordinateSystem = "sheet",
                    _widthMm = (outline.Max.U - outline.Min.U) * FeetToMm,
                    _heightMm = (outline.Max.V - outline.Min.V) * FeetToMm,
                    _topLeft = new ImageVector(outline.Min.U * FeetToMm, outline.Max.V * FeetToMm),
                    _right = new ImageVector(1, 0),
                    _up = new ImageVector(0, 1)
                };
            }

            if (view is View3D || !view.CropBoxActive) return null;
            var box = view.CropBox;
            var topLeft = box.Transform.OfPoint(new XYZ(box.Min.X, box.Max.Y, box.Max.Z));
            return new ImageFrame
            {
                _widthMm = (box.Max.X - box.Min.X) * FeetToMm,
                _heightMm = (box.Max.Y - box.Min.Y) * FeetToMm,
                _topLeft = new ImageVector(topLeft.X * FeetToMm, topLeft.Y * FeetToMm, topLeft.Z * FeetToMm),
                _right = new ImageVector(box.Transform.BasisX.X, box.Transform.BasisX.Y, box.Transform.BasisX.Z),
                _up = new ImageVector(box.Transform.BasisY.X, box.Transform.BasisY.Y, box.Transform.BasisY.Z)
            };
        }

        /// <summary>
        /// Draws a line across the crop (rolled back with the export's group), exports again and finds it by comparing
        /// the two images. A line inside the crop does not change what the image covers. Very large images are compared
        /// at a smaller size, since the two pictures are held in memory.
        /// </summary>
        public ViewImageTransform? Measure(Document doc, View view, ImageExportOptions options, string exported, string folder, string baseName)
        {
            int finalWidth, finalHeight;
            using (var stream = File.OpenRead(exported))
                if (!ViewImageTransform.TryReadPngSize(stream, out finalWidth, out finalHeight)) return null;

            var temporary = new List<string>();
            try
            {
                var comparePath = exported;
                if (options.PixelSize > MaxMeasuredPixelSize)
                {
                    options.PixelSize = MaxMeasuredPixelSize;
                    comparePath = ExportTo(doc, options, folder, baseName + "_base");
                    temporary.Add(comparePath);
                }

                using (var t = new Transaction(doc, "Temporary measuring line"))
                {
                    t.Start();
                    var line = Line.CreateBound(
                        PointAt(view, MarkerInset, MarkerInset),
                        PointAt(view, 1 - MarkerInset, 1 - MarkerInset));
                    var curve = doc.Create.NewDetailCurve(view, line);
                    view.SetElementOverrides(curve.Id, new OverrideGraphicSettings()
                        .SetProjectionLineColor(new Autodesk.Revit.DB.Color(255, 0, 255))
                        .SetProjectionLineWeight(1));
                    t.Commit();
                }

                var markedPath = ExportTo(doc, options, folder, baseName + "_marker");
                temporary.Add(markedPath);

                var (before, width, height) = ReadPixels(comparePath);
                var (after, afterWidth, afterHeight) = ReadPixels(markedPath);
                if (width != afterWidth || height != afterHeight) return null;
                if (ViewImageTransform.DifferenceBox(before, after, width, height) is not { } marker) return null;

                return ViewImageTransform.FromMarker(
                    finalWidth, finalHeight, _widthMm, _heightMm, _topLeft, _right, _up,
                    MarkerInset, marker, (double)finalWidth / width);
            }
            finally
            {
                foreach (var path in temporary)
                {
                    try { File.Delete(path); }
                    catch (IOException) { }
                }
            }
        }

        /// <summary>The point a share of the way across the crop from its top-left, on the view's plane.</summary>
        private XYZ PointAt(View view, double acrossShare, double downShare)
        {
            var across = _widthMm * acrossShare / FeetToMm;
            var down = _heightMm * downShare / FeetToMm;
            var point = new XYZ(
                _topLeft.X / FeetToMm + _right.X * across - _up.X * down,
                _topLeft.Y / FeetToMm + _right.Y * across - _up.Y * down,
                _topLeft.Z / FeetToMm + _right.Z * across - _up.Z * down);
            var normal = view.ViewDirection;
            return point - normal.Multiply(normal.DotProduct(point - view.Origin));
        }

        private static string ExportTo(Document doc, ImageExportOptions options, string folder, string fileBase)
        {
            options.FilePath = fileBase;
            doc.ExportImage(options);
            return Directory.GetFiles(folder, Path.GetFileName(fileBase) + "*.png")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault() ?? throw new IOException("Revit did not write the measuring image.");
        }

        private static (byte[] Pixels, int Width, int Height) ReadPixels(string path)
        {
            using var stream = File.OpenRead(path);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                decoder.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            var pixels = new byte[width * height * 4];
            converted.CopyPixels(pixels, width * 4, 0);
            return (pixels, width, height);
        }
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
        // Weight 10 smeared small symbols into red blobs at 1:100 (2026-10-25 check); 5 keeps them readable.
        var settings = new OverrideGraphicSettings()
            .SetProjectionLineColor(new Color(255, 0, 0))
            .SetProjectionLineWeight(5)
            .SetCutLineColor(new Color(255, 0, 0))
            .SetCutLineWeight(5);

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
