using RevitMCP.Addin.Graph;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>SQLite layer tests — in-memory database, synthetic nodes/edges, no Revit.</summary>
public class GraphDatabaseTests
{
    // Synthetic model:
    //   panel P1 (id 10) on level L1 (id 1), workset ws:5
    //   circuits C1 (20) and C2 (21) fed_by P1; C3 (22) has no panel (orphan)
    //   elements E1 (30), E2 (31) fed_by C1; E3 (32) fed_by C2; E4 (33) uncircuited
    //   E1, E2 located_in space S1 (40); E3 has no location
    //   E1 hosted_on wall W1 (50); all elements type_of T1 (60)
    //   view V1 (70) on_sheet SH1 (80); E1 tagged_in V1
    private static GraphDatabase BuildSample()
    {
        var db = GraphDatabase.CreateInMemory();
        var nodes = new List<GraphNode>
        {
            N("1", "level", "L1", "Levels"),
            N("ws:5", "workset", "Electrical", "Worksets"),
            N("10", "panel", "P1", "Electrical Equipment", "L1", "Electrical", "{\"panelName\":\"P1\"}"),
            N("20", "circuit", "P1/1 Lights", "Electrical Circuits", "L1"),
            N("21", "circuit", "P1/2 Sockets", "Electrical Circuits", "L1"),
            N("22", "circuit", "?/3 Orphan", "Electrical Circuits"),
            N("30", "element", "Luminaire A", "Lighting Fixtures", "L1", "Electrical"),
            N("31", "element", "Luminaire B", "Lighting Fixtures", "L1", "Electrical"),
            N("32", "element", "Socket Ä", "Electrical Fixtures", "L1", "Electrical"),
            N("33", "element", "Unwired socket", "Electrical Fixtures", "L2"),
            N("40", "space", "101 Office", "Rooms", "L1"),
            N("50", "element", "Basic Wall", "Walls", "L1", "Architecture"),
            N("60", "type", "LED: 600x600", "Lighting Fixtures"),
            N("70", "view", "Level 1 - Electrical", "Views", "L1"),
            N("80", "sheet", "E-101 - Lighting", "Sheets")
        };
        var edges = new List<GraphEdge>
        {
            E("20", "10", "fed_by"), E("21", "10", "fed_by"),
            E("30", "20", "fed_by"), E("31", "20", "fed_by"), E("32", "21", "fed_by"),
            E("30", "40", "located_in"), E("31", "40", "located_in"),
            E("30", "50", "hosted_on"),
            E("30", "60", "type_of"), E("31", "60", "type_of"), E("32", "60", "type_of"), E("33", "60", "type_of"),
            E("70", "80", "on_sheet"),
            E("30", "70", "tagged_in"),
            E("10", "1", "on_level"), E("30", "1", "on_level"), E("40", "1", "on_level"),
            E("10", "ws:5", "in_workset"), E("30", "ws:5", "in_workset"),
            // duplicate edge must be ignored
            E("30", "20", "fed_by")
        };
        var meta = new Dictionary<string, string>
        {
            [GraphSchema.MetaKeys.ModelName] = "Sample",
            [GraphSchema.MetaKeys.BuiltAt] = "2026-01-01T00:00:00Z",
            [GraphSchema.MetaKeys.CentralVersion] = "saves:3:abc",
            [GraphSchema.MetaKeys.ElementCount] = "15",
            [GraphSchema.MetaKeys.SchemaVersion] = GraphSchema.SchemaVersion.ToString()
        };
        db.WriteGraph(nodes, edges, meta);
        return db;
    }

    private static GraphNode N(string id, string kind, string name, string category, string level = "", string workset = "", string? extra = null) =>
        new() { Id = id, Kind = kind, Name = name, Category = category, Level = level, Workset = workset, Extra = extra };

    private static GraphEdge E(string src, string dst, string rel) => new(src, dst, rel);

