using RevitMCP.Addin.Documentation.Views;
using RevitMCP.Addin.FilledRegions;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#135: loop validation, hole nesting, chaining, padding and the projection onto a view's plane.</summary>
public class FilledRegionGeometryTests
{
    private static readonly LoopValidationOptions Strict = new();
    private static readonly LoopValidationOptions Disjoint = new() { AllowDisjoint = true };

    private static RegionLoop Loop(params (double U, double V)[] points)
    {
        var segments = new List<LoopSegment>();
        for (var i = 0; i < points.Length; i++)
        {
            var next = points[(i + 1) % points.Length];
            segments.Add(new LoopSegment(new Pt2(points[i].U, points[i].V), new Pt2(next.U, next.V)));
        }
        return new RegionLoop(segments);
    }

    private static RegionLoop Square(double min, double max) => Loop((min, min), (max, min), (max, max), (min, max));

    // A plan looks down: right = +X, up = +Y, view direction = +Z.
    private static readonly ViewFrame Plan = new(new Vec3(0, 0, 3000), new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1));

    // A section along y = 5000 looking north: right = +X, up = +Z, toward the viewer = -Y.
    private static readonly ViewFrame Section = new(new Vec3(0, 5000, 0), new Vec3(1, 0, 0), new Vec3(0, 0, 1), new Vec3(0, -1, 0));

    private static ShapePoint M(double x, double y, double? z = null, ShapePoint? arcMid = null) => new() { X = x, Y = y, Z = z, ArcMid = arcMid };

    // ── Validation ───────────────────────────────────────────────────────────

    [Fact]
    public void ASquareIsValidWithItsArea()
    {
        var result = LoopValidator.Validate(new[] { Square(0, 1000) }, Strict);
        Assert.True(result.IsValid, result.Reason);
        Assert.Equal(1_000_000, result.AreaMm2, 3);
        Assert.Equal(1, result.OutlineCount);
        Assert.Equal(0, result.HoleCount);
    }

    [Fact]
    public void NoLoopsIsReported() => Assert.Equal(RegionCodes.LoopEmpty, LoopValidator.Validate(Array.Empty<RegionLoop>(), Strict).Code);

    [Fact]
    public void AGapInALoopIsOpen()
    {
        var loop = new RegionLoop(new[]
        {
            new LoopSegment(new Pt2(0, 0), new Pt2(1000, 0)),
            new LoopSegment(new Pt2(1000, 0), new Pt2(1000, 1000)),
            new LoopSegment(new Pt2(1000, 1000), new Pt2(0, 10))
        });
        var result = LoopValidator.Validate(new[] { loop }, Strict);
        Assert.Equal(RegionCodes.LoopOpen, result.Code);
        Assert.Contains("10 mm gap", result.Reason);
    }

    [Fact]
    public void ABowTieIsSelfIntersecting_EvenThoughItsSignedAreaIsZero()
    {
        var result = LoopValidator.Validate(new[] { Loop((0, 0), (1000, 1000), (1000, 0), (0, 1000)) }, Strict);
        Assert.Equal(RegionCodes.LoopSelfIntersecting, result.Code);
    }

    [Fact]
    public void AnEdgeFoldingBackOnItselfIsSelfIntersecting()
    {
        var result = LoopValidator.Validate(new[] { Loop((0, 0), (1000, 0), (500, 0), (500, 500)) }, Strict);
        Assert.Equal(RegionCodes.LoopSelfIntersecting, result.Code);
    }

    [Fact]
    public void AVertexTouchingAnotherEdgeIsSelfIntersecting()
    {
        var result = LoopValidator.Validate(new[] { Loop((0, 0), (1000, 0), (1000, 1000), (500, 0), (0, 1000)) }, Strict);
        Assert.Equal(RegionCodes.LoopSelfIntersecting, result.Code);
    }

    [Fact]
    public void PointsOnOneLineHaveNoArea() =>
        Assert.Equal(RegionCodes.LoopDegenerate, LoopValidator.Validate(new[] { Loop((0, 0), (1000, 0), (2000, 0)) }, Strict).Code);

    [Fact]
    public void ASegmentBelowTheShortCurveToleranceIsReported()
    {
        var result = LoopValidator.Validate(new[] { Loop((0, 0), (1000, 0), (1000, 0.5), (0, 1000)) }, Strict);
        Assert.Equal(RegionCodes.LoopSegmentTooShort, result.Code);
    }

    [Fact]
    public void TwoPointsAreTooFew() =>
        Assert.Equal(RegionCodes.LoopTooFewPoints, LoopValidator.Validate(new[] { Loop((0, 0), (1000, 0)) }, Strict).Code);

    [Fact]
    public void AHoleInsideTheOutlineSubtractsItsArea_AndRunsTheOtherWay()
    {
        var result = LoopValidator.Validate(new[] { Square(0, 1000), Square(250, 750) }, Strict);
        Assert.True(result.IsValid, result.Reason);
        Assert.Equal(1, result.OutlineCount);
        Assert.Equal(1, result.HoleCount);
        Assert.Equal(750_000, result.AreaMm2, 3);
        Assert.True(RegionLoopMath.SignedArea(result.Loops[0].Polyline()) > 0);
        Assert.True(RegionLoopMath.SignedArea(result.Loops[1].Polyline()) < 0);
    }

    [Fact]
    public void TheOutlineIsFoundEvenWhenAHoleIsListedFirst_ForALooseSource()
    {
        var result = LoopValidator.Validate(new[] { Square(250, 750), Square(0, 1000) }, Disjoint);
        Assert.True(result.IsValid, result.Reason);
        Assert.Equal(new[] { 0, 1 }, result.Depths);
        Assert.Equal(1_000_000, Math.Abs(RegionLoopMath.SignedArea(result.Loops[0].Polyline())), 3);
    }

    [Fact]
    public void AHoleOutsideTheOutlineIsRefused_ForCoordinates_ButSeparateOutlinesAreFineForLooseSources()
    {
        var loops = new[] { Square(0, 1000), Square(2000, 3000) };
        Assert.Equal(RegionCodes.HoleOutsideOuter, LoopValidator.Validate(loops, Strict).Code);

        var loose = LoopValidator.Validate(loops, Disjoint);
        Assert.True(loose.IsValid, loose.Reason);
        Assert.Equal(2, loose.OutlineCount);
        Assert.Equal(2_000_000, loose.AreaMm2, 3);
    }

    [Fact]
    public void AnIslandInAHoleCountsAsArea()
    {
        var result = LoopValidator.Validate(new[] { Square(0, 1000), Square(100, 900), Square(400, 600) }, Strict);
        Assert.True(result.IsValid, result.Reason);
        Assert.Equal(new[] { 0, 1, 2 }, result.Depths);
        Assert.Equal(1_000_000 - 640_000 + 40_000, result.AreaMm2, 3);
    }

    [Fact]
    public void LoopsCrossingEachOtherAreReported()
    {
        var result = LoopValidator.Validate(new[] { Square(0, 1000), Square(500, 1500) }, Disjoint);
        Assert.Equal(RegionCodes.LoopsIntersect, result.Code);
    }

    [Fact]
    public void AHoleTouchingTheOutlineIsReported()
    {
        var result = LoopValidator.Validate(new[] { Square(0, 1000), Loop((0, 500), (500, 250), (500, 750)) }, Strict);
        Assert.Equal(RegionCodes.LoopsIntersect, result.Code);
    }

    [Fact]
    public void ACircleOfTwoArcsIsValid_WithTheAreaOfACircle()
    {
        var a = new Pt2(-500, 0);
        var b = new Pt2(500, 0);
        var loop = new RegionLoop(new[]
        {
            new LoopSegment(a, b, new Pt2(0, -500)),
            new LoopSegment(b, a, new Pt2(0, 500))
        });
        var result = LoopValidator.Validate(new[] { loop }, Strict);
        Assert.True(result.IsValid, result.Reason);
        Assert.InRange(result.AreaMm2, Math.PI * 500 * 500 * 0.985, Math.PI * 500 * 500);
    }

    [Fact]
    public void AnArcGoesThroughItsMidPoint_OnTheShortOrLongWayRound()
    {
        var points = RegionLoopMath.ArcInteriorPoints(new Pt2(100, 0), new Pt2(0, 100), new Pt2(-100, 0));
        Assert.All(points, p => Assert.True(p.V > 0));
        var other = RegionLoopMath.ArcInteriorPoints(new Pt2(100, 0), new Pt2(0, -100), new Pt2(-100, 0));
        Assert.All(other, p => Assert.True(p.V < 0));
        Assert.All(points, p => Assert.Equal(100, new Pt2(0, 0).DistanceTo(p), 6));
    }

    // ── Chaining loose lines ─────────────────────────────────────────────────

    [Fact]
    public void LooseLinesInAnyOrderAndDirectionChainIntoALoop()
    {
        var pieces = new[]
        {
            new LoopSegment(new Pt2(1000, 1000), new Pt2(1000, 0)),   // reversed
            new LoopSegment(new Pt2(0, 0), new Pt2(1000, 0.2)),       // a small gap at the corner
            new LoopSegment(new Pt2(0, 1000), new Pt2(0, 0)),
            new LoopSegment(new Pt2(1000, 1000), new Pt2(0, 1000))
        };
        var chained = LoopChainer.Chain(pieces, 0.5);
        Assert.Null(chained.Code);
        var loop = Assert.Single(chained.Loops);
        Assert.Equal(4, loop.Segments.Count);
        for (var i = 0; i < 4; i++)
            Assert.Equal(0, loop.Segments[i].End.DistanceTo(loop.Segments[(i + 1) % 4].Start), 9);
        Assert.True(LoopValidator.Validate(chained.Loops, Strict).IsValid);
    }

    [Fact]
    public void LooseLinesMakeSeveralLoops()
    {
        var pieces = new List<LoopSegment>();
        pieces.AddRange(Square(0, 1000).Segments);
        pieces.AddRange(Square(2000, 3000).Segments);
        Assert.Equal(2, LoopChainer.Chain(pieces, 0.5).Loops.Count);
    }

    [Fact]
    public void AChainThatDoesNotCloseIsOpen()
    {
        var chained = LoopChainer.Chain(new[]
        {
            new LoopSegment(new Pt2(0, 0), new Pt2(1000, 0)),
            new LoopSegment(new Pt2(1000, 0), new Pt2(1000, 1000))
        }, 0.5);
        Assert.Equal(RegionCodes.LoopOpen, chained.Code);
    }

    [Fact]
    public void ThreeLinesMeetingAtAPointAreAmbiguous()
    {
        var pieces = Square(0, 1000).Segments.ToList();
        pieces.Add(new LoopSegment(new Pt2(1000, 0), new Pt2(2000, 0)));
        Assert.Equal(RegionCodes.LoopBranching, LoopChainer.Chain(pieces, 0.5).Code);
    }

    [Fact]
    public void NoLinesIsEmpty() => Assert.Equal(RegionCodes.LoopEmpty, LoopChainer.Chain(Array.Empty<LoopSegment>(), 0.5).Code);

    // ── Padding ──────────────────────────────────────────────────────────────

    [Fact]
    public void PaddingGrowsTheOutlineAndShrinksTheHole()
    {
        var shape = new ShapeResult();
        shape.Loops.Add(Square(0, 1000));
        shape.Loops.Add(Square(250, 750));
        var finished = RegionShapeFinisher.Finish(shape, 100, 0.8);
        Assert.True(finished.IsValid, finished.Validation.Reason);
        Assert.Equal(1200 * 1200 - 300 * 300, finished.Validation.AreaMm2, 3);
    }

    [Fact]
    public void NegativePaddingShrinksTheOutlineAndGrowsTheHole()
    {
        var shape = new ShapeResult();
        shape.Loops.Add(Square(0, 1000));
        shape.Loops.Add(Square(400, 600));
        var finished = RegionShapeFinisher.Finish(shape, -50, 0.8);
        Assert.True(finished.IsValid, finished.Validation.Reason);
        Assert.Equal(900 * 900 - 300 * 300, finished.Validation.AreaMm2, 3);
    }

    [Fact]
    public void PaddingThatClosesAHoleDropsIt_WithAWarning()
    {
        var shape = new ShapeResult();
        shape.Loops.Add(Square(0, 1000));
        shape.Loops.Add(Square(450, 550));
        var finished = RegionShapeFinisher.Finish(shape, 60, 0.8);
        Assert.True(finished.IsValid, finished.Validation.Reason);
        Assert.Equal(0, finished.Validation.HoleCount);
        Assert.Contains(finished.Warnings, w => w.Contains("closes hole"));
    }

    [Fact]
    public void ShrinkingAnOutlineAwayIsAnError()
    {
        var shape = new ShapeResult();
        shape.Loops.Add(Square(0, 100));
        Assert.Equal(RegionCodes.PaddingCollapsed, RegionShapeFinisher.Finish(shape, -60, 0.8).Validation.Code);
    }

    [Fact]
    public void PaddingAnLShapeKeepsItsConcaveCornerMitred()
    {
        var shape = new ShapeResult();
        shape.Loops.Add(Loop((0, 0), (2000, 0), (2000, 1000), (1000, 1000), (1000, 2000), (0, 2000)));
        var finished = RegionShapeFinisher.Finish(shape, 100, 0.8);
        Assert.True(finished.IsValid, finished.Validation.Reason);
        var points = finished.Validation.Loops[0].Polyline();
        Assert.Contains(points, p => Math.Abs(p.U - 1100) < 1e-6 && Math.Abs(p.V - 1100) < 1e-6);
        Assert.Contains(points, p => Math.Abs(p.U + 100) < 1e-6 && Math.Abs(p.V + 100) < 1e-6);
    }

    [Fact]
    public void PaddingFlattensArcs_WithAWarning()
    {
        var shape = new ShapeResult();
        var a = new Pt2(-500, 0);
        var b = new Pt2(500, 0);
        shape.Loops.Add(new RegionLoop(new[] { new LoopSegment(a, b, new Pt2(0, -500)), new LoopSegment(b, a, new Pt2(0, 500)) }));
        var finished = RegionShapeFinisher.Finish(shape, 100, 0.8);
        Assert.True(finished.IsValid, finished.Validation.Reason);
        Assert.Contains(finished.Warnings, w => w.Contains("flattens"));
        Assert.InRange(finished.Validation.AreaMm2, Math.PI * 600 * 600 * 0.98, Math.PI * 600 * 600 * 1.02);
    }

    // ── View planes ──────────────────────────────────────────────────────────

    [Fact]
    public void UvOfAPointIsItsDotProductWithTheViewAxes_AndGoesBackOntoThePlane()
    {
        var model = new Vec3(1200, 7000, 2500);
        var uv = Section.ToUv(model);
        Assert.Equal(1200, uv.U, 9);
        Assert.Equal(2500, uv.V, 9);

        var back = Section.ToModel(uv);
        Assert.Equal(1200, back.X, 9);
        Assert.Equal(5000, back.Y, 9);   // on the cut plane, not at 7000
        Assert.Equal(2500, back.Z, 9);

        var onPlan = Plan.ToModel(Plan.ToUv(new Vec3(1, 2, 999)));
        Assert.Equal(3000, onPlan.Z, 9);
    }

    [Fact]
    public void ARotatedViewUsesItsOwnAxes()
    {
        var s = Math.Sqrt(0.5);
        var frame = new ViewFrame(Vec3.Zero, new Vec3(s, s, 0), new Vec3(-s, s, 0), new Vec3(0, 0, 1));
        var uv = frame.ToUv(new Vec3(1000, 1000, 0));
        Assert.Equal(1000 * 2 * s, uv.U, 6);
        Assert.Equal(0, uv.V, 6);
        var back = frame.ToModel(uv);
        Assert.Equal(1000, back.X, 6);
        Assert.Equal(1000, back.Y, 6);
    }

    private static RegionShapeSpec LoopsSpec(params List<ShapePoint>[] loops)
    {
        var spec = new RegionShapeSpec { Kind = RegionShapeKind.Loops };
        spec.Loops.AddRange(loops);
        return spec;
    }

    [Fact]
    public void ModelCoordinatesInAPlanDropZ_AndAMissingZIsFine()
    {
        var result = RegionShapeBuilder.FromLoops(LoopsSpec(new List<ShapePoint> { M(0, 0), M(1000, 0), M(1000, 1000), M(0, 0) }), Plan, 0.5);
        Assert.Null(result.Code);
        Assert.Equal(new Pt2(1000, 1000).U, result.Loops[0].Segments[2].Start.U, 9);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ModelCoordinatesInASectionKeepHeight_AndRequireZ()
    {
        var missing = RegionShapeBuilder.FromLoops(LoopsSpec(new List<ShapePoint> { M(0, 5000), M(1000, 5000), M(1000, 5000), M(0, 5000) }), Section, 0.5);
        Assert.Equal(RegionCodes.CoordinateZRequired, missing.Code);

        var result = RegionShapeBuilder.FromLoops(LoopsSpec(new List<ShapePoint>
        {
            M(0, 5000, 0), M(1000, 5000, 0), M(1000, 5000, 2000), M(0, 5000, 2000), M(0, 5000, 0)
        }), Section, 0.5);
        Assert.Null(result.Code);
        var finished = RegionShapeFinisher.Finish(result, 0, 0.8);
        Assert.True(finished.IsValid, finished.Validation.Reason);
        Assert.Equal(2_000_000, finished.Validation.AreaMm2, 3);
    }

    [Fact]
    public void PointsOffTheViewPlaneAreProjected_WithAWarning()
    {
        var result = RegionShapeBuilder.FromLoops(LoopsSpec(new List<ShapePoint>
        {
            M(0, 5000, 0), M(1000, 7000, 0), M(1000, 7000, 2000), M(0, 5000, 2000), M(0, 5000, 0)
        }), Section, 0.5);
        Assert.Null(result.Code);
        Assert.Contains(result.Warnings, w => w.Contains("projected"));
    }

    [Fact]
    public void AHorizontalLoopInASectionCollapsesToALine()
    {
        var result = RegionShapeBuilder.FromLoops(LoopsSpec(new List<ShapePoint>
        {
            M(0, 0, 0), M(1000, 0, 0), M(1000, 1000, 0), M(0, 1000, 0), M(0, 0, 0)
        }), Section, 0.5);
        Assert.Equal(RegionCodes.LoopDegenerate, RegionShapeFinisher.Finish(result, 0, 0.8).Validation.Code);
    }

    [Fact]
    public void ViewSpaceCoordinatesAreUsedAsTheyAre()
    {
        var spec = LoopsSpec(new List<ShapePoint>
        {
            new() { U = 0, V = 0 }, new() { U = 500, V = 0 }, new() { U = 500, V = 500 }, new() { U = 0, V = 0 }
        });
        spec.ViewSpace = true;
        var result = RegionShapeBuilder.FromLoops(spec, Section, 0.5);
        Assert.Null(result.Code);
        Assert.Equal(500, result.Loops[0].Segments[1].End.V, 9);
    }

    [Fact]
    public void AnUnclosedLoopIsOpenUnlessCloseLoopsIsAsked()
    {
        var points = new List<ShapePoint> { M(0, 0), M(1000, 0), M(1000, 1000), M(0, 1000) };
        var open = RegionShapeBuilder.FromLoops(LoopsSpec(points), Plan, 0.5);
        Assert.Equal(RegionCodes.LoopOpen, open.Code);
        Assert.Contains("closeLoops", open.Reason);

        var spec = LoopsSpec(points);
        spec.CloseLoops = true;
        var closed = RegionShapeBuilder.FromLoops(spec, Plan, 0.5);
        Assert.Null(closed.Code);
        Assert.Equal(4, closed.Loops[0].Segments.Count);
    }

    [Fact]
    public void AnArcMidTravelsWithItsPoint()
    {
        var result = RegionShapeBuilder.FromLoops(LoopsSpec(new List<ShapePoint>
        {
            M(-500, 0, null, M(0, -500)), M(500, 0, null, M(0, 500)), M(-500, 0)
        }), Plan, 0.5);
        Assert.Null(result.Code);
        Assert.Equal(2, result.Loops[0].Segments.Count);
        Assert.True(result.Loops[0].Segments.All(s => s.IsArc));
    }

    [Fact]
    public void ARectangleFollowsTheViewAxes_InAnyView()
    {
        var spec = new RegionShapeSpec { Kind = RegionShapeKind.Rectangle, From = M(0, 5000, 500), To = M(3000, 5000, 2500) };
        var finished = RegionShapeFinisher.Finish(RegionShapeBuilder.FromRectangle(spec, Section), 0, 0.8);
        Assert.True(finished.IsValid, finished.Validation.Reason);
        Assert.Equal(3000 * 2000, finished.Validation.AreaMm2, 3);

        var centered = new RegionShapeSpec { Kind = RegionShapeKind.Rectangle, Center = M(100, 100), WidthMm = 200, HeightMm = 400 };
        Assert.Equal(200 * 400, RegionShapeFinisher.Finish(RegionShapeBuilder.FromRectangle(centered, Plan), 0, 0.8).Validation.AreaMm2, 3);
    }

    [Fact]
    public void AHorizontalRoomBoundaryLiesInAPlan_ButNotInASection()
    {
        var boundary = new List<ModelSegment>
        {
            new(new Vec3(0, 0, 3000), new Vec3(4000, 0, 3000)),
            new(new Vec3(4000, 0, 3000), new Vec3(4000, 3000, 3000)),
            new(new Vec3(4000, 3000, 3000), new Vec3(0, 3000, 3000)),
            new(new Vec3(0, 3000, 3000), new Vec3(0, 0, 3000))
        };
        var inPlan = RegionShapeBuilder.FromModelLoops(new[] { (IReadOnlyList<ModelSegment>)boundary }, Plan, "Room 1");
        Assert.Null(inPlan.Code);
        Assert.Equal(12_000_000, RegionShapeFinisher.Finish(inPlan, 0, 0.8).Validation.AreaMm2, 3);

        var inSection = RegionShapeBuilder.FromModelLoops(new[] { (IReadOnlyList<ModelSegment>)boundary }, Section, "Room 1");
        Assert.Equal(RegionCodes.BoundaryNotInViewPlane, inSection.Code);
        Assert.Contains("Room 1", inSection.Reason);
    }

    [Fact]
    public void LinesDrawnInASectionLieInThatSection_ButNotInAPlan()
    {
        var pieces = new List<ModelSegment>
        {
            new(new Vec3(0, 5000, 0), new Vec3(1000, 5000, 0)),
            new(new Vec3(1000, 5000, 2000), new Vec3(1000, 5000, 0)),
            new(new Vec3(1000, 5000, 2000), new Vec3(0, 5000, 0))
        };
        var inSection = RegionShapeBuilder.FromLoosePieces(pieces, Section, 0.5, "The lines");
        Assert.Null(inSection.Code);
        Assert.Equal(1_000_000, RegionShapeFinisher.Finish(inSection, 0, 0.8).Validation.AreaMm2, 3);

        Assert.Equal(RegionCodes.BoundaryNotInViewPlane, RegionShapeBuilder.FromLoosePieces(pieces, Plan, 0.5, "The lines").Code);
    }

    [Fact]
    public void AnElementBoxIsTheBoundsOfItsCornersSeenInTheView()
    {
        var corners = new List<Vec3>();
        foreach (var x in new double[] { 0, 1000 })
        foreach (var y in new double[] { 5000, 6000 })
        foreach (var z in new double[] { 100, 300 })
            corners.Add(new Vec3(x, y, z));

        Assert.Equal(1_000_000, RegionShapeFinisher.Finish(RegionShapeBuilder.FromCorners(corners, Plan), 0, 0.8).Validation.AreaMm2, 3);
        Assert.Equal(200_000, RegionShapeFinisher.Finish(RegionShapeBuilder.FromCorners(corners, Section), 0, 0.8).Validation.AreaMm2, 3);
        Assert.Equal(RegionCodes.NoBoundingBox, RegionShapeBuilder.FromCorners(new List<Vec3>(), Plan).Code);
    }

    [Fact]
    public void DirectCoordinatesAreStrictAboutNesting_WhileRoomsMayBeLoose()
    {
        var strict = RegionShapeBuilder.FromLoops(LoopsSpec(
            new List<ShapePoint> { M(0, 0), M(1000, 0), M(1000, 1000), M(0, 0) },
            new List<ShapePoint> { M(2000, 0), M(3000, 0), M(3000, 1000), M(2000, 0) }), Plan, 0.5);
        Assert.Equal(RegionCodes.HoleOutsideOuter, RegionShapeFinisher.Finish(strict, 0, 0.8).Validation.Code);
    }

    [Fact]
    public void CloseGaps_ClosesARoomsSubMillimetreGap()
    {
        // Found live: room C1002 has a 0.5 mm gap where Revit left out a piece too short to be a curve.
        var loop = new RegionLoop(new[]
        {
            new LoopSegment(new Pt2(0, 0), new Pt2(1000, 0)),
            new LoopSegment(new Pt2(1000, 0.6), new Pt2(1000, 1000)),
            new LoopSegment(new Pt2(1000, 1000), new Pt2(0, 0))
        });

        loop.CloseGaps(2);

        Assert.Equal(0.6, loop.Segments[0].End.V, 6);
        Assert.Null(LoopValidator.Validate(new[] { loop }, new LoopValidationOptions()).Code);
    }

    [Fact]
    public void CloseGaps_LeavesARealGapOpen()
    {
        var loop = new RegionLoop(new[]
        {
            new LoopSegment(new Pt2(0, 0), new Pt2(1000, 0)),
            new LoopSegment(new Pt2(1000, 50), new Pt2(1000, 1000)),
            new LoopSegment(new Pt2(1000, 1000), new Pt2(0, 0))
        });

        loop.CloseGaps(2);

        Assert.Equal(RegionCodes.LoopOpen, LoopValidator.Validate(new[] { loop }, new LoopValidationOptions()).Code);
    }
}
