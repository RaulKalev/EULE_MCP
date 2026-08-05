using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Placement;
using Xunit;

namespace RevitMCP.Tests;

public class MoveElementsMathTests
{
    private const double Tolerance = MoveElementsMath.DefaultPositionToleranceMm;

    /// <summary>The worked example from the DWG-alignment workflow, in millimetres.</summary>
    private static readonly PointMm Fixture = new(75388.79, 71229.96, 59275.0);

    private static MoveRequest Move(
        long elementId = 1756386,
        double? x = null,
        double? y = null,
        double? z = null,
        double? expectedX = null,
        double? expectedY = null,
        double? expectedZ = null) => new()
    {
        ElementId = elementId,
        TargetXMm = x,
        TargetYMm = y,
        TargetZMm = z,
        ExpectedXMm = expectedX,
        ExpectedYMm = expectedY,
        ExpectedZMm = expectedZ
    };

    // ── Millimetre / internal-unit conversion ────────────────────────────────

    [Fact]
    public void MmToFt_UsesTheExactInternationalFoot()
    {
        // Revit's internal length unit is the decimal foot; 1 ft is exactly 304.8 mm.
        Assert.Equal(1.0, MoveElementsMath.MmToFt(304.8), 12);
        Assert.Equal(0.0, MoveElementsMath.MmToFt(0.0), 12);
        Assert.Equal(-2.0, MoveElementsMath.MmToFt(-609.6), 12);
    }

    [Fact]
    public void FtToMm_IsTheInverse()
    {
        Assert.Equal(304.8, MoveElementsMath.FtToMm(1.0), 12);
        Assert.Equal(1234.5678, MoveElementsMath.FtToMm(MoveElementsMath.MmToFt(1234.5678)), 9);
    }

    [Fact]
    public void PointFromFeet_ConvertsEveryAxis()
    {
        var point = MoveElementsMath.PointFromFeet(1.0, 2.0, -3.0);

        Assert.Equal(304.8, point.X, 9);
        Assert.Equal(609.6, point.Y, 9);
        Assert.Equal(-914.4, point.Z, 9);
    }

    [Theory]
    [InlineData(0.0, Tolerance)]          // zero and negatives fall back to the default
    [InlineData(-5.0, Tolerance)]
    [InlineData(0.0000001, MoveElementsMath.MinPositionToleranceMm)]
    [InlineData(99999999.0, MoveElementsMath.MaxPositionToleranceMm)]
    [InlineData(2.5, 2.5)]
    public void ClampTolerance_KeepsTheToleranceUsable(double requested, double expected)
    {
        Assert.Equal(expected, MoveElementsMath.ClampTolerance(requested), 9);
    }

    // ── Exact XY movement, preserving Z ──────────────────────────────────────

    [Fact]
    public void OmittingTargetZ_KeepsTheElementAtItsElevation()
    {
        var plan = MoveElementsMath.Build(
            Move(x: 76871.5, y: 71602.9), Fixture, pinned: false, skipPinned: true, Tolerance);

        Assert.Equal(MoveStatus.Ready, plan.Status);
        Assert.True(plan.CanMove);

        Assert.Equal(76871.5, plan.TargetPointMm!.Value.X, 6);
        Assert.Equal(71602.9, plan.TargetPointMm!.Value.Y, 6);
        Assert.Equal(Fixture.Z, plan.TargetPointMm!.Value.Z, 6);

        Assert.Equal(1482.71, MoveElementsMath.Round(plan.TranslationMm!.Value.X), 6);
        Assert.Equal(372.94, MoveElementsMath.Round(plan.TranslationMm!.Value.Y), 6);
        Assert.Equal(0.0, plan.TranslationMm!.Value.Z, 9);
        Assert.Equal(1528.89, MoveElementsMath.Round(plan.DistanceMm), 6);
    }

    [Fact]
    public void OmittingTargetX_OrY_KeepsThatAxisToo()
    {
        var plan = MoveElementsMath.Build(
            Move(y: 71602.9), Fixture, pinned: false, skipPinned: true, Tolerance);

        Assert.Equal(Fixture.X, plan.TargetPointMm!.Value.X, 9);
        Assert.Equal(Fixture.Z, plan.TargetPointMm!.Value.Z, 9);
        Assert.Equal(0.0, plan.TranslationMm!.Value.X, 9);
        Assert.Equal(0.0, plan.TranslationMm!.Value.Z, 9);
    }