    [Fact]
    public void WriteGraph_StoresCountsAndMeta_AndIgnoresDuplicateEdges()
    {
        using var db = BuildSample();
        var (nodes, edges) = db.Counts();
        Assert.Equal(15, nodes);
        Assert.Equal(19, edges); // 20 listed, one duplicate

        var meta = db.ReadMeta();
        Assert.Equal("Sample", meta.ModelName);
        Assert.Equal("saves:3:abc", meta.CentralVersion);
        Assert.Equal(15, meta.ElementCount);
        Assert.Equal(GraphSchema.SchemaVersion, meta.SchemaVersion);
        Assert.Equal(15, meta.NodeCount);
        Assert.Equal(19, meta.EdgeCount);
    }

    [Fact]
    public void GetNode_ReturnsRowWithExtra()
    {
        using var db = BuildSample();
        var panel = db.GetNode("10");
        Assert.NotNull(panel);
        Assert.Equal("panel", panel!.Kind);
        Assert.Equal("P1", panel.Name);
        Assert.Equal("L1", panel.Level);
        Assert.Equal("Electrical", panel.Workset);
        Assert.Contains("panelName", panel.Extra);
        Assert.Null(db.GetNode("999"));
    }

    [Fact]
    public void Neighbors_RespectsRelAndDirection()
    {
        using var db = BuildSample();

        var incomingFeeds = db.Neighbors("10", "fed_by", "in", 100);
        Assert.Equal(2, incomingFeeds.Count);
        Assert.All(incomingFeeds, n => Assert.Equal("circuit", n.Node.Kind));
        Assert.All(incomingFeeds, n => Assert.Equal("in", n.Direction));

        var outgoing = db.Neighbors("30", null, "out", 100);
        Assert.Equal(7, outgoing.Count); // fed_by, located_in, hosted_on, type_of, tagged_in, on_level, in_workset
    }

    [Fact]
    public void Neighbors_OutgoingFromElement_ListsEveryRelationship()
    {
        using var db = BuildSample();
        var outgoing = db.Neighbors("30", null, "out", 100);
        var rels = outgoing.Select(n => n.Rel).OrderBy(r => r).ToArray();
        Assert.Equal(new[] { "fed_by", "hosted_on", "in_workset", "located_in", "on_level", "tagged_in", "type_of" }, rels);

        var both = db.Neighbors("20", null, "both", 100);
        Assert.Contains(both, n => n.Direction == "out" && n.Node.Id == "10");
        Assert.Contains(both, n => n.Direction == "in" && n.Node.Id == "30");
    }

    [Fact]
    public void Neighbors_HonoursLimit()
    {
        using var db = BuildSample();
        var limited = db.Neighbors("60", "type_of", "in", 2);
        Assert.Equal(2, limited.Count);
    }

    [Fact]
    public void Find_FiltersAndPaginates()
    {
        using var db = BuildSample();

        var lighting = db.Find("element", "Lighting Fixtures", null, null, null, 0, 10);
        Assert.Equal(2, lighting.TotalAvailable);
        Assert.False(lighting.HasMore);

        var page0 = db.Find("element", null, null, null, null, 0, 2);
        Assert.Equal(5, page0.TotalAvailable);
        Assert.Equal(2, page0.ItemsReturned);
        Assert.True(page0.HasMore);
        Assert.Equal("1", page0.NextPageToken);

        var page2 = db.Find("element", null, null, null, null, 2, 2);
        Assert.Equal(1, page2.ItemsReturned);
        Assert.False(page2.HasMore);

        var all = page0.Items.Concat(db.Find("element", null, null, null, null, 1, 2).Items).Concat(page2.Items)
            .Select(n => n.Id).ToList();
        Assert.Equal(5, all.Distinct().Count());
    }

    [Fact]
    public void Find_NameContains_IsCaseInsensitiveForAscii_AndEscapesWildcards()
    {
        using var db = BuildSample();
        var byName = db.Find(null, null, null, null, "luminaire", 0, 10);
        Assert.Equal(2, byName.TotalAvailable);

        var literalPercent = db.Find(null, null, null, null, "%", 0, 10);
        Assert.Equal(0, literalPercent.TotalAvailable);

        var unicode = db.Find(null, null, null, null, "Ä", 0, 10);
        Assert.Equal(1, unicode.TotalAvailable);
    }

