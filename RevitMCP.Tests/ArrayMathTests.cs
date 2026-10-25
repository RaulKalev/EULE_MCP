using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Placement;
using Xunit;

namespace RevitMCP.Tests;

public class ArrayMathTests
{
    private static Dictionary<string, object?> Args(string json)
    {
        var result = new Dictionary<string, object?>();
        foreach (var property in JObject.Parse(json).Properties())
        {
            // The pipe hands primitives over as CLR values and objects/arrays as JTokens.
            result[property.Name] = property.Value switch
            {
                JValue { Type: JTokenType.Integer } v => v.Value<long>(),
                JValue { Type: JTokenType.Float } v => v.Value<double>(),
                JValue { Type: JTokenType.Boolean } v => v.Value<bool>(),
                JValue { Type: JTokenType.String } v => v.Value<string>(),
                var token => token
            };
        }
        return result;
    }

    private static ArrayRequest Parse(string json)
    {
        var request = ArrayMath.Parse(Args(json), out var error, out _);
        Assert.True(request != null, error);
        return request!;
    }

    private static void AssertPoint(PointMm expected, PointMm actual, int precision = 6)
    {
        Assert.Equal(expected.X, actual.X, precision);
        Assert.Equal(expected.Y, actual.Y, precision);
        Assert.Equal(expected.Z, actual.Z, precision);
    }

    [Fact]
    public void LinearBySpacing_SixLuminairesAt1200()
    {
        var request = Parse("{elementIds: [1], kind: 'linear', count: 6, spacingMm: {x: 1200, y: 0, z: 0}}");
        Assert.Equal(5, request.Copies);
        var step = ArrayMath.LinearStep(request, null, out var error);
        Assert.Null(error);
        var steps = ArrayMath.Steps(request, step, new PointMm(0, 0, 0));
        Assert.Equal(5, steps.Count);
        AssertPoint(new PointMm(1200, 0, 0), steps[0].TranslationMm);
        AssertPoint(new PointMm(6000, 0, 0), steps[4].TranslationMm);
        Assert.All(steps, s => Assert.Equal(0.0, s.RotationDeg));
    }

    [Fact]
    public void IncludeOriginal_DecidesHowManyCopies()
    {
        Assert.Equal(5, Parse("{elementIds: [1], count: 6, spacingMm: {x: 1, y: 0}}").Copies);
        Assert.Equal(6, Parse("{elementIds: [1], count: 6, includeOriginal: false, spacingMm: {x: 1, y: 0}}").Copies);
        Assert.Equal(1, ArrayMath.CopiesFor(2, includeOriginal: true));
    }

    [Fact]
    public void LinearByTotal_RunsFromTheOriginalToTheLastCopy()
    {
        var request = Parse("{elementIds: [1], count: 5, totalMm: {x: 0, y: 4800, z: 0}}");
        var step = ArrayMath.LinearStep(request, null, out _)!.Value;
        AssertPoint(new PointMm(0, 1200, 0), step);
        var steps = ArrayMath.Steps(request, step, new PointMm(0, 0, 0));
        AssertPoint(new PointMm(0, 4800, 0), steps[^1].TranslationMm);

        // Without the original, the total still ends at the last copy: 4 copies over 4800 → 1200 each.
        var excluded = Parse("{elementIds: [1], count: 4, includeOriginal: false, totalMm: {x: 0, y: 4800}}");
        AssertPoint(new PointMm(0, 1200, 0), ArrayMath.LinearStep(excluded, null, out _)!.Value);
    }

    [Fact]
    public void LinearByViewDeltas_FollowsTheViewFrame()
    {
        var section = new ViewFrame(new PointMm(0, 1, 0), new PointMm(0, 0, 1), new PointMm(1, 0, 0));
        var request = Parse("{elementIds: [1], count: 3, deltaRightMm: 500, deltaUpMm: 250}");
        AssertPoint(new PointMm(0, 500, 250), ArrayMath.LinearStep(request, section, out _)!.Value);

        Assert.Null(ArrayMath.LinearStep(request, null, out var error));
        Assert.Contains("viewId", error);
    }

    [Fact]
    public void RadialFullCircle_SpreadsEightDetectorsEvenly()
    {
        var request = Parse("{elementIds: [1], kind: 'radial', count: 8, centerMm: {x: 0, y: 0, z: 0}, totalAngleDeg: 360}");
        Assert.Equal(45.0, ArrayMath.StepAngleDeg(request), 9);
        var steps = ArrayMath.Steps(request, null, new PointMm(3000, 0, 2700));
        Assert.Equal(7, steps.Count);
        Assert.Equal(315.0, steps[^1].RotationDeg, 9);
        Assert.All(steps, s => AssertPoint(new PointMm(0, 0, 0), s.TranslationMm));

        // Detector at (3000, 0) lands at (0, 3000) in the second step, at the same height.
        AssertPoint(new PointMm(0, 3000, 2700), ArrayMath.PositionAfter(steps[1], new PointMm(3000, 0, 2700), request));
    }

