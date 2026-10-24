using System.IO;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Memory;
using RevitMCP.Addin.Memory.Hosting;
using Xunit;

namespace RevitMCP.Tests;

public sealed class MemoryReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "memory-review-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySettings _settings = new();
    private readonly string _projectRoot;
    private readonly MemoryService _service;

    public MemoryReviewTests()
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

    private MemoryContext Ctx(string user = "u1") => new()
    {
        Project = "1626",
        ProjectRoot = _projectRoot,
        ProjectFolder = _service.ProjectFolderFor(_projectRoot),
        Model = "1626_PP_EN",
        ModelVersion = "central:10",
        User = user,
        Client = "Claude Code",
        Now = DateTimeOffset.UtcNow
    };

    private MemoryEntry Remember(string key, string value, string user = "u1", string scope = MemoryScopes.Project) =>
        _service.Remember(Ctx(user), new MemoryRememberRequest { Key = key, Value = value, Reason = "The client asked for it in the kickoff.", Scope = scope }).Entry!;

    private MemoryEntry Get(string id) => _service.List(Ctx(), "all", "all", null, 0, 100).Hits.Single(h => h.Entry.Id == id).Entry;

    private string ProjectFolder => _service.ProjectFolderFor(_projectRoot);

    [Fact]
    public void Confirm_SettlesAFlipFlop_RetireKeepsHistory()
    {
        Remember("tags/detector", "Detector tags use tag A always.", "u1");
        Remember("tags/detector", "Detector tags use tag B always.", "u2");
        var back = Remember("tags/detector", "Detector tags use tag A always.", "u1");
        Assert.Contains(MemoryPolicy.FlagFlipFlop, back.Flags);

        var confirmed = _service.Review(Ctx(), MemoryReviewActions.Confirm, new[] { back.Id }, null);
        Assert.True(confirmed.Success, confirmed.Message);
        var e = Get(back.Id);
        Assert.Equal(MemoryStates.Confirmed, e.State);
        Assert.DoesNotContain(MemoryPolicy.FlagFlipFlop, e.Flags);

        var forgotten = _service.Forget(Ctx(), new[] { back.Id });
        Assert.True(forgotten.Success);
        Assert.Equal(MemoryStates.Retired, Get(back.Id).State);
        Assert.True(File.Exists(Path.Combine(ProjectFolder, MemoryStore.FileName(back.Id))));
    }

    [Fact]
    public void Review_RefusesUnknownIdsAndCompanyEntries()
    {
        Assert.Equal("not_found", _service.Review(Ctx(), MemoryReviewActions.Retire, new[] { "nope" }, null).Status);
        Assert.Equal("validation_failed", _service.Review(Ctx(), "delete", new[] { "x" }, null).Status);

        var company = Path.Combine(_root, "company");
        Directory.CreateDirectory(company);
        var entry = new MemoryEntry { Id = Guid.NewGuid().ToString(), Key = "company/tags/x", Scope = MemoryScopes.Company, Value = "Company tags use style A.", Reason = "Standard.", Updated = DateTimeOffset.UtcNow };
        File.WriteAllText(Path.Combine(company, MemoryStore.FileName(entry.Id)), MemoryEntryParser.Serialize(entry));
        _settings.CompanyFolder = company;
        var refused = _service.Review(Ctx(), MemoryReviewActions.Retire, new[] { entry.Id }, null);
        Assert.Equal("not_found", refused.Status);
        Assert.Contains("administrator", refused.Message);
    }

    [Fact]
    public void Promote_WritesACompanyProposal_AndFlagsTheEntry()
    {
        var e = Remember("heights/sockets", "Sockets in classrooms at 300 mm above the floor.");
        var result = _service.Review(Ctx(), MemoryReviewActions.Promote, new[] { e.Id }, null);

        Assert.True(result.Success, result.Message);
        var proposal = JObject.Parse(File.ReadAllText(result.Produced!));
        Assert.Equal("company/heights/sockets", (string?)proposal["key"]);
        Assert.Contains(MemoryService.FlagPromoted, Get(e.Id).Flags);
    }

    [Fact]
    public void AcceptingAMerge_WritesOneConfirmedEntry_AndRetiresTheOthers()
    {
        var a = Remember("tags/detector-a", "Detector tags use the ATS number tag.");
        var b = Remember("tags/detector-b", "Use the ATS number tag for detectors.");
        var store = new MemoryProposalStore(ProjectFolder);
        var proposal = new MemoryProposal
        {
            Id = "p1",
            Kind = MemoryProposalKinds.Merge,
            EntryIds = { a.Id, b.Id },
            Key = "tags/detector-tag-type",
            Value = "Detector tags use the ATS number tag (ATS_Tag_Andur_Nr).",
            Reason = "Two notes said the same; the client's legend carries the zone.",
            Note = "Duplicates",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "lead"
        };
        store.Save(proposal);

        var result = _service.Review(Ctx(), MemoryReviewActions.Accept, Array.Empty<string>(), "p1");

        Assert.True(result.Success, result.Message);
        var merged = Get(result.Produced!);
        Assert.Equal("project/1626/tags/detector-tag-type", merged.Key);
        Assert.Equal(MemoryStates.Confirmed, merged.State);
        Assert.Equal(MemoryStates.Retired, Get(a.Id).State);
        Assert.Equal(MemoryStates.Retired, Get(b.Id).State);
        Assert.Equal(MemoryProposalStatus.Accepted, store.Get("p1")!.Status);
        Assert.Equal("already_decided", _service.Review(Ctx(), MemoryReviewActions.Accept, Array.Empty<string>(), "p1").Status);
    }

    [Fact]
    public void AcceptingARewrite_SupersedesTheEntry_RejectingChangesNothing()
    {
        var e = Remember("defaults/place-tags/tagtypeid", "In this project revit_place_tags is called with tagTypeId = 712345.");
        var store = new MemoryProposalStore(ProjectFolder);
        store.Save(new MemoryProposal { Id = "p2", Kind = MemoryProposalKinds.Rewrite, EntryIds = { e.Id }, Value = "Detector tags here use tag type 712345 (ATS_Tag_Andur_Nr).", Reason = "Observed in most tag placements.", CreatedAt = DateTimeOffset.UtcNow });
        store.Save(new MemoryProposal { Id = "p3", Kind = MemoryProposalKinds.Retire, EntryIds = { e.Id }, CreatedAt = DateTimeOffset.UtcNow });

        Assert.True(_service.Review(Ctx(), MemoryReviewActions.Reject, Array.Empty<string>(), "p3").Success);
        Assert.Equal(MemoryProposalStatus.Rejected, store.Get("p3")!.Status);
        Assert.NotEqual(MemoryStates.Retired, Get(e.Id).State);

        var rewritten = _service.Review(Ctx(), MemoryReviewActions.Accept, Array.Empty<string>(), "p2");
        Assert.True(rewritten.Success, rewritten.Message);
        Assert.Equal(MemoryStates.Retired, Get(e.Id).State);
        Assert.Equal(e.Id, Get(rewritten.Produced!).Supersedes);
    }

    [Fact]
    public void ReviewData_BuildsTheDigest_AndThePageShowsIt()
    {
        var e = Remember("tags/detector", "Detector tags use tag A always.");
        new MemoryProposalStore(ProjectFolder).Save(new MemoryProposal { Id = "p4", Kind = MemoryProposalKinds.Retire, EntryIds = { e.Id }, Note = "Looks obsolete <b>", CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "lead" });

        var data = _service.ReviewData(Ctx());
        Assert.Single(data.Waiting);
        Assert.Single(data.Proposals);

        var html = MemoryPageHtml.Build(data);
        Assert.Contains("data-memory-proposal=\"p4\"", html);
        Assert.Contains($"data-memory-id=\"{e.Id}\"", html);
        Assert.Contains("data-revit-tools=\"rows\"", html);
        Assert.Contains("Looks obsolete &lt;b&gt;", html);
        Assert.DoesNotContain("Looks obsolete <b>", html);
        Assert.Equal("memory-1626", MemoryPageHtml.PageId("1626"));
        Assert.Equal("memory-unfiled", MemoryPageHtml.PageId(null));
    }
}

