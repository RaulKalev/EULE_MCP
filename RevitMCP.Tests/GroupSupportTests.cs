using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Placement;
using Xunit;

namespace RevitMCP.Tests;

public class GroupSupportTests
{
    private static GroupMemberInfo Model(long id, string category = "Electrical Fixtures") =>
        new() { Id = id, Category = category, ClassName = "FamilyInstance" };

    private static GroupMemberInfo InView(long id, long viewId, string category = "Detail Items") =>
        new() { Id = id, Category = category, ClassName = "FamilyInstance", OwnerViewId = viewId };

    [Fact]
    public void AllModelElements_MakeAModelGroup()
    {
        var result = GroupSupport.DetectKind(new[] { Model(1), Model(2, "Lighting Fixtures") });
        Assert.Equal(GroupKind.Model, result.Kind);
        Assert.Null(result.Error);
        Assert.Null(result.ViewId);
    }

    [Fact]
    public void ViewSpecificElementsOfOneView_MakeADetailGroup()
    {
        var result = GroupSupport.DetectKind(new[] { InView(1, 50), InView(2, 50, "Tags") });
        Assert.Equal(GroupKind.Detail, result.Kind);
        Assert.Equal(50, result.ViewId);
    }

    [Fact]
    public void MixedModelAndDetail_IsRefusedNamingTheOffenders()
    {
        var result = GroupSupport.DetectKind(new[] { Model(1), Model(2), Model(3), InView(9, 50) });
        Assert.Null(result.Kind);
        Assert.Equal(GroupSupport.MembersMixedStatus, result.Status);
        Assert.Equal(new long[] { 9 }, result.Offenders);
        Assert.Contains("attached detail group", result.Error);
    }

    [Fact]
    public void SeveralViews_AreRefused()
    {
        var result = GroupSupport.DetectKind(new[] { InView(1, 50), InView(2, 50), InView(3, 60) });
        Assert.Null(result.Kind);
        Assert.Equal(GroupSupport.MembersMixedStatus, result.Status);
        Assert.Equal(new long[] { 3 }, result.Offenders);
    }

    [Fact]
    public void PinnedGroupedMissingAndViews_Block()
    {
        var result = GroupSupport.DetectKind(new[]
        {
            Model(1),
            new GroupMemberInfo { Id = 2, Pinned = true, Category = "Walls" },
            new GroupMemberInfo { Id = 3, GroupId = 77, Category = "Walls" },
            new GroupMemberInfo { Id = 4, Exists = false },
            new GroupMemberInfo { Id = 5, NotGroupable = true, ClassName = "ViewPlan" }
        });
        Assert.Null(result.Kind);
        Assert.Null(result.Status);
        Assert.Equal(new long[] { 2, 3, 4, 5 }, result.Offenders);
        Assert.Contains("Pinned", result.Error);
        Assert.Contains("group 77", result.Error);
    }

    [Fact]
    public void UniqueNames_AreNumberedCaseInsensitively()
    {
        Assert.Equal("Classroom EN", GroupSupport.ResolveUniqueName("Classroom EN", new[] { "Other" }));
        Assert.Equal("Classroom EN 2", GroupSupport.ResolveUniqueName("Classroom EN", new[] { "classroom en" }));
        Assert.Equal("Classroom EN 3", GroupSupport.ResolveUniqueName(" Classroom EN ", new[] { "Classroom EN", "Classroom EN 2" }));
        Assert.Equal(GroupSupport.DefaultName, GroupSupport.ResolveUniqueName(null, Array.Empty<string>()));
        Assert.Equal(GroupSupport.DefaultName + " 2", GroupSupport.ResolveUniqueName("", new[] { GroupSupport.DefaultName }));
    }

    [Fact]
    public void InvalidNames_AreRejected()
    {
        Assert.False(GroupSupport.IsValidName("Room {A}", out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.True(GroupSupport.IsValidName("Room A", out _));
    }

    [Theory]
    [InlineData("OST_IOSModelGroups", GroupKind.Model)]
    [InlineData("OST_IOSDetailGroups", GroupKind.Detail)]
    [InlineData("OST_IOSAttachedDetailGroups", GroupKind.AttachedDetail)]
    [InlineData("OST_Walls", null)]
    [InlineData(null, null)]
    public void KindFromCategory(string? category, string? expected) => Assert.Equal(expected, GroupKind.FromCategory(category));

    [Fact]
    public void MixedInstances_AreInstancesWithDifferentMemberCounts()
    {
        Assert.False(GroupSupport.HasMixedInstances(new[] { 12, 12, 12 }));
        Assert.False(GroupSupport.HasMixedInstances(Array.Empty<int>()));
        Assert.True(GroupSupport.HasMixedInstances(new[] { 12, 11, 12 }));
    }

    [Fact]
    public void Placements_ReadPointsRotationViewAndLevel()
    {
        var placements = GroupSupport.ParsePlacements(JArray.Parse(
            "[{pointMm: {x: 1000, y: 2000}, rotationDeg: 90}, {pointMm: {x: 1, y: 2, z: 3000}, viewId: 44, levelOverride: 'L2'}, {x: 5, y: 6}]"),
            out var error);
        Assert.Null(error);
        Assert.Equal(3, placements!.Count);
        Assert.False(placements[0].HasZ);
        Assert.Equal(90, placements[0].RotationDeg);
        Assert.True(placements[1].HasZ);
        Assert.Equal(3000, placements[1].PointMm.Z);
        Assert.Equal(44, placements[1].ViewId);
        Assert.Equal("L2", placements[1].LevelOverride);
        Assert.Equal(5, placements[2].PointMm.X);
        Assert.Equal(2, placements[2].Index);
    }

    [Fact]
    public void Placements_AreValidated()
    {
        Assert.Null(GroupSupport.ParsePlacements(null, out var error));
        Assert.Contains("placements", error);
        Assert.Null(GroupSupport.ParsePlacements(JArray.Parse("[{rotationDeg: 5}]"), out error));
        Assert.Contains("placements[0]", error);
        var tooMany = new JArray(Enumerable.Range(0, GroupSupport.MaxPlacements + 1).Select(i => new JObject { ["x"] = i, ["y"] = 0 }));
        Assert.Null(GroupSupport.ParsePlacements(tooMany, out error));
        Assert.Contains("limit", error);
        // A JSON string is read too.
        Assert.Single(GroupSupport.ParsePlacements("[{\"pointMm\": [1, 2]}]", out _)!);
    }

    [Fact]
    public void GroupTypes_MatchByIdNameOrUniqueFragment()
    {
        var types = new List<(long Id, string Name, string Kind)>
        {
            (101, "Classroom EN", GroupKind.Model),
            (102, "Classroom EN tags", GroupKind.Detail),
            (103, "Corridor", GroupKind.Model)
        };
        Assert.Equal(2, GroupSupport.MatchGroupType("103", types, out _));
        Assert.Equal(0, GroupSupport.MatchGroupType("classroom en", types, out _));
        Assert.Equal(2, GroupSupport.MatchGroupType("corr", types, out _));

        Assert.Equal(-1, GroupSupport.MatchGroupType("Classroom", types, out var ambiguous));
        Assert.Contains("matches 2", ambiguous);
        Assert.Equal(-1, GroupSupport.MatchGroupType("Office", types, out var missing));
        Assert.Contains("No group type named 'Office'", missing);
        Assert.Equal(-1, GroupSupport.MatchGroupType("", types, out var empty));
        Assert.Contains("revit_list_groups", empty);
    }
}
