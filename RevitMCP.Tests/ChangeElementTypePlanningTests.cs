using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Editing;
using RevitMCP.Addin.Plans;
using RevitMCP.Addin.Services;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

public class ChangeElementTypePlanningTests
{
    private static readonly TypeChangeTargetFacts Target = new() { Id = 500, CategoryId = -2001, FamilyName = "Fam", TypeName = "B" };

    private static TypeChangeElementFacts El(long id = 1, Action<TypeChangeElementFacts>? edit = null)
    {
        var f = new TypeChangeElementFacts { Id = id, CurrentTypeId = 400, CategoryId = -2001, TargetIsValid = true };
        edit?.Invoke(f);
        return f;
    }

    private static readonly List<TypeCandidate> Types = new()
    {
        new() { Id = 500, FamilyName = "Fam", TypeName = "B", CategoryName = "Lighting Fixtures" },
        new() { Id = 501, FamilyName = "Other", TypeName = "B", CategoryName = "Lighting Fixtures" },
        new() { Id = 502, FamilyName = "Wall", TypeName = "B", CategoryName = "Walls" },
        new() { Id = 503, FamilyName = "Fam", TypeName = "C", CategoryName = "Lighting Fixtures" }
    };

    // ── Per element ──

    [Fact]
    public void ValidSwitchIsSwitchable()
    {
        var d = ChangeElementTypePlanning.Classify(El(), Target);
        Assert.Equal(TypeChangeStatus.Switchable, d.Status);
        Assert.Null(d.Reason);
    }

    [Fact]
    public void SameTypeIsAnUnchangedNoOpEvenWhenPinned()
    {
        var d = ChangeElementTypePlanning.Classify(El(1, f => { f.CurrentTypeId = 500; f.Pinned = true; }), Target);
        Assert.Equal(TypeChangeStatus.Unchanged, d.Status);
        Assert.Equal(TypeChangeReason.SameType, d.Reason);
    }

    [Theory]
    [InlineData("missing", TypeChangeReason.NotFound)]
    [InlineData("type", TypeChangeReason.IsElementType)]
    [InlineData("linked", TypeChangeReason.LinkedDocument)]
    [InlineData("notype", TypeChangeReason.NoType)]
    [InlineData("group", TypeChangeReason.InGroup)]
    [InlineData("pinned", TypeChangeReason.Pinned)]
    [InlineData("owned", TypeChangeReason.NotEditable)]
    [InlineData("category", TypeChangeReason.CategoryMismatch)]
    [InlineData("invalid", TypeChangeReason.InvalidForElement)]
    public void BlockedCasesCarryAReason(string kind, string reason)
    {
        var f = El(7, e =>
        {
            switch (kind)
            {
                case "missing": e.Exists = false; break;
                case "type": e.IsElementType = true; break;
                case "linked": e.InLinkedDocument = true; break;
                case "notype": e.HasType = false; break;
                case "group": e.GroupId = 99; break;
                case "pinned": e.Pinned = true; break;
                case "owned": e.EditProblem = "owned by someone"; break;
                case "category": e.CategoryId = -2000; break;
                case "invalid": e.TargetIsValid = false; break;
            }
        });
        var d = ChangeElementTypePlanning.Classify(f, Target);
        Assert.Equal(TypeChangeStatus.Blocked, d.Status);
        Assert.Equal(reason, d.Reason);
        Assert.Equal(7, d.ElementId);
        Assert.False(string.IsNullOrEmpty(d.Detail));
    }

    [Fact]
    public void GroupMemberDetailNamesTheGroup()
    {
        var d = ChangeElementTypePlanning.Classify(El(1, f => f.GroupId = 4242), Target);
        Assert.Contains("4242", d.Detail);
    }

    // ── Target resolution ──

    [Fact]
    public void TypeIdWinsAndUnknownIdIsRefused()
    {
        Assert.Equal(503, ChangeElementTypePlanning.ResolveTarget(Types, 503, "x", "y").Type!.Id);
        var unknown = ChangeElementTypePlanning.ResolveTarget(Types, 999, null, null);
        Assert.Null(unknown.Type);
        Assert.Equal("unknownType", unknown.Code);
    }

    [Fact]
    public void UniqueNameResolvesCaseInsensitively()
    {
        Assert.Equal(503, ChangeElementTypePlanning.ResolveTarget(Types, 0, null, " c ").Type!.Id);
    }

    [Fact]
    public void AmbiguousNameIsRefusedNotGuessed()
    {
        var r = ChangeElementTypePlanning.ResolveTarget(Types, 0, null, "B");
        Assert.Null(r.Type);
        Assert.Equal("ambiguousType", r.Code);
        Assert.Equal(3, r.Matches.Count);
        Assert.Contains("typeId", r.Error);
    }

