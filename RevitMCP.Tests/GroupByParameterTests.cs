using RevitMCP.Addin.Query;
using RevitMCP.Core.Safety;
using Xunit;

namespace RevitMCP.Tests;

public class GroupByParameterTests
{
    private static ElementInfoDto Element(long id, string? paramValue, string type = "T", string family = "F")
    {
        var dto = new ElementInfoDto { ElementId = id, Category = "Data Devices", Type = type, Family = family };
        if (paramValue != null)
            dto.Parameters["ELENEA_ÜLD 001_Nimetus"] = new ParameterValueDto
            {
                Name = "ELENEA_ÜLD 001_Nimetus",
                Value = paramValue,
                Scope = "Instance"
            };
        return dto;
    }

    private static GroupingOptions ByParameter(string name, bool includeElements = false) => new()
    {
        GroupBy = new List<GroupKeyOptions> { new() { Type = "Parameter", ParameterName = name } },
        IncludeElements = includeElements
    };

    [Fact]
    public void AggregateCap_IgnoresPageSize_AndRespectsHardScanCap()
    {
        var limits = QueryLimits.Default;

        // The bug: a 0/large limit used to collapse to DefaultPageSize (100) elements.
        Assert.Equal(limits.MaxScanElements, QueryGuard.ResolveAggregateCap(0, limits));
        Assert.Equal(5000, QueryGuard.ResolveAggregateCap(5000, limits));
        Assert.Equal(limits.MaxScanElements, QueryGuard.ResolveAggregateCap(limits.MaxScanElements * 10, limits));
        Assert.True(QueryGuard.ResolveAggregateCap(0, limits) > limits.DefaultPageSize);
    }

    [Fact]
    public void Summary_CountsEveryElement_NotJustTheFirstHundred()
    {
        // 165 elements as in issue #88: 120 "A", 40 "B", 5 without the parameter.
        var elements = new List<ElementInfoDto>();
        long id = 1;
        for (var i = 0; i < 120; i++) elements.Add(Element(id++, "A"));
        for (var i = 0; i < 40; i++) elements.Add(Element(id++, "B"));
        for (var i = 0; i < 5; i++) elements.Add(Element(id++, null));

        var grouped = new GroupingEngine().Group(elements, ByParameter("Nimetus"));
        var summary = ParameterGroupSummary.Build(grouped.GroupsFlat, elements.Count, includeElementIds: false, maxElementIdsPerGroup: 100);

        Assert.Equal(165, summary.ElementsGrouped);
        Assert.Equal(160, summary.MatchedElements);
        Assert.Equal(5, summary.NotFoundElements);
        Assert.Equal(2, summary.Groups.Count);
        Assert.Equal("A", summary.Groups[0].Name);
        Assert.Equal(120, summary.Groups[0].Count);
        Assert.Equal(40, summary.Groups[1].Count);
        Assert.All(summary.Groups, g => Assert.Null(g.ElementIds));
    }

    [Fact]
    public void Summary_PagesOnlyTheElementIdLists()
    {
        var elements = Enumerable.Range(1, 150).Select(i => Element(i, i <= 130 ? "A" : "B")).ToList();

        var grouped = new GroupingEngine().Group(elements, ByParameter("Nimetus", includeElements: true));
        var summary = ParameterGroupSummary.Build(grouped.GroupsFlat, elements.Count, includeElementIds: true, maxElementIdsPerGroup: 100);

        var a = summary.Groups.Single(g => g.Name == "A");
        Assert.Equal(130, a.Count);
        Assert.Equal(100, a.ElementIds!.Count);
        Assert.True(a.ElementIdsTruncated);

        var b = summary.Groups.Single(g => g.Name == "B");
        Assert.Equal(20, b.Count);
        Assert.Equal(20, b.ElementIds!.Count);
        Assert.False(b.ElementIdsTruncated);
    }

    [Fact]
    public void Grouping_ByTypePseudoParameter_UsesTypeName()
    {
        var elements = new List<ElementInfoDto>
        {
            Element(1, "x", type: "2 x RJ45 , WiFi", family: "Pistik"),
            Element(2, "x", type: "2 x RJ45 , WiFi", family: "Pistik"),
            Element(3, "x", type: "2 x RJ45", family: "Pistik"),
        };
        // A real ElementId "Type" parameter would otherwise group by numeric id.
        foreach (var e in elements)
            e.Parameters["Type"] = new ParameterValueDto { Name = "Type", Value = (900 + e.ElementId).ToString(), Scope = "Instance" };

        var byType = new GroupingEngine().Group(elements, ByParameter("Type"));
        Assert.Equal(2, byType.TotalGroups);
        Assert.Equal(2, byType.GroupsFlat.Single(r => r.Keys["Type"] == "2 x RJ45 , WiFi").Count);

        var byFamilyAndType = new GroupingEngine().Group(elements, ByParameter("Family and Type"));
        Assert.Contains(byFamilyAndType.GroupsFlat, r => r.Keys["Family and Type"] == "Pistik: 2 x RJ45");
    }
}
