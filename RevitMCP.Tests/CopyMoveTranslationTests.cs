using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Placement;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// #92: moving by a displacement, along model or view axes, including elements without an insertion
/// point. #91: reading a copy request and pairing each source with its copy.
/// </summary>
public class CopyMoveTranslationTests
{
    private const double Tolerance = MoveElementsMath.DefaultPositionToleranceMm;

    /// <summary>A floor plan: right = model X, up = model Y, looking down the Z axis.</summary>
    private static readonly ViewFrame Plan = new(new PointMm(1, 0, 0), new PointMm(0, 1, 0), new PointMm(0, 0, 1));

    /// <summary>A section looking north: right = model X, up = model Z, normal = -Y.</summary>
    private static readonly ViewFrame Section = new(new PointMm(1, 0, 0), new PointMm(0, 0, 1), new PointMm(0, -1, 0));

    private static MoveRequest Delta(double? dx = null, double? dy = null, double? dz = null,
        double? right = null, double? up = null, double? expectedX = null) => new()
    {
        ElementId = 42,
        DeltaXMm = dx,
        DeltaYMm = dy,
        DeltaZMm = dz,
        DeltaRightMm = right,
        DeltaUpMm = up,
        ExpectedXMm = expectedX
    };

    // ── Displacement along model axes ────────────────────────────────────────

    [Fact]
    public void ModelDelta_IsTheTranslation_AndAnOmittedAxisIsZero()
    {
        var translation = MoveElementsMath.TranslationFromDelta(Delta(dx: 500, dz: -20), null, out var error);

        Assert.Null(error);
        Assert.Equal(500, translation!.Value.X);
        Assert.Equal(0, translation.Value.Y);
        Assert.Equal(-20, translation.Value.Z);
    }

    // ── Displacement along view axes ─────────────────────────────────────────

    [Fact]
    public void ViewDelta_InAPlan_IsModelXAndY()
    {
        var translation = MoveElementsMath.TranslationFromDelta(Delta(right: 300, up: 100), Plan, out _)!.Value;

        Assert.Equal(300, translation.X, 9);
        Assert.Equal(100, translation.Y, 9);
        Assert.Equal(0, translation.Z, 9);
    }

    [Fact]
    public void ViewDelta_InASection_UpIsModelZ()
    {
        var translation = MoveElementsMath.TranslationFromDelta(Delta(right: 300, up: 100), Section, out _)!.Value;

        Assert.Equal(300, translation.X, 9);
        Assert.Equal(0, translation.Y, 9);
        Assert.Equal(100, translation.Z, 9);
        Assert.Equal(0, MoveElementsMath.OutOfPlaneMm(translation, Section), 9);
    }

    [Fact]
    public void ViewDelta_FollowsARotatedView()
    {
        // A section cut at 45 degrees: right runs diagonally across the plan.
        var h = Math.Sqrt(0.5);
        var rotated = new ViewFrame(new PointMm(h, h, 0), new PointMm(0, 0, 1), new PointMm(h, -h, 0));

        var translation = MoveElementsMath.TranslationFromDelta(Delta(right: 1000), rotated, out _)!.Value;

        Assert.Equal(707.1068, translation.X, 3);
        Assert.Equal(707.1068, translation.Y, 3);
        Assert.Equal(1000, translation.Length, 6);
    }

    [Fact]
    public void ViewDelta_WithoutAView_IsAnError_NotAGuess()
    {
        var translation = MoveElementsMath.TranslationFromDelta(Delta(right: 300), null, out var error);

        Assert.Null(translation);
        Assert.Contains("viewId", error);
    }

    // ── The view plane ───────────────────────────────────────────────────────

    [Fact]
    public void OutOfPlane_IsTheComponentAlongTheViewNormal()
    {
        // In a plan a Z move leaves the plane; in a north-facing section a Y move does.
        Assert.Equal(50, MoveElementsMath.OutOfPlaneMm(new PointMm(100, 100, 50), Plan), 9);
        Assert.Equal(0, MoveElementsMath.OutOfPlaneMm(new PointMm(100, 100, 0), Plan), 9);
        Assert.Equal(100, MoveElementsMath.OutOfPlaneMm(new PointMm(100, 100, 50), Section), 9);
        Assert.Equal(0, MoveElementsMath.OutOfPlaneMm(new PointMm(100, 0, 50), Section), 9);
    }

