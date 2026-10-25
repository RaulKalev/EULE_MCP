using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Tagging;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// revit_retag / revit_preview_retag: the bridge forwards only what the caller gave, the add-in keeps
/// each tag's leader and orientation unless given, and an explicit direction wins over the current head.
/// </summary>
public class RetagTests
{
    private static Dictionary<string, object?> Build(
        string? direction = null,
        bool? addLeader = null,
        string? leaderEndCondition = null,
        string? orientation = null)
    {
        return TagToolArgs.BuildRetagArgs(
            viewId: 0, tagIds: null, elementIds: null, direction: direction, anchorPoint: "Center",
            attachedLengthMm: 0, freeLengthMm: 0, addLeader: addLeader, leaderEndCondition: leaderEndCondition,
            orientation: orientation, rotationDegrees: 0, detectElementRotation: false,
            enableCollisionDetection: true, collisionGapMm: 1, minimumOffsetMm: 300);
    }

    [Fact]
    public void BuildRetagArgs_OmittedOptionalSettings_AreNotSent()
    {
        var args = Build();

        Assert.False(args.ContainsKey("direction"));
        Assert.False(args.ContainsKey("addLeader"));
        Assert.False(args.ContainsKey("leaderEndCondition"));
        Assert.False(args.ContainsKey("orientation"));
        // The add-in reads presence as "requested", so none of them may leak a default.
        Assert.False(RetagDecision.IsArgumentGiven(args!, "addLeader"));
        Assert.False(RetagDecision.IsArgumentGiven(args!, "orientation"));
        Assert.False(RetagDecision.IsArgumentGiven(args!, "direction"));
    }

    [Fact]
    public void BuildRetagArgs_GivenSettings_AreForwarded()
    {
        var args = Build(direction: "Left", addLeader: false, leaderEndCondition: "Free", orientation: "Vertical");

        Assert.Equal("Left", args["direction"]);
        Assert.Equal(false, args["addLeader"]);
        Assert.Equal("Free", args["leaderEndCondition"]);
        Assert.Equal("Vertical", args["orientation"]);
    }

    [Fact]
    public void BuildRetagArgs_OnlyDirection_LeavesLeaderAndOrientationOut()
    {
        // The live-test case: direction=Left alone must not remove leaders or force Horizontal.
        var args = Build(direction: "Left");

        Assert.Equal("Left", args["direction"]);
        Assert.False(args.ContainsKey("addLeader"));
        Assert.False(args.ContainsKey("orientation"));
        Assert.False(args.ContainsKey("leaderEndCondition"));
    }

    [Fact]
    public void BuildRetagArgs_BlankStrings_AreTreatedAsOmitted()
    {
        var args = Build(direction: " ", leaderEndCondition: "", orientation: "");

        Assert.False(args.ContainsKey("direction"));
        Assert.False(args.ContainsKey("leaderEndCondition"));
        Assert.False(args.ContainsKey("orientation"));
    }

    [Fact]
    public void BuildRetagArgs_KeepsPlacementSettings()
    {
        var args = Build();

        Assert.Equal("Center", args["anchorPoint"]);
        Assert.Equal(true, args["enableCollisionDetection"]);
        Assert.Equal(300.0, args["minimumOffsetMm"]);
        Assert.Empty((long[])args["tagIds"]!);
    }

    [Fact]
    public void IsArgumentGiven_NullJsonNullAndBlank_AreNotGiven()
    {
        var args = new Dictionary<string, object>
        {
            ["a"] = null!,
            ["b"] = JValue.CreateNull(),
            ["c"] = "  ",
            ["d"] = new JValue(""),
            ["e"] = new JValue(false),
            ["f"] = "Vertical",
            ["g"] = false
        };

        Assert.False(RetagDecision.IsArgumentGiven(args, "missing"));
        Assert.False(RetagDecision.IsArgumentGiven(args, "a"));
        Assert.False(RetagDecision.IsArgumentGiven(args, "b"));
        Assert.False(RetagDecision.IsArgumentGiven(args, "c"));
        Assert.False(RetagDecision.IsArgumentGiven(args, "d"));
        Assert.True(RetagDecision.IsArgumentGiven(args, "e"));
        Assert.True(RetagDecision.IsArgumentGiven(args, "f"));
        Assert.True(RetagDecision.IsArgumentGiven(args, "g"));
        Assert.False(RetagDecision.IsArgumentGiven(null!, "f"));
    }

    [Theory]
    [InlineData(false, "Requested", "Existing", "Existing")]
    [InlineData(true, "Requested", "Existing", "Requested")]
    public void KeepUnlessSpecified_KeepsExistingWhenNotGiven(
        bool specified, string requested, string existing, string expected)
    {
        Assert.Equal(expected, RetagDecision.KeepUnlessSpecified(specified, requested, existing));
    }

    [Fact]
    public void KeepUnlessSpecified_LeaderAndOrientation()
    {
        // Tag with a leader, not asked about leaders: keeps it.
        Assert.True(RetagDecision.KeepUnlessSpecified(false, false, true));
        // Explicit addLeader=false removes it.
        Assert.False(RetagDecision.KeepUnlessSpecified(true, false, true));
        // Vertical tag, orientation not given: stays Vertical.
        Assert.Equal(TestOrientation.Vertical,
            RetagDecision.KeepUnlessSpecified(false, TestOrientation.Horizontal, TestOrientation.Vertical));
    }

    [Theory]
    [InlineData(true, true, "Free", "Attached", "Free")]
    [InlineData(true, false, "Free", "Attached", "Attached")]
    [InlineData(false, true, "Free", "Attached", "Attached")]
    public void ResolveLeaderEndCondition_AppliesOnlyWithLeaderAndWhenGiven(
        bool resultingHasLeader, bool specified, string requested, string existing, string expected)
    {
        Assert.Equal(expected,
            RetagDecision.ResolveLeaderEndCondition(resultingHasLeader, specified, requested, existing));
    }

    [Theory]
    // Collision avoidance off: always normalize to the computed head.
    [InlineData(false, false, true, true, RetagHeadPlacement.Computed)]
    [InlineData(false, true, true, true, RetagHeadPlacement.Computed)]
    // On, no direction: a collision-free tag stays; a colliding or headless one moves.
    [InlineData(true, false, true, true, RetagHeadPlacement.KeepExisting)]
    [InlineData(true, false, true, false, RetagHeadPlacement.NearestFreeToComputed)]
    [InlineData(true, false, false, false, RetagHeadPlacement.NearestFreeToComputed)]
    // On, explicit direction: honoured even when the current head is free.
    [InlineData(true, true, true, true, RetagHeadPlacement.NearestFreeToComputed)]
    [InlineData(true, true, true, false, RetagHeadPlacement.NearestFreeToComputed)]
    public void ChooseHeadPlacement_ExplicitDirectionWinsOverExistingHead(
        bool collision, bool directionSpecified, bool hasHead, bool headFree, RetagHeadPlacement expected)
    {
        Assert.Equal(expected,
            RetagDecision.ChooseHeadPlacement(collision, directionSpecified, hasHead, headFree));
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, false, false)]
    public void NeedsExistingHeadCheck_OnlyWhenTheHeadMayBeKept(
        bool collision, bool directionSpecified, bool hasHead, bool expected)
    {
        Assert.Equal(expected, RetagDecision.NeedsExistingHeadCheck(collision, directionSpecified, hasHead));
    }

    private enum TestOrientation
    {
        Horizontal,
        Vertical
    }
}
