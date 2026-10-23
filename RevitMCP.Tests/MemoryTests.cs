using System.IO;
using RevitMCP.Addin.Memory;
using RevitMCP.Addin.Memory.Hosting;
using Xunit;

namespace RevitMCP.Tests;

public class MemoryEntryParserTests
{
    private static MemoryEntry Sample() => new()
    {
        Id = "8b2f5c1e-3c1a-4f6e-9d2b-0a7e1c4d5f60",
        Key = "project/1626/tags/detector-tag-type",
        Scope = MemoryScopes.Project,
        Project = "1626",
        Stage = "PP",
        Model = "1626_PP_EN",
        ModelVersion = "central:412:abc",
        Source = MemorySources.Agent,
        Author = "raul.kalev",
        Authors = { "raul.kalev" },
        Client = "Claude Code",
        Created = new DateTimeOffset(2026, 10, 23, 9, 12, 0, TimeSpan.Zero),
        Updated = new DateTimeOffset(2026, 10, 23, 9, 12, 0, TimeSpan.Zero),
        State = MemoryStates.Candidate,
        Tags = { "tags", "fire-alarm" },
        Counters = { Shown = 3, Used = 1, Remembered = 2 },
        Value = "Detector tags use \"ATS_Tag_Andur_Nr\" (type id 712345), not the default tag.",
        Reason = "The client's drawing standard shows the detector number only: the legend carries the zone. # not a comment"
    };

    [Fact]
    public void Serialize_ThenParse_RoundTripsEveryField()
    {
        var text = MemoryEntryParser.Serialize(Sample());
        var parsed = MemoryEntryParser.Parse(text, out var error);

        Assert.Null(error);
        Assert.NotNull(parsed);
        var e = parsed!;
        var s = Sample();
        Assert.Equal(s.Id, e.Id);
        Assert.Equal(s.Key, e.Key);
        Assert.Equal(s.Project, e.Project);
        Assert.Equal(s.Stage, e.Stage);
        Assert.Equal(s.Model, e.Model);
        Assert.Equal(s.ModelVersion, e.ModelVersion);
        Assert.Equal(s.Author, e.Author);
        Assert.Equal(s.Authors, e.Authors);
        Assert.Equal(s.Client, e.Client);
        Assert.Equal(s.Created, e.Created);
        Assert.Equal(s.State, e.State);
        Assert.Null(e.Supersedes);
        Assert.Equal(s.Tags, e.Tags);
        Assert.Equal(3, e.Counters.Shown);
        Assert.Equal(1, e.Counters.Used);
        Assert.Equal(2, e.Counters.Remembered);
        Assert.Equal(s.Value, e.Value);
        Assert.Equal(s.Reason, e.Reason);
    }

    [Fact]
    public void Parse_ReadsTheDesignDocExample_WithCommentsAndContinuationLines()
    {
        const string text = """
            ---
            id: 8b2f5c1e-3c1a-4f6e-9d2b-0a7e1c4d5f60
            key: project/1626/tags/detector-tag-type        # tier/project/topic/setting — merge key
            scope: project
            project: "1626"
            stage: PP                                       # EP | PP | TP | TJ | null
            model: 1626_PP_EN
            modelVersion: "LatestCentralVersion=412"
            source: agent                                   # observed | agent | human
            author: raul.kalev
            client: Claude Code
            created: 2026-10-23T09:12:00Z
            updated: 2026-10-23T09:12:00Z
            state: candidate                                # candidate | confirmed | dormant | retired
            supersedes: null
            tags: [tags, fire-alarm]
            counters: { shown: 0, used: 0, worked: 0, contradicted: 0 }
            ---
            value: Detector tags use "ATS_Tag_Andur_Nr" (type id 712345), not the default tag.
            reason: The client's drawing standard shows the detector number only; the default tag adds the
                    zone, which the sheet legend already carries. Confirmed by the user on 2026-10-23.
            """;

        var e = MemoryEntryParser.Parse(text, out var error);

        Assert.Null(error);
        Assert.Equal("project/1626/tags/detector-tag-type", e!.Key);
        Assert.Equal("PP", e.Stage);
        Assert.Equal("Claude Code", e.Client);
        Assert.Equal(new[] { "tags", "fire-alarm" }, e.Tags);
        Assert.Equal(MemoryStates.Candidate, e.State);
        Assert.Equal("The client's drawing standard shows the detector number only; the default tag adds the zone, which the sheet legend already carries. Confirmed by the user on 2026-10-23.", e.Reason);
    }

