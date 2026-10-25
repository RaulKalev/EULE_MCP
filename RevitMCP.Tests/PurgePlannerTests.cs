using RevitMCP.Addin.Families;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#102: what revit_purge_unused removes, what it never touches, and the pass loop.</summary>
public class PurgePlannerTests
{
    private static PurgeTypeInfo Type(long id, long familyId, string family, string name, int instances = 0, string category = "Electrical Fixtures") => new()
    {
        TypeId = id, FamilyId = familyId, FamilyName = family, TypeName = name, InstanceCount = instances, Category = category
    };

    private static PurgeFamilyInfo Family(long id, string name, string category = "Electrical Fixtures") => new()
    {
        FamilyId = id, Name = name, Category = category
    };

    private static PurgePlan Plan(IEnumerable<PurgeTypeInfo> types, IEnumerable<PurgeFamilyInfo> families, PurgeOptions? options = null, IEnumerable<PurgeOtherInfo>? others = null)
        => PurgePlanner.Plan(types, families, others ?? Array.Empty<PurgeOtherInfo>(), options ?? new PurgeOptions());

    [Fact]
    public void UnusedTypesOfAUsedFamily_ArePurged_TheFamilyStays()
    {
        var plan = Plan(new[] { Type(11, 1, "Socket", "Single", instances: 4), Type(12, 1, "Socket", "Double") }, new[] { Family(1, "Socket") });

        Assert.Equal(new long[] { 12 }, plan.Types.Select(t => t.Id));
        Assert.Empty(plan.Families);
        Assert.Equal(new long[] { 12 }, plan.DeletionIds());
        Assert.Equal(1, plan.FamiliesTouched);
    }

    [Fact]
    public void AFamilyWithEveryTypeUnused_IsPurgedAsAWhole()
    {
        var plan = Plan(new[] { Type(21, 2, "Lamp", "A"), Type(22, 2, "Lamp", "B") }, new[] { Family(2, "Lamp") });

        Assert.Equal(2, plan.Types.Count);
        Assert.Equal(new long[] { 2 }, plan.Families.Select(f => f.Id));
        Assert.Equal(new long[] { 2 }, plan.DeletionIds());   // deleting the family takes its types along
    }

    [Fact]
    public void InPlaceFamilies_AreNeverPurged()
    {
        var family = Family(3, "Custom Bench");
        family.IsInPlace = true;
        var type = Type(31, 3, "Custom Bench", "Custom Bench");
        type.IsInPlace = true;

        var plan = Plan(new[] { type }, new[] { family });

        Assert.True(plan.IsEmpty);
        Assert.Equal(PurgePlanner.ReasonInPlace, Assert.Single(plan.Protected).Reason);
    }

    [Fact]
    public void ProtectedTypes_StayAndKeepTheirFamily()
    {
        var mapped = Type(41, 4, "Detector", "Smoke");
        mapped.Protection = "deviceMap";
        var plan = Plan(new[] { mapped, Type(42, 4, "Detector", "Heat") }, new[] { Family(4, "Detector") });

        Assert.Equal(new long[] { 42 }, plan.Types.Select(t => t.Id));
        Assert.Empty(plan.Families);
        Assert.Equal("deviceMap", Assert.Single(plan.Protected).Reason);
    }

    [Fact]
    public void ATypeRevitSaysIsUsed_Stays()
    {
        var tag = Type(51, 5, "Device Tag", "Standard", category: "Electrical Fixture Tags");
        tag.UnusedPerRevit = false;   // e.g. the category's default tag type
        var plan = Plan(new[] { tag }, new[] { Family(5, "Device Tag", "Electrical Fixture Tags") });

        Assert.True(plan.IsEmpty);
        Assert.Equal(PurgePlanner.ReasonUsedPerRevit, Assert.Single(plan.Protected).Reason);
    }

    [Fact]
    public void AProtectedFamily_KeepsItsLastType()
    {
        var family = Family(6, "A1 Title Block", "Title Blocks");
        family.Protection = "titleBlockOnSheet";
        var plan = Plan(new[] { Type(61, 6, "A1 Title Block", "A1", category: "Title Blocks"), Type(62, 6, "A1 Title Block", "A1 wide", category: "Title Blocks") },
            new[] { family });

        Assert.Empty(plan.Families);
        Assert.Single(plan.Types);
        Assert.Contains(plan.Protected, p => p.Reason == PurgePlanner.ReasonLastType);
        Assert.Contains(plan.Protected, p => p.Kind == "family" && p.Reason == "titleBlockOnSheet");
    }