    // ── Planning a displacement ──────────────────────────────────────────────

    [Fact]
    public void Translation_OfAPointElement_ReportsWhereItLands()
    {
        var plan = MoveElementsMath.BuildTranslation(
            Delta(dx: 500), new PointMm(1000, 2000, 0), new PointMm(500, 0, 0), false, true, Tolerance);

        Assert.Equal(MoveStatus.Ready, plan.Status);
        Assert.Equal(MoveMode.Translation, plan.Mode);
        Assert.True(plan.CanMove);
        Assert.Equal(1500, plan.TargetPointMm!.Value.X);
        Assert.Equal(500, plan.DistanceMm);
    }

    [Fact]
    public void Translation_WorksWithoutAnInsertionPoint()
    {
        // A detail line or a wall: no LocationPoint, so no current or target point — but it can move.
        var plan = MoveElementsMath.BuildTranslation(Delta(dx: 500), null, new PointMm(500, 0, 0), false, true, Tolerance);

        Assert.Equal(MoveStatus.Ready, plan.Status);
        Assert.True(plan.CanMove);
        Assert.Null(plan.CurrentPointMm);
        Assert.Null(plan.TargetPointMm);
        Assert.Equal(500, plan.TranslationMm!.Value.X);
    }

    [Fact]
    public void Translation_WithExpectedCoordinates_NeedsAnInsertionPointToCompare()
    {
        var plan = MoveElementsMath.BuildTranslation(
            Delta(dx: 500, expectedX: 1000), null, new PointMm(500, 0, 0), false, true, Tolerance);

        Assert.Equal(MoveStatus.UnsupportedLocation, plan.Status);
        Assert.True(plan.IsFailure);
        Assert.False(plan.CanMove);
    }