    [Theory]
    [InlineData("no front matter", "front matter")]
    [InlineData("---\nid: x\nkey: k\n", "not closed")]
    [InlineData("---\nkey: k\n---\nvalue: v\n", "no id")]
    [InlineData("---\nid: x\nkey: k\n---\nreason: r\n", "no value")]
    public void Parse_RejectsBrokenFiles(string text, string expected)
    {
        Assert.Null(MemoryEntryParser.Parse(text, out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Serialize_KeepsValuesOnOneLine()
    {
        var e = Sample();
        e.Value = "line one\r\n   line two";
        var parsed = MemoryEntryParser.Parse(MemoryEntryParser.Serialize(e), out _);
        Assert.Equal("line one line two", parsed!.Value);
    }
}

public class MemoryPolicyTests
{
    [Theory]
    [InlineData("tags/Detector Tag Type", "tags/detector-tag-type")]
    [InlineData("Kõrgused/Pistikud klassis", "korgused/pistikud-klassis")]
    [InlineData("project/1626/tags/x", "tags/x")]
    [InlineData("personal/tags/x", "tags/x")]
    [InlineData("\\heights\\\\sockets\\", "heights/sockets")]
    public void NormalizeTopic_MakesAStableKey(string key, string expected) =>
        Assert.Equal(expected, MemoryPolicy.NormalizeTopic(key, "1626"));

    [Fact]
    public void NormalizeTopic_EmptyIsNull() => Assert.Null(MemoryPolicy.NormalizeTopic(" / ", null));

    [Fact]
    public void FullKey_AddsTierAndProject()
    {
        Assert.Equal("project/1626/tags/x", MemoryPolicy.FullKey(MemoryScopes.Project, "1626", "tags/x"));
        Assert.Equal("personal/1626/tags/x", MemoryPolicy.FullKey(MemoryScopes.Personal, "1626", "tags/x"));
        Assert.Equal("personal/tags/x", MemoryPolicy.FullKey(MemoryScopes.Personal, null, "tags/x"));
        Assert.Equal("project/_unfiled/tags/x", MemoryPolicy.FullKey(MemoryScopes.Project, "", "tags/x"));
    }

    [Fact]
    public void Validate_AcceptsADecisionWithAReason()
    {
        var problems = MemoryPolicy.Validate(
            "Detector tags use \"ATS_Tag_Andur_Nr\" (type id 712345), not the default tag.",
            "The client's drawing standard shows the detector number only.");
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("", "why", "value is required")]
    [InlineData("Use tag A for detectors", "", "reason is required")]
    [InlineData("712345, 712346, 712347, 712348", "ids of the detectors", "readable from the model")]
    [InlineData("Detectors 123456 223456 323456 423456 are on level 2", "found them", "readable from the model")]
    [InlineData("42", "count", "readable from the model")]
    [InlineData("Ask mari.maasikas@example.com about the legend", "she owns it", "contact data")]
    [InlineData("Call +372 5555 1234 for the key", "site manager", "contact data")]
    [InlineData("The share password: hunter2 works", "admin said", "Credentials")]
    [InlineData("Use key QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVphYmNkZWZnaGlqa2xtbm9w here", "api", "Credentials")]
    public void Validate_Refuses(string value, string reason, string expected)
    {
        var problems = MemoryPolicy.Validate(value, reason);
        Assert.Contains(problems, p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_AllowsLongFamilyNames() =>
        Assert.Empty(MemoryPolicy.Validate("Use EN_SIDE_Pistik_Andmeside_Topelt_Seinale_RJ45_Valge for teacher desks", "Client standard"));

    [Fact]
    public void Validate_CapsLength()
    {
        var problems = MemoryPolicy.Validate(new string('a', 30) + " " + new string('b', 400), "ok reason", 400);
        Assert.Contains(problems, p => p.Contains("limit is 400"));
    }

    private static MemoryEntry E(string value, string author, string state = MemoryStates.Confirmed, int minutesAgo = 0) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Key = "project/1/k",
        Value = value,
        Author = author,
        Authors = { author },
        State = state,
        Updated = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo)
    };

    [Fact]
    public void Decide_NewReaffirmAndSupersede()
    {
        Assert.Equal(MemoryWriteKind.New, MemoryPolicy.Decide(new List<MemoryEntry>(), "A", "u1").Kind);

        var active = E("Use tag A", "u1");
        var same = MemoryPolicy.Decide(new[] { active }, "  use TAG a ", "u2");
        Assert.Equal(MemoryWriteKind.Reaffirm, same.Kind);
        Assert.Same(active, same.Existing);

        var other = MemoryPolicy.Decide(new[] { active }, "Use tag B", "u2");
        Assert.Equal(MemoryWriteKind.Supersede, other.Kind);
        Assert.Same(active, other.Existing);
        Assert.False(other.FlipFlop);
    }

    [Fact]
    public void Decide_FlagsAFlipFlopAcrossUsers_ButNotOneUserChangingTheirMind()
    {
        var history = new[] { E("Use tag A", "u1", MemoryStates.Retired, 60), E("Use tag B", "u2", MemoryStates.Candidate, 30) };
        Assert.True(MemoryPolicy.Decide(history, "Use tag A", "u1").FlipFlop);

        var mine = new[] { E("Use tag A", "u1", MemoryStates.Retired, 60), E("Use tag B", "u1", MemoryStates.Candidate, 30) };
        Assert.False(MemoryPolicy.Decide(mine, "Use tag A", "u1").FlipFlop);
    }

    [Fact]
    public void Cap_RetiresTheLeastUsefulDormantEntry_OrReportsFull()
    {
        var now = DateTimeOffset.UtcNow;
        var keep = E("keep", "u", MemoryStates.Dormant);
        keep.Counters.Shown = 4; keep.Counters.Used = 4;
        var drop = E("drop", "u", MemoryStates.Dormant);
        drop.Counters.Shown = 10;
        var active = new List<MemoryEntry> { keep, drop, E("c", "u") };

        Assert.Null(MemoryPolicy.EntryToRetireForCap(active, 5, now, out var notFull));
        Assert.False(notFull);
        Assert.Same(drop, MemoryPolicy.EntryToRetireForCap(active, 3, now, out _));

        var noDormant = new List<MemoryEntry> { E("a", "u"), E("b", "u") };
        Assert.Null(MemoryPolicy.EntryToRetireForCap(noDormant, 2, now, out var full));
        Assert.True(full);
    }
}

public sealed class MemoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "memory-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static MemoryEntry Entry(string value) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Key = "project/1/topic",
        Value = value,
        Reason = "because",
        Author = "u1",
        Created = DateTimeOffset.UtcNow,
        Updated = DateTimeOffset.UtcNow
    };

    [Fact]
    public void Write_IsAtomic_AndReadAllSkipsTempAndBrokenFiles()
    {
        var folder = Path.Combine(_root, "tier");
        var store = new MemoryStore(folder);
        var e = Entry("first value here");
        store.Write(e);
        e.Value = "second value here";
        store.Write(e);
        File.WriteAllText(Path.Combine(folder, ".x.md.123.tmp"), "half");
        File.WriteAllText(Path.Combine(folder, "broken.md"), "not an entry");

        var errors = new List<string>();
        var all = store.ReadAll(folder, null, errors);

        var only = Assert.Single(all);
        Assert.Equal("second value here", only.Entry.Value);
        Assert.Single(errors);
        Assert.Contains("broken.md", errors[0]);
        // Only the planted temp file: the store's own temp files were renamed into place.
        Assert.Equal(".x.md.123.tmp", Path.GetFileName(Assert.Single(Directory.GetFiles(folder, "*.tmp"))));
    }

    [Fact]
    public void Tallies_ArePerUser_AndSummed()
    {
        var store = new MemoryStore(Path.Combine(_root, "tier"));
        store.Write(Entry("value one two"));
        Assert.True(store.AddShown("u1", new[] { "a", "b" }));
        Assert.True(store.AddShown("u1", new[] { "a" }));
        Assert.True(store.AddShown("u2", new[] { "a" }));

        var totals = store.ReadTallies();
        Assert.Equal(3, totals["a"].Shown);
        Assert.Equal(1, totals["b"].Shown);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "tier", MemoryStore.CountersFolderName), "*.json").Length);
    }

    [Fact]
    public void UnreachableShare_WritesToTheMirror_AndReplaysWhenItReturns()
    {
        var projectRoot = Path.Combine(_root, "share", "P1626");
        var folder = Path.Combine(projectRoot, ".rktools", "memory");
        var mirror = Path.Combine(_root, "mirror", "1626");
        var store = new MemoryStore(folder, mirror, projectRoot);

        // Share reachable: written there and mirrored.
        Directory.CreateDirectory(projectRoot);
        var online = Entry("written online here");
        store.Write(online);
        var read = new MemoryCache(store, watch: false).Read();
        Assert.False(read.FromMirror);
        Assert.True(File.Exists(Path.Combine(mirror, MemoryStore.FileName(online.Id))));

        // Share gone: reads and writes the mirror.
        Directory.Delete(Path.Combine(_root, "share"), true);
        var offline = Entry("written offline here");
        var location = store.Write(offline);
        Assert.True(location.Pending);
        Assert.Equal(1, store.PendingCount());
        var cache = new MemoryCache(store, watch: false);
        var away = cache.Read();
        Assert.True(away.FromMirror);
        Assert.Equal(2, away.Entries.Count);

        // Share back: the pending entry is copied on the next read.
        Directory.CreateDirectory(projectRoot);
        var back = cache.Read();
        Assert.False(back.FromMirror);
        Assert.Contains(back.Entries, x => x.Id == offline.Id);
        Assert.Equal(0, store.PendingCount());
    }

    [Fact]
    public void Cache_SeesAFileWrittenByAnotherMachine_WithoutAWatcher()
    {
        var folder = Path.Combine(_root, "tier");
        var cache = new MemoryCache(new MemoryStore(folder), watch: false);
        new MemoryStore(folder).Write(Entry("first entry text"));
        Assert.Single(cache.Read().Entries);

        // Another process writes; nothing tells this cache except the folder's last-write time.
        Thread.Sleep(20);
        new MemoryStore(folder).Write(Entry("second entry text"));
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(2, cache.Read().Entries.Count);
    }
}

