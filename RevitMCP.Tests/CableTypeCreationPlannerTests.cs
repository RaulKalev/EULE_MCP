using RevitMCP.Addin.Electrical;
using Xunit;

namespace RevitMCP.Tests;

public class CableTypeCreationPlannerTests
{
    private static CableTypeCreationItem Item(string name) => new() { NewName = name };

    [Theory]
    [InlineData(null, "skip")]
    [InlineData("", "skip")]
    [InlineData("Skip", "skip")]
    [InlineData(" ERROR ", "error")]
    [InlineData("overwrite", null)]
    public void NormalizeIfExists_AcceptsSkipAndError(string? input, string? expected) =>
        Assert.Equal(expected, CableTypeCreationPlanner.NormalizeIfExists(input));

    [Fact]
    public void NewName_IsCreated()
    {
        var plan = CableTypeCreationPlanner.Plan([Item("ATS ahelakaabel 2x2x0,8")], ["Default"], "skip");
        Assert.Equal(CableTypeCreationAction.Create, Assert.Single(plan).Action);
    }

    [Fact]
    public void ExistingName_IsSkippedCaseInsensitively_WithSkip()
    {
        var plan = CableTypeCreationPlanner.Plan([Item("default")], ["Default"], "skip");
        var entry = Assert.Single(plan);
        Assert.Equal(CableTypeCreationAction.SkipExisting, entry.Action);
        Assert.Equal("Default", entry.ExistingName);
    }

    [Fact]
    public void ExistingName_IsBlocked_WithError()
    {
        var plan = CableTypeCreationPlanner.Plan([Item("Default")], ["Default"], "error");
        Assert.Equal(CableTypeCreationAction.Blocked, Assert.Single(plan).Action);
    }

    [Fact]
    public void DuplicateNameInBatch_SecondIsBlocked()
    {
        var plan = CableTypeCreationPlanner.Plan([Item("A"), Item("a")], [], "skip");
        Assert.Equal(CableTypeCreationAction.Create, plan[0].Action);
        Assert.Equal(CableTypeCreationAction.Blocked, plan[1].Action);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" padded")]
    [InlineData("bad:name")]
    public void InvalidName_IsBlocked(string name)
    {
        var plan = CableTypeCreationPlanner.Plan([Item(name)], [], "skip");
        Assert.Equal(CableTypeCreationAction.Blocked, Assert.Single(plan).Action);
    }

    [Fact]
    public void Batch_MixesActionsInRequestOrder()
    {
        var plan = CableTypeCreationPlanner.Plan(
            [Item("ATS tulekindel 1x2x1,5 FE180"), Item("Default"), Item("x|y")],
            ["Default"],
            "skip");
        Assert.Equal(
            [CableTypeCreationAction.Create, CableTypeCreationAction.SkipExisting, CableTypeCreationAction.Blocked],
            plan.Select(p => p.Action).ToArray());
    }
}