    [Fact]
    public void FamilyOrCategoryNarrowsAnAmbiguousName()
    {
        Assert.Equal(501, ChangeElementTypePlanning.ResolveTarget(Types, 0, "Other", "B").Type!.Id);
        Assert.Equal(502, ChangeElementTypePlanning.ResolveTarget(Types, 0, null, "B", "Walls").Type!.Id);
    }

    [Fact]
    public void UnknownNameIsRefused()
    {
        var r = ChangeElementTypePlanning.ResolveTarget(Types, 0, "Fam", "Nope");
        Assert.Null(r.Type);
        Assert.Equal("unknownType", r.Code);
    }

    // ── Arguments, requireAll, summaries ──

    [Theory]
    [InlineData(0, false, 5L, "", "", false)]
    [InlineData(3, false, 0L, "", "", false)]
    [InlineData(3, false, 0L, "Fam", " ", false)]
    [InlineData(3, false, 5L, "", "", true)]
    [InlineData(0, true, 0L, "", "B", true)]
    [InlineData(5001, false, 5L, "", "", false)]
    public void ArgumentValidation(int ids, bool selection, long typeId, string family, string type, bool ok) =>
        Assert.Equal(ok, ChangeElementTypePlanning.ValidateArguments(ids, selection, typeId, family, type) == null);

    [Fact]
    public void RequireAllRefusesOnlyWhenSomethingIsBlocked()
    {
        var mixed = new[]
        {
            ChangeElementTypePlanning.Classify(El(1), Target),
            ChangeElementTypePlanning.Classify(El(2, f => f.Pinned = true), Target),
            ChangeElementTypePlanning.Classify(El(3, f => f.CurrentTypeId = 500), Target)
        };
        Assert.True(ChangeElementTypePlanning.ShouldRefuse(true, mixed));
        Assert.False(ChangeElementTypePlanning.ShouldRefuse(false, mixed));
        Assert.False(ChangeElementTypePlanning.ShouldRefuse(true, mixed.Where(d => d.Status != TypeChangeStatus.Blocked).ToList()));

        Assert.Equal((1, 1, 1), ChangeElementTypePlanning.Count(mixed));
        Assert.Equal(1, ChangeElementTypePlanning.CountByReason(mixed)[TypeChangeReason.Pinned]);
    }

    [Fact]
    public void ReplacedElementsGetIdMappings()
    {
        var outcomes = new[]
        {
            ChangeElementTypePlanning.Outcome(10, 400, 500, 10),   // same id
            ChangeElementTypePlanning.Outcome(11, 400, 500, 911),  // replaced
            ChangeElementTypePlanning.Failure(12, 400, 500, "nope")
        };
        Assert.Equal("changed", outcomes[0].Status);
        Assert.Equal("replaced", outcomes[1].Status);
        Assert.Equal("failed", outcomes[2].Status);
        var map = ChangeElementTypePlanning.IdMappings(outcomes);
        Assert.Single(map);
        Assert.Equal(911, map[11]);
        Assert.Equal(400, outcomes[1].OldTypeId);
        Assert.Equal(500, outcomes[1].NewTypeId);
    }

    [Theory]
    [InlineData(0, ChangeElementTypePlanning.DefaultSampleSize)]
    [InlineData(7, 7)]
    [InlineData(100000, ChangeElementTypePlanning.MaxSampleSize)]
    public void SampleSizeIsClamped(int requested, int expected) => Assert.Equal(expected, ChangeElementTypePlanning.ClampSample(requested));

    // ── Surface ──

    [Theory]
    [InlineData("revit_preview_change_element_type", "ReadOnly")]
    [InlineData("revit_change_element_type", "RequiresApproval")]
    public void ToolsAreInTheEditGroupWithTheRightPermissions(string tool, string permission)
    {
        Assert.Equal("edit", BridgeToolGroups.GroupOf(tool));
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
    }

    [Fact]
    public void TheWriteModifiesForPlansAndItsIdsAreInScope()
    {
        Assert.Equal(PlanToolKind.Modifies, PlanToolClassification.Classify("revit_change_element_type"));
        var args = JObject.Parse("{\"elementIds\":[6,7],\"typeId\":9}")
            .Properties().ToDictionary(p => p.Name, p => (object?)p.Value);
        Assert.Equal(new long[] { 6, 7 }, PlanArgumentScope.Extract(args).ElementIds.OrderBy(i => i));
    }
}
