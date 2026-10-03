using Microsoft.Data.Sqlite;
using RevitMCP.Addin.Graph;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// Incremental graph updates (#61): change tracking, the incremental/full decision, dependent-id
/// expansion and <see cref="GraphDatabase.ApplyDelta"/>. Each delta test checks the resulting node
/// and edge sets against what a full build of the changed model would contain.
/// </summary>
public class GraphIncrementalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "graph-incr-" + Guid.NewGuid().ToString("N"));

    public GraphIncrementalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Base model (as a full build extracts it, with edge owners):
    //   levels L1 (1), L2 (2); panel P1 (10) on L1; circuit C1 (20) fed_by P1
    //   elements 30, 31 on circuit C1, type 60, in space 40; element 32 type 61, no circuit
    //   view V1 (70) on sheet 80; tags 90 and 91 both tag element 30 in V1
    private static List<GraphNode> BaseNodes() =>
    [
        N("1", "level", "L1"), N("2", "level", "L2"),
        N("10", "panel", "P1", "L1"),
        N("20", "circuit", "P1/1", "L1"),
        N("30", "element", "Light A", "L1"), N("31", "element", "Light B", "L1"), N("32", "element", "Socket", "L1"),
        N("40", "space", "101 Office", "L1"),
        N("60", "type", "LED"), N("61", "type", "Socket type"),
        N("70", "view", "Plan L1", "L1"), N("80", "sheet", "E-101")
    ];

    private static List<GraphEdge> BaseEdges() =>
    [
        E("10", "1", "on_level", "10"),
        E("20", "10", "fed_by", "20"),
        E("30", "20", "fed_by", "20"), E("31", "20", "fed_by", "20"),
        E("30", "1", "on_level", "30"), E("30", "60", "type_of", "30"), E("30", "40", "located_in", "30"),
        E("31", "1", "on_level", "31"), E("31", "60", "type_of", "31"), E("31", "40", "located_in", "31"),
        E("32", "1", "on_level", "32"), E("32", "61", "type_of", "32"),
        E("40", "1", "on_level", "40"),
        E("70", "1", "on_level", "70"), E("70", "80", "on_sheet", "80"),
        E("30", "70", "tagged_in", "90"), E("30", "70", "tagged_in", "91")
    ];

    // ─── ApplyDelta ────────────────────────────────────────────────────────

    [Fact]
    public void ModifiedElement_ReplacesItsNodeAndOwnedEdges_KeepsEdgesOwnedByOthers()
    {
        var path = Seed();
        // Element 30 moved to L2 and out of the room; still on circuit C1 and still tagged.
        Apply(path, refreshed: ["30"], deleted: [],
            nodes: [N("30", "element", "Light A", "L2")],
            edges: [E("30", "2", "on_level", "30"), E("30", "60", "type_of", "30")]);

        var expectedNodes = BaseNodes().Select(n => n.Id == "30" ? N("30", "element", "Light A", "L2") : n);
        var expectedEdges = BaseEdges()
            .Where(e => !(e.Src == "30" && e.Owner == "30"))
            .Concat([E("30", "2", "on_level", "30"), E("30", "60", "type_of", "30")]);
        AssertGraph(path, expectedNodes, expectedEdges);
        Assert.Equal("L2", NodeLevel(path, "30"));
    }

    [Fact]
    public void DeletedElement_RemovesNodeAndEveryTouchingEdge_EvenWhenOwnedByItsCircuit()
    {
        var path = Seed();
        var result = Apply(path, refreshed: [], deleted: ["31"], nodes: [], edges: []);

        AssertGraph(path,
            BaseNodes().Where(n => n.Id != "31"),
            BaseEdges().Where(e => e.Src != "31" && e.Dst != "31"));
        Assert.Equal(1, result.NodesRemoved);
    }

    [Fact]
    public void DeletingTheLastInstance_PrunesItsTypeNode()
    {
        var path = Seed();
        var result = Apply(path, refreshed: [], deleted: ["32"], nodes: [], edges: []);

        AssertGraph(path,
            BaseNodes().Where(n => n.Id is not "32" and not "61"),
            BaseEdges().Where(e => e.Src != "32"));
        Assert.Equal(1, result.TypesPruned);
    }

    [Fact]
    public void AddedElement_WithNewType_AddsNodesAndEdges()
    {
        var path = Seed();
        Apply(path, refreshed: ["33", "62"], deleted: [],
            nodes: [N("33", "element", "Detector", "L1"), N("62", "type", "Smoke")],
            edges: [E("33", "1", "on_level", "33"), E("33", "62", "type_of", "33"), E("33", "40", "located_in", "33")]);

        AssertGraph(path,
            BaseNodes().Concat([N("33", "element", "Detector", "L1"), N("62", "type", "Smoke")]),
            BaseEdges().Concat([E("33", "1", "on_level", "33"), E("33", "62", "type_of", "33"), E("33", "40", "located_in", "33")]));
    }

    [Fact]
    public void EdgesToMissingNodes_AreDroppedLikeAFullBuild()
    {
        var path = Seed();
        var result = Apply(path, refreshed: ["32"], deleted: [],
            nodes: [N("32", "element", "Socket", "L1")],
            edges: [E("32", "1", "on_level", "32"), E("32", "61", "type_of", "32"), E("32", "999", "hosted_on", "32")]);

        Assert.Equal(1, result.DanglingEdgesDropped);
        AssertGraph(path, BaseNodes(), BaseEdges());
    }

    [Fact]
    public void RefreshedCircuit_DropsMembersThatLeftIt()
    {
        var path = Seed();
        // Element 31 was removed from circuit C1.
        Apply(path, refreshed: ["20"], deleted: [],
            nodes: [N("20", "circuit", "P1/1", "L1")],
            edges: [E("20", "10", "fed_by", "20"), E("30", "20", "fed_by", "20")]);

        AssertGraph(path, BaseNodes(), BaseEdges().Where(e => !(e.Src == "31" && e.Rel == "fed_by")));
    }

    [Fact]
    public void EdgeProducedByTwoOwners_SurvivesUntilBothAreGone()
    {
        var path = Seed();
        Apply(path, refreshed: [], deleted: ["90"], nodes: [], edges: []);
        Assert.Contains(("30", "70", "tagged_in"), ReadEdges(path));

        Apply(path, refreshed: [], deleted: ["91"], nodes: [], edges: []);
        Assert.DoesNotContain(("30", "70", "tagged_in"), ReadEdges(path));
    }

    [Fact]
    public void RefreshedIdThatNoLongerYieldsANode_LosesEdgesOwnedByOthers()
    {
        var path = Seed();
        // Element 30 re-extracted but is no longer a graph element (no node returned).
        Apply(path, refreshed: ["30"], deleted: [], nodes: [], edges: []);

        Assert.DoesNotContain(ReadEdges(path), e => e.Src == "30" || e.Dst == "30");
        Assert.Equal(0, ScalarLong(path, "SELECT COUNT(*) FROM edge_owners WHERE src = '30'"));
    }

    [Fact]
    public void ApplyDelta_UpdatesMetaAndCounts()
    {
        var path = Seed();
        var result = Apply(path, refreshed: [], deleted: ["31"], nodes: [], edges: [],
            meta: new() { [GraphSchema.MetaKeys.BuiltAt] = "2026-02-02T00:00:00Z", [GraphSchema.MetaKeys.IncrementalUpdates] = "1" });

        using var db = GraphDatabase.OpenReadOnly(path);
        var meta = db.ReadMeta();
        Assert.Equal("2026-02-02T00:00:00Z", meta.BuiltAt);
        Assert.Equal("1", meta.Get(GraphSchema.MetaKeys.IncrementalUpdates));
        Assert.Equal(result.NodeCount, meta.NodeCount);
        Assert.Equal(result.EdgeCount, meta.EdgeCount);
        Assert.Equal(BaseNodes().Count - 1, result.NodeCount);
    }

    [Fact]
    public void FullBuild_RecordsEdgeOwners()
    {
        var path = Seed();
        using var db = GraphDatabase.OpenReadOnly(path);
        Assert.True(db.HasEdgeOwners());
        Assert.Equal(BaseEdges().Count, ScalarLong(path, "SELECT COUNT(*) FROM edge_owners"));
        Assert.Equal(BaseEdges().Count - 1, ScalarLong(path, "SELECT COUNT(*) FROM edges")); // tagged_in shared by two tags
    }

    [Fact]
    public void KindsOf_ReturnsOnlyExistingNodes()
    {
        var path = Seed();
        using var db = GraphDatabase.OpenReadOnly(path);
        var kinds = db.KindsOf(["1", "20", "404"]);
        Assert.Equal(2, kinds.Count);
        Assert.Equal("level", kinds["1"]);
        Assert.Equal("circuit", kinds["20"]);
    }

    // ─── Planner ───────────────────────────────────────────────────────────

    private static IncrementalInputs Ok() => new() { GraphExists = true, HasEdgeOwners = true, BaselineMatches = true };

    [Fact]
    public void Decide_IncrementalWhenEverythingIsKnown()
    {
        Assert.True(GraphIncrementalPlanner.Decide(Ok()).Incremental);
    }

    [Theory]
    [InlineData("noGraph", "no graph")]
    [InlineData("noOwners", "schema 1")]
    [InlineData("baseline", "not tracked")]
    [InlineData("overflow", "element changes")]
    [InlineData("limit", "element limit")]
    [InlineData("unsafe", "a level was deleted")]
    public void Decide_FallsBackToFull(string which, string reasonPart)
    {
        var i = Ok();
        switch (which)
        {
            case "noGraph": i.GraphExists = false; break;
            case "noOwners": i.HasEdgeOwners = false; break;
            case "baseline": i.BaselineMatches = false; break;
            case "overflow": i.Overflow = true; break;
            case "limit": i.ElementLimitReached = true; break;
            case "unsafe": i.UnsafeChanges.Add("a level was deleted"); break;
        }
        var d = GraphIncrementalPlanner.Decide(i);
        Assert.False(d.Incremental);
        Assert.Contains(reasonPart, d.Reason);
    }

    [Theory]
    [InlineData("level", false, false, true)]
    [InlineData("level", false, true, true)]
    [InlineData("level", true, false, false)]   // a new level has no elements yet
    [InlineData("space", true, false, true)]
    [InlineData("space", false, false, true)]
    [InlineData("space", false, true, false)]   // deleting a room only removes edges to it
    [InlineData("element", false, false, false)]
    [InlineData("circuit", false, true, false)]
    [InlineData(null, true, false, false)]
    public void UnsafeReason_ByKind(string? kind, bool added, bool deleted, bool isUnsafe)
    {
        Assert.Equal(isUnsafe, GraphIncrementalPlanner.UnsafeReason(kind, added, deleted) != null);
    }

    [Fact]
    public void ExpandRefreshSet_AddsDependentPanelsCircuitsAndInstances()
    {
        var path = Seed();
        using var db = GraphDatabase.OpenReadOnly(path);
        string? KindOf(string id) => db.GetNode(id)?.Kind;
        IEnumerable<string> Neighbors(string id, string rel, bool outgoing) =>
            db.Neighbors(id, rel, outgoing ? "out" : "in", 1000).Select(n => n.Node.Id);

        // Changed circuit → its panel (may stop being a panel).
        Assert.Equal(["10", "20"], Sorted(GraphIncrementalPlanner.ExpandRefreshSet(["20"], [], KindOf, Neighbors)));
        // Deleted circuit → its panel, never the deleted id itself.
        Assert.Equal(["10"], Sorted(GraphIncrementalPlanner.ExpandRefreshSet([], ["20"], KindOf, Neighbors)));
        // Changed panel → its circuits (their names/levels come from the panel).
        Assert.Equal(["10", "20"], Sorted(GraphIncrementalPlanner.ExpandRefreshSet(["10"], [], KindOf, Neighbors)));
        // Changed type → its instances.
        Assert.Equal(["30", "31", "60"], Sorted(GraphIncrementalPlanner.ExpandRefreshSet(["60"], [], KindOf, Neighbors)));
        // Plain element → only itself.
        Assert.Equal(["32"], Sorted(GraphIncrementalPlanner.ExpandRefreshSet(["32"], [], KindOf, Neighbors)));
    }

    // ─── Change set ────────────────────────────────────────────────────────

    [Fact]
    public void ChangeSet_AddedThenDeleted_CancelsOut()
    {
        var set = new GraphChangeSet();
        set.Record(added: [5], modified: [], deleted: []);
        set.Record(added: [], modified: [5], deleted: []);
        set.Record(added: [], modified: [], deleted: [5]);
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void ChangeSet_ModifiedThenDeleted_IsDeleted()
    {
        var set = new GraphChangeSet();
        set.Record(added: [], modified: [7], deleted: []);
        set.Record(added: [], modified: [], deleted: [7]);
        Assert.Empty(set.Modified);
        Assert.Equal([7L], set.Deleted);
    }

    [Fact]
    public void ChangeSet_OverflowsPastTheLimit_AndResetClearsIt()
    {
        var set = new GraphChangeSet();
        set.Record(Enumerable.Range(0, GraphChangeSet.MaxTrackedIds + 1).Select(i => (long)i), [], []);
        Assert.True(set.Overflow);
        Assert.Equal(0, set.Count);

        set.Reset("t1", @"C:\g\a.graph.db");
        Assert.False(set.Overflow);
        Assert.True(set.MatchesBaseline("t1", @"c:\G\A.graph.db"));
        Assert.False(set.MatchesBaseline("t2", @"C:\g\a.graph.db"));
        Assert.False(new GraphChangeSet().MatchesBaseline("t1", @"C:\g\a.graph.db"));
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private string Seed()
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + GraphSchema.FileExtension);
        using var db = GraphDatabase.CreateNew(path);
        db.WriteGraph(BaseNodes(), BaseEdges(), new Dictionary<string, string>
        {
            [GraphSchema.MetaKeys.BuiltAt] = "2026-01-01T00:00:00Z",
            [GraphSchema.MetaKeys.SchemaVersion] = GraphSchema.SchemaVersion.ToString()
        });
        return path;
    }

    private static GraphDeltaResult Apply(string path, string[] refreshed, string[] deleted,
        GraphNode[] nodes, GraphEdge[] edges, Dictionary<string, string>? meta = null)
    {
        using var db = GraphDatabase.OpenReadWrite(path);
        return db.ApplyDelta(refreshed, deleted, nodes, edges, meta ?? new Dictionary<string, string>());
    }

    private static void AssertGraph(string path, IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges)
    {
        Assert.Equal(
            nodes.Select(n => n.Id).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            ReadNodeIds(path));
        Assert.Equal(
            edges.Select(e => (e.Src, e.Dst, e.Rel)).Distinct().OrderBy(x => x).ToList(),
            ReadEdges(path).OrderBy(x => x).ToList());
    }

    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        c.Open();
        return c;
    }

    private static List<string> ReadNodeIds(string path)
    {
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM nodes";
        using var r = cmd.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static List<(string Src, string Dst, string Rel)> ReadEdges(string path)
    {
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT src, dst, rel FROM edges";
        using var r = cmd.ExecuteReader();
        var edges = new List<(string, string, string)>();
        while (r.Read()) edges.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        return edges;
    }

    private static string NodeLevel(string path, string id)
    {
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT level FROM nodes WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return (string)cmd.ExecuteScalar()!;
    }

    private static long ScalarLong(string path, string sql)
    {
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static List<string> Sorted(IEnumerable<string> ids) => ids.OrderBy(x => x, StringComparer.Ordinal).ToList();

    private static GraphNode N(string id, string kind, string name, string level = "") =>
        new() { Id = id, Kind = kind, Name = name, Category = kind, Level = level };

    private static GraphEdge E(string src, string dst, string rel, string owner) => new(src, dst, rel, owner);
}
