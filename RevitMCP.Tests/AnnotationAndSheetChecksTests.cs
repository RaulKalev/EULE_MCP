using System.IO;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Annotation;
using RevitMCP.Addin.CadManagement;
using RevitMCP.Addin.Documentation.Sheets;
using RevitMCP.Addin.Export;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#139 image transform, #140 lines and rectangles, #141 formatting and overlaps, #142 title blocks, #143 block extents.</summary>
public class AnnotationAndSheetChecksTests
{
    // ── #139 ──────────────────────────────────────────────────────────────

    [Fact]
    public void ImageTransform_TheVbhExport_MarksThePaddingAndMapsPixels()
    {
        // A 24000 x 26000 mm crop at pixelSize 2400 came back as 2400 x 3797 px with the lower part empty.
        var t = ViewImageTransform.Create(2400, 3797, 24000, 26000,
            new ImageVector(1000, 30000), new ImageVector(1, 0), new ImageVector(0, 1))!;

        Assert.Equal(10, t.MmPerPx, 6);
        Assert.Equal(2400, t.ContentBoxPx.Width);
        Assert.Equal(2600, t.ContentBoxPx.Height);

        var bottomRight = t.PixelToModel(2400, 2600);
        Assert.Equal(25000, bottomRight.X, 3);
        Assert.Equal(4000, bottomRight.Y, 3);
        var (x, y) = t.ModelToPixel(new ImageVector(1000 + 500, 30000 - 700));
        Assert.Equal(50, x, 6);
        Assert.Equal(70, y, 6);
    }

    [Fact]
    public void ImageTransform_RotatedView_UsesTheViewDirections()
    {
        // A plan turned 90 degrees: right on the image is model +Y, up is model -X.
        var t = ViewImageTransform.Create(1000, 1000, 1000, 1000,
            new ImageVector(0, 0), new ImageVector(0, 1), new ImageVector(-1, 0))!;

        var p = t.PixelToModel(100, 200);
        Assert.Equal(200, p.X, 6);
        Assert.Equal(100, p.Y, 6);
    }

    [Fact]
    public void ImageTransform_CropTallerThanTheImage_FitsByHeight()
    {
        var t = ViewImageTransform.Create(1000, 1000, 1000, 4000,
            new ImageVector(0, 0), new ImageVector(1, 0), new ImageVector(0, 1))!;

        Assert.Equal(4, t.MmPerPx, 6);
        Assert.Equal(250, t.ContentBoxPx.Width);
        Assert.Equal(1000, t.ContentBoxPx.Height);
    }

    [Fact]
    public void ImageTransform_NoSize_IsNull() =>
        Assert.Null(ViewImageTransform.Create(0, 100, 10, 10, default, new ImageVector(1, 0), new ImageVector(0, 1)));

    [Fact]
    public void PngSize_IsReadFromTheHeader()
    {
        var png = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 0x09, 0x60, 0, 0, 0x0E, 0xD5
        };
        Assert.True(ViewImageTransform.TryReadPngSize(new MemoryStream(png), out var w, out var h));
        Assert.Equal(2400, w);
        Assert.Equal(3797, h);