    // ── Explicit XYZ movement ────────────────────────────────────────────────

    [Fact]
    public void AnExplicitZ_MovesTheElementVertically()
    {
        var plan = MoveElementsMath.Build(
            Move(x: 76871.5, y: 71602.9, z: 60000.0), Fixture, pinned: false, skipPinned: true, Tolerance);

        Assert.Equal(MoveStatus.Ready, plan.Status);
        Assert.Equal(60000.0, plan.TargetPointMm!.Value.Z, 6);
        Assert.Equal(725.0, plan.TranslationMm!.Value.Z, 6);
    }

    [Fact]
    public void AnExplicitZeroZ_IsATarget_NotAnOmission()
    {
        // The nullable target is the whole point: 0 means "put it on the project origin plane",
        // which is a different request from leaving the axis out.
        var plan = MoveElementsMath.Build(
            Move(z: 0.0), Fixture, pinned: false, skipPinned: true, Tolerance);

        Assert.Equal(0.0, plan.TargetPointMm!.Value.Z, 9);
        Assert.Equal(-Fixture.Z, plan.TranslationMm!.Value.Z, 6);
        Assert.True(plan.CanMove);
    }

    // ── No-op movements ──────────────────────────────────────────────────────

    [Fact]
    public void AnElementAlreadyAtItsTarget_IsNotMoved()
    {
        var plan = MoveElementsMath.Build(
            Move(x: Fixture.X, y: Fixture.Y, z: Fixture.Z),
            Fixture, pinned: false, skipPinned: true, Tolerance);

        Assert.Equal(MoveStatus.AlreadyThere, plan.Status);
        Assert.False(plan.CanMove);
        Assert.False(plan.IsFailure);
        Assert.Equal(0.0, plan.DistanceMm, 9);
    }

    [Fact]
    public void AMoveBelowTheNegligibleDistance_IsANoOp()
    {
        var plan = MoveElementsMath.Build(
            Move(x: Fixture.X + 0.01), Fixture, pinned: false, skipPinned: true, Tolerance);

        Assert.Equal(MoveStatus.AlreadyThere, plan.Status);
    }

    [Fact]
    public void ANoOpThresholdIsNotDerivedFromTheStalenessTolerance()
    {
        // A loose staleness tolerance must not start swallowing real moves.
        var plan = MoveElementsMath.Build(
            Move(x: Fixture.X + 50.0), Fixture, pinned: false, skipPinned: true, positionToleranceMm: 500.0);

        Assert.Equal(MoveStatus.Ready, plan.Status);
        Assert.Equal(50.0, plan.DistanceMm, 6);
    }

