using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#169: model-family documents, the pure argument logic and the tool surface.</summary>
public class ModelFamilyDocumentTests
{
    // ── settings ─────────────────────────────────────────────────────────────

    [Fact]
    public void SettingsTableMapsEveryKeyToItsBuiltInParameter()
    {
        var table = ModelFamilyDocumentSpec.Settings.ToDictionary(s => s.Key, s => s.BuiltInParameter);
        Assert.Equal("FAMILY_WORK_PLANE_BASED", table["workPlaneBased"]);
        Assert.Equal("FAMILY_ALWAYS_VERTICAL", table["alwaysVertical"]);
        Assert.Equal("FAMILY_SHARED", table["shared"]);
        Assert.Equal("FAMILY_ALLOW_CUT_WITH_VOIDS", table["cutsWithVoids"]);
        Assert.Equal("ROOM_CALCULATION_POINT", table["roomCalculationPoint"]);
        Assert.Equal("FAMILY_CONTENT_PART_TYPE", table["partType"]);
        Assert.Equal(ModelFamilySettingKind.PartType, ModelFamilyDocumentSpec.Settings.Single(s => s.Key == "partType").Kind);
        Assert.All(ModelFamilyDocumentSpec.Settings.Where(s => s.Key != "partType"), s => Assert.Equal(ModelFamilySettingKind.Bool, s.Kind));
    }

    [Fact]
    public void SettingsAreParsedInTableOrder()
    {
        var problems = new List<string>();
        var token = JObject.Parse("{\"partType\":\" Junction Box \",\"Shared\":\"true\",\"workPlaneBased\":false}");
        var settings = ModelFamilyDocumentSpec.ParseSettings(token, "settings", problems);
        Assert.Empty(problems);
        Assert.Equal(new[] { "workPlaneBased", "shared", "partType" }, settings.Select(s => s.Info.Key));
        Assert.False(settings[0].BoolValue);
        Assert.True(settings[1].BoolValue);
        Assert.Equal("Junction Box", settings[2].TextValue);
        Assert.Equal("workPlaneBased=false, shared=true, partType='Junction Box'", string.Join(", ", settings.Select(s => s.Describe())));
    }

    [Fact]
    public void SettingsProblemsNameTheKey()
    {
        var problems = new List<string>();
        var token = JObject.Parse("{\"workplanebased\":\"maybe\",\"workPlaneBase\":true,\"partType\":3}");
        var settings = ModelFamilyDocumentSpec.ParseSettings(token, "settings", problems);
        Assert.Empty(settings);
        Assert.Contains(problems, p => p.StartsWith("settings.workPlaneBase: unknown setting; did you mean 'workPlaneBased'?", StringComparison.Ordinal));
        Assert.Contains("settings.workPlaneBased: must be true or false.", problems);
        Assert.Contains(problems, p => p.StartsWith("settings.partType: give the part type as text", StringComparison.Ordinal));

        problems.Clear();
        Assert.Empty(ModelFamilyDocumentSpec.ParseSettings(new JArray(), "settings", problems));
        Assert.StartsWith("settings: must be an object", Assert.Single(problems));
        problems.Clear();
        Assert.Empty(ModelFamilyDocumentSpec.ParseSettings(null, "settings", problems));
        Assert.Empty(problems);
    }

    // ── category ─────────────────────────────────────────────────────────────

    private static readonly List<(string? BuiltIn, string Name)> Categories = new()
    {
        ("OST_ElectricalFixtures", "Electrical Fixtures"),
        ("OST_CommunicationDevices", "Communication Devices"),
        ("OST_DataDevices", "Data Devices"),
        ("OST_GenericModel", "Generic Models"),
        ("OST_FireAlarmDevices", "Fire Alarm Devices"),
        (null, "Imported Stuff")
    };

