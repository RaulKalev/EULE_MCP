using RevitMCP.Addin.Schedules;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#103: filter types map onto ScheduleFilterType names, and values are coerced to what the field holds.</summary>
public class ScheduleFilterTypeMapTests
{
    /// <summary>The ScheduleFilterType members of the Revit 2024 and 2026 APIs (identical in both).</summary>
    private static readonly string[] RevitMembers =
    {
        "Invalid", "HasParameter", "Equal", "NotEqual", "GreaterThan", "GreaterThanOrEqual", "LessThan", "LessThanOrEqual",
        "Contains", "NotContains", "BeginsWith", "NotBeginsWith", "EndsWith", "NotEndsWith",
        "IsAssociatedWithGlobalParameter", "IsNotAssociatedWithGlobalParameter", "HasValue", "HasNoValue"
    };

    [Theory]
    [InlineData("equals", "Equal")]
    [InlineData("Equal", "Equal")]
    [InlineData("=", "Equal")]
    [InlineData("not equals", "NotEqual")]
    [InlineData("!=", "NotEqual")]
    [InlineData("greater_than", "GreaterThan")]
    [InlineData(">=", "GreaterThanOrEqual")]
    [InlineData("lte", "LessThanOrEqual")]
    [InlineData("<", "LessThan")]
    [InlineData("Contains", "Contains")]
    [InlineData("does not contain", "NotContains")]
    [InlineData("startsWith", "BeginsWith")]
    [InlineData("not-begins-with", "NotBeginsWith")]
    [InlineData("endsWith", "EndsWith")]
    [InlineData("doesNotEndWith", "NotEndsWith")]
    [InlineData("isEmpty", "HasNoValue")]
    [InlineData("hasValue", "HasValue")]
    [InlineData("exists", "HasParameter")]
    public void Aliases_MapToRevitNames(string alias, string expected)
    {
        Assert.True(ScheduleFilterTypeMap.TryMap(alias, out var canonical));
        Assert.Equal(expected, canonical);
        Assert.Contains(canonical, RevitMembers);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("roughly")]
    [InlineData("Invalid")]
    public void UnknownTypes_DoNotMap(string? alias) => Assert.False(ScheduleFilterTypeMap.TryMap(alias, out _));

    [Fact]
    public void EveryAcceptedName_MapsAndRoundTrips()
    {
        foreach (var name in ScheduleFilterTypeMap.AcceptedText.Split(',').Select(s => s.Trim()))
        {
            Assert.True(ScheduleFilterTypeMap.TryMap(name, out var canonical), name);
            Assert.True(ScheduleFilterTypeMap.TryMap(ScheduleFilterTypeMap.ToAlias(canonical), out var again));
            Assert.Equal(canonical, again);
        }
        Assert.Equal("equals", ScheduleFilterTypeMap.ToAlias("Equal"));
        Assert.Equal("greaterThanOrEqual", ScheduleFilterTypeMap.ToAlias("GreaterThanOrEqual"));
    }

    [Fact]
    public void PresenceFilters_TakeNoValue()
    {
        Assert.False(ScheduleFilterTypeMap.NeedsValue("HasValue"));
        Assert.False(ScheduleFilterTypeMap.NeedsValue("HasParameter"));
        Assert.True(ScheduleFilterTypeMap.NeedsValue("Equal"));
        Assert.Equal("none", ScheduleFilterTypeMap.Coerce("HasNoValue", null, null, "string").Kind);
    }

    [Fact]
    public void Coerce_Text()
    {
        var number = ScheduleFilterTypeMap.Coerce("Equal", "12", 12, "string");
        Assert.Equal("string", number.Kind);
        Assert.Equal("12", number.Text);
        Assert.Equal("Level 2", ScheduleFilterTypeMap.Coerce("Contains", "Level 2", null, "string").Text);
    }

