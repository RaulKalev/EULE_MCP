using RevitMCP.Addin.Query;
using Xunit;

namespace RevitMCP.Tests;

public class ParameterFilterEvaluatorTests
{
    // What the parameter reader yields for a Data Devices instance: the built-in "Type",
    // "Family" and "Family and Type" are ElementId parameters, read as numeric ids, and the
    // type element's own name is not among its iterable parameters.
    private static readonly IReadOnlyList<ParameterValueDto> DataDeviceParams = new List<ParameterValueDto>
    {
        new() { Name = "Type", Value = "987654", Scope = "Instance", StorageType = "ElementId" },
        new() { Name = "Family", Value = "987650", Scope = "Instance", StorageType = "ElementId" },
        new() { Name = "Family and Type", Value = "987654", Scope = "Instance", StorageType = "ElementId" },
        new() { Name = "Type Id", Value = "987654", Scope = "Instance", StorageType = "ElementId" },
        new() { Name = "Type Mark", Value = "D1", Scope = "Type", StorageType = "String" },
        new() { Name = "Type Comments", Value = "", Scope = "Type", StorageType = "String" },
        new() { Name = "Comments", Value = "Room 101", Scope = "Instance", StorageType = "String" },
    };

    private static readonly ElementIdentity WifiOutlet = new()
    {
        FamilyName = "EN_SIDE_Pistik",
        TypeName = "2 x RJ45 , WiFi"
    };

    private static ParameterFilterDto Filter(string name, string op, string value = "", string matchMode = "ContainsNormalized") =>
        new() { ParameterName = name, Operator = op, Value = value, MatchMode = matchMode };

    [Fact]
    public void ElementIdParameter_MatchesItsNameAndItsId()
    {
        // X3: id parameters show a name now; a filter written against the id keeps working.
        var level = new List<ParameterValueDto>
        {
            new() { Name = "Level", Value = "Esimene korrus", RawValue = 2123366L, Scope = "Instance", StorageType = "ElementId" }
        };
        Assert.True(ParameterFilterEvaluator.Passes(level, [Filter("Level", "equals", "Esimene korrus", "Exact")]));
        Assert.True(ParameterFilterEvaluator.Passes(level, [Filter("Level", "equals", "2123366", "Exact")]));
        Assert.False(ParameterFilterEvaluator.Passes(level, [Filter("Level", "equals", "Teine korrus", "Exact")]));
    }

    [Fact]
    public void TypeContains_MatchesTypeName_NotTheElementIdValue()
    {
        // Issue #88: this returned 0 elements because only numeric ids were compared.
        var filters = new List<ParameterFilterDto> { Filter("Type", "contains", "WiFi") };

        Assert.True(ParameterFilterEvaluator.Passes(DataDeviceParams, filters, WifiOutlet));
    }

    [Fact]
    public void TypeContains_DoesNotMatchOtherTypes()
    {
        var filters = new List<ParameterFilterDto> { Filter("Type", "contains", "WiFi") };
        var other = new ElementIdentity { FamilyName = "EN_SIDE_Pistik", TypeName = "2 x RJ45" };

        Assert.False(ParameterFilterEvaluator.Passes(DataDeviceParams, filters, other));
    }

    [Theory]
    [InlineData("Type", "equals", "2 x RJ45 , WiFi")]
    [InlineData("Type Name", "startsWith", "2 x RJ45")]
    [InlineData("type_name", "contains", "wifi")]
    [InlineData("Family", "equals", "EN_SIDE_Pistik")]
    [InlineData("Family Name", "contains", "SIDE")]
    [InlineData("Family and Type", "equals", "EN_SIDE_Pistik: 2 x RJ45 , WiFi")]
    [InlineData("Type", "notContains", "Camera")]
    [InlineData("Type", "isNotEmpty", "")]
    public void IdentityPseudoParameters_ResolveFromElementType(string name, string op, string value)
    {
        var filters = new List<ParameterFilterDto> { Filter(name, op, value) };

        Assert.True(ParameterFilterEvaluator.Passes(DataDeviceParams, filters, WifiOutlet));
    }

    [Fact]
    public void IdentityFilter_WithoutResolvedType_IsEmpty()
    {
        var filters = new List<ParameterFilterDto> { Filter("Type", "isEmpty") };

        Assert.True(ParameterFilterEvaluator.Passes(Array.Empty<ParameterValueDto>(), filters, identity: null));
        Assert.False(ParameterFilterEvaluator.Passes(
            Array.Empty<ParameterValueDto>(),
            new List<ParameterFilterDto> { Filter("Type", "contains", "WiFi") },
            identity: null));
    }