public class MemoryRecallTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static MemoryEntry E(string key, string value, string scope = MemoryScopes.Project, string? project = "1626",
        string state = MemoryStates.Confirmed, int daysAgo = 0, string author = "u1", params string[] tags) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Key = key,
        Value = value,
        Reason = "because",
        Scope = scope,
        Project = project,
        State = state,
        Author = author,
        Updated = Now.AddDays(-daysAgo),
        Model = "1626_PP_EN",
        ModelVersion = "central:10",
        Tags = tags.ToList()
    };

    private static MemoryRecallContext Ctx(string version = "central:10") =>
        new() { Project = "1626", Model = "1626_PP_EN", ModelVersion = version, User = "u2", Now = Now };

    [Fact]
    public void Rank_MatchesKeyTagsAndText_AndDropsUnrelated()
    {
        var entries = new[]
        {
            E("project/1626/tags/detector-tag-type", "Detector tags use ATS_Tag_Andur_Nr"),
            E("project/1626/heights/sockets", "Sockets at 300 mm in classrooms", tags: "heights"),
            E("project/1626/sheets/naming", "Sheet names follow the client's list")
        };

        var hits = MemoryRecall.Rank(entries, new MemoryRecallOptions { Query = "detector tag" }, Ctx());

        var hit = Assert.Single(hits);
        Assert.Equal("project/1626/tags/detector-tag-type", hit.Entry.Key);
    }

    [Fact]
    public void Rank_FoldsDiacritics()
    {
        var entries = new[] { E("project/1626/korgused/pistikud", "Pistikud 300 mm kõrgusel klassides") };
        Assert.Single(MemoryRecall.Rank(entries, new MemoryRecallOptions { Query = "korgusel" }, Ctx()));
    }

    [Fact]
    public void Rank_PrefersProjectOverPersonal_ConfirmedOverCandidate_RecentOverOld()
    {
        var project = E("project/1626/tags/a", "Detector tag A");
        var personal = E("personal/1626/tags/a", "Detector tag A", MemoryScopes.Personal);
        var candidate = E("project/1626/tags/b", "Detector tag B", state: MemoryStates.Candidate);
        var old = E("project/1626/tags/c", "Detector tag C", daysAgo: 400);

        var hits = MemoryRecall.Rank(new[] { old, candidate, personal, project }, new MemoryRecallOptions { Query = "detector", Limit = 10 }, Ctx());

        Assert.Equal(project.Id, hits[0].Entry.Id);
        Assert.True(hits.FindIndex(h => h.Entry.Id == personal.Id) < hits.FindIndex(h => h.Entry.Id == candidate.Id));
        Assert.Equal(old.Id, hits[^1].Entry.Id);
    }

    [Fact]
    public void Rank_FiltersRetiredOtherProjectsAndTags()
    {
        var entries = new[]
        {
            E("project/1626/a", "Detector one", state: MemoryStates.Retired),
            E("project/9999/a", "Detector two", project: "9999"),
            E("personal/a", "Detector three", MemoryScopes.Personal, project: null),
            E("project/1626/b", "Detector four", tags: "fire-alarm")
        };

        var hits = MemoryRecall.Rank(entries, new MemoryRecallOptions { Query = "detector", Limit = 10 }, Ctx());
        Assert.Equal(new[] { "Detector four", "Detector three" }.OrderBy(x => x), hits.Select(h => h.Entry.Value).OrderBy(x => x));

        var tagged = MemoryRecall.Rank(entries, new MemoryRecallOptions { Tags = { "Fire Alarm" }, Limit = 10 }, Ctx());
        Assert.Equal("Detector four", Assert.Single(tagged).Entry.Value);

        var withRetired = MemoryRecall.Rank(entries, new MemoryRecallOptions { Query = "detector", IncludeRetired = true, Limit = 10 }, Ctx());
        Assert.Equal(3, withRetired.Count);
    }

    [Fact]
    public void Staleness_OnlyForTheSameModelAtAnotherVersion()
    {
        var e = E("project/1626/a", "Detector");
        Assert.False(MemoryRecall.Staleness(e, Ctx()).Stale);
        var (stale, reason) = MemoryRecall.Staleness(e, Ctx("central:11"));
        Assert.True(stale);
        Assert.Equal("written against an earlier model", reason);

        var otherModel = Ctx("central:11");
        otherModel.Model = "1626_PP_EL";
        Assert.False(MemoryRecall.Staleness(e, otherModel).Stale);
    }
}