    [Fact]
    public void Translation_HonoursTheStalenessCheck()
    {
        var plan = MoveElementsMath.BuildTranslation(
            Delta(dx: 500, expectedX: 1000), new PointMm(1010, 0, 0), new PointMm(500, 0, 0), false, true, Tolerance);

        Assert.Equal(MoveStatus.Stale, plan.Status);
        Assert.True(plan.IsFailure);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Translation_OfAPinnedElement_IsSkippedOrFails(bool skipPinned, bool isFailure)
    {
        var plan = MoveElementsMath.BuildTranslation(
            Delta(dx: 500), new PointMm(0, 0, 0), new PointMm(500, 0, 0), true, skipPinned, Tolerance);

        Assert.Equal(MoveStatus.Pinned, plan.Status);
        Assert.Equal(isFailure, plan.IsFailure);
        Assert.False(plan.CanMove);
    }

    [Fact]
    public void ZeroDisplacement_IsAlreadyThere_NotAFailure()
    {
        var plan = MoveElementsMath.BuildTranslation(Delta(dx: 0), null, new PointMm(0, 0, 0), false, true, Tolerance);

        Assert.Equal(MoveStatus.AlreadyThere, plan.Status);
        Assert.False(plan.IsFailure);
    }

    [Fact]
    public void Blocked_KeepsTheNumbers_AndCountsAsConstrained()
    {
        var ready = MoveElementsMath.BuildTranslation(
            Delta(dx: 500), new PointMm(0, 0, 0), new PointMm(500, 0, 0), false, true, Tolerance);
        var blocked = MoveElementsMath.Blocked(ready, MoveStatus.InGroup, "group member");

        Assert.False(blocked.CanMove);
        Assert.True(blocked.IsFailure);
        Assert.Equal(500, blocked.DistanceMm);

        var summary = MoveElementsMath.Summarise(new[]
        {
            blocked, MoveElementsMath.Blocked(ready, MoveStatus.OutOfViewPlane, "off plane")
        });
        Assert.Equal(2, summary.Constrained.Count);
        Assert.Equal(2, summary.ProblemCount);
    }

    // ── Parsing move entries ─────────────────────────────────────────────────

    [Fact]
    public void ParseMoves_ReadsModelAndViewDeltas()
    {
        var moves = MoveElementsMath.ParseMoves(
            JArray.Parse("[{\"elementId\": 7, \"deltaXmm\": 500}, {\"elementId\": 8, \"deltaRightMm\": 250, \"deltaUpMm\": -100}]"),
            new List<string>(), out var error);

        Assert.Null(error);
        Assert.True(moves![0].HasModelDelta);
        Assert.False(moves[0].HasTarget);
        Assert.Equal(500, moves[0].DeltaXMm);
        Assert.True(moves[1].HasViewDelta);
        Assert.Equal(-100, moves[1].DeltaUpMm);
    }

    [Fact]
    public void ParseMoves_RejectsATargetTogetherWithADelta()
    {
        var moves = MoveElementsMath.ParseMoves(
            JArray.Parse("[{\"elementId\": 7, \"targetXmm\": 1, \"deltaYmm\": 2}]"), new List<string>(), out var error);

        Assert.Null(moves);
        Assert.Contains("both a target and a delta", error);
    }

    [Fact]
    public void ParseMoves_RejectsModelAndViewAxesMixed()
    {
        var moves = MoveElementsMath.ParseMoves(
            JArray.Parse("[{\"elementId\": 7, \"deltaXmm\": 1, \"deltaUpMm\": 2}]"), new List<string>(), out var error);

        Assert.Null(moves);
        Assert.Contains("one set of axes", error);
    }

    [Fact]
    public void ParseMoves_AbsoluteEntriesStillWork()
    {
        var warnings = new List<string>();
        var moves = MoveElementsMath.ParseMoves(
            JArray.Parse("[{\"elementId\": 7, \"targetXmm\": 76871.5, \"expectedXmm\": 75388.79}]"), warnings, out var error);

        Assert.Null(error);
        Assert.Empty(warnings);
        Assert.True(moves![0].HasTarget);
        Assert.False(moves[0].HasDelta);
    }

    // ── Copy request ─────────────────────────────────────────────────────────

    private static Dictionary<string, object?> Args(string json) =>
        JObject.Parse(json).Properties().ToDictionary(p => p.Name, p => (object?)p.Value);

    [Fact]
    public void CopyRequest_ReadsIdsDeltasAndViews()
    {
        var request = CopyElementsMath.Parse(
            Args("{\"elementIds\": [11, 9000000001], \"deltaRightMm\": 500, \"targetViewId\": 77, \"atomic\": false}"),
            out var error);

        Assert.Null(error);
        Assert.Equal(new List<long> { 11, 9000000001 }, request!.ElementIds);
        Assert.True(request.HasViewDelta);
        Assert.Equal(77, request.TargetViewId);
        Assert.False(request.Atomic);
        Assert.Equal(500, request.AsDelta().DeltaRightMm);
    }

    [Theory]
    [InlineData("{}", "useSelection")]
    [InlineData("{\"elementIds\": [1], \"useSelection\": true}", "not both")]
    [InlineData("{\"elementIds\": [1, 1]}", "appears 2 times")]
    [InlineData("{\"elementIds\": [1], \"deltaXmm\": 1, \"deltaUpMm\": 2}", "one set of axes")]
    [InlineData("{\"elementIds\": \"nope\"}", "array")]
    public void CopyRequest_RejectsWhatItCannotActOn(string json, string expected)
    {
        var request = CopyElementsMath.Parse(Args(json), out var error);

        Assert.Null(request);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void CopyRequest_AllowsTheSelection_AndAZeroTranslation()
    {
        var request = CopyElementsMath.Parse(Args("{\"useSelection\": true}"), out var error);

        Assert.Null(error);
        Assert.True(request!.UseSelection);
        Assert.False(request.HasModelDelta);
        Assert.True(request.Atomic);
    }

    // ── Pairing sources with copies ──────────────────────────────────────────

    private static ElementSnapshot Element(long id, string cls, long type, double x, double y = 0, string category = "Detail Items") => new()
    {
        Id = id, ClassName = cls, TypeId = type, Category = category, Anchor = new PointMm(x, y, 0)
    };

    [Fact]
    public void Copies_ArePairedByPosition_WhateverOrderRevitReturnsThem()
    {
        var sources = new[] { Element(1, "FamilyInstance", 50, 0), Element(2, "FamilyInstance", 50, 1000) };
        var created = new[] { Element(102, "FamilyInstance", 50, 1500), Element(101, "FamilyInstance", 50, 500) };

        var matches = CopyElementsMath.MatchCopies(sources, created, new PointMm(500, 0, 0));

        Assert.Equal(101, matches[1].Copy.Id);
        Assert.Equal(102, matches[2].Copy.Id);
        Assert.All(matches.Values, m => Assert.Equal(CopyMapping.Position, m.Mapping));
    }

    [Fact]
    public void Dependents_AreWhatNoSourceClaims()
    {
        // A tagged element: Revit copies the element, and a tag of another class comes along.
        var sources = new[] { Element(1, "FamilyInstance", 50, 0) };
        var created = new[]
        {
            Element(101, "FamilyInstance", 50, 500),
            Element(102, "IndependentTag", 60, 500, category: "Tags")
        };

        var matches = CopyElementsMath.MatchCopies(sources, created, new PointMm(500, 0, 0));
        var dependents = CopyElementsMath.Dependencies(created, matches);

        Assert.Equal(101, matches[1].Copy.Id);
        Assert.Single(dependents);
        Assert.Equal(102, dependents[0].Id);
    }

    [Fact]
    public void AcrossViews_CopiesArePairedByKindAndOrder()
    {
        // No translation to compare against: the destination view repositions the copies.
        var sources = new[]
        {
            Element(1, "FamilyInstance", 50, 0), Element(2, "FamilyInstance", 50, 1000), Element(3, "DetailLine", -1, 0, category: "Lines")
        };
        var created = new[]
        {
            Element(201, "FamilyInstance", 50, 9000), Element(202, "FamilyInstance", 50, 9500), Element(203, "DetailLine", -1, 9700, category: "Lines")
        };

        var matches = CopyElementsMath.MatchCopies(sources, created, null);

        Assert.Equal(201, matches[1].Copy.Id);
        Assert.Equal(202, matches[2].Copy.Id);
        Assert.Equal(203, matches[3].Copy.Id);
        Assert.Equal(CopyMapping.Order, matches[1].Mapping);
    }

    [Fact]
    public void ASingleElement_CopiedAlone_IsItsCopy()
    {
        var sources = new[] { Element(1, "TextNote", 70, 0, category: "Text Notes") };
        var created = new[] { Element(101, "TextNote", 70, 12345, category: "Text Notes") };

        var matches = CopyElementsMath.MatchCopies(sources, created, null);

        Assert.Equal(101, matches[1].Copy.Id);
        Assert.Equal(CopyMapping.Single, matches[1].Mapping);
    }

    [Fact]
    public void WhenTheCountsDoNotAddUp_NothingIsGuessed()
    {
        // Two sources of one type, three new elements of that type: no honest pairing exists.
        var sources = new[] { Element(1, "FamilyInstance", 50, 0), Element(2, "FamilyInstance", 50, 1000) };
        var created = new[]
        {
            Element(201, "FamilyInstance", 50, 7000), Element(202, "FamilyInstance", 50, 8000), Element(203, "FamilyInstance", 50, 9000)
        };

        var matches = CopyElementsMath.MatchCopies(sources, created, null);

        Assert.Empty(matches);
        Assert.Equal(3, CopyElementsMath.Dependencies(created, matches).Count);
    }

    [Fact]
    public void ACopyOfADifferentType_IsNeverPaired()
    {
        var sources = new[] { Element(1, "FamilyInstance", 50, 0) };
        var created = new[] { Element(101, "FamilyInstance", 51, 500) };

        Assert.Empty(CopyElementsMath.MatchCopies(sources, created, new PointMm(500, 0, 0)));
    }
}
