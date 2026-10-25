using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Parameters;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#102: what revit_create_parameters and revit_bind_parameter_categories decide for each request.</summary>
public class ParameterCreatePlannerTests
{
    private const string TextId = "autodesk.spec:spec.string-2.0.0";
    private const string LengthId = "autodesk.spec.aec:length-2.0.0";
    private const string CodeGuid = "6f1c3a2e-9b1d-4c55-8a0e-1d2f3a4b5c6d";

    private static readonly string[] Categories = { "Electrical Fixtures", "Data Devices", "Lighting Fixtures" };

    private static ParameterPlanContext Context(bool createInFile = false, List<SharedDefinitionInfo>? file = null, ExistingBinding? existing = null) => new()
    {
        Existing = existing == null ? new List<ExistingBinding>() : new List<ExistingBinding> { existing },
        SharedFile = file ?? new List<SharedDefinitionInfo>
        {
            new() { Group = "EULE", Name = "EULE_DeviceCode", Guid = CodeGuid, DataTypeId = TextId },
            new() { Group = "EULE", Name = "EULE_MountHeight", Guid = Guid.NewGuid().ToString(), DataTypeId = LengthId }
        },
        CreateInFileIfMissing = createInFile,
        ResolveCategory = name =>
        {
            var match = Categories.FirstOrDefault(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
            return match != null ? (match, null) : name == "Views" ? (null, "does not allow bound parameters") : (null, null);
        },
        ResolveSpec = value => ParameterSpecMap.ResolveSpec(value)?.Key switch
        {
            "String.Text" => TextId,
            "Length" => LengthId,
            "Boolean.YesNo" => "autodesk.spec:spec.bool-1.0.0",
            _ => null
        }
    };

    private static ParameterRequest Shared(string name, params string[] categories) => new()
    {
        Name = name, Source = "shared", Categories = categories.ToList(), ParameterGroup = "Data"
    };

    private static ParameterPlanItem One(ParameterRequest request, ParameterPlanContext context) =>
        Assert.Single(ParameterCreatePlanner.Plan(new[] { request }, context));

    [Fact]
    public void ASharedDefinitionByName_WillBeCreated()
    {
        var item = One(Shared("EULE_DeviceCode", "electrical fixtures", "Data Devices"), Context());
        Assert.Equal("willCreate", item.Action);
        Assert.Equal(CodeGuid, item.Definition!.Guid);
        Assert.Equal(new[] { "Electrical Fixtures", "Data Devices" }, item.Categories);
        Assert.False(item.CreateInFile);
        Assert.Equal("Data", item.ParameterGroupKey);
    }

    [Fact]
    public void ByGuid_WinsOverName()
    {
        var request = Shared("Whatever", "Data Devices");
        request.Guid = CodeGuid;
        Assert.Equal("EULE_DeviceCode", One(request, Context()).Definition!.Name);
    }

    [Fact]
    public void AMissingDefinition_IsReported_TheFileIsNotTouched()
    {
        var request = Shared("EULE_Missing", "Data Devices");
        request.DataType = "text";
        var item = One(request, Context());
        Assert.Equal("missingDefinition", item.Action);
        Assert.Contains("createInFileIfMissing=true", item.Reason);
        Assert.False(item.CreateInFile);
    }

    [Fact]
    public void AMissingDefinition_IsCreatedInTheFile_OnlyWhenAsked()
    {
        var request = Shared("EULE_Missing", "Data Devices");
        request.DataType = "text";
        request.Group = "EULE";
        var item = One(request, Context(createInFile: true));
        Assert.Equal("willCreate", item.Action);
        Assert.True(item.CreateInFile);
        Assert.Equal("EULE", item.FileGroup);
        Assert.Equal(TextId, item.DataTypeId);

        request.DataType = string.Empty;
        Assert.Equal("invalidType", One(request, Context(createInFile: true)).Action);
    }

    [Fact]
    public void NoSharedFile_IsAMissingDefinition()
    {
        var context = Context();
        context.SharedFile = null;
        context.SharedFileProblem = "No shared parameter file is set.";
        var item = One(Shared("EULE_DeviceCode", "Data Devices"), context);
        Assert.Equal("missingDefinition", item.Action);
        Assert.Equal("No shared parameter file is set.", item.Reason);
    }

    [Fact]
    public void ADataTypeThatDisagreesWithTheFile_IsInvalidType()
    {
        var request = Shared("EULE_DeviceCode", "Data Devices");
        request.DataType = "length";
        Assert.Equal("invalidType", One(request, Context()).Action);
        request.DataType = "text";
        Assert.Equal("willCreate", One(request, Context()).Action);
        request.DataType = "colour";
        Assert.Equal("invalidType", One(request, Context()).Action);
    }

    [Fact]
    public void UnknownCategories_StopTheItem()
    {
        var item = One(Shared("EULE_DeviceCode", "Data Devices", "Spaceships", "Views"), Context());
        Assert.Equal("unknownCategory", item.Action);
        Assert.Equal(2, item.UnknownCategories.Count);
        Assert.Contains("does not allow bound parameters", item.Reason);
    }

    [Fact]
    public void AnExistingBinding_IsExtendedOnlyWithRebind()
    {
        var existing = new ExistingBinding { Name = "EULE_DeviceCode", Guid = CodeGuid, IsShared = true, IsInstance = true, Categories = { "Electrical Fixtures" } };
        var request = Shared("EULE_DeviceCode", "Electrical Fixtures", "Data Devices");

        var skip = One(request, Context(existing: existing));
        Assert.Equal("exists", skip.Action);
        Assert.Contains("ifExists=rebind", skip.Reason);

        request.IfExists = "rebind";
        var rebind = One(request, Context(existing: existing));
        Assert.Equal("willRebind", rebind.Action);
        Assert.Equal(new[] { "Data Devices" }, rebind.AddedCategories);
        Assert.Equal(new[] { "Electrical Fixtures", "Data Devices" }, rebind.Categories);   // union, nothing dropped
    }

    [Fact]
    public void AnExistingBindingWithEveryCategory_IsExists()
    {
        var existing = new ExistingBinding { Name = "EULE_DeviceCode", Guid = CodeGuid, IsShared = true, Categories = { "Data Devices", "Electrical Fixtures" } };
        var request = Shared("EULE_DeviceCode", "Data Devices");
        request.IfExists = "rebind";
        Assert.Equal("exists", One(request, Context(existing: existing)).Action);
    }

    [Fact]
    public void BindingKindAndNameConflicts_AreRefused()
    {
        var typeBound = new ExistingBinding { Name = "EULE_DeviceCode", Guid = CodeGuid, IsShared = true, IsInstance = false, Categories = { "Data Devices" } };
        Assert.Contains("binding kind", One(Shared("EULE_DeviceCode", "Data Devices"), Context(existing: typeBound)).Reason);

        var projectParameter = new ExistingBinding { Name = "EULE_DeviceCode", IsShared = false, Categories = { "Data Devices" } };
        Assert.Equal("invalid", One(Shared("EULE_DeviceCode", "Data Devices"), Context(existing: projectParameter)).Action);

        var otherGuid = new ExistingBinding { Name = "EULE_DeviceCode", Guid = Guid.NewGuid().ToString(), IsShared = true, Categories = { "Data Devices" } };
        Assert.Contains("different parameter", One(Shared("EULE_DeviceCode", "Data Devices"), Context(existing: otherGuid)).Reason);
    }

    [Fact]
    public void SourceProject_GoesThroughTheScratchFile()
    {
        var request = new ParameterRequest { Name = "Temp Note", Source = "project", DataType = "text", Categories = { "Data Devices" } };
        var item = One(request, Context());
        Assert.Equal("willCreate", item.Action);
        Assert.True(item.CreateInFile);
        Assert.Equal(ParameterCreatePlanner.ScratchGroup, item.FileGroup);
        Assert.Contains("no API for a non-shared project parameter", item.Reason);

        var context = Context();
        context.ScratchFile.Add(new SharedDefinitionInfo { Group = "RevitMCP", Name = "Temp Note", Guid = Guid.NewGuid().ToString(), DataTypeId = TextId });
        var reused = One(request, context);
        Assert.False(reused.CreateInFile);
        Assert.NotNull(reused.Definition);

        request.DataType = string.Empty;
        Assert.Equal("invalidType", One(request, Context()).Action);
    }

    [Fact]
    public void RequestShape_IsValidated()
    {
        Assert.Equal("invalid", One(Shared("", "Data Devices"), Context()).Action);
        Assert.Equal("invalid", One(Shared("Bad:Name", "Data Devices"), Context()).Action);
        Assert.Equal("invalid", One(Shared("EULE_DeviceCode"), Context()).Action);   // no categories

        var badGroup = Shared("EULE_DeviceCode", "Data Devices");
        badGroup.ParameterGroup = "Electrics";
        Assert.Equal("invalid", One(badGroup, Context()).Action);

        var varies = Shared("EULE_DeviceCode", "Data Devices");
        varies.Binding = "type";
        varies.VariesAcrossGroups = true;
        Assert.Equal("invalid", One(varies, Context()).Action);

        var twice = ParameterCreatePlanner.Plan(new[] { Shared("EULE_DeviceCode", "Data Devices"), Shared("eule_devicecode", "Data Devices") }, Context());
        Assert.Equal("willCreate", twice[0].Action);
        Assert.Equal("invalid", twice[1].Action);
    }

    [Fact]
    public void ANameInTwoGroups_NeedsGroupOrGuid()
    {
        var file = new List<SharedDefinitionInfo>
        {
            new() { Group = "A", Name = "Code", Guid = Guid.NewGuid().ToString(), DataTypeId = TextId },
            new() { Group = "B", Name = "Code", Guid = Guid.NewGuid().ToString(), DataTypeId = TextId }
        };
        Assert.Contains("pass group or guid", One(Shared("Code", "Data Devices"), Context(file: file)).Reason);
        var request = Shared("Code", "Data Devices");
        request.Group = "B";
        Assert.Equal("B", One(request, Context(file: file)).Definition!.Group);
    }

    [Fact]
    public void Parse_ReadsTheArgumentArray()
    {
        var raw = JArray.Parse("""
            [{"name":"EULE_DeviceCode","source":"shared","group":"EULE","parameterGroup":"Identity Data","binding":"type",
              "categories":["Data Devices","Electrical Fixtures"],"variesAcrossGroups":false,"ifExists":"rebind"},
             {"name":"Temp","source":"project","dataType":"text","categories":"Data Devices; Lighting Fixtures"}]
            """);
        var parsed = ParameterCreatePlanner.Parse(raw, out var error);
        Assert.Null(error);
        Assert.Equal(2, parsed.Count);
        Assert.False(parsed[0].IsInstance);
        Assert.True(parsed[0].Rebind);
        Assert.Equal(false, parsed[0].VariesAcrossGroups);
        Assert.Equal(new[] { "Data Devices", "Lighting Fixtures" }, parsed[1].Categories);
        Assert.False(parsed[1].IsShared);

        ParameterCreatePlanner.Parse(null, out error);
        Assert.NotNull(error);
        ParameterCreatePlanner.Parse(new JArray(Enumerable.Range(0, 51).Select(i => new JObject { ["name"] = "P" + i })), out error);
        Assert.Contains("at most 50", error);
    }

    [Fact]
    public void CategoryChange_AddsRemovesAndKeepsOne()
    {
        var change = ParameterCreatePlanner.PlanCategoryChange(new[] { "Data Devices", "Electrical Fixtures" }, new[] { "Lighting Fixtures", "data devices" }, new[] { "Electrical Fixtures", "Walls" });
        Assert.Null(change.Error);
        Assert.Equal(new[] { "Data Devices", "Lighting Fixtures" }, change.Categories);
        Assert.Equal(new[] { "Lighting Fixtures" }, change.Added);
        Assert.Equal(new[] { "Electrical Fixtures" }, change.Removed);
        Assert.Equal(new[] { "data devices" }, change.AlreadyBound);
        Assert.Equal(new[] { "Walls" }, change.NotBound);
        Assert.True(change.Changes);

        var last = ParameterCreatePlanner.PlanCategoryChange(new[] { "Data Devices" }, Array.Empty<string>(), new[] { "Data Devices" });
        Assert.Contains("last category", last.Error);

        Assert.NotNull(ParameterCreatePlanner.PlanCategoryChange(new[] { "A" }, new[] { "B" }, new[] { "B" }).Error);
    }
}