public sealed class MemoryConsolidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "memory-consolidate-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySettings _settings = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Project(string name, params (string Topic, string Value, string Source)[] notes)
    {
        var root = Path.Combine(_root, name);
        var folder = Path.Combine(root, _settings.ProjectFolderName);
        var store = new MemoryStore(folder);
        foreach (var (topic, value, source) in notes)
            store.Write(new MemoryEntry
            {
                Id = Guid.NewGuid().ToString(),
                Key = MemoryPolicy.FullKey(MemoryScopes.Project, name, topic),
                Scope = MemoryScopes.Project,
                Project = name,
                Value = value,
                Reason = "Because.",
                Source = source,
                Author = "u1",
                Authors = { "u1" },
                Created = DateTimeOffset.UtcNow,
                Updated = DateTimeOffset.UtcNow
            });
        return root;
    }

    [Fact]
    public void Read_FindsDuplicatesObservedAndCrossProjectEntries()
    {
        var p1 = Project("1626",
            ("tags/a", "Detector tags use the ATS number tag in this project.", MemorySources.Agent),
            ("tags/b", "In this project detector tags use the ATS number tag.", MemorySources.Agent),
            ("defaults/place-tags/tagtypeid", "In this project revit_place_tags is called with tagTypeId = 712345.", MemorySources.Observed),
            ("heights/sockets", "Sockets in classrooms at 300 mm above the floor.", MemorySources.Agent));
        var p2 = Project("1700", ("heights/sockets", "Sockets in classrooms at 300 mm above the floor.", MemorySources.Agent));

        var runner = new MemoryConsolidationRunner(_settings);
        var warnings = new List<string>();
        var projects = runner.Load(new[] { p1, p2, Path.Combine(_root, "missing") }, DateTimeOffset.UtcNow, warnings);
        Assert.Equal(2, projects.Count);
        Assert.Single(warnings);

        var report = runner.Read(projects, null, false, DateTimeOffset.UtcNow);
        var group = Assert.Single(report.Duplicates);
        Assert.Equal(2, group.Entries.Count);
        Assert.Single(report.Observed);
        var shared = Assert.Single(report.CrossProject);
        Assert.Equal("heights/sockets", shared.Topic);
        Assert.Equal(new[] { "1626", "1700" }, shared.Projects.OrderBy(x => x));
        Assert.Equal(5, report.Changed.Count);
    }

    [Fact]
    public void Propose_StoresValidProposals_RefusesBadOnes_DoneLimitsTheNextRead()
    {
        var root = Project("1626",
            ("tags/a", "Detector tags use the ATS number tag in this project.", MemorySources.Agent),
            ("tags/b", "In this project detector tags use the ATS number tag.", MemorySources.Agent));
        var runner = new MemoryConsolidationRunner(_settings);
        var now = DateTimeOffset.UtcNow;
        var projects = runner.Load(new[] { root }, now, new List<string>());
        var ids = projects[0].Entries.Select(e => e.Id).ToList();

        var outcomes = runner.Propose(projects, new[]
        {
            new MemoryProposal { Kind = "merge", EntryIds = ids, Value = "Detector tags use the ATS number tag.", Reason = "Two notes said the same.", Note = "dup" },
            new MemoryProposal { Kind = "merge", EntryIds = { ids[0] }, Value = "x y z", Reason = "r" },
            new MemoryProposal { Kind = "promote", EntryIds = { "not-there" } },
            new MemoryProposal { Kind = "explode", EntryIds = ids }
        }, "lead", "Claude Code", now);

        Assert.NotNull(outcomes[0].Id);
        Assert.All(outcomes.Skip(1), o => Assert.NotEmpty(o.Problems));
        var stored = new MemoryProposalStore(projects[0].Folder).List();
        Assert.Equal(MemoryProposalStatus.Pending, Assert.Single(stored).Status);
        Assert.Equal("lead", stored[0].CreatedBy);

        Assert.Empty(runner.Done(projects, now.AddSeconds(1), "lead"));
        var again = runner.Load(new[] { root }, now.AddMinutes(1), new List<string>());
        var report = runner.Read(again, null, false, now.AddMinutes(1));
        Assert.Empty(report.Changed);
        Assert.Empty(report.Duplicates);
        Assert.Equal(1, report.PendingProposals);
        Assert.Equal(2, runner.Read(again, null, true, now.AddMinutes(1)).Changed.Count);
    }

    [Fact]
    public void Settings_ReadConsolidationRoots()
    {
        var user = System.Text.Json.Nodes.JsonNode.Parse("""{ "memory": { "consolidation": { "projectRoots": ["P:\\1626", " ", "P:\\1700"] } } }""")!.AsObject();
        Assert.Equal(new[] { "P:\\1626", "P:\\1700" }, MemorySettings.From(user, null).ConsolidationRoots);
    }
}