    [Fact]
    public void Find_LevelAndWorksetFilters_AreCaseInsensitive()
    {
        using var db = BuildSample();
        Assert.Equal(1, db.Find("element", null, "l2", null, null, 0, 10).TotalAvailable);
        Assert.Equal(3, db.Find("element", null, null, "electrical", null, 0, 10).TotalAvailable);
        Assert.Equal(1, db.Find("PANEL", null, null, null, null, 0, 10).TotalAvailable);
    }

    [Fact]
    public void FindPath_FindsShortestUndirectedPath()
    {
        using var db = BuildSample();

        // Panel → luminaire is 2 hops either via the circuit (10 ← 20 ← 30) or via the shared
        // level (10 → 1 ← 30); BFS must report 2 hops with the right endpoints.
        var path = db.FindPath("10", "30", 6);
        Assert.True(path.Found);
        Assert.Equal(2, path.Hops);
        Assert.Equal(3, path.Steps.Count);
        Assert.Equal("10", path.Steps[0].Node.Id);
        Assert.Equal("30", path.Steps[2].Node.Id);
        Assert.Null(path.Steps[0].Rel);
        Assert.All(path.Steps.Skip(1), s => Assert.False(string.IsNullOrEmpty(s.Rel)));

        // Circuit → its socket: the only route is the fed_by edge 32 → 21 walked backwards.
        var direct = db.FindPath("21", "32", 6);
        Assert.True(direct.Found);
        Assert.Equal(1, direct.Hops);
        Assert.Equal("fed_by", direct.Steps[1].Rel);
        Assert.Equal("in", direct.Steps[1].Direction);

        // Socket → circuit walks the same edge forwards.
        var forward = db.FindPath("32", "21", 6);
        Assert.Equal("out", forward.Steps[1].Direction);
    }

    [Fact]
    public void FindPath_RespectsMaxHops_AndUnknownIds()
    {
        using var db = BuildSample();
        Assert.False(db.FindPath("10", "30", 1).Found);
        Assert.False(db.FindPath("10", "999", 6).Found);
        Assert.False(db.FindPath("999", "10", 6).Found);

        var self = db.FindPath("10", "10", 3);
        Assert.True(self.Found);
        Assert.Equal(0, self.Hops);

        // sheet SH1 → panel P1: 80 ← 70 ← 30 → 20 → 10 (4 hops)
        var sheetToPanel = db.FindPath("80", "10", 10);
        Assert.True(sheetToPanel.Found);
        Assert.Equal(4, sheetToPanel.Hops);
    }

