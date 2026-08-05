using RevitMCP.Addin.CadManagement;
using Xunit;

namespace RevitMCP.Tests;

public class CadShapeMathTests
{
    private const string Layer = "New_Valgustid_SA";

    private static CadSegment Segment(
        double x1, double y1, double x2, double y2,
        string layer = Layer, bool fromArc = false, double z = 0) =>
        new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Layer = layer, FromArc = fromArc, Zmm = z };

    /// <summary>The four sides of a rectangle centred on the origin, then rotated and moved.</summary>
    private static List<CadSegment> Rectangle(
        double centerX, double centerY, double length, double width, double angleDegrees,
        string layer = Layer, double z = 0)
    {
        var halfLength = length / 2.0;
        var halfWidth = width / 2.0;
        var corners = new[]
        {
            (-halfLength, -halfWidth), (halfLength, -halfWidth),
            (halfLength, halfWidth), (-halfLength, halfWidth)
        };

        var radians = angleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        var placed = corners
            .Select(c => (X: centerX + c.Item1 * cos - c.Item2 * sin,
                          Y: centerY + c.Item1 * sin + c.Item2 * cos))
            .ToList();

        var segments = new List<CadSegment>();
        for (var i = 0; i < placed.Count; i++)
        {
            var a = placed[i];
            var b = placed[(i + 1) % placed.Count];
            segments.Add(Segment(a.X, a.Y, b.X, b.Y, layer, z: z));
        }
        return segments;
    }

    /// <summary>A circle as the arc pieces Revit tessellates a DWG circle into.</summary>
    private static List<CadSegment> Circle(
        double centerX, double centerY, double diameter, int pieces = 24, string layer = Layer)
    {
        var radius = diameter / 2.0;
        var segments = new List<CadSegment>();

        for (var i = 0; i < pieces; i++)
        {
            var a = 2 * Math.PI * i / pieces;
            var b = 2 * Math.PI * (i + 1) / pieces;
            segments.Add(Segment(
                centerX + radius * Math.Cos(a), centerY + radius * Math.Sin(a),
                centerX + radius * Math.Cos(b), centerY + radius * Math.Sin(b),
                layer, fromArc: true));
        }
        return segments;
    }

    // ── Reassembling fixtures from loose lines ───────────────────────────────

    [Fact]
    public void Cluster_FourTouchingLines_BecomeOneFixture()
    {
        var shapes = CadShapeMath.Cluster(Rectangle(0, 0, 1200, 200, 0));

        Assert.Single(shapes);
        Assert.Equal(4, shapes[0].SegmentCount);
    }

    [Fact]
    public void Cluster_SeparateSymbols_StayApart()
    {
        var segments = Rectangle(0, 0, 1200, 200, 0)
            .Concat(Rectangle(5000, 0, 1200, 200, 0))
            .Concat(Rectangle(0, 5000, 1200, 200, 0))
            .ToList();

        Assert.Equal(3, CadShapeMath.Cluster(segments).Count);
    }

    [Fact]
    public void Cluster_NeverJoinsAcrossLayers()
    {
        // Two rectangles drawn on top of each other on different layers are two fixtures.
        var segments = Rectangle(0, 0, 600, 600, 0, layer: "Valgustid")
            .Concat(Rectangle(0, 0, 600, 600, 0, layer: "Pistikud"))
            .ToList();

        Assert.Equal(2, CadShapeMath.Cluster(segments).Count);
    }

    [Fact]
    public void Cluster_CentreIsTheMiddleOfTheDrawnShape()
    {
        var shapes = CadShapeMath.Cluster(Rectangle(3500, -1200, 1200, 200, 0));

        Assert.Equal(3500, shapes[0].CenterX, 3);
        Assert.Equal(-1200, shapes[0].CenterY, 3);
    }

    [Fact]
    public void Cluster_MeasuresLongSideAndShortSide()
    {
        var shapes = CadShapeMath.Cluster(Rectangle(0, 0, 1200, 200, 0));

        Assert.Equal(1200, shapes[0].LengthMm, 3);
        Assert.Equal(200, shapes[0].WidthMm, 3);
    }

    [Fact]
    public void Cluster_SizeIsTheSameWhateverAngleItWasDrawnAt()
    {
        // The whole point of the minimum-area box: a luminaire turned 37 degrees is still 1200x200.
        var shapes = CadShapeMath.Cluster(Rectangle(0, 0, 1200, 200, 37));

        Assert.Equal(1200, shapes[0].LengthMm, 3);
        Assert.Equal(200, shapes[0].WidthMm, 3);
    }

    [Fact]
    public void Cluster_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(CadShapeMath.Cluster(Array.Empty<CadSegment>()));
    }

    [Fact]
    public void Cluster_CarriesTheDrawingHeightThrough()
    {
        var shapes = CadShapeMath.Cluster(Rectangle(0, 0, 1200, 200, 0, z: 2500));
        Assert.Equal(2500, shapes[0].Zmm, 3);
    }

    [Fact]
    public void Cluster_KeepsTheLayerTheFixtureWasDrawnOn()
    {
        var shapes = CadShapeMath.Cluster(Rectangle(0, 0, 1200, 200, 0, layer: "Valgustid"));
        Assert.Equal("Valgustid", shapes[0].Layer);
    }

    // ── Rotation ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(135)]
    public void Cluster_ReadsTheAngleTheFixtureWasDrawnAt(double drawn)
    {
        var shapes = CadShapeMath.Cluster(Rectangle(0, 0, 1200, 200, drawn));

        Assert.Equal(CadShapeMath.NormalizeHalfTurn(drawn), shapes[0].RotationDegrees, 3);
    }

    [Fact]
    public void Cluster_HalfATurnIsTheSameFixture()
    {
        // A drawn rectangle is symmetric: 190 degrees and 10 degrees look identical on the plan.
        var ten = CadShapeMath.Cluster(Rectangle(0, 0, 1200, 200, 10))[0];
        var oneNinety = CadShapeMath.Cluster(Rectangle(0, 0, 1200, 200, 190))[0];

        Assert.Equal(ten.RotationDegrees, oneNinety.RotationDegrees, 3);
    }

    [Fact]
    public void Cluster_ACircleReportsNoRotation()
    {
        // A round downlight has no orientation; reporting the tessellation's angle would spin them.
        var shapes = CadShapeMath.Cluster(Circle(0, 0, 200));
        Assert.Equal(0.0, shapes[0].RotationDegrees);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(180, 0)]
    [InlineData(190, 10)]
    [InlineData(-10, 170)]
    [InlineData(360, 0)]
    public void NormalizeHalfTurn_WrapsIntoHalfATurn(double input, double expected)
    {
        Assert.Equal(expected, CadShapeMath.NormalizeHalfTurn(input), 6);
    }

    // ── Telling one kind of symbol from another ──────────────────────────────

    [Fact]
    public void Cluster_ARectangleIsClassifiedAsOne()
    {
        Assert.Equal(CadShapeMath.KindRectangle, CadShapeMath.Cluster(Rectangle(0, 0, 1200, 200, 0))[0].Kind);
    }

    [Fact]
    public void Cluster_ACircleIsClassifiedAsOne()
    {
        Assert.Equal(CadShapeMath.KindCircle, CadShapeMath.Cluster(Circle(0, 0, 200))[0].Kind);
    }

    [Fact]
    public void Cluster_ASingleLineIsClassifiedAsOne()
    {
        var shapes = CadShapeMath.Cluster(new[] { Segment(0, 0, 1000, 0) });

        Assert.Equal(CadShapeMath.KindLine, shapes[0].Kind);
        Assert.Equal(1000, shapes[0].LengthMm, 3);
    }

    [Fact]
    public void Cluster_ACircleMeasuresItsDiameter()
    {
        var shapes = CadShapeMath.Cluster(Circle(0, 0, 200, pieces: 64));

        // A tessellated circle is a polygon just inside the true circle, so allow a little slack.
        Assert.Equal(200, shapes[0].LengthMm, 0);
    }

    // ── Signatures ───────────────────────────────────────────────────────────

    [Fact]
    public void Cluster_SameSizedFixtures_ShareASignature()
    {
        var segments = Rectangle(0, 0, 1200, 200, 0)
            .Concat(Rectangle(5000, 0, 1200, 200, 90))
            .ToList();

        var shapes = CadShapeMath.Cluster(segments);
        Assert.Equal(shapes[0].Signature, shapes[1].Signature);
    }

    [Fact]
    public void Cluster_DifferentSizedFixtures_GetDifferentSignatures()
    {
        var segments = Rectangle(0, 0, 1200, 200, 0)
            .Concat(Rectangle(5000, 0, 600, 600, 0))
            .ToList();

        var shapes = CadShapeMath.Cluster(segments);
        Assert.NotEqual(shapes[0].Signature, shapes[1].Signature);
    }

    [Fact]
    public void Cluster_NearlyIdenticalFixtures_BucketToOneSignature()
    {
        // Drawn symbols never measure exactly alike; without bucketing every fixture would be unique.
        var segments = Rectangle(0, 0, 1198, 202, 0)
            .Concat(Rectangle(5000, 0, 1203, 197, 0))
            .ToList();

        var shapes = CadShapeMath.Cluster(segments, signatureBucketMm: 10);
        Assert.Equal(shapes[0].Signature, shapes[1].Signature);
    }

    [Fact]
    public void Cluster_SignatureNamesTheSize()
    {
        var shapes = CadShapeMath.Cluster(Rectangle(0, 0, 1200, 200, 0));
        Assert.Equal("rectangle 1200x200", shapes[0].Signature);
    }

    [Fact]
    public void SummariseSignatures_CountsFixturesPerSignature()
    {
        var segments = Rectangle(0, 0, 1200, 200, 0)
            .Concat(Rectangle(5000, 0, 1200, 200, 0))
            .Concat(Rectangle(10000, 0, 600, 600, 0))
            .ToList();

        var summary = CadShapeMath.SummariseSignatures(CadShapeMath.Cluster(segments));

        Assert.Equal(2, summary.Count);
        Assert.Equal(2, summary[0].Count);
        Assert.Equal(1, summary[1].Count);
    }

    // ── Guarding against drawing line work ───────────────────────────────────

    [Fact]
    public void Cluster_ALineTouchingASymbol_DragsItIntoOneOversizeCluster()
    {
        // The failure this guard exists for: a wall line that happens to end on a luminaire corner.
        var segments = Rectangle(0, 0, 1200, 200, 0);
        segments.Add(Segment(600, 100, 40000, 100));

        var shapes = CadShapeMath.Cluster(segments, maxShapeSizeMm: 3000);

        Assert.Single(shapes);
        Assert.True(shapes[0].Oversize);
    }

    [Fact]
    public void Cluster_NormalFixtures_AreNotFlaggedOversize()
    {
        Assert.All(
            CadShapeMath.Cluster(Rectangle(0, 0, 1200, 200, 0), maxShapeSizeMm: 3000),
            s => Assert.False(s.Oversize));
    }

    [Fact]
    public void Cluster_ZeroMaxSize_TurnsTheGuardOff()
    {
        var segments = Rectangle(0, 0, 1200, 200, 0);
        segments.Add(Segment(600, 100, 40000, 100));

        Assert.All(CadShapeMath.Cluster(segments, maxShapeSizeMm: 0), s => Assert.False(s.Oversize));
    }

    // ── Join tolerance ───────────────────────────────────────────────────────

    [Fact]
    public void Cluster_CornersThatMissByLessThanTheTolerance_StillJoin()
    {
        // Hand-drawn CAD rarely closes exactly; a 0.5 mm gap is still one rectangle.
        var segments = new[]
        {
            Segment(0, 0, 1200, 0),
            Segment(1200.4, 0, 1200.4, 200),
            Segment(1200, 200.3, 0, 200.3),
            Segment(0, 200, 0, 0)
        };

        Assert.Single(CadShapeMath.Cluster(segments, joinToleranceMm: 2));
    }

    [Fact]
    public void Cluster_ATighterToleranceLeavesThoseCornersApart()
    {
        var segments = new[]
        {
            Segment(0, 0, 1200, 0),
            Segment(1200.4, 0, 1200.4, 200),
            Segment(1200, 200.3, 0, 200.3),
            Segment(0, 200, 0, 0)
        };

        Assert.True(CadShapeMath.Cluster(segments, joinToleranceMm: 0.1).Count > 1);
    }

    [Fact]
    public void Cluster_ZeroTolerance_LeavesEverySegmentOnItsOwn()
    {
        Assert.Equal(4, CadShapeMath.Cluster(Rectangle(0, 0, 1200, 200, 0), joinToleranceMm: 0).Count);
    }

    // ── The box fitting itself ───────────────────────────────────────────────

    [Fact]
    public void MinAreaBox_FitsATiltedSquareTightly()
    {
        // A square turned 45 degrees: the axis-aligned box would be 1.41x too big in each direction.
        var points = new[] { (0.0, 100.0), (100.0, 0.0), (0.0, -100.0), (-100.0, 0.0) };

        var box = CadShapeMath.MinAreaBox(points);

        Assert.NotNull(box);
        Assert.Equal(141.42, box!.Value.LongSide, 1);
        Assert.Equal(141.42, box.Value.ShortSide, 1);
        Assert.Equal(0.0, box.Value.CenterX, 6);
        Assert.Equal(0.0, box.Value.CenterY, 6);
    }

    [Fact]
    public void MinAreaBox_CollinearPoints_HaveLengthButNoWidth()
    {
        var box = CadShapeMath.MinAreaBox(new[] { (0.0, 0.0), (500.0, 0.0), (1000.0, 0.0) });

        Assert.NotNull(box);
        Assert.Equal(1000, box!.Value.LongSide, 6);
        Assert.Equal(0, box.Value.ShortSide, 6);
    }

    [Fact]
    public void MinAreaBox_ASinglePoint_HasNoSize()
    {
        var box = CadShapeMath.MinAreaBox(new[] { (42.0, 7.0) });

        Assert.NotNull(box);
        Assert.Equal(0, box!.Value.LongSide, 6);
        Assert.Equal(42.0, box.Value.CenterX, 6);
    }

    [Fact]
    public void MinAreaBox_NoPoints_IsNull()
    {
        Assert.Null(CadShapeMath.MinAreaBox(Array.Empty<(double, double)>()));
    }

    [Fact]
    public void ConvexHull_DropsPointsInsideTheOutline()
    {
        var points = new[]
        {
            (0.0, 0.0), (100.0, 0.0), (100.0, 100.0), (0.0, 100.0),
            (50.0, 50.0) // inside
        };

        Assert.Equal(4, CadShapeMath.ConvexHull(points).Count);
    }

    [Fact]
    public void ConvexHull_DropsPointsSittingOnAnEdge()
    {
        var points = new[]
        {
            (0.0, 0.0), (50.0, 0.0), (100.0, 0.0), (100.0, 100.0), (0.0, 100.0)
        };

        Assert.Equal(4, CadShapeMath.ConvexHull(points).Count);
    }

    [Theory]
    [InlineData("rectangle")]
    [InlineData("circle")]
    [InlineData("line")]
    [InlineData("other")]
    public void IsKnownKind_AcceptsTheDocumentedKinds(string kind)
    {
        Assert.True(CadShapeMath.IsKnownKind(kind));
    }

    [Theory]
    [InlineData("Rectangle")]
    [InlineData("block")]
    [InlineData("")]
    public void IsKnownKind_RejectsAnythingElse(string kind)
    {
        Assert.False(CadShapeMath.IsKnownKind(kind));
    }
}
