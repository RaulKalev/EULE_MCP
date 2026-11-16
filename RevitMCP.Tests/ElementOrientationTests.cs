using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#162: pin/unpin and flip facing/hand — decisions, summaries and the tool surface.</summary>
public class ElementOrientationTests
{
    private static OrientationFacts Hosted(bool pinned = false) => new()
    {
        ElementId = 1, IsFamilyInstance = true, CanFlipFacing = true, CanFlipHand = true, Pinned = pinned
    };

    [Fact]
    public void ABasicHostedDeviceCanBeFlipped()
    {
        var d = ElementOrientationMath.DecideFlip(Hosted(), facing: true, hand: true, unpinToFlip: false);
        Assert.True(d.Ready);
        Assert.False(d.NeedsUnpin);
    }

    [Fact]
    public void ANonHostedFamilyThatCannotFlipHandIsRefusedWithAReason()
    {
        var f = Hosted();
        f.CanFlipHand = false;
        var d = ElementOrientationMath.DecideFlip(f, facing: false, hand: true, unpinToFlip: false);
        Assert.Equal(OrientationStatus.CannotFlip, d.Status);
        Assert.Contains("hand", d.Reason);
        // The same instance can still flip facing.
        Assert.True(ElementOrientationMath.DecideFlip(f, true, false, false).Ready);
    }

    [Fact]
    public void ANonFamilyInstanceCannotFlip()
    {
        var d = ElementOrientationMath.DecideFlip(new OrientationFacts { ElementId = 2 }, true, false, false);
        Assert.Equal(OrientationStatus.NotFamilyInstance, d.Status);
    }

    [Fact]
    public void APinnedTargetIsNeverUnpinnedUnlessAsked()
    {
        var blocked = ElementOrientationMath.DecideFlip(Hosted(pinned: true), true, false, unpinToFlip: false);
        Assert.Equal(OrientationStatus.Pinned, blocked.Status);
        Assert.Contains("unpinToFlip", blocked.Reason);

        var allowed = ElementOrientationMath.DecideFlip(Hosted(pinned: true), true, false, unpinToFlip: true);
        Assert.True(allowed.Ready);
        Assert.True(allowed.NeedsUnpin);
    }

    [Fact]
    public void AGroupedTargetIsBlockedForPinAndFlip()
    {
        var f = Hosted();
        f.GroupId = 77;
        Assert.Equal(OrientationStatus.Grouped, ElementOrientationMath.DecideFlip(f, true, false, false).Status);
        var pin = ElementOrientationMath.DecidePin(f, true);
        Assert.Equal(OrientationStatus.Grouped, pin.Status);
        Assert.Contains("77", pin.Reason);
    }

    [Fact]
    public void AWorksharedElementOwnedBySomeoneElseIsBlocked()
    {
        var f = Hosted();
        f.OwnedByOther = "anna";
        var d = ElementOrientationMath.DecidePin(f, true);
        Assert.Equal(OrientationStatus.OwnedByOther, d.Status);
        Assert.Contains("anna", d.Reason);
    }

    [Fact]
    public void MissingElementsAndTypesAreReported()
    {
        Assert.Equal(OrientationStatus.NotFound, ElementOrientationMath.DecidePin(new OrientationFacts { Found = false }, true).Status);
        Assert.Equal(OrientationStatus.NotAnInstance, ElementOrientationMath.DecidePin(new OrientationFacts { IsType = true }, true).Status);
    }

    [Theory]
    [InlineData(true, true, OrientationStatus.NoOp)]
    [InlineData(false, false, OrientationStatus.NoOp)]
    [InlineData(false, true, OrientationStatus.Ready)]
    [InlineData(true, false, OrientationStatus.Ready)]
    public void PinIsIdempotent(bool current, bool wanted, string expected)
    {
        var d = ElementOrientationMath.DecidePin(new OrientationFacts { Pinned = current }, wanted);
        Assert.Equal(expected, d.Status);
    }

    [Fact]
    public void FlipNeedsAnAxis()
    {
        Assert.NotNull(ElementOrientationMath.ValidateFlip(false, false));
        Assert.Null(ElementOrientationMath.ValidateFlip(true, false));
        Assert.Equal("facing and hand", ElementOrientationMath.DescribeFlip(true, true));
        Assert.Equal("hand", ElementOrientationMath.DescribeFlip(false, true));
    }

    [Fact]
    public void SummaryCountsAndLine()
    {
        var counts = ElementOrientationMath.CountByStatus(new[] { "ready", "ready", "noOp", "grouped", "ready" });
        Assert.Equal(3, counts["ready"]);
        var line = ElementOrientationMath.SummaryLine("would be pinned", 5, counts);
        Assert.Contains("3 of 5", line);
        Assert.Contains("1 noOp", line);
        Assert.Contains("1 grouped", line);
    }

    [Theory]
    [InlineData(0, ElementOrientationMath.DefaultSampleSize)]
    [InlineData(5, 5)]
    [InlineData(5000, ElementOrientationMath.MaxSampleSize)]
    public void SampleSizeIsClamped(int requested, int expected) => Assert.Equal(expected, ElementOrientationMath.ClampSample(requested));

    [Fact]
    public void ALocationShiftIsDetectedAboveTolerance()
    {
        Assert.False(ElementOrientationMath.MovedBeyond(0.1));
        Assert.True(ElementOrientationMath.MovedBeyond(2.0));
    }

    // ── Surface ──

    [Theory]
    [InlineData("revit_preview_set_pin_state", "ReadOnly")]
    [InlineData("revit_set_pin_state", "RequiresApproval")]
    [InlineData("revit_preview_flip_family_instances", "ReadOnly")]
    [InlineData("revit_flip_family_instances", "RequiresApproval")]
    public void ToolsAreInTheEditGroupWithTheRightPermissions(string tool, string permission)
    {
        Assert.Equal("edit", BridgeToolGroups.GroupOf(tool));
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
    }

    [Theory]
    [InlineData("revit_set_pin_state")]
    [InlineData("revit_flip_family_instances")]
    public void TheWritesModifyForPlans(string tool)
    {
        Assert.Equal(PlanToolKind.Modifies, PlanToolClassification.Classify(tool));
        var args = JObject.Parse("{\"elementIds\":[6,7]}").Properties().ToDictionary(p => p.Name, p => (object?)p.Value);
        Assert.Equal(new long[] { 6, 7 }, PlanArgumentScope.Extract(args).ElementIds.OrderBy(i => i));
    }
}