        Assert.False(ViewImageTransform.TryReadPngSize(new MemoryStream(new byte[24]), out _, out _));
        Assert.False(ViewImageTransform.TryReadPngSize(new MemoryStream(new byte[3]), out _, out _));
    }

    // ── #140 ──────────────────────────────────────────────────────────────

    [Fact]
    public void ParseEdits_ReadsLinesAndOptionalZ()
    {
        var edits = LineShapePlanner.ParseEdits(JArray.Parse("[{elementId:5,x1:0,y1:1,x2:2,y2:3},{elementId:6,x1:0,y1:0,x2:1,y2:0,z1:7,z2:8}]"), out var error)!;

        Assert.Null(error);
        Assert.Equal(5, edits[0].ElementId);
        Assert.Null(edits[0].Z1);
        Assert.Equal(8, edits[1].Z2);
    }

    [Theory]
    [InlineData("[{elementId:5,x1:0,y1:1,x2:2}]", "required")]
    [InlineData("[{x1:0,y1:1,x2:2,y2:3}]", "required")]
    [InlineData("[{elementId:5,x1:0,y1:1,x2:2,y2:3},{elementId:5,x1:0,y1:1,x2:2,y2:3}]", "twice")]
    [InlineData("[]", "Provide")]
    public void ParseEdits_RefusesWhatItCannotUse(string json, string expected)
    {
        Assert.Null(LineShapePlanner.ParseEdits(JArray.Parse(json), out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Rectangle_FromMinMax()
    {
        var plan = LineShapePlanner.PlanRectangle(0, 0, 4000, 2000, null, null, null, null, 0, out _)!;

        Assert.Equal(new[] { (0d, 0d), (4000d, 0d), (4000d, 2000d), (0d, 2000d) }, plan.Corners);
        Assert.Equal(2000, plan.CenterX);
        Assert.Equal(1000, plan.CenterY);
    }

    [Fact]
    public void Rectangle_FromCentre_Rotated()
    {
        var plan = LineShapePlanner.PlanRectangle(null, null, null, null, 100, 100, 40, 20, 90, out _)!;

        // Unrotated first corner (-20, -10) from the centre turns to (10, -20).
        Assert.Equal(110, plan.Corners[0].X, 6);
        Assert.Equal(80, plan.Corners[0].Y, 6);
    }

    [Theory]
    [InlineData(0d, 0d, 10d, null, null, null, null, null, 0d, "all required")]
    [InlineData(0d, 0d, 10d, 10d, 5d, 5d, 2d, 2d, 0d, "not both")]
    [InlineData(10d, 0d, 0d, 10d, null, null, null, null, 0d, "no size")]
    [InlineData(0d, 0d, 10d, 10d, null, null, null, null, 45d, "rotationDeg")]
    [InlineData(null, null, null, null, 1d, 1d, 2d, null, 0d, "all required")]
    [InlineData(null, null, null, null, null, null, null, null, 0d, "Give")]
    public void Rectangle_RefusesIncompleteInput(
        double? minX, double? minY, double? maxX, double? maxY, double? cx, double? cy, double? w, double? h, double rotation, string expected)
    {
        Assert.Null(LineShapePlanner.PlanRectangle(minX, minY, maxX, maxY, cx, cy, w, h, rotation, out var error));
        Assert.Contains(expected, error);
    }

    // ── #141 ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "keep")]
    [InlineData("", "keep")]
    [InlineData("PLAIN", "plain")]
    [InlineData("matchfirstrun", "matchFirstRun")]
    public void Formatting_IsNormalised(string? raw, string expected)
    {
        Assert.Equal(expected, TextNoteEditPlanner.ParseFormatting(raw, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Formatting_UnknownIsRefused()
    {
        Assert.Null(TextNoteEditPlanner.ParseFormatting("bold", out var error));
        Assert.Contains("bold", error);
    }

    [Fact]
    public void EditedRanges_ArePositionsInTheNewText()
    {
        var plan = new TextNoteEditPlan
        {
            Splices = new[] { new TextSplice(0, 3, "x"), new TextSplice(10, 2, "long text"), new TextSplice(20, 1, string.Empty) }
        };

        Assert.Equal(new[] { (0, 1), (8, 9) }, TextNoteEditPlanner.EditedRanges(plan));
    }

    [Theory]
    [InlineData("", 40, 2.5, 0)]
    [InlineData("short", 40, 2.5, 1)]
    [InlineData("one\rtwo\rthree", 40, 2.5, 3)]
    public void LineCount_CountsParagraphs(string text, double widthMm, double sizeMm, int expected) =>
        Assert.Equal(expected, TextNoteEditPlanner.EstimateLineCount(text, widthMm, sizeMm, 1.0));

    [Fact]
    public void LineCount_CountsWrapping()
    {
        // A line several times wider than the note takes that many rows.
        var text = new string('x', 100);
        var oneRow = TextNoteEditPlanner.EstimateWidthMm(text, 2.5, 1.0);
        Assert.Equal(3, TextNoteEditPlanner.EstimateLineCount(text, oneRow / 2.5, 2.5, 1.0));
    }

    private static OverlapItem Box(long id, double minX, double minY, double maxX, double maxY, string label = "") =>
        new() { Id = id, Label = label, MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY };

    [Fact]
    public void Overlap_TwoNotes_ReportsBothIdsAndTheOverlap()
    {
        var findings = AnnotationOverlapMath.Find(
            new[] { Box(1, 0, 0, 100, 20, "Heading"), Box(2, 50, 10, 150, 40, "Notes") }, [], [], [], 0.5);

        var f = Assert.Single(findings);
        Assert.Equal(1, f.TextNoteId);
        Assert.Equal(2, f.OtherId);
        Assert.Equal("textNote", f.OtherKind);
        Assert.Equal(50, f.OverlapXmm);
        Assert.Equal(10, f.OverlapYmm);
    }

    [Fact]
    public void Overlap_TouchingIsNotOverlapping()
    {
        Assert.Empty(AnnotationOverlapMath.Find(new[] { Box(1, 0, 0, 100, 20), Box(2, 100, 0, 200, 20) }, [], [], [], 0.5));
        Assert.Empty(AnnotationOverlapMath.Find(new[] { Box(1, 0, 0, 100, 20), Box(2, 99.8, 0, 200, 20) }, [], [], [], 0.5));
    }

    [Fact]
    public void Overlap_Tag()
    {
        var f = Assert.Single(AnnotationOverlapMath.Find(new[] { Box(1, 0, 0, 10, 10) }, new[] { Box(9, 5, 5, 20, 20) }, [], [], 0.5));
        Assert.Equal("tag", f.OtherKind);
    }

    [Fact]
    public void Overlap_Line_ReportsItsLengthInsideOnce()
    {
        // Two pieces of one line (an arc tessellated): one finding, the longer piece.
        var lines = new[]
        {
            new OverlapSegment { Id = 7, X1 = -10, Y1 = 5, X2 = 4, Y2 = 5 },
            new OverlapSegment { Id = 7, X1 = 4, Y1 = 5, X2 = 50, Y2 = 5 }
        };
        var f = Assert.Single(AnnotationOverlapMath.Find(new[] { Box(1, 0, 0, 10, 10) }, [], lines, [], 0.5));
        Assert.Equal("detailLine", f.OtherKind);
        Assert.True(f.OverlapXmm is > 5 and < 6.1, f.OverlapXmm.ToString());
    }

    [Fact]
    public void Overlap_LineOutsideOrAlongTheEdge_IsNotReported()
    {
        var lines = new[]
        {
            new OverlapSegment { Id = 7, X1 = -5, Y1 = 20, X2 = 50, Y2 = 20 },
            new OverlapSegment { Id = 8, X1 = 0, Y1 = 0, X2 = 10, Y2 = 0 }   // a box drawn around the note
        };
        Assert.Empty(AnnotationOverlapMath.Find(new[] { Box(1, 0, 0, 10, 10) }, [], lines, [], 0.5));
    }

    [Fact]
    public void Overlap_Viewport_OnlyWhenCrossingItsEdge()
    {
        var viewport = Box(5, 0, 0, 100, 100, "Plan");
        Assert.Empty(AnnotationOverlapMath.Find(new[] { Box(1, 10, 10, 20, 20) }, [], [], new[] { viewport }, 0.5));
        Assert.Empty(AnnotationOverlapMath.Find(new[] { Box(1, 200, 200, 220, 220) }, [], [], new[] { viewport }, 0.5));

        var f = Assert.Single(AnnotationOverlapMath.Find(new[] { Box(1, 90, 10, 120, 20) }, [], [], new[] { viewport }, 0.5));
        Assert.Equal("viewportEdge", f.OtherKind);
        Assert.Equal("Plan", f.OtherLabel);
        Assert.Equal(10, f.OverlapXmm);
    }

    // ── #142 ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(420, 297, "A3")]
    [InlineData(297, 420, "A3")]
    [InlineData(841, 594, "A1")]
    [InlineData(1189, 841, "A0")]
    [InlineData(211, 296, "A4")]
    [InlineData(891, 420, "A3x3")]
    [InlineData(1261, 297, "A4x6")]
    [InlineData(1300, 297, null)]
    [InlineData(500, 500, null)]
    public void IsoSize_IsMatched(double w, double h, string? expected) =>
        Assert.Equal(expected, TitleBlockChecks.MatchIsoSize(w, h));

    [Theory]
    [InlineData("A3", "A3")]
    [InlineData("a1", "A1")]
    [InlineData("Formaat A2", "A2")]
    [InlineData("A3 x 3", "A3x3")]
    [InlineData("A1 (841x594)", "A1")]
    [InlineData("594x841", "594x841")]
    [InlineData("A5475", null)]
    [InlineData("vt plaan", null)]
    [InlineData("", null)]
    public void WrittenFormat_IsRead(string written, string? expected) =>
        Assert.Equal(expected, TitleBlockChecks.ParseWrittenFormat(written));

    [Fact]
    public void Check_FormatAgainstTheRealSize()
    {
        var warnings = TitleBlockChecks.Check(new[]
        {
            new TitleBlockSheetValues { SheetNumber = "7-01", WidthMm = 841, HeightMm = 594, Format = "A1" },
            new TitleBlockSheetValues { SheetNumber = "7-02", WidthMm = 841, HeightMm = 594, Format = "A3" },
            new TitleBlockSheetValues { SheetNumber = "7-03", WidthMm = 841, HeightMm = 594, Format = "594x841" },
            new TitleBlockSheetValues { SheetNumber = "7-04", WidthMm = 841, HeightMm = 594, Format = "NR 12" }
        });

        Assert.Equal(2, warnings.Count);
        Assert.Equal("format_mismatch", warnings[0].Code);
        Assert.Equal("7-02", warnings[0].SheetNumbers.Single());
        Assert.Equal("format_unreadable", warnings[1].Code);
        Assert.Equal("7-04", warnings[1].SheetNumbers.Single());
    }

    [Fact]
    public void Check_DatesAndVersionsAcrossTheSet()
    {
        var warnings = TitleBlockChecks.Check(new[]
        {
            new TitleBlockSheetValues { SheetNumber = "7-01", Date = "01.11.2026", Version = "A" },
            new TitleBlockSheetValues { SheetNumber = "7-02", Date = "01.11.2026", Version = "a" },
            new TitleBlockSheetValues { SheetNumber = "7-03", Date = "28.10.2026" },
        });

        var w = Assert.Single(warnings);
        Assert.Equal("dates_differ", w.Code);
        Assert.Contains("7-03", w.Message);
    }

    [Fact]
    public void FindParameter_ByKnownNameOrTheCallersName()
    {
        var names = new[] { "Sheet Name", "FORMAAT", "Kuupäev", "Projekt" };

        Assert.Equal("FORMAAT", TitleBlockChecks.FindParameter(names, "format", null));
        Assert.Equal("Kuupäev", TitleBlockChecks.FindParameter(names, "date", null));
        Assert.Null(TitleBlockChecks.FindParameter(names, "version", null));
        Assert.Equal("Projekt", TitleBlockChecks.FindParameter(names, "version", "projekt"));
        Assert.Null(TitleBlockChecks.FindParameter(names, "format", "Missing"));
    }

    // ── #143 ──────────────────────────────────────────────────────────────

    [Fact]
    public void BlockExtents_UnrotatedBlock()
    {
        var points = new[] { (1000d, 2000d), (3000d, 2000d), (3000d, 3000d), (1000d, 3000d) };

        var e = CadBlockExtents.FromPoints(points, 1000, 2000, 1, 0)!;

        Assert.Equal(1000, e.MinX);
        Assert.Equal(3000, e.MaxX);
        Assert.Equal(2000, e.MinY);
        Assert.Equal(3000, e.MaxY);
        Assert.Equal(2000, e.Width, 6);
        Assert.Equal(1000, e.Depth, 6);
    }

    [Fact]
    public void BlockExtents_RotatedBlock_KeepsItsOwnSizeInWidthAndDepth()
    {
        // 2000 x 1000 turned 90 degrees: the host box is 1000 wide, the block is still 2000 by 1000.
        var points = new[] { (0d, 0d), (0d, 2000d), (-1000d, 2000d), (-1000d, 0d) };

        var e = CadBlockExtents.FromPoints(points, 0, 0, 0, 1)!;

        Assert.Equal(-1000, e.MinX);
        Assert.Equal(0, e.MaxX);
        Assert.Equal(2000, e.MaxY);
        Assert.Equal(2000, e.Width, 6);
        Assert.Equal(1000, e.Depth, 6);
    }

    [Fact]
    public void BlockExtents_NoLineWork_IsNull() =>
        Assert.Null(CadBlockExtents.FromPoints(Array.Empty<(double, double)>(), 0, 0, 1, 0));

    [Fact]
    public void Merge_KeepsTheBlocksExtents()
    {
        var extents = new CadBlockExtents();
        var merged = CadPointMath.Merge(new[]
        {
            new CadPoint { Layer = "L", Source = CadPointMath.SourceCircle },
            new CadPoint { Layer = "L", Source = CadPointMath.SourceBlock, Extents = extents }
        }, 1);

        Assert.Same(extents, Assert.Single(merged).Extents);
    }
}
