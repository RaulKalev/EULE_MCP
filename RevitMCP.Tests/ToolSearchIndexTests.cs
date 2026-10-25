using System.Text.RegularExpressions;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// #65: natural-language tool search over the real catalog. Names and (first literal of the)
/// descriptions are read from the bridge sources, so the tests track the actual tool surface.
/// </summary>
public class ToolSearchIndexTests
{
    private static readonly Lazy<List<ToolEntry>> Catalog = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitMCP.slnx"))) dir = dir.Parent;
        var entries = new List<ToolEntry>();
        var bridge = Path.Combine(dir!.FullName, "RevitMCP.Bridge");
        foreach (var file in Directory.GetFiles(bridge, "RevitMcpTools*.cs").OrderBy(f => f, StringComparer.Ordinal).Append(Path.Combine(bridge, "ToolDiscoveryTools.cs")))
        {
            var source = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(source,
                         @"McpServerTool\(Name = ""(?<name>[^""]+)""(?<ro>, ReadOnly = true)?\)\s*,\s*Description\(\s*""(?<desc>(?:[^""\\]|\\.)*)"""))
            {
                entries.Add(new ToolEntry
                {
                    Name = m.Groups["name"].Value,
                    Group = BridgeToolGroups.GroupOf(m.Groups["name"].Value) ?? "other",
                    Description = m.Groups["desc"].Value,
                    ReadOnly = m.Groups["ro"].Success
                });
            }
        }
        return entries;
    });

    private static List<string> Top(string query, int n = 3, string? group = null) =>
        ToolSearchIndex.Search(Catalog.Value, query, group, n).Select(t => t.Name).ToList();

    [Fact]
    public void CatalogIsParsed() => Assert.True(Catalog.Value.Count > 200, $"parsed {Catalog.Value.Count} tools");

    [Theory]
    [InlineData("circuit info", "revit_get_circuit_info")]
    [InlineData("list levels", "revit_list_levels")]
    [InlineData("place devices in rooms", "revit_place_in_room")]
    [InlineData("create cable type", "revit_create_cable_type")]
    [InlineData("export view image", "revit_export_view_image")]
    [InlineData("clash detection", "revit_detect_clashes")]
    [InlineData("ruumi geomeetria", "revit_get_room_geometry")]
    [InlineData("what changed since checkpoint", "revit_checkpoint_diff")]
    [InlineData("revit model warnings", "revit_get_warnings")]
    [InlineData("undo changes since checkpoint", "revit_preview_checkpoint_rollback")]
    [InlineData("one approval for several write steps plan", "revit_plan_submit")]
    [InlineData("plan status remaining calls", "revit_plan_status")]
    [InlineData("run a multi-step job unattended", "revit_job_submit")]
    [InlineData("job progress status", "revit_job_status")]
    public void IntentFindsTheTool_InTheTopThree(string query, string expected) =>
        Assert.Contains(expected, Top(query));

    [Fact]
    public void EstonianSynonyms_MapToTheDomain()
    {
        Assert.Contains("circuit", ToolSearchIndex.QueryTerms("ahela info"));
        Assert.Contains("panel", ToolSearchIndex.QueryTerms("kilbi koormus"));
        Assert.Contains(Top("ahela kaabel", 5), n => n.Contains("cable") || n.Contains("circuit"));
    }

    [Fact]
    public void GroupFilter_RestrictsResults()
    {
        var results = ToolSearchIndex.Search(Catalog.Value, "export", "electrical", 20);
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal("electrical", r.Group));
    }

    [Fact]
    public void EmptyOrStopWordQuery_ReturnsNothing()
    {
        Assert.Empty(ToolSearchIndex.Search(Catalog.Value, "", null));
        Assert.Empty(ToolSearchIndex.Search(Catalog.Value, "the tool for revit", null));
    }

    [Fact]
    public void Summary_IsOneSentence_AndBounded()
    {
        var entry = new ToolEntry { Description = "Lists levels. Second sentence that should go." };
        Assert.Equal("Lists levels.", entry.Summary());
        Assert.True(new ToolEntry { Description = new string('x', 500) }.Summary().Length <= 160);
    }
}