    [Theory]
    [InlineData("OST_ElectricalFixtures", 0)]
    [InlineData("ost_electricalfixtures", 0)]
    [InlineData("ElectricalFixtures", 0)]
    [InlineData("Electrical Fixtures", 0)]
    [InlineData("electrical_fixtures", 0)]
    [InlineData(" DataDevices ", 2)]
    [InlineData("Generic Models", 3)]
    [InlineData("OST_GenericModel", 3)]
    [InlineData("imported stuff", 5)]
    public void CategoryAliasesAreNormalised(string wanted, int index)
    {
        Assert.Equal(index, ModelFamilyDocumentSpec.MatchCategory(wanted, Categories, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void UnknownCategorySuggestsCloseNames()
    {
        Assert.Equal(-1, ModelFamilyDocumentSpec.MatchCategory("OST_ElectricFixtures", Categories, out var error));
        Assert.Contains("did you mean 'OST_ElectricalFixtures'", error);
        Assert.Equal(-1, ModelFamilyDocumentSpec.MatchCategory("Zebra", Categories, out error));
        Assert.Contains("Pass a built-in name", error);
        Assert.Equal(-1, ModelFamilyDocumentSpec.MatchCategory(" ", Categories, out error));
        Assert.Equal("give a category.", error);
    }

    [Fact]
    public void AmbiguousCategoryAsksForTheBuiltInName()
    {
        var twins = new List<(string? BuiltIn, string Name)> { ("OST_A", "Same"), ("OST_B", "Same") };
        Assert.Equal(-1, ModelFamilyDocumentSpec.MatchCategory("same", twins, out var error));
        Assert.Contains("more than one category", error);
        Assert.Equal(1, ModelFamilyDocumentSpec.MatchCategory("OST_B", twins, out _));
    }

    [Fact]
    public void CategoryArgumentMustBeText()
    {
        var problems = new List<string>();
        Assert.Equal("OST_DataDevices", ModelFamilyDocumentSpec.ParseCategory(new JValue(" OST_DataDevices "), "category", problems));
        Assert.Equal(string.Empty, ModelFamilyDocumentSpec.ParseCategory(null, "category", problems));
        Assert.Empty(problems);
        Assert.Equal(string.Empty, ModelFamilyDocumentSpec.ParseCategory(new JValue(5), "category", problems));
        Assert.StartsWith("category: give the category as text", Assert.Single(problems));
    }

    [Theory]
    [InlineData("OST_FireAlarmDevices", "firealarmdevices")]
    [InlineData("Junction Box", "junctionbox")]
    [InlineData(" fire-alarm_devices ", "firealarmdevices")]
    public void NamesNormalise(string value, string expected) => Assert.Equal(expected, ModelFamilyDocumentSpec.NormalizeName(value));

    [Theory]
    [InlineData("Junction Box", "JunctionBox")]
    [InlineData("junction_box", "JunctionBox")]
    [InlineData("Normal", "Normal")]
    [InlineData("panel board", "PanelBoard")]
    [InlineData("Nothing", null)]
    [InlineData("", null)]
    public void PartTypeTextMatchesEnumNames(string text, string? expected) =>
        Assert.Equal(expected, ModelFamilyDocumentSpec.MatchEnumName(text, new[] { "Undefined", "Normal", "JunctionBox", "PanelBoard", "Switch" }));

    // ── include, limit, ids ──────────────────────────────────────────────────

    private static readonly string[] Sections = { "family", "views", "levels", "elements", "referencePlanes" };

    [Fact]
    public void IncludeDefaultsToEverySectionInRegisteredOrder()
    {
        var problems = new List<string>();
        Assert.Equal(Sections, ModelFamilyDocumentSpec.ParseInclude(null, Sections, problems));
        Assert.Equal(Sections, ModelFamilyDocumentSpec.ParseInclude(new JArray(), Sections, problems));
        Assert.Equal(new[] { "family", "levels" }, ModelFamilyDocumentSpec.ParseInclude(new JArray("LEVELS", "family", "family"), Sections, problems));
        Assert.Equal(new[] { "views", "referencePlanes" }, ModelFamilyDocumentSpec.ParseInclude(new JValue("referenceplanes, views"), Sections, problems));
        Assert.Empty(problems);
    }

    [Fact]
    public void UnknownSectionsAreProblemsListingTheKnownOnes()
    {
        var problems = new List<string>();
        Assert.Equal(new[] { "family" }, ModelFamilyDocumentSpec.ParseInclude(new JArray("family", "view", 3), Sections, problems));
        Assert.Contains("include[2]: must be a section name.", problems);
        Assert.Contains(problems, p => p.StartsWith("include: unknown section 'view'; did you mean 'views'?", StringComparison.Ordinal) && p.Contains("Known: family, views, levels, elements, referencePlanes."));
        problems.Clear();
        Assert.Empty(ModelFamilyDocumentSpec.ParseInclude(new JObject(), Sections, problems));
        Assert.StartsWith("include: must be a list of section names.", Assert.Single(problems));
    }

    [Fact]
    public void LimitDefaultsAndClamps()
    {
        var problems = new List<string>();
        Assert.Equal(200, ModelFamilyDocumentSpec.ParseLimit(null, problems));
        Assert.Equal(1, ModelFamilyDocumentSpec.ParseLimit(new JValue(0), problems));
        Assert.Equal(1, ModelFamilyDocumentSpec.ParseLimit(new JValue(-50), problems));
        Assert.Equal(2000, ModelFamilyDocumentSpec.ParseLimit(new JValue(1_000_000), problems));
        Assert.Equal(50, ModelFamilyDocumentSpec.ParseLimit(new JValue("50"), problems));
        Assert.Empty(problems);
        Assert.Equal(200, ModelFamilyDocumentSpec.ParseLimit(new JValue("lots"), problems));
        Assert.StartsWith("limit: must be a whole number", Assert.Single(problems));
    }

    [Fact]
    public void IdsAcceptAListOrOneId()
    {
        var problems = new List<string>();
        Assert.Equal(new long[] { 12, 34 }, ModelFamilyDocumentSpec.ParseIds(new JArray(12, "34", 12), "elementIds", problems));
        Assert.Equal(new long[] { 7 }, ModelFamilyDocumentSpec.ParseIds(new JValue(7), "elementIds", problems));
        Assert.Empty(ModelFamilyDocumentSpec.ParseIds(null, "elementIds", problems));
        Assert.Empty(problems);
        Assert.Equal(new long[] { 1 }, ModelFamilyDocumentSpec.ParseIds(new JArray(1, "x"), "elementIds", problems));
        Assert.Equal("elementIds[1]: must be an element id (a whole number).", Assert.Single(problems));
        problems.Clear();
        Assert.Empty(ModelFamilyDocumentSpec.ParseIds(new JValue("abc"), "elementIds", problems));
        Assert.Equal("elementIds: must be a list of element ids.", Assert.Single(problems));
        // Without a problem list (a section reading the argument again) nothing is reported.
        Assert.Empty(ModelFamilyDocumentSpec.ParseIds(new JValue("abc"), "elementIds", null));
    }

    // ── placement ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("OneLevelBased", false)]
    [InlineData("WorkPlaneBased", false)]
    [InlineData("OneLevelBasedHosted", true)]
    [InlineData("CurveBased", false)]
    [InlineData("Invalid", null)]
    [InlineData("SomethingNew", null)]
    public void OnlyHostedPlacementNeedsAHost(string placement, bool? needsHost) =>
        Assert.Equal(needsHost, ModelFamilyDocumentSpec.PlacementNeedsHost(placement));

    [Fact]
    public void PlacementNoteSaysWhetherAHostFaceIsNeeded()
    {
        Assert.Contains("without selecting a host face", ModelFamilyDocumentSpec.PlacementNote("OneLevelBased", false));
        Assert.Contains("level or work plane without selecting a host face", ModelFamilyDocumentSpec.PlacementNote("OneLevelBased", true));
        Assert.Contains("no host face is required", ModelFamilyDocumentSpec.PlacementNote("WorkPlaneBased", true));
        Assert.Contains("Metric Generic Model.rft", ModelFamilyDocumentSpec.PlacementNote("OneLevelBasedHosted", null));
        Assert.Contains("view", ModelFamilyDocumentSpec.PlacementNote("ViewBased", null));
        Assert.Contains("check how instances are placed", ModelFamilyDocumentSpec.PlacementNote("Invalid", null));
    }

    // ── preserved content ────────────────────────────────────────────────────

    [Fact]
    public void CountDiffSeparatesUnchangedFromChanged()
    {
        var before = new Dictionary<string, int> { ["Extrusion"] = 2, ["ReferencePlane"] = 5, ["FamilyParameter"] = 9, ["ImportInstance"] = 1 };
        var after = new Dictionary<string, int> { ["Extrusion"] = 2, ["ReferencePlane"] = 5, ["FamilyParameter"] = 7, ["ConnectorElement"] = 1 };
        var diff = ModelFamilyDocumentSpec.DiffCounts(before, after);
        Assert.Equal(new[] { "Extrusion", "ReferencePlane" }, diff.Unchanged.Keys);
        Assert.Equal(new[] { ("ConnectorElement", 0, 1), ("FamilyParameter", 9, 7), ("ImportInstance", 1, 0) }, diff.Changed);

        var json = diff.ToJson();
        Assert.Equal(5, (int)json["unchangedCounts"]!["ReferencePlane"]!);
        Assert.Equal("FamilyParameter", (string?)json["changedCounts"]![1]!["key"]);

        var warnings = ModelFamilyDocumentSpec.CountWarnings(diff, new[] { "FamilyParameter" });
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.StartsWith("The number of ImportInstance elements changed from 1 to 0", StringComparison.Ordinal));
        Assert.Equal(3, ModelFamilyDocumentSpec.CountWarnings(diff).Count);
        Assert.Empty(ModelFamilyDocumentSpec.CountWarnings(ModelFamilyDocumentSpec.DiffCounts(before, before)));
    }

    [Fact]
    public void DisappearedListsNamesThatAreGone()
    {
        Assert.Equal(new[] { "Apparent Load", "Voltage" },
            ModelFamilyDocumentSpec.Disappeared(new[] { "Width", "Voltage", "Apparent Load", "Voltage" }, new[] { "Width", "Height" }));
        Assert.Empty(ModelFamilyDocumentSpec.Disappeared(new[] { "Width" }, new[] { "Width" }));
    }

    [Fact]
    public void DescribeChangesForTheApprovalPrompt()
    {
        var problems = new List<string>();
        var settings = ModelFamilyDocumentSpec.ParseSettings(JObject.Parse("{\"workPlaneBased\":false}"), "settings", problems);
        Assert.Equal("set the category to 'OST_DataDevices'; set workPlaneBased=false", ModelFamilyDocumentSpec.DescribeChanges("OST_DataDevices", settings));
        Assert.Equal("no setting changes", ModelFamilyDocumentSpec.DescribeChanges(string.Empty, Array.Empty<ModelFamilySettingRequest>()));
    }

    // ── tool surface ─────────────────────────────────────────────────────────

    private static readonly string[] Tools =
    {
        "revit_get_model_family", "revit_preview_create_model_family", "revit_create_model_family",
        "revit_preview_edit_model_family", "revit_edit_model_family"
    };

    [Fact]
    public void EveryToolIsInTheFamilyEditorGroup()
    {
        foreach (var tool in Tools) Assert.Equal("familyeditor", BridgeToolGroups.GroupOf(tool));
    }

    [Theory]
    [InlineData("revit_get_model_family", "ReadOnly")]
    [InlineData("revit_preview_create_model_family", "ReadOnly")]
    [InlineData("revit_create_model_family", "RequiresApproval")]
    [InlineData("revit_preview_edit_model_family", "ReadOnly")]
    [InlineData("revit_edit_model_family", "RequiresApproval")]
    public void Permissions(string tool, string permission)
    {
        var addin = ToolSourceScan.Addin.Value[tool];
        Assert.Equal(permission, addin.Permission);
        Assert.Contains("ToolCategory.FamilyCreation", addin.Source);
        if (permission != "ReadOnly") Assert.Contains("IApprovalSummaryTool", addin.Source);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
    }

    [Theory]
    [InlineData("revit_create_model_family", PlanToolKind.Creates)]
    [InlineData("revit_edit_model_family", PlanToolKind.Creates)]
    public void PlanClassification(string tool, PlanToolKind kind) => Assert.Equal(kind, PlanToolClassification.Classify(tool));

    [Fact]
    public void TheReadToolSendsElementIds()
    {
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == "revit_get_model_family");
        Assert.Contains("args[\"elementIds\"]", bridge.Source);
        Assert.Contains("args[\"include\"]", bridge.Source);
        Assert.Contains("args[\"limit\"]", bridge.Source);
    }
}