    [Theory]
    [InlineData("Type Id", false)]
    [InlineData("Type Mark", false)]
    [InlineData("Type Comments", false)]
    [InlineData("Family Type Code", false)]
    [InlineData("TYPE", true)]
    [InlineData(" Family and Type ", true)]
    [InlineData("Family-Name", true)]
    public void IsIdentityName_MatchesWholeNamesOnly(string name, bool expected)
    {
        Assert.Equal(expected, ElementIdentityParameters.IsIdentityName(name));
    }

    [Fact]
    public void RealParameters_StillEvaluatedAndAnded()
    {
        var filters = new List<ParameterFilterDto>
        {
            Filter("Type", "contains", "WiFi"),
            Filter("Type Mark", "equals", "D1"),
            Filter("Type Id", "equals", "987654"),
        };

        Assert.True(ParameterFilterEvaluator.Passes(DataDeviceParams, filters, WifiOutlet));

        filters.Add(Filter("Comments", "contains", "Room 202"));
        Assert.False(ParameterFilterEvaluator.Passes(DataDeviceParams, filters, WifiOutlet));
    }

    [Fact]
    public void MissingParameter_OnlyPassesIsEmpty()
    {
        Assert.True(ParameterFilterEvaluator.Passes(DataDeviceParams,
            new List<ParameterFilterDto> { Filter("Nonexistent", "isEmpty") }, WifiOutlet));
        Assert.False(ParameterFilterEvaluator.Passes(DataDeviceParams,
            new List<ParameterFilterDto> { Filter("Nonexistent", "equals", "x") }, WifiOutlet));
    }

    [Fact]
    public void Scope_IsRespectedForRealParameters()
    {
        var typeScoped = new ParameterFilterDto { ParameterName = "Comments", Operator = "isNotEmpty", Scope = "Type" };
        Assert.False(ParameterFilterEvaluator.Passes(DataDeviceParams, new List<ParameterFilterDto> { typeScoped }, WifiOutlet));
    }

    [Fact]
    public void ParameterBackedFilters_ExcludesIdentityFilters()
    {
        var filters = new List<ParameterFilterDto>
        {
            Filter("Type", "contains", "WiFi"),
            Filter("Comments", "isNotEmpty"),
        };

        Assert.True(ParameterFilterEvaluator.NeedsIdentity(filters));
        var backed = ParameterFilterEvaluator.ParameterBackedFilters(filters);
        Assert.Single(backed);
        Assert.Equal("Comments", backed[0].ParameterName);
    }

    [Theory]
    [InlineData("Fam", "T1", "Fam: T1")]
    [InlineData("", "T1", "T1")]
    [InlineData("Fam", "", "Fam")]
    public void FormatFamilyAndType_UsesRevitDisplayForm(string family, string type, string expected)
    {
        Assert.Equal(expected, ElementIdentityParameters.FormatFamilyAndType(family, type));
    }

    [Theory]
    [InlineData("12", "greaterThan", "10", true)]
    [InlineData("abc", "greaterThan", "10", false)]
    [InlineData("Foo", "unknownOp", "Foo", false)]
    [InlineData("Foo", "endsWith", "OO", true)]
    public void EvaluateOperator_Basics(string value, string op, string filterValue, bool expected)
    {
        Assert.Equal(expected, ParameterFilterEvaluator.EvaluateOperator(value, op, filterValue));
    }

    // ── #109: model-state pseudo-parameters ─────────────────────────────────────────────

    private static readonly ElementIdentity InOptionDemolished = new()
    {
        FamilyName = "EN_SIDE_Pistik",
        TypeName = "2 x RJ45",
        ModelState = new ElementModelState
        {
            PhaseCreated = "Olemasolev",
            PhaseDemolished = "Uus",
            DesignOption = "Variant A",
            Workset = "EN_Nõrkvool"
        }
    };

    private static readonly ElementIdentity MainModelNew = new()
    {
        ModelState = new ElementModelState { PhaseCreated = "Uus", Workset = "EN_Nõrkvool" }
    };

    [Theory]
    [InlineData("Phase Created", true)]
    [InlineData("phase created", true)]
    [InlineData("Phase_Demolished", true)]
    [InlineData("Design Option", true)]
    [InlineData("Workset", true)]
    [InlineData("Phase", false)]
    [InlineData("Workset Name", false)]
    [InlineData("Design Option Set", false)]
    public void IsModelStateName_MatchesWholeNamesOnly(string name, bool expected)
    {
        Assert.Equal(expected, ModelStateParameters.IsModelStateName(name));
        Assert.False(ElementIdentityParameters.IsIdentityName(name)); // grouping by these names is unchanged
    }