    [Fact]
    public void RadialPartialTotal_EndsAtTheTotalAngle()
    {
        var request = Parse("{elementIds: [1], kind: 'radial', count: 4, centerMm: {x: 0, y: 0}, totalAngleDeg: 90}");
        Assert.Equal(30.0, ArrayMath.StepAngleDeg(request), 9);
        Assert.Equal(90.0, ArrayMath.Steps(request, null, new PointMm(0, 0, 0))[^1].RotationDeg, 9);

        var perStep = Parse("{elementIds: [1], kind: 'radial', count: 3, centerMm: {x: 0, y: 0}, angleDeg: -20}");
        Assert.Equal(-40.0, ArrayMath.Steps(perStep, null, new PointMm(0, 0, 0))[^1].RotationDeg, 9);
    }

    [Fact]
    public void RadialWithoutElementRotation_TranslatesTheSetCentre()
    {
        var request = Parse("{elementIds: [1, 2], kind: 'radial', count: 2, centerMm: {x: 0, y: 0, z: 0}, angleDeg: 90, rotateElements: false}");
        var reference = ArrayMath.Centroid(new[] { new PointMm(900, 0, 0), new PointMm(1100, 0, 0) });
        var steps = ArrayMath.Steps(request, null, reference);
        Assert.Single(steps);
        Assert.Equal(0.0, steps[0].RotationDeg);
        AssertPoint(new PointMm(-1000, 1000, 0), steps[0].TranslationMm);
    }

    [Fact]
    public void RotatePoint_IsCounterClockwiseAboutEachAxis()
    {
        AssertPoint(new PointMm(0, 1, 0), ArrayMath.RotatePoint(new PointMm(1, 0, 0), new PointMm(0, 0, 0), "z", 90));
        AssertPoint(new PointMm(0, 0, 1), ArrayMath.RotatePoint(new PointMm(0, 1, 0), new PointMm(0, 0, 0), "x", 90));
        AssertPoint(new PointMm(1, 0, 0), ArrayMath.RotatePoint(new PointMm(0, 0, 1), new PointMm(0, 0, 0), "y", 90));
        AssertPoint(new PointMm(10, 12, 5), ArrayMath.RotatePoint(new PointMm(12, 10, 5), new PointMm(10, 10, 0), "z", 90));
    }

    [Theory]
    [InlineData("{elementIds: [1], count: 1, spacingMm: {x: 1, y: 0}}")]
    [InlineData("{elementIds: [1], count: 201, spacingMm: {x: 1, y: 0}}")]
    [InlineData("{elementIds: [1], kind: 'radial', count: 2, asGroup: true, centerMm: {x: 0, y: 0}, angleDeg: 10}")]
    [InlineData("{elementIds: [1], count: 200, includeOriginal: false, spacingMm: {x: 1, y: 0}}")]
    public void CountOutOfRange_HasItsStatus(string json)
    {
        Assert.Null(ArrayMath.Parse(Args(json), out var error, out var status));
        Assert.Equal(ArrayMath.CountOutOfRangeStatus, status);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("{count: 3, spacingMm: {x: 1, y: 0}}", "elementIds")]
    [InlineData("{elementIds: [1, 1], count: 3, spacingMm: {x: 1, y: 0}}", "appears 2 times")]
    [InlineData("{elementIds: [1], kind: 'spiral', count: 3}", "linear' or 'radial")]
    [InlineData("{elementIds: [1], count: 3}", "spacingMm")]
    [InlineData("{elementIds: [1], count: 3, spacingMm: {x: 1, y: 0}, totalMm: {x: 1, y: 0}}", "one way")]
    [InlineData("{elementIds: [1], kind: 'radial', count: 3, angleDeg: 10}", "centerMm")]
    [InlineData("{elementIds: [1], kind: 'radial', count: 3, centerMm: {x: 0, y: 0}}", "exactly one")]
    [InlineData("{elementIds: [1], kind: 'radial', count: 3, centerMm: {x: 0, y: 0}, angleDeg: 5, totalAngleDeg: 10}", "exactly one")]
    [InlineData("{elementIds: [1], kind: 'radial', count: 3, centerMm: {x: 0, y: 0}, angleDeg: 0}", "zero")]
    [InlineData("{elementIds: [1], count: 3, asGroup: true, includeOriginal: false, spacingMm: {x: 1, y: 0}}", "includeOriginal=true")]
    public void BadRequests_AreRefusedWithAReason(string json, string expected)
    {
        Assert.Null(ArrayMath.Parse(Args(json), out var error, out _));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Defaults_ArePlainCopiesIncludingTheOriginal()
    {
        var request = Parse("{elementIds: [7], count: 2, spacingMm: {x: 100, y: 0}}");
        Assert.Equal(ArrayKind.Linear, request.Kind);
        Assert.False(request.AsGroup);
        Assert.True(request.IncludeOriginal);
        Assert.True(request.Atomic);
        Assert.True(request.RotateElements);
        Assert.Equal("z", request.Axis);
    }
}