    [Fact]
    public void CategoryAndFamilyNameFilters_Narrow()
    {
        var types = new[]
        {
            Type(71, 7, "Socket", "A"), Type(81, 8, "Panel", "B", category: "Electrical Equipment")
        };
        var families = new[] { Family(7, "Socket"), Family(8, "Panel", "Electrical Equipment") };

        var byCategory = Plan(types, families, new PurgeOptions { Categories = { "electrical equipment" } });
        Assert.Equal(new long[] { 81 }, byCategory.Types.Select(t => t.Id));

        var byName = Plan(types, families, new PurgeOptions { FamilyNameContains = "sock" });
        Assert.Equal(new long[] { 71 }, byName.Types.Select(t => t.Id));
    }

    [Fact]
    public void SystemTypes_AreNotFamilyTypes()
    {
        var wallType = Type(91, 0, "Basic Wall", "Generic 200", category: "Walls");
        Assert.True(Plan(new[] { wallType }, Array.Empty<PurgeFamilyInfo>()).IsEmpty);
    }

    [Fact]
    public void OtherElements_OnlyWithIncludeOther_AndProtectionsHold()
    {
        var others = new[]
        {
            new PurgeOtherInfo { Id = 501, Name = "Dash 2", Kind = "LinePatternElement" },
            new PurgeOtherInfo { Id = 502, Name = "EL Plan", Kind = "View", Protection = "viewTemplate" },
            new PurgeOtherInfo { Id = 11, Name = "Single", Kind = "FamilySymbol" }   // already decided as a family type
        };
        var types = new[] { Type(11, 1, "Socket", "Single", instances: 1) };
        var families = new[] { Family(1, "Socket") };

        Assert.Empty(Plan(types, families, null, others).Other);

        var plan = Plan(types, families, new PurgeOptions { IncludeOther = true }, others);
        Assert.Equal(new long[] { 501 }, plan.Other.Select(o => o.Id));
        Assert.Contains(plan.Protected, p => p.Id == 502 && p.Reason == "viewTemplate");
        Assert.DoesNotContain(plan.Other, o => o.Id == 11);

        var narrowed = Plan(types, families, new PurgeOptions { IncludeOther = true, FamilyNameContains = "Sock" }, others);
        Assert.Empty(narrowed.Other);
        Assert.NotEmpty(narrowed.Notes);
    }

    [Fact]
    public void AnEmptyFamily_IsPurged()
    {
        var plan = Plan(Array.Empty<PurgeTypeInfo>(), new[] { Family(9, "Leftover") });
        Assert.Equal(new long[] { 9 }, plan.Families.Select(f => f.Id));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(7, 3)]
    public void PassesAreClamped(int requested, int expected) => Assert.Equal(expected, PurgePlanner.NormalizePasses(requested));

    [Fact]
    public void Passes_PickUpWhatThePreviousPassOrphaned()
    {
        // Pass 1 purges the parent family; only then does its nested family become unused.
        var model = new List<PurgeTypeInfo> { Type(101, 10, "Parent", "P") };
        var nested = Type(201, 20, "Nested", "N");
        nested.UnusedPerRevit = false;
        model.Add(nested);
        var families = new List<PurgeFamilyInfo> { Family(10, "Parent"), Family(20, "Nested") };
        families[1].UnusedPerRevit = false;

        var passes = PurgePlanner.RunPasses(3,
            _ => PurgePlanner.Plan(model, families, Array.Empty<PurgeOtherInfo>(), new PurgeOptions()),
            plan =>
            {
                var ids = plan.DeletionIds();
                model.RemoveAll(t => ids.Contains(t.TypeId) || ids.Contains(t.FamilyId));
                families.RemoveAll(f => ids.Contains(f.FamilyId));
                // The nested family is orphaned once its parent is gone.
                foreach (var t in model) t.UnusedPerRevit = true;
                foreach (var f in families) f.UnusedPerRevit = true;
                return ids.Count;
            });

        Assert.Equal(2, passes.Count);
        Assert.Equal(new long[] { 10 }, passes[0].Families.Select(f => f.Id));
        Assert.Equal(new long[] { 20 }, passes[1].Families.Select(f => f.Id));
        Assert.Empty(model);
    }

    [Fact]
    public void Passes_StopWhenNothingIsLeft()
    {
        var calls = 0;
        var passes = PurgePlanner.RunPasses(3, _ => { calls++; return new PurgePlan(); }, _ => 1);
        Assert.Single(passes);
        Assert.True(passes[0].IsEmpty);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void BatchesOf500()
    {
        var ids = Enumerable.Range(1, 1201).Select(i => (long)i).ToList();
        Assert.Equal(new[] { 500, 500, 201 }, PurgePlanner.Batches(ids).Select(b => b.Count));
    }

    [Fact]
    public void SummaryWording()
    {
        Assert.Equal("DESTRUCTIVE: purge 12 unused family types in 5 families (+ 3 other unused elements). Manual approval required.",
            PurgePlanner.Summary(12, 5, 3));
        Assert.Equal("DESTRUCTIVE: purge 1 unused family type in 1 family. Manual approval required.", PurgePlanner.Summary(1, 1, 0));
    }
}