    [Fact]
    public void Subtree_FollowsOneRelationship_InGivenDirection()
    {
        using var db = BuildSample();

        var fedByPanel = db.Subtree("10", "fed_by", 3, "in", 100);
        Assert.NotNull(fedByPanel.Root);
        var ids = fedByPanel.Nodes.Select(n => n.Node.Id).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "20", "21", "30", "31", "32" }, ids);
        Assert.Equal(1, fedByPanel.Nodes.First(n => n.Node.Id == "20").Depth);
        Assert.Equal(2, fedByPanel.Nodes.First(n => n.Node.Id == "30").Depth);
        Assert.Equal("20", fedByPanel.Nodes.First(n => n.Node.Id == "30").ParentId);
        Assert.False(fedByPanel.Truncated);
        Assert.Equal(2, fedByPanel.MaxDepthReached);

        var depth1 = db.Subtree("10", "fed_by", 1, "in", 100);
        Assert.Equal(2, depth1.Nodes.Count);

        var outward = db.Subtree("30", "fed_by", 5, "out", 100);
        Assert.Equal(new[] { "20", "10" }, outward.Nodes.Select(n => n.Node.Id).ToArray());
    }

    [Fact]
    public void Subtree_TruncatesAtMaxNodes_AndHandlesUnknownRoot()
    {
        using var db = BuildSample();
        var truncated = db.Subtree("10", "fed_by", 3, "in", 3);
        Assert.True(truncated.Truncated);
        Assert.Equal(3, truncated.Nodes.Count);

        var missing = db.Subtree("999", "fed_by", 3, "in", 10);
        Assert.Null(missing.Root);
        Assert.Empty(missing.Nodes);
    }

    [Fact]
    public void Summarize_ReportsCountsOrphansAndUnlocatedElements()
    {
        using var db = BuildSample();
        var s = db.Summarize(topN: 10, sampleSize: 10);

        Assert.Equal(5, s.NodesByKind.First(c => c.Key == "element").Count);
        Assert.Equal(3, s.NodesByKind.First(c => c.Key == "circuit").Count);
        Assert.Equal(5, s.EdgesByRel.First(c => c.Key == "fed_by").Count);

        Assert.Equal(2, s.ElementsByCategory.First(c => c.Key == "Lighting Fixtures").Count);
        Assert.Equal(4, s.ElementsByLevel.First(c => c.Key == "L1").Count);
        Assert.Contains(s.ElementsByWorkset, c => c.Key == string.Empty && c.Count == 1); // E4 has no workset

        var panel = Assert.Single(s.Panels);
        Assert.Equal("10", panel.Id);
        Assert.Equal(2, panel.CircuitCount);
        Assert.Equal(3, panel.FedElementCount);

        Assert.Equal(1, s.CircuitsWithoutPanelCount);
        Assert.Equal("22", Assert.Single(s.CircuitsWithoutPanel).Id);
        Assert.Equal(1, s.CircuitsWithoutElementsCount);
        Assert.Equal("22", Assert.Single(s.CircuitsWithoutElements).Id);

        Assert.Equal(3, s.ElementsWithoutLocationCount); // 32, 33, 50
        Assert.Equal(new[] { "32", "33", "50" }, s.ElementsWithoutLocation.Select(n => n.Id).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Summarize_TopN_LimitsCategoryList()
    {
        using var db = BuildSample();
        var s = db.Summarize(topN: 1, sampleSize: 0);
        Assert.Single(s.ElementsByCategory);
        Assert.Empty(s.CircuitsWithoutPanel);
        Assert.Equal(1, s.CircuitsWithoutPanelCount);
    }

    [Fact]
    public void CreateNew_ThenOpenReadOnly_RoundTripsThroughFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rkmcp_graph_{Guid.NewGuid():N}.graph.db");
        try
        {
            using (var db = GraphDatabase.CreateNew(path))
            {
                db.WriteGraph(
                    new[] { N("1", "level", "L1", "Levels"), N("2", "element", "Wall", "Walls", "L1") },
                    new[] { E("2", "1", "on_level") },
                    new Dictionary<string, string> { [GraphSchema.MetaKeys.ModelName] = "File" });
            }
            Assert.False(File.Exists(path + "-journal"));
            Assert.False(File.Exists(path + "-wal"));

            using var ro = GraphDatabase.OpenReadOnly(path);
            Assert.True(ro.IsReadOnly);
            Assert.Equal("File", ro.ReadMeta().ModelName);
            Assert.Single(ro.Neighbors("2", "on_level", "out", 10));
            Assert.Throws<InvalidOperationException>(() =>
                ro.WriteGraph(Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), new Dictionary<string, string>()));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void OpenReadOnly_MissingFile_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rkmcp_graph_missing_{Guid.NewGuid():N}.graph.db");
        Assert.Throws<FileNotFoundException>(() => GraphDatabase.OpenReadOnly(path));
    }

    [Fact]
    public void Schema_VocabularyChecks()
    {
        Assert.True(GraphSchema.IsKind("Panel"));
        Assert.False(GraphSchema.IsKind("wall"));
        Assert.True(GraphSchema.IsRel("FED_BY"));
        Assert.False(GraphSchema.IsRel("feeds"));
        Assert.Equal(9, GraphSchema.Kinds.All.Length);
        Assert.Equal(8, GraphSchema.Rels.All.Length);
    }
}
