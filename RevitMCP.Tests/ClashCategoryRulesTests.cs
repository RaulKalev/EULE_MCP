using RevitMCP.Addin.Coordination.Clash.Services;
using Xunit;

namespace RevitMCP.Tests;

public class ClashCategoryRulesTests
{
    private static ClashCategoryFacts Model(string builtIn, string name, int? count = 10, bool? solid = true) => new()
    {
        Name = name, BuiltInName = builtIn, CategoryType = "Model", ElementCount = count, HasSolidGeometry = solid
    };

    [Theory]
    [InlineData("OST_CommunicationDevices", "Communication Devices")]
    [InlineData("OST_DataDevices", "Data Devices")]
    [InlineData("OST_SecurityDevices", "Security Devices")]
    [InlineData("OST_NurseCallDevices", "Nurse Call Devices")]
    [InlineData("OST_TelephoneDevices", "Telephone Devices")]
    [InlineData("OST_LightingFixtures", "Lighting Fixtures")]
    [InlineData("OST_LightingDevices", "Lighting Devices")]
    [InlineData("OST_ElectricalEquipment", "Electrical Equipment")]
    [InlineData("OST_Doors", "Doors")]
    [InlineData("OST_CableTrayFitting", "Cable Tray Fittings")]
    [InlineData("OST_ConduitFitting", "Conduit Fittings")]
    [InlineData("OST_DuctFitting", "Duct Fittings")]
    [InlineData("OST_PipeFitting", "Pipe Fittings")]
    public void PhysicalModelCategoryWithSolids_IsClashable(string builtIn, string name)
    {
        var verdict = ClashCategoryRules.Evaluate(Model(builtIn, name));
        Assert.True(verdict.Clashable, verdict.Reason);
    }

    [Theory]
    [InlineData("Annotation")]
    [InlineData("Internal")]
    [InlineData("AnalyticalModel")]
    [InlineData("")]
    public void NonModelCategoryType_IsRejected(string type)
    {
        var facts = Model("OST_TextNotes", "Text Notes");
        facts.CategoryType = type;
        Assert.False(ClashCategoryRules.Evaluate(facts).Clashable);
    }

    [Fact]
    public void TagCategory_IsRejected()
    {
        var facts = Model("OST_DoorTags", "Door Tags");
        facts.IsTagCategory = true;
        var verdict = ClashCategoryRules.Evaluate(facts);
        Assert.False(verdict.Clashable);
        Assert.Contains("tag", verdict.Reason);
    }

    [Fact]
    public void Subcategory_IsRejected()
    {
        var facts = Model("OST_DoorsFrameMullionProjection", "Frame/Mullion");
        facts.IsSubcategory = true;
        Assert.Contains("subcategory", ClashCategoryRules.Evaluate(facts).Reason);
    }

    [Fact]
    public void NonBuiltInCategory_IsRejected_PointingToImportedGeometry()
    {
        var verdict = ClashCategoryRules.Evaluate(Model("", "plan.dwg"));
        Assert.False(verdict.Clashable);
        Assert.Contains(ClashCategoryRules.ImportedGeometry, verdict.Reason);
    }

    [Theory]
    [InlineData("OST_Rooms")]
    [InlineData("OST_MEPSpaces")]
    [InlineData("OST_Areas")]
    [InlineData("OST_ElectricalCircuit")]
    [InlineData("OST_Wire")]
    [InlineData("OST_IOSModelGroups")]
    [InlineData("OST_Assemblies")]
    [InlineData("OST_Lines")]
    [InlineData("OST_RvtLinks")]
    [InlineData("OST_DetailComponents")]
    [InlineData("OST_ProjectBasePoint")]
    [InlineData("OST_AnalyticalMember")]
    public void NonPhysicalModelCategory_IsRejected(string builtIn)
    {
        Assert.False(ClashCategoryRules.Evaluate(Model(builtIn, builtIn)).Clashable);
    }

    [Fact]
    public void CategoryWithoutElements_IsRejected()
    {
        var verdict = ClashCategoryRules.Evaluate(Model("OST_Doors", "Doors", count: 0, solid: null));
        Assert.False(verdict.Clashable);
        Assert.Contains("no elements", verdict.Reason);
    }

    [Fact]
    public void CategoryWithoutSolidGeometry_IsRejected()
    {
        var verdict = ClashCategoryRules.Evaluate(Model("OST_GenericModel", "Generic Models", count: 3, solid: false));
        Assert.False(verdict.Clashable);
        Assert.Contains("solid", verdict.Reason);
    }

    [Fact]
    public void KindOnlyFacts_ForNameResolution_AreClashable()
    {
        // Name resolution checks the kind only: count and geometry are not known yet.
        Assert.True(ClashCategoryRules.Evaluate(Model("OST_CommunicationDevices", "Communication Devices", null, null)).Clashable);
    }

    [Theory]
    [InlineData("Cable Trays", "OST_CableTray")]
    [InlineData("fire alarm devices", "OST_FireAlarmDevices")]
    [InlineData(" Structural Foundations ", "OST_StructuralFoundation")]
    [InlineData("OST_Doors", "OST_Doors")]
    [InlineData("ost_CommunicationDevices", "OST_CommunicationDevices")]
    public void TryResolveAlias_MapsAliasesAndBuiltInNames(string input, string expected)
    {
        Assert.Equal(expected, ClashCategoryRules.TryResolveAlias(input));
    }

    [Theory]
    [InlineData("Communication Devices")]
    [InlineData("Uksed")]
    [InlineData("")]
    public void TryResolveAlias_LeavesDocumentNamesToTheDocument(string input)
    {
        Assert.Null(ClashCategoryRules.TryResolveAlias(input));
    }

    [Fact]
    public void Order_PutsPreferredCategoriesFirstThenByName()
    {
        var items = new[]
        {
            ("OST_SecurityDevices", "Security Devices"),
            ("OST_FireAlarmDevices", "Fire Alarm Devices"),
            ("OST_CommunicationDevices", "Communication Devices"),
            ("OST_CableTray", "Cable Trays"),
        };
        var ordered = ClashCategoryRules.Order(items, i => i.Item1, i => i.Item2).Select(i => i.Item2).ToList();
        Assert.Equal(new[] { "Cable Trays", "Fire Alarm Devices", "Communication Devices", "Security Devices" }, ordered);
    }

    [Fact]
    public void DefaultPresets_UseOnlyClashableCategoryNames()
    {
        foreach (var preset in ClashPresetService.GetDefaultPresets())
        {
            Assert.True(new ClashPresetService().Validate(preset).valid);
            foreach (var rule in preset.Rules)
                foreach (var name in rule.SourceCategories.Concat(rule.TargetCategories))
                    Assert.True(name == ClashCategoryRules.ImportedGeometry || ClashCategoryRules.TryResolveAlias(name) != null,
                        $"Preset '{preset.Name}' category '{name}' no longer resolves.");
        }
    }
}