public sealed class MemoryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "memory-service-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySettings _settings = new();
    private readonly MemoryService _service;
    private readonly string _projectRoot;

    public MemoryServiceTests()
    {
        _projectRoot = Path.Combine(_root, "projects", "P1626");
        Directory.CreateDirectory(_projectRoot);
        _service = new MemoryService(Path.Combine(_root, "personal"), Path.Combine(_root, "mirror"), () => _settings, watch: false);
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private MemoryContext Ctx(string user = "u1", string version = "central:10") => new()
    {
        Project = "1626",
        ProjectRoot = _projectRoot,
        ProjectFolder = _service.ProjectFolderFor(_projectRoot),
        Model = "1626_PP_EN",
        ModelVersion = version,
        User = user,
        Client = "Claude Code",
        Now = DateTimeOffset.UtcNow
    };

    private static MemoryRememberRequest Note(string value, string key = "tags/detector-tag-type", string scope = MemoryScopes.Project) => new()
    {
        Key = key,
        Value = value,
        Reason = "The client's drawing standard shows the detector number only.",
        Scope = scope,
        Tags = { "tags", "Fire Alarm" },
        Stage = "pp"
    };

    [Fact]
    public void Remember_WritesAProjectEntryNextToTheModel()
    {
        var result = _service.Remember(Ctx(), Note("Detector tags use ATS_Tag_Andur_Nr, not the default tag."));

        Assert.True(result.Success, result.Message);
        Assert.Equal(MemoryWriteKind.New, result.Kind);
        var e = result.Entry!;
        Assert.Equal("project/1626/tags/detector-tag-type", e.Key);
        Assert.Equal("PP", e.Stage);
        Assert.Equal(new[] { "tags", "fire-alarm" }, e.Tags);
        Assert.Equal(MemoryStates.Candidate, e.State);
        Assert.Equal("central:10", e.ModelVersion);
        Assert.StartsWith(Path.Combine(_projectRoot, ".rktools", "memory"), result.Path);
        Assert.True(File.Exists(result.Path));
    }

    [Fact]
    public void Remember_SameValueReaffirms_NewValueSupersedes_AndHistoryStays()
    {
        var first = _service.Remember(Ctx("u1"), Note("Detector tags use tag A always."));
        var again = _service.Remember(Ctx("u2"), Note("detector tags use TAG A always."));
        Assert.Equal(MemoryWriteKind.Reaffirm, again.Kind);
        Assert.Equal(first.Entry!.Id, again.Entry!.Id);
        Assert.Equal(1, again.Entry.Counters.Remembered);
        Assert.Equal(new[] { "u1", "u2" }, again.Entry.Authors);

        var changed = _service.Remember(Ctx("u2"), Note("Detector tags use tag B always."));
        Assert.Equal(MemoryWriteKind.Supersede, changed.Kind);
        Assert.Equal(first.Entry.Id, changed.Entry!.Supersedes);
        Assert.Equal(MemoryStates.Retired, changed.Superseded!.State);

        var all = _service.List(Ctx(), "project", "all", null, 0, 10);
        Assert.Equal(2, all.Total);
        var active = _service.List(Ctx(), "project", null, null, 0, 10);
        Assert.Equal("Detector tags use tag B always.", Assert.Single(active.Hits).Entry.Value);
    }

    [Fact]
    public void Remember_FlipFlopBetweenPeople_IsFlagged()
    {
        _service.Remember(Ctx("u1"), Note("Detector tags use tag A always."));
        _service.Remember(Ctx("u2"), Note("Detector tags use tag B always."));
        var back = _service.Remember(Ctx("u1"), Note("Detector tags use tag A always."));

        Assert.Contains(MemoryPolicy.FlagFlipFlop, back.Entry!.Flags);
        Assert.Equal(MemoryStates.Candidate, back.Entry.State);
    }

    [Fact]
    public void Remember_RefusesCompanyScopeFactsAndMissingProjectFolder()
    {
        Assert.Equal("validation_failed", _service.Remember(Ctx(), Note("Use tag A for detectors.", scope: MemoryScopes.Company)).Status);
        Assert.Equal("validation_failed", _service.Remember(Ctx(), Note("712345 712346 712347 712348")).Status);
        Assert.Equal("validation_failed", _service.Remember(Ctx(), new MemoryRememberRequest { Key = "k", Value = "Use tag A for detectors.", Reason = "" }).Status);

        var cloud = Ctx();
        cloud.ProjectFolder = null;
        cloud.ProjectProblem = "Cloud models have no project folder.";
        var refused = _service.Remember(cloud, Note("Use tag A for detectors."));
        Assert.Equal("not_available", refused.Status);
        Assert.Equal("Cloud models have no project folder.", refused.Message);

        // Personal memory still works for a cloud model.
        Assert.True(_service.Remember(cloud, Note("Use tag A for detectors.", scope: MemoryScopes.Personal)).Success);
    }

    [Fact]
    public void Remember_RefusesWhenTheCapIsReachedAndNothingIsDormant()
    {
        _settings.MaxEntriesPerProject = 2;
        Assert.True(_service.Remember(Ctx(), Note("Use tag A for detectors.", "a")).Success);
        Assert.True(_service.Remember(Ctx(), Note("Use tag B for sounders.", "b")).Success);
        Assert.Equal("memory_full", _service.Remember(Ctx(), Note("Use tag C for buttons.", "c")).Status);
        // Superseding an existing key does not grow the project, so it is still allowed.
        Assert.True(_service.Remember(Ctx(), Note("Use tag D for detectors.", "a")).Success);
    }

    [Fact]
    public void Recall_SpansTiers_CountsShownPerUser_AndMarksStaleEntries()
    {
        _service.Remember(Ctx(), Note("Detector tags use tag A always."));
        _service.Remember(Ctx(), Note("I prefer detector tags placed to the right.", "tags/detector-side", MemoryScopes.Personal));

        var read = _service.Recall(Ctx(version: "central:11"), new MemoryRecallOptions { Query = "detector tags", Limit = 5 });

        Assert.Equal(2, read.Hits.Count);
        Assert.Equal(MemoryScopes.Project, read.Hits[0].Entry.Scope);
        Assert.All(read.Hits, h => Assert.True(h.Stale));
        Assert.Equal(2, read.Tiers.Count);

        var again = _service.Recall(Ctx(), new MemoryRecallOptions { Query = "detector tags" });
        Assert.All(again.Hits, h => Assert.Equal(1, h.Entry.Counters.Shown));
        Assert.All(again.Hits, h => Assert.False(h.Stale));

        // Tallies never leak into entry files: reaffirming rewrites the file with its own counters only.
        var reaffirmed = _service.Remember(Ctx(), Note("Detector tags use tag A always."));
        var onDisk = MemoryEntryParser.Parse(File.ReadAllText(reaffirmed.Path!), out _)!;
        Assert.Equal(0, onDisk.Counters.Shown);
    }

    [Fact]
    public void Personal_NotesAreBoundToTheProject_UnlessAnyProject()
    {
        _service.Remember(Ctx(), Note("Detector tags to the right in 1626.", "tags/side", MemoryScopes.Personal));
        var anywhere = Note("Detector tags always in Estonian.", "tags/language", MemoryScopes.Personal);
        anywhere.AnyProject = true;
        _service.Remember(Ctx(), anywhere);

        var other = Ctx();
        other.Project = "9999";
        other.ProjectFolder = null;
        var hits = _service.Recall(other, new MemoryRecallOptions { Query = "detector", Limit = 5 }, MemoryScopes.Personal).Hits;
        Assert.Equal("Detector tags always in Estonian.", Assert.Single(hits).Entry.Value);
        Assert.Equal("personal/tags/language", hits[0].Entry.Key);
    }

    [Fact]
    public void Disabled_RefusesWritesAndReadsNothing()
    {
        _settings.Enabled = false;
        Assert.Equal("not_available", _service.Remember(Ctx(), Note("Use tag A for detectors.")).Status);
        var read = _service.Recall(Ctx(), new MemoryRecallOptions { Query = "tag" });
        Assert.Empty(read.Hits);
        Assert.Contains(read.Warnings, w => w.Contains("memory.enabled=false"));
    }
}

