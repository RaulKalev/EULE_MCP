using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Annotation;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#161: arcs, circles and polylines as detail and model curves.</summary>
public class CurveShapePlannerTests
{
    private const double Short = 0.78;

    private static PlannedShape One(string json, bool detail = true) =>
        CurveShapePlanner.PlanOne(JObject.Parse(json), 1, detail, Short);

    [Fact]
    public void NinetyDegreeCenterArcHasTheRightEndpointsAndLength()
    {
        var arc = One("{\"shape\":\"arc\",\"center\":{\"x\":100,\"y\":50},\"radius\":200,\"startAngleDeg\":0,\"endAngleDeg\":90}");
        Assert.Null(arc.Error);
        var c = Assert.Single(arc.Curves);
        Assert.Equal("arc", c.Kind);
        Assert.Equal(90, c.SweepDeg, 6);
        Assert.Equal(300, c.Start.X, 6);
        Assert.Equal(50, c.Start.Y, 6);
        Assert.Equal(100, c.End.X, 6);
        Assert.Equal(250, c.End.Y, 6);
        Assert.Equal(Math.PI / 2 * 200, c.LengthMm, 6);
        Assert.Equal(100 + 200 * Math.Cos(Math.PI / 4), c.Mid.X, 6);
    }

    [Fact]
    public void EndAngleBelowStartWrapsCounterClockwise()
    {
        var arc = One("{\"shape\":\"arc\",\"center\":{\"x\":0,\"y\":0},\"radius\":100,\"startAngleDeg\":270,\"endAngleDeg\":90}");
        Assert.Equal(180, arc.Curves[0].SweepDeg, 6);
    }

    [Fact]
    public void NegativeSweepGoesClockwise()
    {
        var arc = One("{\"shape\":\"arc\",\"center\":{\"x\":0,\"y\":0},\"radius\":100,\"startAngleDeg\":0,\"sweepDeg\":-90}");
        Assert.Null(arc.Error);
        Assert.Equal(-90, arc.Curves[0].SweepDeg, 6);
        Assert.Equal(-100, arc.Curves[0].End.Y, 6);
    }

    [Fact]
    public void ThreePointArcFindsCentreRadiusAndNinetyDegreeSweep()
    {
        // Quarter circle of radius 100 about the origin, from (100,0) over 45 degrees to (0,100).
        var s = Math.Sqrt(0.5) * 100;
        var arc = One(FormattableString.Invariant($"{{\"shape\":\"arc\",\"start\":{{\"x\":100,\"y\":0}},\"through\":{{\"x\":{s},\"y\":{s}}},\"end\":{{\"x\":0,\"y\":100}}}}"));
        Assert.Null(arc.Error);
        var c = arc.Curves[0];
        Assert.Equal(0, c.Center.X, 6);
        Assert.Equal(0, c.Center.Y, 6);
        Assert.Equal(100, c.Radius, 6);
        Assert.Equal(90, c.SweepDeg, 6);
    }

    [Fact]
    public void ThreePointArcTheOtherWayRoundHasAClockwiseSweep()
    {
        // Same ends, through the far side of the circle: 270 degrees clockwise.
        var s = Math.Sqrt(0.5) * 100;
        var arc = One(FormattableString.Invariant($"{{\"shape\":\"arc\",\"start\":{{\"x\":100,\"y\":0}},\"through\":{{\"x\":{-s},\"y\":{-s}}},\"end\":{{\"x\":0,\"y\":100}}}}"));
        Assert.Null(arc.Error);
        Assert.Equal(-270, arc.Curves[0].SweepDeg, 6);
    }

    [Fact]
    public void CollinearThreePointArcIsRejected()
    {
        var arc = One("{\"shape\":\"arc\",\"start\":[0,0],\"through\":[100,100],\"end\":[200,200]}");
        Assert.Contains("collinear", arc.Error);
        Assert.Empty(arc.Curves);
    }