    [Fact]
    public void ModelStateFilters_AreAnsweredFromTheModelState()
    {
        Assert.True(ParameterFilterEvaluator.Passes(Array.Empty<ParameterValueDto>(), [Filter("Phase Created", "equals", "uus")], MainModelNew));
        Assert.False(ParameterFilterEvaluator.Passes(Array.Empty<ParameterValueDto>(), [Filter("Phase Created", "equals", "Uus")], InOptionDemolished));
        Assert.True(ParameterFilterEvaluator.Passes(Array.Empty<ParameterValueDto>(), [Filter("Phase Demolished", "equals", "Uus")], InOptionDemolished));
        Assert.True(ParameterFilterEvaluator.Passes(Array.Empty<ParameterValueDto>(), [Filter("Workset", "contains", "nõrk")], MainModelNew));
    }

    [Fact]
    public void DesignOption_EmptyMeansMainModel()
    {
        var inAnOption = Filter("Design Option", "notEquals", "");
        Assert.True(ParameterFilterEvaluator.Passes(Array.Empty<ParameterValueDto>(), [inAnOption], InOptionDemolished));
        Assert.False(ParameterFilterEvaluator.Passes(Array.Empty<ParameterValueDto>(), [inAnOption], MainModelNew));
        Assert.True(ParameterFilterEvaluator.Passes(Array.Empty<ParameterValueDto>(), [Filter("Design Option", "isEmpty")], MainModelNew));
        Assert.True(ParameterFilterEvaluator.Passes(Array.Empty<ParameterValueDto>(), [Filter("Phase Demolished", "isEmpty")], MainModelNew));
    }

    [Fact]
    public void ModelStateFilters_CombineWithIdentityAndParameterFilters()
    {
        var filters = new List<ParameterFilterDto>
        {
            Filter("Phase Created", "equals", "Olemasolev"),
            Filter("Type", "contains", "RJ45"),
            Filter("Comments", "contains", "Room")
        };
        Assert.True(ParameterFilterEvaluator.Passes(DataDeviceParams, filters, InOptionDemolished));
        Assert.True(ParameterFilterEvaluator.NeedsIdentity(filters));
        Assert.True(ParameterFilterEvaluator.NeedsModelState(filters));
    }

    [Fact]
    public void WithoutAModelState_TheFiltersFallBackToRevitParameters()
    {
        // Callers that do not read the model state (the uncircuited-element tools, set_parameter) keep matching Revit's
        // own "Workset" / "Phase Created" parameters, as before #109.
        var parameters = new List<ParameterValueDto>
        {
            new() { Name = "Workset", Value = "EN_Nõrkvool", Scope = "Instance", StorageType = "Integer" },
            new() { Name = "Phase Created", Value = "Uus", RawValue = 123L, Scope = "Instance", StorageType = "ElementId" }
        };
        var identityOnly = new ElementIdentity { FamilyName = "F", TypeName = "T" };
        Assert.True(ParameterFilterEvaluator.Passes(parameters, [Filter("Workset", "equals", "EN_Nõrkvool", "Exact")], identityOnly));
        Assert.True(ParameterFilterEvaluator.Passes(parameters, [Filter("Phase Created", "equals", "123", "Exact")], null));
        Assert.False(ParameterFilterEvaluator.Passes(parameters, [Filter("Phase Created", "equals", "Olemasolev", "Exact")], identityOnly));
    }

    [Fact]
    public void ParameterBackedFilters_DropModelStateFiltersOnlyWhenResolved()
    {
        var filters = new List<ParameterFilterDto>
        {
            Filter("Design Option", "notEquals", ""),
            Filter("Workset", "equals", "A"),
            Filter("Family", "equals", "F"),
            Filter("Comments", "contains", "x")
        };
        Assert.Equal(new[] { "Design Option", "Workset", "Comments" },
            ParameterFilterEvaluator.ParameterBackedFilters(filters).Select(f => f.ParameterName));
        Assert.Equal(new[] { "Comments" },
            ParameterFilterEvaluator.ParameterBackedFilters(filters, modelStateResolved: true).Select(f => f.ParameterName));
        Assert.False(ParameterFilterEvaluator.NeedsModelState([Filter("Family", "equals", "F")]));
    }
}