public sealed class MemoryProjectRootAndSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "memory-root-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Find_UsesTheNearestRktoolsAboveTheModel_ElseTheModelFolder()
    {
        var project = Path.Combine(_root, "P1626");
        var models = Path.Combine(project, "EN", "Models");
        Directory.CreateDirectory(models);
        var model = Path.Combine(models, "1626_PP_EN.rvt");

        Assert.Equal(models, MemoryProjectRoot.Find(model, null).Root);

        Directory.CreateDirectory(Path.Combine(project, ".rktools"));
        Assert.Equal(project, MemoryProjectRoot.Find(model, null).Root);
        Assert.Equal(models, MemoryProjectRoot.Find(model, models).Root);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Autodesk Docs://Project/1626_PP_EN.rvt")]
    [InlineData("RSN://server/1626_PP_EN.rvt")]
    public void Find_GivesAReasonWhenThereIsNoFolder(string path)
    {
        var (root, problem) = MemoryProjectRoot.Find(path, null);
        Assert.Null(root);
        Assert.Contains("scope=personal", problem);
    }

    [Fact]
    public void Settings_UserOverridesCompany_AndRejectsUnsafeFolders()
    {
        var user = System.Text.Json.Nodes.JsonNode.Parse("""{ "memory": { "valueMaxLength": 250, "projectFolderName": "..\\elsewhere" } }""")!.AsObject();
        var company = System.Text.Json.Nodes.JsonNode.Parse("""{ "memory": { "enabled": false, "valueMaxLength": 300, "maxEntriesPerProject": 5 } }""")!.AsObject();

        var s = MemorySettings.From(user, company);

        Assert.False(s.Enabled);
        Assert.Equal(250, s.ValueMaxLength);
        Assert.Equal(10, s.MaxEntriesPerProject); // clamped to the minimum
        Assert.Equal(MemorySettings.DefaultProjectFolderName, s.ProjectFolderName);
    }
}
