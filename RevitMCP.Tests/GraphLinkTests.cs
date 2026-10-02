using RevitMCP.Addin.Graph;
using RevitMCP.Core.Safety;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Linked-model nodes in the graph (#62): namespaced ids, coexistence and the find link filter.</summary>
public class GraphLinkTests
{
    // Host: element 100, space 40, link instances 5 (architecture) and 55 (electrical) — host elements.
    // Both links contain an element with linked id 100 and a type 200 (same ids as the host element),
    // link 5 contains room 300; link 55's element sits in host space 40 (geometric located_in).
    private static GraphDatabase BuildSample()
    {
        var db = GraphDatabase.CreateInMemory();
        var nodes = new List<GraphNode>
        {
            N("100", "element", "Host socket", "Electrical Fixtures"),
            N("40", "space", "101 Office", "Rooms"),
            N("5", "link", "ARK.rvt : 1 : location <Not Shared>", "RVT Links"),
            N("55", "link", "EL.rvt : 2 : location <Not Shared>", "RVT Links"),
            N(GraphLinkIds.Make(5, 100), "element", "Door 900", "Doors", "{\"linkName\":\"ARK.rvt : 1\"}"),
            N(GraphLinkIds.Make(5, 200), "type", "Single door: 900", "Doors"),
            N(GraphLinkIds.Make(5, 300), "space", "1.12 Corridor", "Rooms"),
            N(GraphLinkIds.Make(55, 100), "element", "Smoke detector", "Fire Alarm Devices"),
            N(GraphLinkIds.Make(55, 200), "type", "Detector: Optical", "Fire Alarm Devices")
        };
        var edges = new List<GraphEdge>
        {
            new(GraphLinkIds.Make(5, 100), "5", "in_link"),
            new(GraphLinkIds.Make(5, 200), "5", "in_link"),
            new(GraphLinkIds.Make(5, 300), "5", "in_link"),
            new(GraphLinkIds.Make(5, 100), GraphLinkIds.Make(5, 200), "type_of"),
            new(GraphLinkIds.Make(5, 100), GraphLinkIds.Make(5, 300), "located_in"),
            new(GraphLinkIds.Make(55, 100), "55", "in_link"),
            new(GraphLinkIds.Make(55, 200), "55", "in_link"),
            new(GraphLinkIds.Make(55, 100), GraphLinkIds.Make(55, 200), "type_of"),
            new(GraphLinkIds.Make(55, 100), "40", "located_in"),
            new("100", "40", "located_in")
        };
        db.WriteGraph(nodes, edges, new Dictionary<string, string> { [GraphSchema.MetaKeys.SchemaVersion] = GraphSchema.SchemaVersion.ToString() });
        return db;
    }

    private static GraphNode N(string id, string kind, string name, string category, string? extra = null) =>
        new() { Id = id, Kind = kind, Name = name, Category = category, Extra = extra };

    private static List<string> Ids(PagedResult<GraphNode> r) => r.Items.Select(n => n.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();

    [Fact]
    public void LinkIds_RoundTrip_AndRejectHostAndMalformedIds()
    {
        var id = GraphLinkIds.Make(5, 100);
        Assert.Equal("link:5:100", id);
        Assert.True(GraphLinkIds.TryParse(id, out var inst, out var elem));
        Assert.Equal((5L, 100L), (inst, elem));
        Assert.Equal("5", GraphLinkIds.LinkInstanceOf(id));

        Assert.False(GraphLinkIds.IsLinked("100"));
        Assert.Null(GraphLinkIds.LinkInstanceOf("100"));
        Assert.Null(GraphLinkIds.LinkInstanceOf("ws:3"));
        Assert.False(GraphLinkIds.TryParse("link:5", out _, out _));
        Assert.False(GraphLinkIds.TryParse("link:5:", out _, out _));
        Assert.False(GraphLinkIds.TryParse("link:x:1", out _, out _));
    }

    [Fact]
    public void OverlappingElementIds_FromHostAndTwoLinks_AreSeparateNodes()
    {
        using var db = BuildSample();
        Assert.Equal("Host socket", db.GetNode("100")!.Name);
        Assert.Equal("Door 900", db.GetNode("link:5:100")!.Name);
        Assert.Equal("Smoke detector", db.GetNode("link:55:100")!.Name);
        Assert.Equal(9, db.Counts().Nodes);
    }

    [Fact]
    public void Find_LinkFilter_HostLinksInstanceAndName()
    {
        using var db = BuildSample();

        Assert.Equal(["100", "40", "5", "55"], Ids(db.Find(null, null, null, null, null, 0, 100, link: "host")));
        Assert.Equal(5, db.Find(null, null, null, null, null, 0, 100, link: "links").TotalAvailable);
        // Instance 5 must not include instance 55 (prefix collision).
        Assert.Equal(["link:5:100", "link:5:200", "link:5:300"], Ids(db.Find(null, null, null, null, null, 0, 100, link: "5")));
        Assert.Equal(["link:55:100", "link:55:200"], Ids(db.Find(null, null, null, null, null, 0, 100, link: "EL.rvt")));
        Assert.Equal(0, db.Find(null, null, null, null, null, 0, 100, link: "no such link").TotalAvailable);
    }

    [Fact]
    public void Find_LinkFilter_CombinesWithOtherFilters()
    {
        using var db = BuildSample();
        Assert.Equal(["link:5:300"], Ids(db.Find("space", null, null, null, null, 0, 100, link: "links")));
        Assert.Equal(["40"], Ids(db.Find("space", null, null, null, null, 0, 100, link: "host")));
        Assert.Equal(["link:55:100"], Ids(db.Find("element", "Fire Alarm Devices", null, null, null, 0, 100, link: "55")));
    }

    [Fact]
    public void Subtree_InLink_ListsALinksContent()
    {
        using var db = BuildSample();
        var subtree = db.Subtree("55", "in_link", 1, "in", 100);
        Assert.Equal(["link:55:100", "link:55:200"], subtree.Nodes.Select(n => n.Node.Id).OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void LinkedElement_CanBeLocatedInAHostSpace()
    {
        using var db = BuildSample();
        var contents = db.Neighbors("40", "located_in", "in", 100).Select(n => n.Node.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(["100", "link:55:100"], contents);
    }

    [Fact]
    public void Neighbors_LinkFilter_SeparatesHostAndLinkedContents()
    {
        using var db = BuildSample();
        List<string> In(string? link) => db.Neighbors("40", "located_in", "in", 100, link).Select(n => n.Node.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(["100", "link:55:100"], In(null));
        Assert.Equal(["100"], In("host"));
        Assert.Equal(["link:55:100"], In("links"));
        Assert.Equal(["link:55:100"], In("55"));
        Assert.Empty(In("5"));
    }

    [Fact]
    public void LinkChange_FallsBackToFullRebuild()
    {
        Assert.NotNull(GraphIncrementalPlanner.UnsafeReason("link", added: false, deleted: false));
        Assert.NotNull(GraphIncrementalPlanner.UnsafeReason("link", added: true, deleted: false));
        Assert.NotNull(GraphIncrementalPlanner.UnsafeReason("link", added: false, deleted: true));
    }
}