    [Fact]
    public void Coerce_Integer()
    {
        Assert.Equal(1, ScheduleFilterTypeMap.Coerce("Equal", "yes", null, "integer").Integer);
        Assert.Equal(0, ScheduleFilterTypeMap.Coerce("Equal", "false", null, "integer").Integer);
        Assert.Equal(16, ScheduleFilterTypeMap.Coerce("GreaterThan", "16", null, "integer").Integer);
        Assert.True(ScheduleFilterTypeMap.Coerce("Equal", "2.5", null, "integer").IsError);
        Assert.True(ScheduleFilterTypeMap.Coerce("Contains", "1", null, "integer").IsError);
    }

    [Fact]
    public void Coerce_Number()
    {
        var comma = ScheduleFilterTypeMap.Coerce("GreaterThan", "2,5", null, "double");
        Assert.Equal("double", comma.Kind);
        Assert.Equal(2.5, comma.Double);
        Assert.Equal(300, ScheduleFilterTypeMap.Coerce("LessThan", "300", 300, "double").Double);
        Assert.True(ScheduleFilterTypeMap.Coerce("Equal", "tall", null, "double").IsError);
        Assert.True(ScheduleFilterTypeMap.Coerce("BeginsWith", "3", null, "double").IsError);
    }

    [Fact]
    public void Coerce_ElementReference()
    {
        var byName = ScheduleFilterTypeMap.Coerce("Equal", "Level 2", null, "elementId");
        Assert.Equal("elementName", byName.Kind);
        Assert.Equal("Level 2", byName.Text);

        var byId = ScheduleFilterTypeMap.Coerce("Equal", "311", 311, "elementId");
        Assert.Equal("elementId", byId.Kind);
        Assert.Equal(311, byId.ElementId);

        Assert.True(ScheduleFilterTypeMap.Coerce("Contains", "Level", null, "elementId").IsError);
    }

    [Fact]
    public void Coerce_UnknownKind_FollowsTheValue()
    {
        Assert.Equal("double", ScheduleFilterTypeMap.Coerce("Equal", "5", 5, "unknown").Kind);
        Assert.Equal("string", ScheduleFilterTypeMap.Coerce("Equal", "5", null, "unknown").Kind);
        Assert.Equal("string", ScheduleFilterTypeMap.Coerce("Contains", "5", 5, "unknown").Kind);
        Assert.True(ScheduleFilterTypeMap.Coerce("Equal", null, null, "unknown").IsError);
    }

    [Theory]
    [InlineData("none", false, false, false)]
    [InlineData("title", true, true, false)]
    [InlineData("Title and totals", true, true, false)]
    [InlineData("count", true, false, true)]
    [InlineData("totals", true, false, false)]
    [InlineData("titleCountTotals", true, true, true)]
    [InlineData("title, count and totals", true, true, true)]
    public void GrandTotal_Choices(string choice, bool show, bool title, bool count)
    {
        Assert.True(ScheduleGrandTotal.TryMap(choice, out var s, out var t, out var c));
        Assert.Equal((show, title, count), (s, t, c));
        Assert.True(ScheduleGrandTotal.TryMap(ScheduleGrandTotal.Describe(s, t, c), out var s2, out var t2, out var c2));
        Assert.Equal((s, t, c), (s2, t2, c2));
    }

    [Fact]
    public void GrandTotal_RejectsUnknown() => Assert.False(ScheduleGrandTotal.TryMap("sometimes", out _, out _, out _));

    [Theory]
    [InlineData("mm", "Millimeters")]
    [InlineData("m²", "SquareMeters")]
    [InlineData("kVA", "KilovoltAmperes")]
    [InlineData("W", "Watts")]
    [InlineData("VoltAmperes", "VoltAmperes")]
    public void FormatUnits_Resolve(string units, string property)
    {
        Assert.True(ScheduleFormatUnits.TryResolve(units, out var resolved));
        Assert.Equal(property, resolved);
    }

    [Fact]
    public void FormatUnits_RejectGarbage()
    {
        Assert.False(ScheduleFormatUnits.TryResolve("furlongs/fortnight", out _));
        Assert.False(ScheduleFormatUnits.TryResolve("", out _));
    }
}
