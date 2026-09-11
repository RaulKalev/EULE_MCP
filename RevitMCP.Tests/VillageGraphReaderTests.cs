using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Graph;
using RevitMCP.Addin.Village;
using RevitMCP.Village;
using Xunit;

namespace RevitMCP.Tests;

public class VillageGraphReaderTests : IDisposable
{
    private readonly string _root = NewDir("village_root");
    private readonly string _cache = NewDir("village_cache");
    private DateTimeOffset _now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static string NewDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rkmcp_{label}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _cache })
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static GraphNode N(string id, string kind, string name, string category, string level = "", string? extra = null) =>
        new() { Id = id, Kind = kind, Name = name, Category = category, Level = level, Extra = extra };

    /// <summary>
    /// Synthetic fire-alarm-heavy model: 3 sheets, 4 views (1 schedule), 2 types, 8 elements,
    /// 1 panel, 2 circuits (1 orphan), 1 level, a few tags.
    /// </summary>
    private static string WriteSampleGraph(string path, string modelName, string builtAt = "2026-09-10T10:00:00Z", int fireDevices = 6)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var nodes = new List<GraphNode>
        {
            N("1", "level", "1. korrus", "Levels"),
            N("60", "type", "EN_ATS-Häiresireen: Standard", "Fire Alarm Devices"),
            N("61", "type", "Basic Wall: Generic", "Walls"),
            N("10", "panel", "ATS-KP", "Fire Alarm Devices", "1. korrus", null),
            N("20", "circuit", "ATS-KP/1", "Electrical Circuits"),
            N("21", "circuit", "?/2 orphan", "Electrical Circuits"),
            N("70", "view", "Level 1", "Views", "1. korrus", "{\"viewType\":\"FloorPlan\",\"isSchedule\":false}"),
            N("71", "view", "Level 2", "Views", "", "{\"viewType\":\"FloorPlan\",\"isSchedule\":false}"),
            N("72", "view", "Device schedule", "Views", "", "{\"viewType\":\"Schedule\",\"isSchedule\":true}"),
            N("73", "view", "3D", "Views", "", "{\"viewType\":\"ThreeD\"}"),
            N("80", "sheet", "EN-5-01 - Plan", "Sheets"),
            N("81", "sheet", "EN-5-02 - Plan", "Sheets"),
            N("82", "sheet", "EN-3-01 - Cover", "Sheets")
        };
        var edges = new List<GraphEdge>
        {
            new("20", "10", "fed_by"),
            new("70", "80", "on_sheet"),
            new("71", "81", "on_sheet")
        };
        for (var i = 0; i < fireDevices; i++)
        {
            var id = (100 + i).ToString();
            nodes.Add(N(id, "element", "Sireen " + i, "Fire Alarm Devices", "1. korrus", "{\"family\":\"EN_ATS-Häiresireen\",\"type\":\"Standard\"}"));
            edges.Add(new GraphEdge(id, "60", "type_of"));
            edges.Add(new GraphEdge(id, "20", "fed_by"));
            edges.Add(new GraphEdge(id, "70", "tagged_in"));
        }
        nodes.Add(N("200", "element", "Wall A", "Walls", "1. korrus"));
        nodes.Add(N("201", "element", "Wall B", "Walls", "1. korrus"));
        edges.Add(new GraphEdge("200", "61", "type_of"));
        edges.Add(new GraphEdge("201", "61", "type_of"));

        var meta = new Dictionary<string, string>
        {
            [GraphSchema.MetaKeys.ModelName] = modelName,
            [GraphSchema.MetaKeys.ModelPath] = @"\\server\bim\" + modelName + ".rvt",
            [GraphSchema.MetaKeys.BuiltAt] = builtAt,
            [GraphSchema.MetaKeys.BuiltBy] = "some.user",
            [GraphSchema.MetaKeys.CentralVersion] = "central:42:abc",
            [GraphSchema.MetaKeys.ElementCount] = (fireDevices + 2).ToString(),
            [GraphSchema.MetaKeys.SchemaVersion] = GraphSchema.SchemaVersion.ToString(),
            [GraphSchema.MetaKeys.IsWorkshared] = "true"
        };

        using (var db = GraphDatabase.CreateNew(path))
        {
            var counts = db.WriteGraph(nodes, edges, meta);
            Assert.True(counts.Nodes > 0);
        }
        return path;
    }

    private VillageGraphReader Reader() => new(_cache, clock: () => _now);

    [Fact]
    public void Read_FindsGraphByModelNameUnderProjectFolder_AndSummarises()
    {
        var path = WriteSampleGraph(Path.Combine(_root, "1626", "1626_PP_EN.graph.db"), "1626_PP_EN");
        var reader = Reader();
        var result = reader.Read(new VillageGraphSource { Roots = { _root }, ModelName = "1626_PP_EN", RootSource = "user config" });

        Assert.True(result.Changed);
        Assert.Equal(path, result.DatabasePath);
        var s = result.Snapshot;
        Assert.True(s.Exists);
        Assert.Null(s.Error);
        Assert.Equal("1626_PP_EN.graph.db", s.FileName);
        Assert.Equal("user config", s.RootSource);
        Assert.Equal("1626_PP_EN", s.ModelName);
        Assert.Equal("2026-09-10T10:00:00Z", s.BuiltAt);
        Assert.True(s.IsWorkshared);
        Assert.Equal(1, s.SchemaVersion);

        Assert.Equal(3, s.Counts.Sheets);
        Assert.Equal(4, s.Counts.Views);
        Assert.Equal(1, s.Counts.Schedules);
        Assert.Equal(2, s.Counts.Types);
        Assert.Equal(8, s.Counts.Elements);
        Assert.Equal(1, s.Counts.Levels);
        Assert.Equal(1, s.Counts.Panels);
        Assert.Equal(2, s.Counts.Circuits);
        Assert.Equal(6, s.Counts.Tags);
        Assert.True(s.Counts.Nodes >= 20);
        Assert.Equal(1, s.Health.CircuitsWithoutPanel);
        Assert.Equal(1, s.Health.CircuitsWithoutElements);
        Assert.Equal(8, s.Health.ElementsWithoutLocation);
        Assert.Contains(s.Categories, c => c.Name == "Fire Alarm Devices" && c.Count == 6);
        Assert.Contains(s.Levels, l => l.Name == "1. korrus" && l.Count == 8);
        Assert.Equal("unknown", s.Freshness.Status);
        Assert.Equal(7200, s.Freshness.AgeSeconds);

        Assert.NotNull(result.Theme);
        // Six category-classified fire devices are below the 20-element minimum: neutral, but the evidence is reported.
        Assert.Equal("neutral", result.Theme!.Theme);
        var fire = result.Theme.Scores.Single(x => x.System == "fire_alarm");
        Assert.Equal(6, fire.CategoryScore);
        Assert.Equal(6, fire.NameScore);          // the type name matched "ATS" with 6 instances
        Assert.Equal(1, fire.MatchedTypes);
    }

    [Fact]
    public void Read_NeverOpensTheSharedFileDirectly_AndReusesTheCacheCopy()
    {
        var path = WriteSampleGraph(Path.Combine(_root, "P", "Model.graph.db"), "Model");
        var reader = Reader();
        var source = new VillageGraphSource { DatabasePath = path, ModelName = "Model" };

        var first = reader.Read(source);
        Assert.True(first.Snapshot.UsedCache);
        var cached = Directory.GetFiles(reader.CacheRoot, "Model.graph.db", SearchOption.AllDirectories);
        Assert.Single(cached);
        Assert.NotEqual(Path.GetFullPath(path), Path.GetFullPath(cached[0]));

        // The shared file can be replaced while the village holds nothing open.
        File.Delete(path);
        Assert.False(File.Exists(path));

        var second = reader.Read(new VillageGraphSource { DatabasePath = path, Roots = { _root }, ModelName = "Model" });
        Assert.False(second.Snapshot.Exists);
        Assert.Equal("missing", second.Snapshot.Freshness.Status);
        Assert.True(second.Changed);
    }

    [Fact]
    public void Read_ReturnsCachedResultWhileTheFileIsUnchanged_AndRefreshesWhenReplaced()
    {
        var path = WriteSampleGraph(Path.Combine(_root, "P", "Model.graph.db"), "Model", fireDevices: 6);
        var reader = Reader();
        var source = new VillageGraphSource { DatabasePath = path, ModelName = "Model" };

        var first = reader.Read(source);
        Assert.True(first.Changed);
        var again = reader.Read(source);
        Assert.False(again.Changed);
        Assert.Same(first, again);

        // Atomic replacement, the way GraphStore.Publish does it.
        var temp = Path.Combine(_root, "new.graph.db");
        WriteSampleGraph(temp, "Model", builtAt: "2026-09-10T11:30:00Z", fireDevices: 30);
        File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddMinutes(5));
        File.Replace(temp, path, null, ignoreMetadataErrors: true);

        var third = reader.Read(source);
        Assert.True(third.Changed);
        Assert.Equal(30, third.Snapshot.Counts.Tags);
        Assert.Equal("2026-09-10T11:30:00Z", third.Snapshot.BuiltAt);
        Assert.Equal("fire_alarm", third.Theme!.Theme);   // 30 fire devices now dominate
    }

    [Fact]
    public void Read_MissingGraph_IsLimitedModeNotAnError()
    {
        var reader = Reader();
        var source = new VillageGraphSource { Roots = { _root, @"C:\definitely\missing\folder" }, ModelName = "Nothing" };
        var result = reader.Read(source);
        Assert.False(result.Snapshot.Exists);
        Assert.Null(result.Snapshot.Error);
        Assert.Equal("missing", result.Snapshot.Freshness.Status);
        Assert.Contains("limited mode", result.Snapshot.Freshness.Reason);
        Assert.Null(result.Theme);
        Assert.True(result.Changed);                 // first answer is news for the viewer
        Assert.False(reader.Read(source).Changed);   // still missing: nothing new
    }

    [Fact]
    public void Read_CorruptFile_ReportsErrorWithoutThrowing()
    {
        var path = Path.Combine(_root, "P", "Broken.graph.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "this is not a sqlite database, just text that is long enough to look like one maybe");

        var result = Reader().Read(new VillageGraphSource { DatabasePath = path, ModelName = "Broken" });
        Assert.True(result.Snapshot.Exists);
        Assert.NotNull(result.Snapshot.Error);
        Assert.Equal("unknown", result.Snapshot.Freshness.Status);
        Assert.Contains("could not be read", result.Snapshot.Freshness.Reason);
        Assert.Null(result.Theme);
    }

    [Fact]
    public void Locate_PrefersExplicitPathThenNewestMatchUnderRoots_AndSkipsTmpAndCache()
    {
        var older = WriteSampleGraph(Path.Combine(_root, "A", "Model.graph.db"), "Model");
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = WriteSampleGraph(Path.Combine(_root, "B", "Model.graph.db"), "Model");
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteSampleGraph(Path.Combine(_root, "tmp", "Model.graph.db"), "Model");
        WriteSampleGraph(Path.Combine(_root, "cache", "Model.graph.db"), "Model");

        Assert.Equal(Path.GetFullPath(newer), VillageGraphReader.Locate(new VillageGraphSource { Roots = { _root }, ModelName = "Model" }));
        Assert.Equal(Path.GetFullPath(older), VillageGraphReader.Locate(new VillageGraphSource { DatabasePath = older, Roots = { _root }, ModelName = "Model" }));
        Assert.Null(VillageGraphReader.Locate(new VillageGraphSource { Roots = { _root }, ModelName = "Other" }));
        Assert.Null(VillageGraphReader.Locate(new VillageGraphSource { Roots = { "" }, ModelName = "Model" }));
        Assert.Null(VillageGraphReader.Locate(new VillageGraphSource { DatabasePath = @"C:\nope\x.graph.db", ModelName = "Model" }));
        // Names are sanitised the same way the graph builder sanitises them.
        Assert.Equal(Path.GetFullPath(newer), VillageGraphReader.Locate(new VillageGraphSource { Roots = { _root }, ModelName = " Model " }));
    }

    [Fact]
    public void Freshness_CombinesGraphMetadataWithConnectorHints()
    {
        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var built = "2026-09-10T10:00:00Z";

        Assert.Equal("missing", VillageGraphFreshness.Evaluate(false, null, null, now).Status);
        Assert.Equal("unknown", VillageGraphFreshness.Evaluate(true, built, null, now).Status);
        Assert.Equal(7200, VillageGraphFreshness.Evaluate(true, built, null, now).AgeSeconds);

        var fresh = new VillageFreshnessHint { Stale = false, Reason = "matches", ReportedAt = now.AddMinutes(-10), Source = "revit_graph_status" };
        var f = VillageGraphFreshness.Evaluate(true, built, fresh, now);
        Assert.Equal("fresh", f.Status);
        Assert.Equal("revit_graph_status", f.Source);
        Assert.Equal("2026-09-10T11:50:00.000Z", f.ReportedAt);

        var stale = new VillageFreshnessHint { Stale = true, Reason = "Element count changed", ReportedAt = now };
        Assert.Equal("stale", VillageGraphFreshness.Evaluate(true, built, stale, now).Status);
        Assert.Equal("Element count changed", VillageGraphFreshness.Evaluate(true, built, stale, now).Reason);

        var written = new VillageFreshnessHint { Stale = false, ReportedAt = now.AddMinutes(-10), LastWriteAt = now.AddMinutes(-5) };
        Assert.Equal("possibly_stale", VillageGraphFreshness.Evaluate(true, built, written, now).Status);

        var writtenBefore = new VillageFreshnessHint { Stale = false, ReportedAt = now.AddMinutes(-5), LastWriteAt = now.AddMinutes(-10) };
        Assert.Equal("fresh", VillageGraphFreshness.Evaluate(true, built, writtenBefore, now).Status);

        var noReportButWrite = new VillageFreshnessHint { LastWriteAt = now.AddMinutes(-30) };
        Assert.Equal("possibly_stale", VillageGraphFreshness.Evaluate(true, built, noReportButWrite, now).Status);

        var unparsable = VillageGraphFreshness.Evaluate(true, "yesterday-ish", null, now);
        Assert.Null(unparsable.AgeSeconds);
        Assert.Equal("unknown", unparsable.Status);
    }

    [Fact]
    public void Read_AppliesHintToFreshnessEvenWhenFileIsUnchanged()
    {
        var path = WriteSampleGraph(Path.Combine(_root, "P", "Model.graph.db"), "Model");
        var reader = Reader();
        var first = reader.Read(new VillageGraphSource { DatabasePath = path, ModelName = "Model" });
        Assert.Equal("unknown", first.Snapshot.Freshness.Status);

        var second = reader.Read(new VillageGraphSource
        {
            DatabasePath = path, ModelName = "Model",
            Hint = new VillageFreshnessHint { Stale = true, Reason = "Model version changed", ReportedAt = _now }
        });
        Assert.False(second.Changed);
        Assert.Equal("stale", second.Snapshot.Freshness.Status);
    }

    [Fact]
    public void Snapshot_SerializesWithoutPathsUserNamesOrIds()
    {
        var path = WriteSampleGraph(Path.Combine(_root, "1626", "1626_PP_EN.graph.db"), "1626_PP_EN");
        var result = Reader().Read(new VillageGraphSource { DatabasePath = path, ModelName = "1626_PP_EN", RootSource = "local fallback" });
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(result.Snapshot);
        var obj = (JObject)VillageEventSerializer.ParseToken(json);

        Assert.DoesNotContain("server", json);
        Assert.DoesNotContain("some.user", json);
        Assert.DoesNotContain(_root.Replace("\\", "\\\\"), json);
        Assert.DoesNotContain("Sireen 0", json);
        Assert.DoesNotContain("\"100\"", json);
        Assert.Equal("1626_PP_EN.graph.db", obj.Value<string>("file_name"));
        Assert.Equal(3, obj["counts"]!.Value<int>("sheets"));
        Assert.Equal(1, obj["health"]!.Value<int>("circuits_without_panel"));
        Assert.Equal(2, obj["health"]!.Value<int>("issues"));
        Assert.Equal("unknown", obj["freshness"]!.Value<string>("status"));
        Assert.Contains("routing and visualization metadata", obj.Value<string>("note"));
    }

    [Fact]
    public void LayoutSizer_ScalesLandmarksFromCountsAndFlagsWarnings()
    {
        var graph = new VillageGraphSnapshot
        {
            Exists = true,
            Counts = new VillageGraphCounts { Nodes = 60_000, Sheets = 30, Views = 300, Schedules = 0, Types = 5, Tags = 2500, Elements = 30_000, Panels = 5, Circuits = 40 },
            Health = new VillageGraphHealth { CircuitsWithoutPanel = 1 },
            Freshness = new VillageGraphFreshness { Status = "fresh" }
        };
        var sized = VillageLayoutSizer.Apply(VillageLayout.Default, graph).ToDictionary(b => b.Id);

        Assert.Equal(4, sized["town_hall"].Size);
        Assert.Equal(3, sized["archive"].Size);
        Assert.Equal(4, sized["lookout"].Size);
        Assert.Equal(1, sized["market"].Size);
        Assert.Equal(2, sized["workshop"].Size);
        Assert.Equal(4, sized["sign_workshop"].Size);
        Assert.Equal(4, sized["houses"].Size);
        Assert.Equal(3, sized["utility_district"].Size);
        Assert.Equal(1, sized["survey_post"].Size);
        Assert.Equal(2, sized["warning_area"].Size);

        var limited = VillageLayoutSizer.Apply(VillageLayout.Default, null).ToDictionary(b => b.Id);
        Assert.All(limited.Values, b => Assert.Equal(b.BaseSize, b.Size));
        Assert.Equal(3, VillageLayout.Default.First(b => b.Id == "town_hall").BaseSize); // shared default untouched

        var staleOnly = VillageLayoutSizer.Apply(VillageLayout.Default, new VillageGraphSnapshot { Exists = true, Freshness = new VillageGraphFreshness { Status = "stale" } });
        Assert.Equal(2, staleOnly.First(b => b.Id == "warning_area").Size);
        Assert.Equal(1, VillageLayoutSizer.Scale(0, 10, 100));
        Assert.Equal(2, VillageLayoutSizer.Scale(9, 10, 100));
        Assert.Equal(3, VillageLayoutSizer.Scale(10, 10, 100));
        Assert.Equal(4, VillageLayoutSizer.Scale(100, 10, 100));
    }
}
