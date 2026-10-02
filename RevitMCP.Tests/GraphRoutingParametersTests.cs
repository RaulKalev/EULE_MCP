using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RevitMCP.Addin.Graph;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Parameter-aware graph search (#63): allowlist parsing, storage, find filters and incremental behaviour.</summary>
public class GraphRoutingParametersTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "graph-params-" + Guid.NewGuid().ToString("N"));

    public GraphRoutingParametersTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ─── Configuration ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_NamesAndObjects()
    {
        var (specs, warnings) = GraphRoutingParameters.Parse(JsonNode.Parse("""
            ["Loop number", { "name": "Seadme Nr.", "categories": ["Fire Alarm Devices"] },
             { "guid": "0d8b5b3a-3c3b-4c41-9a35-4c8c9a1b2c3d" }, "loop NUMBER", ""]
            """));
        Assert.Empty(warnings);
        Assert.Equal(["Loop number", "Seadme Nr.", "0d8b5b3a-3c3b-4c41-9a35-4c8c9a1b2c3d"], specs.Select(s => s.Name));
        Assert.True(specs[1].AppliesTo("fire alarm devices"));
        Assert.False(specs[1].AppliesTo("Lighting Fixtures"));
        Assert.True(specs[0].AppliesTo("anything"));
        Assert.NotNull(specs[2].Guid);
    }

    [Fact]
    public void Parse_IsBounded_AndRefusesSecrets()
    {
        var names = new JsonArray(Enumerable.Range(1, 25).Select(i => (JsonNode?)JsonValue.Create($"P{i}")).ToArray());
        names.Insert(0, "Wifi password");
        var (specs, warnings) = GraphRoutingParameters.Parse(names);
        Assert.Equal(GraphRoutingParameters.MaxParameters, specs.Count);
        Assert.DoesNotContain(specs, s => s.Name.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, w => w.Contains("secret"));
        Assert.Contains(warnings, w => w.Contains("first 20"));
    }

    [Fact]
    public void Parse_RejectsNonArrays_AndBadGuids()
    {
        Assert.Single(GraphRoutingParameters.Parse(JsonNode.Parse("\"Loop number\"")).Warnings);
        var (specs, warnings) = GraphRoutingParameters.Parse(JsonNode.Parse("""[{ "name": "X", "guid": "nope" }]"""));
        Assert.Single(specs);
        Assert.Null(specs[0].Guid);
        Assert.Single(warnings);
        Assert.Empty(GraphRoutingParameters.Parse(null).Specs);
    }

    [Fact]
    public void Signature_ChangesWithTheAllowlist()
    {
        var a = GraphRoutingParameters.Parse(JsonNode.Parse("""["Loop", "Panel"]""")).Specs;
        var b = GraphRoutingParameters.Parse(JsonNode.Parse("""["Loop", {"name": "Panel", "categories": ["Doors"]}]""")).Specs;
        Assert.NotEqual(GraphRoutingParameters.Signature(a), GraphRoutingParameters.Signature(b));
        Assert.Equal("", GraphRoutingParameters.Signature([]));
    }

    [Fact]
    public void CleanAndNormalize_TrimCollapseTruncateLowercase()
    {
        Assert.Null(GraphRoutingParameters.Clean("   "));
        Assert.Equal("A 1 B", GraphRoutingParameters.Clean("  A \n 1\t B "));
        Assert.Equal("äriruum 3", GraphRoutingParameters.Normalize("ÄRIRUUM  3"));
        Assert.Equal(GraphRoutingParameters.MaxValueLength, GraphRoutingParameters.Clean(new string('x', 500))!.Length);
    }

    // ─── Storage and find ──────────────────────────────────────────────────

    private string Seed()
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + GraphSchema.FileExtension);
        using var db = GraphDatabase.CreateNew(path);
        db.WriteGraph(
        [
            Node("1", "Detector A", new() { ["Loop number"] = "L1", ["Seadme Nr."] = "ATS-01" }),
            Node("2", "Detector B", new() { ["Loop number"] = "l1 " }),
            Node("3", "Detector C", new() { ["Loop number"] = "L2", ["Seadme Nr."] = "ATS-03" }),
            Node("4", "Detector D", null),
            Node("link:9:1", "Linked detector", new() { ["Loop number"] = "L1" })
        ], [], new Dictionary<string, string> { [GraphSchema.MetaKeys.BuiltAt] = "t0" });
        return path;
    }

    private static GraphNode Node(string id, string name, Dictionary<string, string>? p) =>
        new() { Id = id, Kind = "element", Name = name, Category = "Fire Alarm Devices", Params = p };

    private static List<string> Find(GraphDatabase db, string? param = null, string? value = null, string? contains = null, string? link = null) =>
        db.Find(null, null, null, null, null, 0, 100, link, param, value, contains).Items.Select(n => n.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();

    [Fact]
    public void Find_ByParameterValue_IsCaseInsensitiveAndTrimmed()
    {
        using var db = GraphDatabase.OpenReadOnly(Seed());
        Assert.Equal(["1", "2", "link:9:1"], Find(db, "loop number", "L1"));
        Assert.Equal(["1", "2", "link:9:1"], Find(db, "Loop number", " l1"));
        Assert.Equal(["3"], Find(db, "Loop number", "L2"));
        Assert.Equal(["1", "2"], Find(db, "Loop number", "L1", link: "host"));
    }

    [Fact]
    public void Find_ByParameterPresenceContainsAndAnyParameter()
    {
        using var db = GraphDatabase.OpenReadOnly(Seed());
        Assert.Equal(["1", "3"], Find(db, "Seadme Nr."));
        Assert.Equal(["3"], Find(db, "Seadme Nr.", contains: "-03"));
        Assert.Equal(["1", "3"], Find(db, contains: "ats"));          // any parameter
        Assert.Empty(Find(db, "Missing parameter"));
        Assert.Empty(Find(db, "Loop number", "L9"));
    }

    [Fact]
    public void ParamsOf_AndStats()
    {
        using var db = GraphDatabase.OpenReadOnly(Seed());
        var values = db.ParamsOf(["1", "4", "404"]);
        Assert.Single(values);
        Assert.Equal("ATS-01", values["1"]["seadme nr."]);
        Assert.Equal("L1", values["1"]["Loop number"]);

        var stats = db.RoutingParameterStats();
        Assert.Equal(("Loop number", 4L, 2L), stats.Single(s => s.Name == "Loop number"));
        Assert.Equal(("Seadme Nr.", 2L, 2L), stats.Single(s => s.Name == "Seadme Nr."));
    }

    [Fact]
    public void ApplyDelta_ReplacesParamsOfRefreshedAndDeletedNodes()
    {
        var path = Seed();
        using (var db = GraphDatabase.OpenReadWrite(path))
            db.ApplyDelta(["1"], ["3"], [Node("1", "Detector A", new() { ["Loop number"] = "L5" })], [], new Dictionary<string, string>());

        using var read = GraphDatabase.OpenReadOnly(path);
        Assert.Equal(["1"], Find(read, "Loop number", "L5"));
        Assert.Empty(Find(read, "Seadme Nr.", "ATS-01"));   // dropped: the refreshed node no longer has it
        Assert.Empty(Find(read, "Seadme Nr.", "ATS-03"));   // node 3 deleted
        Assert.Equal(["2", "link:9:1"], Find(read, "Loop number", "L1"));
    }

    [Fact]
    public void GraphWithoutParamTable_StillWorks_AndMatchesNothing()
    {
        var path = Path.Combine(_dir, "old" + GraphSchema.FileExtension);
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE nodes (id TEXT PRIMARY KEY, kind TEXT NOT NULL, name TEXT, category TEXT, level TEXT, workset TEXT, extra TEXT);" +
                              "CREATE TABLE edges (src TEXT, dst TEXT, rel TEXT, PRIMARY KEY (src, dst, rel));" +
                              "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);" +
                              "INSERT INTO nodes (id, kind, name) VALUES ('1', 'element', 'Old');";
            cmd.ExecuteNonQuery();
        }

        using var db = GraphDatabase.OpenReadOnly(path);
        Assert.False(db.HasRoutingParameters());
        Assert.Empty(db.RoutingParameterStats());
        Assert.Empty(db.ParamsOf(["1"]));
        Assert.Equal(["1"], Find(db));
        Assert.Empty(Find(db, "Loop number", "L1"));
    }

    [Fact]
    public void ChangedAllowlist_ForcesAFullRebuild()
    {
        var d = GraphIncrementalPlanner.Decide(new IncrementalInputs
        {
            GraphExists = true, HasEdgeOwners = true, BaselineMatches = true, RoutingParametersChanged = true
        });
        Assert.False(d.Incremental);
        Assert.Contains("routing-parameter", d.Reason);
    }
}