    [Fact]
    public void AMoveWithNoTargetAtAll_IsANoOpWithAnExplanation()
    {
        var plan = MoveElementsMath.Build(Move(), Fixture, pinned: false, skipPinned: true, Tolerance);

        Assert.Equal(MoveStatus.AlreadyThere, plan.Status);
        Assert.False(plan.IsFailure);
        Assert.Contains("no target coordinate", plan.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Missing elements ─────────────────────────────────────────────────────

    [Fact]
    public void AMissingElement_IsAFailureWithNoGeometry()
    {
        var plan = MoveElementsMath.Missing(1756386);

        Assert.Equal(MoveStatus.Missing, plan.Status);
        Assert.False(plan.CanMove);
        Assert.True(plan.IsFailure);
        Assert.Null(plan.CurrentPointMm);
        Assert.Null(plan.TranslationMm);
        Assert.Equal(1756386, plan.ElementId);
    }

    // ── Pinned elements ──────────────────────────────────────────────────────

    [Fact]
    public void APinnedElement_IsSkippedWhenSkipPinnedIsTrue()
    {
        var plan = MoveElementsMath.Build(
            Move(x: 76871.5), Fixture, pinned: true, skipPinned: true, Tolerance);

        Assert.Equal(MoveStatus.Pinned, plan.Status);
        Assert.False(plan.CanMove);
        Assert.False(plan.IsFailure); // a deliberate skip never rolls an atomic batch back
        Assert.True(plan.Pinned);
    }

    [Fact]
    public void APinnedElement_FailsWhenSkipPinnedIsFalse()
    {
        var plan = MoveElementsMath.Build(
            Move(x: 76871.5), Fixture, pinned: true, skipPinned: false, Tolerance);

        Assert.Equal(MoveStatus.Pinned, plan.Status);
        Assert.False(plan.CanMove);
        Assert.True(plan.IsFailure);
        Assert.Contains("unpin", plan.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APinnedElementAlreadyAtItsTarget_IsStillReportedAsPinned()
    {
        // Reporting it as "already there" would hide the pin from anyone reading the result.
        var plan = MoveElementsMath.Build(
            Move(x: Fixture.X, y: Fixture.Y), Fixture, pinned: true, skipPinned: true, Tolerance);

        Assert.Equal(MoveStatus.Pinned, plan.Status);
    }

    // ── Unsupported location types ───────────────────────────────────────────

    [Fact]
    public void AnElementWithoutALocationPoint_IsUnsupported()
    {
        var plan = MoveElementsMath.UnsupportedLocation(
            1756386, "This element is placed on a curve (a wall, pipe, duct, conduit or cable tray).");

        Assert.Equal(MoveStatus.UnsupportedLocation, plan.Status);
        Assert.False(plan.CanMove);
        Assert.True(plan.IsFailure);
        Assert.Null(plan.CurrentPointMm);
        Assert.Null(plan.TargetPointMm);
    }

    // ── Stale expected coordinates ───────────────────────────────────────────

    [Fact]
    public void MatchingExpectedCoordinates_LetTheMoveThrough()
    {
        var plan = MoveElementsMath.Build(
            Move(x: 76871.5, y: 71602.9,
                 expectedX: 75388.79, expectedY: 71229.96, expectedZ: 59275.0),
            Fixture, pinned: false, skipPinned: true, Tolerance);

        Assert.Equal(MoveStatus.Ready, plan.Status);
        Assert.True(plan.CanMove);
        Assert.Equal(0.0, plan.StaleDeviationMm!.Value, 6);
    }

    [Fact]
    public void ExpectedCoordinatesWithinTolerance_AreNotStale()
    {
        var plan = MoveElementsMath.Build(
            Move(x: 76871.5, expectedX: Fixture.X + 0.9), Fixture,
            pinned: false, skipPinned: true, positionToleranceMm: 1.0);

        Assert.Equal(MoveStatus.Ready, plan.Status);
        Assert.Equal(0.9, plan.StaleDeviationMm!.Value, 6);
    }

    [Fact]
    public void AnElementThatDriftedFurtherThanTheTolerance_IsStale()
    {
        var plan = MoveElementsMath.Build(
            Move(x: 76871.5, expectedX: Fixture.X + 12.0), Fixture,
            pinned: false, skipPinned: true, positionToleranceMm: 1.0);

        Assert.Equal(MoveStatus.Stale, plan.Status);
        Assert.False(plan.CanMove);
        Assert.True(plan.IsFailure);
        Assert.Equal(12.0, plan.StaleDeviationMm!.Value, 6);
    }

    [Fact]
    public void StalenessIsCheckedPerAxis_AndReportsTheWorstOne()
    {
        var plan = MoveElementsMath.Build(
            Move(x: 76871.5, expectedX: Fixture.X, expectedY: Fixture.Y, expectedZ: Fixture.Z - 300.0),
            Fixture, pinned: false, skipPinned: true, Tolerance);

        // Only the elevation drifted, but that is still the model disagreeing with the caller.
        Assert.Equal(MoveStatus.Stale, plan.Status);
        Assert.Equal(300.0, plan.StaleDeviationMm!.Value, 6);
    }

    [Fact]
    public void AxesWithNoExpectedValue_AreNotChecked()
    {
        var plan = MoveElementsMath.Build(
            Move(x: 76871.5, expectedX: Fixture.X), Fixture, pinned: false, skipPinned: true, Tolerance);

        Assert.Equal(MoveStatus.Ready, plan.Status);
        Assert.Equal(0.0, plan.StaleDeviationMm!.Value, 6);
    }

    [Fact]
    public void WithNoExpectedCoordinatesAtAll_TheCheckIsSkipped()
    {
        var plan = MoveElementsMath.Build(
            Move(x: 76871.5), Fixture, pinned: false, skipPinned: true, Tolerance);

        Assert.Null(plan.StaleDeviationMm);
        Assert.Equal(MoveStatus.Ready, plan.Status);
    }

    [Fact]
    public void StalenessOutranksPinning()
    {
        // A stale element's target was calculated from a position the model no longer has, so
        // nothing else about the request can be trusted either.
        var plan = MoveElementsMath.Build(
            Move(x: 76871.5, expectedX: Fixture.X + 500.0), Fixture,
            pinned: true, skipPinned: true, Tolerance);

        Assert.Equal(MoveStatus.Stale, plan.Status);
        Assert.True(plan.IsFailure);
    }

    // ── Atomic rollback ──────────────────────────────────────────────────────

    [Fact]
    public void AtomicRollsBackAsSoonAsAnythingFails()
    {
        Assert.True(MoveElementsMath.ShouldRollBack(atomic: true, failureCount: 1));
        Assert.False(MoveElementsMath.ShouldRollBack(atomic: true, failureCount: 0));
    }

    [Fact]
    public void NonAtomicNeverRollsBack()
    {
        Assert.False(MoveElementsMath.ShouldRollBack(atomic: false, failureCount: 7));
    }

    [Fact]
    public void ABenignSkipDoesNotTriggerAnAtomicRollback()
    {
        var plans = new List<MovePlan>
        {
            MoveElementsMath.Build(Move(1, x: 76871.5), Fixture, false, true, Tolerance),
            MoveElementsMath.Build(Move(2, x: Fixture.X), Fixture, false, true, Tolerance),
            MoveElementsMath.Build(Move(3, x: 76871.5), Fixture, pinned: true, skipPinned: true, Tolerance)
        };

        var failures = MoveElementsMath.CountFailures(plans);

        Assert.Equal(0, failures);
        Assert.False(MoveElementsMath.ShouldRollBack(atomic: true, failures));
    }

    [Fact]
    public void AnAtomicBatchWithOneStaleElement_ReportsEverythingAsUnmoved()
    {
        var plans = new List<MovePlan>
        {
            MoveElementsMath.Build(Move(1, x: 76871.5), Fixture, false, true, Tolerance),
            MoveElementsMath.Build(Move(2, x: 76871.5, expectedX: Fixture.X + 40.0), Fixture, false, true, Tolerance),
            MoveElementsMath.Build(Move(3, x: 76871.5), Fixture, false, true, Tolerance)
        };

        var failures = MoveElementsMath.CountFailures(plans);
        Assert.True(MoveElementsMath.ShouldRollBack(atomic: true, failures));

        // What the write tool does when it rejects the batch up front.
        foreach (var plan in plans.Where(plan => plan.CanMove))
            plan.Status = MoveStatus.NotAttempted;

        var summary = MoveElementsMath.Summarise(plans);

        Assert.Empty(summary.Moved);
        Assert.Equal(new long[] { 2 }, summary.Stale);
        Assert.Equal(new long[] { 1, 3 }, summary.NotAttempted);
    }

    [Fact]
    public void AnAtomicBatchThatFailsMidWay_ReportsTheMovedElementsAsRolledBack()
    {
        var plans = new List<MovePlan>
        {
            MoveElementsMath.Build(Move(1, x: 76871.5), Fixture, false, true, Tolerance),
            MoveElementsMath.Build(Move(2, x: 76871.5), Fixture, false, true, Tolerance),
            MoveElementsMath.Build(Move(3, x: 76871.5), Fixture, false, true, Tolerance)
        };

        // Element 1 moved, element 2 was refused by Revit, element 3 never got its turn.
        plans[0].Status = MoveStatus.Moved;
        plans[1].Status = MoveStatus.Failed;

        foreach (var plan in plans.Where(plan => plan.Status == MoveStatus.Moved))
            plan.Status = MoveStatus.RolledBack;
        foreach (var plan in plans.Where(plan => plan.CanMove && plan.Status == MoveStatus.Ready))
            plan.Status = MoveStatus.NotAttempted;

        var summary = MoveElementsMath.Summarise(plans);

        Assert.Empty(summary.Moved);
        Assert.Equal(new long[] { 1 }, summary.RolledBack);
        Assert.Equal(new long[] { 2 }, summary.Failed);
        Assert.Equal(new long[] { 3 }, summary.NotAttempted);
    }

    // ── Non-atomic partial success ───────────────────────────────────────────

    [Fact]
    public void NonAtomicKeepsWhatLandedAndReportsTheRest()
    {
        var plans = new List<MovePlan>
        {
            MoveElementsMath.Build(Move(1, x: 76871.5), Fixture, false, true, Tolerance),
            MoveElementsMath.Missing(2),
            MoveElementsMath.Build(Move(3, x: 76871.5, expectedX: Fixture.X + 40.0), Fixture, false, true, Tolerance),
            MoveElementsMath.Build(Move(4, x: 76871.5), Fixture, pinned: true, skipPinned: true, Tolerance),
            MoveElementsMath.UnsupportedLocation(5, "placed on a curve"),
            MoveElementsMath.Build(Move(6, x: Fixture.X), Fixture, false, true, Tolerance),
            MoveElementsMath.Build(Move(7, x: 76871.5), Fixture, false, true, Tolerance)
        };

        // Element 1 moved; element 7 was refused by Revit and cost only itself.
        plans[0].Status = MoveStatus.Moved;
        plans[6].Status = MoveStatus.Failed;

        var summary = MoveElementsMath.Summarise(plans);

        Assert.Equal(new long[] { 1 }, summary.Moved);
        Assert.Equal(new long[] { 2 }, summary.Missing);
        Assert.Equal(new long[] { 3 }, summary.Stale);
        Assert.Equal(new long[] { 4 }, summary.Pinned);
        Assert.Equal(new long[] { 5 }, summary.Unsupported);
        Assert.Equal(new long[] { 6 }, summary.Skipped);
        Assert.Equal(new long[] { 7 }, summary.Failed);

        // Missing, stale, pinned, unsupported and failed — the element already there is not a problem.
        Assert.Equal(5, summary.ProblemCount);
    }

    [Fact]
    public void EveryElementLandsInExactlyOneBucket()
    {
        var plans = new List<MovePlan>
        {
            MoveElementsMath.Build(Move(1, x: 76871.5), Fixture, false, true, Tolerance),
            MoveElementsMath.Missing(2),
            MoveElementsMath.UnsupportedLocation(3, "no location")
        };
        plans[0].Status = MoveStatus.Moved;

        var summary = MoveElementsMath.Summarise(plans);
        var total = summary.Moved.Count + summary.Skipped.Count + summary.Stale.Count +
                    summary.Missing.Count + summary.Pinned.Count + summary.Unsupported.Count +
                    summary.Failed.Count + summary.RolledBack.Count + summary.NotAttempted.Count;

        Assert.Equal(plans.Count, total);
    }

    // ── Revit 2024 / Revit 2026 element id compatibility ─────────────────────

    [Fact]
    public void ElementIdsBeyondIntRange_SurviveParsing()
    {
        // Element ids have been 64-bit since Revit 2024, and Revit 2026 issues values above
        // int.MaxValue. Anything that narrowed to int here would silently move the wrong element.
        const long revit2026Id = 8_589_934_592L; // 2^33
        var json = JArray.Parse(
            $"[{{\"elementId\": {revit2026Id}, \"targetXmm\": 76871.5, \"targetYmm\": 71602.9}}]");

        var warnings = new List<string>();
        var moves = MoveElementsMath.ParseMoves(json, warnings, out var error);

        Assert.Null(error);
        Assert.NotNull(moves);
        Assert.Equal(revit2026Id, moves![0].ElementId);
        Assert.True(moves[0].ElementId > int.MaxValue);
    }

    [Fact]
    public void ElementIdsBeyondIntRange_SurviveThePlanAndTheSummary()
    {
        const long revit2026Id = long.MaxValue - 1;

        var plan = MoveElementsMath.Build(
            Move(revit2026Id, x: 76871.5), Fixture, false, true, Tolerance);
        var summary = MoveElementsMath.Summarise(new[] { MoveElementsMath.Missing(revit2026Id) });

        Assert.Equal(revit2026Id, plan.ElementId);
        Assert.Equal(new[] { revit2026Id }, summary.Missing);
    }

    [Fact]
    public void Revit2024SizedElementIds_StillWork()
    {
        // The ids a Revit 2024 model actually hands out sit comfortably inside int range.
        const long revit2024Id = 1_756_386L;
        var json = JArray.Parse($"[{{\"elementId\": {revit2024Id}, \"targetXmm\": 1.0}}]");

        var moves = MoveElementsMath.ParseMoves(json, new List<string>(), out var error);

        Assert.Null(error);
        Assert.Equal(revit2024Id, moves![0].ElementId);
    }

    // ── Request parsing ──────────────────────────────────────────────────────

    [Fact]
    public void ParseMoves_ReadsTheWorkedExample()
    {
        const string json = """
            [{
              "elementId": 1756386,
              "targetXmm": 76871.5,
              "targetYmm": 71602.9,
              "expectedXmm": 75388.79,
              "expectedYmm": 71229.96,
              "expectedZmm": 59275.0
            }]
            """;

        var warnings = new List<string>();
        var moves = MoveElementsMath.ParseMoves(json, warnings, out var error);

        Assert.Null(error);
        var move = Assert.Single(moves!);
        Assert.Equal(1756386, move.ElementId);
        Assert.Equal(76871.5, move.TargetXMm!.Value, 6);
        Assert.Equal(71602.9, move.TargetYMm!.Value, 6);
        Assert.Null(move.TargetZMm);
        Assert.Equal(59275.0, move.ExpectedZMm!.Value, 6);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("targetZmm")]
    [InlineData("targetZMm")]
    [InlineData("targetZMM")]
    [InlineData("TargetZmm")]
    public void ParseMoves_AcceptsTheCommonSpellingsOfACoordinate(string key)
    {
        var moves = MoveElementsMath.ParseMoves(
            JArray.Parse($"[{{\"elementId\": 1, \"{key}\": 1234.5}}]"), new List<string>(), out var error);

        Assert.Null(error);
        Assert.Equal(1234.5, moves![0].TargetZMm!.Value, 6);
    }

    [Fact]
    public void ParseMoves_RejectsADuplicateElement()
    {
        var moves = MoveElementsMath.ParseMoves(
            JArray.Parse("[{\"elementId\": 7, \"targetXmm\": 1}, {\"elementId\": 7, \"targetXmm\": 2}]"),
            new List<string>(), out var error);

        Assert.Null(moves);
        Assert.Contains("only be given one destination", error!);
    }

    [Fact]
    public void ParseMoves_RejectsAMissingOrUnusableElementId()
    {
        Assert.Null(MoveElementsMath.ParseMoves(
            JArray.Parse("[{\"targetXmm\": 1}]"), new List<string>(), out var missing));
        Assert.Contains("elementId", missing!);

        Assert.Null(MoveElementsMath.ParseMoves(
            JArray.Parse("[{\"elementId\": 0, \"targetXmm\": 1}]"), new List<string>(), out var zero));
        Assert.Contains("elementId", zero!);
    }

    [Fact]
    public void ParseMoves_RejectsAnEmptyOrAbsentArray()
    {
        Assert.Null(MoveElementsMath.ParseMoves(null, new List<string>(), out var absent));
        Assert.Contains("moves", absent!);

        Assert.Null(MoveElementsMath.ParseMoves(new JArray(), new List<string>(), out var empty));
        Assert.Contains("empty", empty!);
    }

    [Fact]
    public void ParseMoves_RefusesToSilentlyTruncateAnOversizedBatch()
    {
        var array = new JArray();
        for (var i = 1; i <= MoveElementsMath.MaxMoves + 1; i++)
            array.Add(JObject.Parse($"{{\"elementId\": {i}, \"targetXmm\": 1}}"));

        var moves = MoveElementsMath.ParseMoves(array, new List<string>(), out var error);

        Assert.Null(moves);
        Assert.Contains("Split the batch", error!);
    }

    [Fact]
    public void ParseMoves_CarriesTheWorkflowsFiveHundredMoves()
    {
        var array = new JArray();
        for (var i = 1; i <= 500; i++)
            array.Add(JObject.Parse($"{{\"elementId\": {i}, \"targetXmm\": {i * 10}}}"));

        var moves = MoveElementsMath.ParseMoves(array, new List<string>(), out var error);

        Assert.Null(error);
        Assert.Equal(500, moves!.Count);
        Assert.Equal(5000.0, moves[499].TargetXMm!.Value, 6);
    }

    [Fact]
    public void ParseMoves_WarnsAboutAMoveWithNoTarget()
    {
        var warnings = new List<string>();
        var moves = MoveElementsMath.ParseMoves(
            JArray.Parse("[{\"elementId\": 7}]"), warnings, out var error);

        Assert.Null(error);
        Assert.False(moves![0].HasTarget);
        Assert.Single(warnings);
    }

    [Fact]
    public void ParseMoves_TreatsANullCoordinateAsAbsent_NotAsZero()
    {
        var moves = MoveElementsMath.ParseMoves(
            JArray.Parse("[{\"elementId\": 7, \"targetXmm\": 1.0, \"targetZmm\": null}]"),
            new List<string>(), out var error);

        Assert.Null(error);
        Assert.Null(moves![0].TargetZMm);
    }
}
