using RevitMCP.Addin.Query;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>M16: an empty duplicate parameter hid the built-in "Sheet Number".</summary>
public class ParameterValueMapTests
{
    private static ParameterValueDto P(string name, string value, string? builtIn = null, string scope = "Instance") =>
        new() { Name = name, Value = value, BuiltInParameterName = builtIn, Scope = scope };

    [Fact]
    public void EmptyDuplicate_DoesNotHideTheValue()
    {
        var map = ParameterValueMap.Build([P("Sheet Number", "EN-5-10", "SHEET_NUMBER"), P("Sheet Number", "")]);
        Assert.Equal("EN-5-10", map["Sheet Number"]);

        var reversed = ParameterValueMap.Build([P("Sheet Number", ""), P("Sheet Number", "EN-5-10", "SHEET_NUMBER")]);
        Assert.Equal("EN-5-10", reversed["Sheet Number"]);
    }

    [Fact]
    public void BothSet_ProjectParameterWins_AsForWrites()
    {
        var map = ParameterValueMap.Build([P("Mark", "builtin", "ALL_MODEL_MARK"), P("Mark", "project")]);
        Assert.Equal("project", map["Mark"]);
    }

    [Fact]
    public void InstanceBeatsType_AndTheFilterApplies()
    {
        var map = ParameterValueMap.Build(
            [P("Comments", "type", scope: "Type"), P("Comments", "instance"), P("Other", "x")],
            p => p.Name == "Comments");
        Assert.Equal("instance", map["Comments"]);
        Assert.False(map.ContainsKey("Other"));
    }

    [Fact]
    public void AllEmpty_GivesEmpty()
    {
        Assert.Equal("", ParameterValueMap.Build([P("Sheet Number", ""), P("Sheet Number", "", "SHEET_NUMBER")])["Sheet Number"]);
    }
}
