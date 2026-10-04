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
}