public sealed class MemoryHardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "memory-hardening-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySettings _settings = new() { ObserverMinSupport = 2, ObserverMinSessions = 1 };

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private MemoryService New(string personal) =>
        new(Path.Combine(_root, personal), Path.Combine(_root, "mirror-" + personal), () => _settings, watch: false);

    private MemoryContext Ctx(MemoryService s, string root, string user = "u1") => new()
    {
        Project = "1626", ProjectRoot = root, ProjectFolder = s.ProjectFolderFor(root), User = user, Client = "Claude Code", Now = DateTimeOffset.UtcNow
    };

    private static ObservedCall Call(string modelPath, string user, string? status = null, bool success = true, string value = "712345") => new()
    {
        Tool = "revit_place_tags", Client = "Claude Code", User = user, Project = "1626", ModelPath = modelPath, IsWrite = true,
        Success = success, Status = status, Time = DateTimeOffset.UtcNow,
        Arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tagTypeId"] = value }
    };

    [Fact]
    public void Feedback_WaitsForTheApprovalOutcome()
    {
        var root = Path.Combine(_root, "P"); Directory.CreateDirectory(root);
        using var s = New("p1");
        s.Remember(Ctx(s, root), new MemoryRememberRequest { Key = "tags/x", Value = "Detector tags use type 712345 here.", Reason = "Client legend." });
        s.Recall(Ctx(s, root), new MemoryRecallOptions { Query = "detector tags" });

        Assert.Equal(0, s.Feedback(Call("m", "u1", status: "approval_required", success: false)));
        Assert.Equal(1, s.Feedback(Call("m", "u1", status: WorkflowObserver.RejectedStatus, success: false)));
        var e = s.List(Ctx(s, root), "project", null, null, 0, 5).Hits.Single().Entry;
        Assert.Equal(1, e.Counters.Contradicted);
    }

    [Theory]
    [InlineData("sockets at 1000 mm", "100", false)]
    [InlineData("sockets at 100 mm", "100", true)]
    [InlineData("automatic numbering", "auto", false)]
    [InlineData("use ats_tag_andur_nr for detectors", "ats_tag_andur_nr", true)]
    public void Feedback_MatchesWholeValues(string text, string value, bool expected) =>
        Assert.Equal(expected, MemoryService.NamesValue(text, value));

    [Fact]
    public void BasedOn_FromAPersonalNote_DoesNotCountAsWorked()
    {
        var root = Path.Combine(_root, "P"); Directory.CreateDirectory(root);
        using var s = New("p1");
        var e = s.Remember(Ctx(s, root), new MemoryRememberRequest { Key = "tags/x", Value = "Detector tags use tag A always.", Reason = "Client legend." }).Entry!;
        s.Remember(Ctx(s, root), new MemoryRememberRequest { Key = "mine", Value = "I place detector tags to the right.", Reason = "Habit.", Scope = MemoryScopes.Personal, BasedOn = { e.Id } });
        var after = s.List(Ctx(s, root), "project", null, null, 0, 5).Hits.Single().Entry;
        Assert.Equal(1, after.Counters.Used);
        Assert.Equal(0, after.Counters.Worked);
    }

    [Fact]
    public void Observer_NeverBringsBackAValueAPersonRetired()
    {
        var root = Path.Combine(_root, "P"); Directory.CreateDirectory(Path.Combine(root, ".rktools"));
        var model = Path.Combine(root, "m.rvt");
        foreach (var u in new[] { "u1", "u2" })
            using (var s = New("p-" + u)) { s.ProcessObserved(Call(model, u)); s.ProcessObserved(Call(model, u)); }

        using var review = New("p-u1");
        var promoted = review.List(Ctx(review, root), "project", null, null, 0, 5).Hits.Single().Entry;
        Assert.True(review.Forget(Ctx(review, root), new[] { promoted.Id }).Success);

        using (var s = New("p-u3")) { s.ProcessObserved(Call(model, "u3")); s.ProcessObserved(Call(model, "u3")); }

        Assert.Empty(review.List(Ctx(review, root), "project", null, null, 0, 5).Hits);
        Assert.Equal(MemoryStates.Retired, review.List(Ctx(review, root), "project", "all", null, 0, 5).Hits.Single().Entry.State);
    }

    [Fact]
    public void Mirror_IsPerProjectFolder_AndOfflineTalliesReachTheShare()
    {
        Assert.NotEqual(MemoryService.MirrorSegment("0001", Path.Combine(_root, "A")), MemoryService.MirrorSegment("0001", Path.Combine(_root, "B")));

        var root = Path.Combine(_root, "share", "P");
        var folder = Path.Combine(root, ".rktools", "memory");
        Directory.CreateDirectory(root);
        var store = new MemoryStore(folder, Path.Combine(_root, "mirror-x"), root);
        store.AddTallies("u2", new Dictionary<string, MemoryCounters> { ["a"] = new() { Worked = 2 } });
        store.ReadTallies(); // mirrors everyone's tallies while online

        // The share goes away (its files stay on the server) and comes back.
        Directory.Move(Path.Combine(_root, "share"), Path.Combine(_root, "share-away"));
        Assert.True(store.AddTallies("u1", new Dictionary<string, MemoryCounters> { ["a"] = new() { Used = 1 } }));
        var offline = store.ReadTallies()["a"];
        Assert.Equal(2, offline.Worked);
        Assert.Equal(1, offline.Used);

        Directory.Move(Path.Combine(_root, "share-away"), Path.Combine(_root, "share"));
        store.ReplayPending();
        var online = store.ReadTallies()["a"];
        Assert.Equal(1, online.Used);
        Assert.Equal(2, online.Worked);
    }

    [Fact]
    public void Ids_AreSafeFileNames_AndGeneratedPageIdsAvoidTheMemoryPrefix()
    {
        Assert.Null(MemoryEntryParser.Parse("---\nid: ..\\evil\nkey: k\n---\nvalue: v w\n", out var error));
        Assert.Contains("letters, digits and hyphens", error);
        Assert.StartsWith("page-memory-", RevitMCP.Addin.ModelPages.ModelPageStore.NewId("Memory audit"));
    }
}