    [Fact]
    public void ThreePointArcWithRepeatedPointsIsRejected()
    {
        var arc = One("{\"shape\":\"arc\",\"start\":[0,0],\"through\":[0,0],\"end\":[200,200]}");
        Assert.NotNull(arc.Error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-50")]
    public void NonPositiveRadiusIsRejected(string radius)
    {
        Assert.Contains("radius", One("{\"shape\":\"circle\",\"center\":{\"x\":0,\"y\":0},\"radius\":" + radius + "}").Error);
        Assert.Contains("radius", One("{\"shape\":\"arc\",\"center\":{\"x\":0,\"y\":0},\"radius\":" + radius + ",\"startAngleDeg\":0,\"endAngleDeg\":90}").Error);
    }

    [Fact]
    public void ArcSweepOfZeroOrFullTurnIsRejected()
    {
        Assert.Contains("same angle", One("{\"shape\":\"arc\",\"center\":[0,0],\"radius\":100,\"startAngleDeg\":30,\"endAngleDeg\":30}").Error);
        Assert.Contains("circle", One("{\"shape\":\"arc\",\"center\":[0,0],\"radius\":100,\"startAngleDeg\":0,\"sweepDeg\":360}").Error);
        Assert.NotNull(One("{\"shape\":\"arc\",\"center\":[0,0],\"radius\":100,\"startAngleDeg\":0}").Error);
    }

    [Fact]
    public void TinyArcIsBelowTheShortCurveTolerance()
    {
        Assert.Contains("short", One("{\"shape\":\"arc\",\"center\":[0,0],\"radius\":100,\"startAngleDeg\":0,\"sweepDeg\":0.2}").Error);
    }

    [Fact]
    public void FullCircleIsOneCurveWithTheRightLength()
    {
        var circle = One("{\"shape\":\"circle\",\"center\":{\"x\":10,\"y\":20},\"radius\":150}");
        Assert.Null(circle.Error);
        var c = Assert.Single(circle.Curves);
        Assert.Equal("circle", c.Kind);
        Assert.Equal(360, c.SweepDeg);
        Assert.Equal(2 * Math.PI * 150, c.LengthMm, 6);
    }

    [Fact]
    public void ModelCirclesAndArcsTakeAPlane()
    {
        var xz = One("{\"shape\":\"circle\",\"center\":{\"x\":0,\"y\":0,\"z\":500},\"radius\":100,\"plane\":\"xz\"}", detail: false);
        Assert.Null(xz.Error);
        Assert.Equal(1, xz.Curves[0].YAxis.Z, 9);
        var yz = One("{\"shape\":\"arc\",\"center\":{\"x\":0,\"y\":0,\"z\":0},\"radius\":100,\"plane\":\"yz\",\"startAngleDeg\":0,\"sweepDeg\":90}", detail: false);
        Assert.Equal(100, yz.Curves[0].Start.Y, 6);
        Assert.Equal(100, yz.Curves[0].End.Z, 6);
        Assert.Contains("plane", One("{\"shape\":\"circle\",\"center\":[0,0],\"radius\":100,\"plane\":\"abc\"}", detail: false).Error);
    }

    [Fact]
    public void DetailCurvesRejectZAndPlane()
    {
        Assert.Contains("z", One("{\"shape\":\"line\",\"x1\":0,\"y1\":0,\"z1\":5,\"x2\":100,\"y2\":0}").Error);
        Assert.Contains("z", One("{\"shape\":\"arc\",\"start\":{\"x\":0,\"y\":0,\"z\":1},\"through\":[1,1],\"end\":[2,0]}").Error);
        Assert.Contains("plane", One("{\"shape\":\"circle\",\"center\":[0,0],\"radius\":100,\"plane\":\"xy\"}").Error);
        Assert.Contains("z", One("{\"shape\":\"polyline\",\"points\":[[0,0,3],[10,10]]}").Error);
    }

    [Fact]
    public void ModelLineKeepsItsZ()
    {
        var line = One("{\"x1\":0,\"y1\":0,\"z1\":100,\"x2\":1000,\"y2\":0,\"z2\":300}", detail: false);
        Assert.Equal("line", line.Shape);
        Assert.Equal(Math.Sqrt(1000 * 1000 + 200 * 200), line.Curves[0].LengthMm, 6);
    }

    [Fact]
    public void ShortSegmentsAreRejected()
    {
        Assert.Contains("short", One("{\"shape\":\"line\",\"x1\":0,\"y1\":0,\"x2\":0.5,\"y2\":0}").Error);
        var poly = One("{\"shape\":\"polyline\",\"points\":[[0,0],[100,0],[100.3,0]]}");
        Assert.Contains("Segment 2", poly.Error);
        Assert.Empty(poly.Curves);
    }

    [Fact]
    public void OpenPolylineIsAChainOfLines()
    {
        var poly = One("{\"shape\":\"polyline\",\"points\":[{\"x\":0,\"y\":0},{\"x\":100,\"y\":0},{\"x\":100,\"y\":100}]}");
        Assert.Null(poly.Error);
        Assert.Equal(2, poly.Curves.Count);
        Assert.False(poly.Closed);
        Assert.Equal(200, poly.TotalLengthMm, 6);
        Assert.Equal(poly.Curves[0].End.X, poly.Curves[1].Start.X);
    }

    [Fact]
    public void ClosedPolylineAddsTheClosingSegment()
    {
        var poly = One("{\"shape\":\"polyline\",\"closed\":true,\"points\":[[0,0],[100,0],[100,100],[0,100]]}");
        Assert.Null(poly.Error);
        Assert.Equal(4, poly.Curves.Count);
        Assert.Equal(0, poly.Curves[3].End.X);
        Assert.Equal(0, poly.Curves[3].End.Y);
        Assert.Equal(400, poly.TotalLengthMm, 6);
    }

    [Fact]
    public void PolylineNeedsEnoughPointsAndNoRepeatedClosingPoint()
    {
        Assert.NotNull(One("{\"shape\":\"polyline\",\"points\":[[0,0]]}").Error);
        Assert.Contains("3 points", One("{\"shape\":\"polyline\",\"closed\":true,\"points\":[[0,0],[10,0]]}").Error);
        Assert.Contains("repeats the first", One("{\"shape\":\"polyline\",\"closed\":true,\"points\":[[0,0],[100,0],[100,100],[0,0]]}").Error);
    }

    [Fact]
    public void ClosedModelPolylineMustBeFlatButAnOpenOneNeedNotBe()
    {
        var bent = "[[0,0,0],[1000,0,0],[1000,1000,0],[0,1000,300]]";
        Assert.Contains("off the plane", One("{\"shape\":\"polyline\",\"closed\":true,\"points\":" + bent + "}", detail: false).Error);
        Assert.Null(One("{\"shape\":\"polyline\",\"points\":" + bent + "}", detail: false).Error);
        var tilted = "[[0,0,0],[1000,0,0],[1000,1000,500],[0,1000,500]]";
        Assert.Null(One("{\"shape\":\"polyline\",\"closed\":true,\"points\":" + tilted + "}", detail: false).Error);
        Assert.Contains("collinear", One("{\"shape\":\"polyline\",\"closed\":true,\"points\":[[0,0,0],[100,0,0],[200,0,0]]}", detail: false).Error);
    }

    [Fact]
    public void BadNumbersAndUnknownShapesAreReportedPerEntry()
    {
        Assert.Contains("number", One("{\"shape\":\"line\",\"x1\":\"abc\",\"y1\":0,\"x2\":10,\"y2\":0}").Error);
        Assert.Contains("finite", One("{\"shape\":\"circle\",\"center\":[0,0],\"radius\":\"Infinity\"}").Error);
        Assert.Contains("Unknown shape", One("{\"shape\":\"spline\"}").Error);
        Assert.Contains("object", CurveShapePlanner.PlanOne(null, 3, true, Short).Error);
        Assert.Contains("not both", One("{\"shape\":\"arc\",\"center\":[0,0],\"radius\":5,\"start\":[0,0],\"through\":[1,1],\"end\":[2,0],\"startAngleDeg\":0}").Error);
    }

    [Fact]
    public void ShapeDefaultsFromTheFieldsGiven()
    {
        Assert.Equal("line", One("{\"x1\":0,\"y1\":0,\"x2\":100,\"y2\":0}").Shape);
        Assert.Equal("polyline", One("{\"points\":[[0,0],[10,10]]}").Shape);
    }

    [Fact]
    public void OneBadEntryDoesNotSpoilTheOthers()
    {
        var curves = JArray.Parse("[{\"shape\":\"circle\",\"center\":[0,0],\"radius\":100},{\"shape\":\"circle\",\"center\":[0,0],\"radius\":-1}," +
                                  "{\"shape\":\"line\",\"x1\":0,\"y1\":0,\"x2\":10,\"y2\":0,\"lineStyle\":\"Thin Lines\"}]");
        var shapes = CurveShapePlanner.Plan(curves, true, Short, out var error)!;
        Assert.Null(error);
        Assert.Null(shapes[0].Error);
        Assert.NotNull(shapes[1].Error);
        Assert.Null(shapes[2].Error);
        Assert.Equal("Thin Lines", shapes[2].LineStyle);
        Assert.Equal(new[] { 1, 2, 3 }, shapes.Select(s => s.Index));
    }

    [Fact]
    public void LimitsAndEmptyInputAreRefused()
    {
        Assert.Null(CurveShapePlanner.Plan(null, true, Short, out var e1));
        Assert.NotNull(e1);
        Assert.Null(CurveShapePlanner.Plan(new JArray(), true, Short, out var e2));
        Assert.NotNull(e2);
        var many = new JArray(Enumerable.Range(0, CurveShapePlanner.MaxShapes + 1)
            .Select(_ => JObject.Parse("{\"shape\":\"circle\",\"center\":[0,0],\"radius\":10}")));
        Assert.Null(CurveShapePlanner.Plan(many, true, Short, out var e3));
        Assert.Contains("limit", e3);
    }

    // ── Surface ──

    [Theory]
    [InlineData("revit_preview_create_curves", "ReadOnly")]
    [InlineData("revit_create_curves", "RequiresApproval")]
    public void ToolsAreInTheSameGroupAsTheOtherLineToolsWithTheRightPermissions(string tool, string permission)
    {
        Assert.Equal(BridgeToolGroups.GroupOf("revit_create_rectangle"), BridgeToolGroups.GroupOf(tool));
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
    }

    [Fact]
    public void TheCreatingToolIsClassifiedAsCreates()
    {
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_create_curves"));
    }

    [Fact]
    public void RevitCreateLinesKeepsItsContract()
    {
        var tool = ToolSourceScan.Addin.Value["revit_create_lines"];
        Assert.Equal("RequiresApproval", tool.Permission);
        Assert.Contains("lines", tool.Source);
        Assert.DoesNotContain("curves", tool.Source);
    }
}
