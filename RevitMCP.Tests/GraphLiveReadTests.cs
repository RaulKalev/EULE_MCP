using RevitMCP.Addin.Graph;
using RevitMCP.Addin.Query;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>revit_graph_query read=[…]: which nodes are read live and the shape of the <c>live</c> block.</summary>
public class GraphLiveReadTests
{
    [Fact]
    public void Names_are_trimmed_and_deduplicated()
        => Assert.Equal(new[] { "Mark", "Level" }, GraphLiveRead.Names(new[] { " Mark ", "", "mark", "Level", null! }));

    [Fact]
    public void Select_keeps_host_elements_in_order_and_counts_the_rest()
    {
        var selection = GraphLiveRead.Select(new[] { "12", "link:5:77", "ws:3", "7", "12", null, "-4" });

        Assert.Equal(new long[] { 12, 7 }, selection.HostIds);
        Assert.Equal(1, selection.Linked);
        Assert.Equal(3, selection.NotElements);
        Assert.Equal(0, selection.Truncated);
    }

    [Fact]
    public void Select_stops_at_the_cap_and_reports_what_was_left_out()
    {
        var selection = GraphLiveRead.Select(Enumerable.Range(1, 7).Select(i => i.ToString()), max: 5);

        Assert.Equal(5, selection.HostIds.Count);
        Assert.Equal(2, selection.Truncated);
        Assert.Contains(GraphLiveRead.Warnings(selection, read: 5), w => w.Contains("2 more were not read"));
    }

    [Fact]
    public void Block_maps_values_by_element_id_and_names_what_was_not_read()
    {
        var selection = GraphLiveRead.Select(new[] { "12", "7", "9", "link:5:77" });
        var values = new Dictionary<long, Dictionary<string, string>>
        {
            [12] = new() { ["Mark"] = "A1", ["Height"] = "3200 mm" },
            [7] = new()
        };

        var block = GraphLiveRead.Block(selection, new[] { "Mark", "Height" }, values);

        Assert.Equal(2, (int)block["read"]!);
        Assert.Equal("A1", (string?)block["values"]!["12"]!["Mark"]);
        Assert.Equal("3200 mm", (string?)block["values"]!["12"]!["Height"]);
        Assert.Empty(block["values"]!["7"]!);
        Assert.Equal(new long[] { 9 }, block["notRead"]!.Select(t => (long)t));
        Assert.Equal(1, (int)block["linkedSkipped"]!);
        Assert.Null(block["truncated"]);
        Assert.Null(block["nonElementSkipped"]);

        var warnings = GraphLiveRead.Warnings(selection, values.Count);
        Assert.Contains(warnings, w => w.Contains("linked-model"));
        Assert.Contains(warnings, w => w.Contains("1 element(s) from the graph could not be read"));
    }

    [Fact]
    public void A_complete_read_has_no_warnings_and_no_optional_fields()
    {
        var selection = GraphLiveRead.Select(new[] { "12" });
        var values = new Dictionary<long, Dictionary<string, string>> { [12] = new() { ["Mark"] = "A1" } };

        var block = GraphLiveRead.Block(selection, new[] { "Mark" }, values);

        Assert.Empty(GraphLiveRead.Warnings(selection, 1));
        Assert.Equal(new[] { "parameters", "read", "values" }, block.Properties().Select(p => p.Name));
    }

    [Fact]
    public void Compact_parameters_carry_the_unit_like_compact_element_reads()
    {
        var dto = new ElementInfoDto { ElementId = 1 };
        dto.Parameters["Height"] = new ParameterValueDto { Name = "Height", Value = "3200", Unit = "mm" };
        dto.Parameters["Mark"] = new ParameterValueDto { Name = "Mark", Value = "A1" };
        dto.Parameters["Offset"] = new ParameterValueDto { Name = "Offset", Value = "", Unit = "mm" };

        var values = ElementQueryResponseFormatter.CompactParameters(dto);

        Assert.Equal("3200 mm", values["Height"]);
        Assert.Equal("A1", values["Mark"]);
        Assert.Equal("", values["Offset"]);
    }
}
