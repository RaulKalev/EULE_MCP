using System.IO;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Memory;
using RevitMCP.Addin.Memory.Hosting;
using Xunit;

namespace RevitMCP.Tests;

public class MemoryLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly MemorySettings Settings = new();

    private static MemoryEntry E(string state = MemoryStates.Candidate, int daysAgo = 0, string author = "u1") => new()
    {
        Id = Guid.NewGuid().ToString(),
        Key = "project/1/k",
        Value = "Use tag A for detectors",
        Author = author,
        Authors = { author },
        State = state,
        Updated = Now.AddDays(-daysAgo)
    };

    [Fact]
    public void Candidate_IsConfirmedByWorkingUses_OrBySecondAuthor()
    {
        var e = E();
        Assert.Equal(MemoryStates.Candidate, MemoryLifecycle.Effective(e, Now, Settings).State);

        e.Counters.Worked = 3;
        var (state, why) = MemoryLifecycle.Effective(e, Now, Settings);
        Assert.Equal(MemoryStates.Confirmed, state);
        Assert.Contains("used successfully 3 times", why);

        var shared = E();
        shared.Authors.Add("u2");
        Assert.Equal(MemoryStates.Confirmed, MemoryLifecycle.Effective(shared, Now, Settings).State);
    }

    [Fact]
    public void Unused_GoesDormant_ThenRetires_AndUseWakesItUp()
    {
        var e = E(MemoryStates.Confirmed, daysAgo: 100);
        Assert.Equal(MemoryStates.Dormant, MemoryLifecycle.Effective(e, Now, Settings).State);

        e.Counters.LastUsed = Now.AddDays(-1);
        Assert.Equal(MemoryStates.Confirmed, MemoryLifecycle.Effective(e, Now, Settings).State);

        var ancient = E(MemoryStates.Confirmed, daysAgo: 90 + 365 + 1);
        Assert.Equal(MemoryStates.Retired, MemoryLifecycle.Effective(ancient, Now, Settings).State);
    }

    [Fact]
    public void ShownInHints_KeepsANoteActive_ButContradictionsStillMakeItDormant()
    {
        // A prose note whose use cannot be detected from tool arguments, shown in hints last week.
        var shown = E(MemoryStates.Confirmed, daysAgo: 200);
        shown.Counters.Shown = 40;
        shown.Counters.LastShown = Now.AddDays(-7);
        Assert.Equal(MemoryStates.Confirmed, MemoryLifecycle.Effective(shown, Now, Settings).State);

        shown.Counters.LastShown = Now.AddDays(-100);
        var (state, why) = MemoryLifecycle.Effective(shown, Now, Settings);
        Assert.Equal(MemoryStates.Dormant, state);
        Assert.Contains("not used or shown", why);

        var contradicted = E(MemoryStates.Confirmed);
        contradicted.Counters.LastShown = Now;
        contradicted.Counters.Contradicted = 3;
        Assert.Equal(MemoryStates.Dormant, MemoryLifecycle.Effective(contradicted, Now, Settings).State);
    }

    [Fact]
    public void Usefulness_IsNeutralWithoutEvidence_HoweverOftenShown()
    {
        Assert.Equal(0.5, MemoryPolicy.Usefulness(new MemoryCounters()));
        Assert.Equal(0.5, MemoryPolicy.Usefulness(new MemoryCounters { Shown = 100 }));
        Assert.True(MemoryPolicy.Usefulness(new MemoryCounters { Shown = 4, Used = 4 }) > 0.5);
        Assert.True(MemoryPolicy.Usefulness(new MemoryCounters { Shown = 20, Contradicted = 1 }) < 0.5);
    }

    [Fact]
    public void Contradicted_GoesDormant_FlipFlopStaysCandidate_StoredRetiredStays()
    {
        var contradicted = E(MemoryStates.Confirmed);
        contradicted.Counters.Contradicted = 3;
        contradicted.Counters.Worked = 1;
        Assert.Equal(MemoryStates.Dormant, MemoryLifecycle.Effective(contradicted, Now, Settings).State);

        var disputed = E();
        disputed.Flags.Add(MemoryPolicy.FlagFlipFlop);
        disputed.Counters.Worked = 10;
        disputed.Authors.Add("u2");
        Assert.Equal(MemoryStates.Candidate, MemoryLifecycle.Effective(disputed, Now, Settings).State);

        var retired = E(MemoryStates.Retired);
        retired.Counters.Worked = 10;
        Assert.Equal(MemoryStates.Retired, MemoryLifecycle.Effective(retired, Now, Settings).State);
    }

    [Fact]
    public void Recipe_RetiresWhenItsToolIsGone()
    {
        var recipe = E(MemoryStates.Confirmed);
        recipe.Requires.AddRange(new[] { "revit_graph_route", "revit_place_tags_old" });
        var (state, why) = MemoryLifecycle.Effective(recipe, Now, Settings, t => t != "revit_place_tags_old");
        Assert.Equal(MemoryStates.Retired, state);
        Assert.Contains("revit_place_tags_old", why);
    }

    [Fact]
    public void Hints_ShowConfirmed_AndOnlyTheAuthorsOwnCandidates()
    {
        Assert.True(MemoryLifecycle.VisibleInHints(E(MemoryStates.Confirmed, author: "u1"), "u2"));
        Assert.True(MemoryLifecycle.VisibleInHints(E(author: "u1"), "U1"));
        Assert.False(MemoryLifecycle.VisibleInHints(E(author: "u1"), "u2"));
        Assert.False(MemoryLifecycle.VisibleInHints(E(MemoryStates.Dormant, author: "u1"), "u1"));
    }

    [Fact]
    public void Parser_RoundTripsObservedFields_AndOmitsThemOnPlainNotes()
    {
        var e = E();
        e.Value = "In this project revit_place_tags is called with tagTypeId = 712345.";
        e.Requires.AddRange(new[] { "revit_graph_route", "revit_place_tags" });
        e.Match = new MemoryMatch { Tool = "revit_place_tags", Argument = "tagTypeId", Value = "712345" };
        e.Connector = "1.0.0.0";

        var parsed = MemoryEntryParser.Parse(MemoryEntryParser.Serialize(e), out _)!;
        Assert.Equal(e.Requires, parsed.Requires);
        Assert.Equal("tagTypeId", parsed.Match!.Argument);
        Assert.Equal("712345", parsed.Match.Value);
        Assert.Equal("1.0.0.0", parsed.Connector);

        var plain = MemoryEntryParser.Serialize(E());
        Assert.DoesNotContain("requires:", plain);
        Assert.DoesNotContain("match:", plain);
    }
}

public class WorkflowObserverTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    private static ObservedCall Call(string tool, DateTimeOffset time, bool write = true, string? status = null, bool success = true,
        string client = "Claude Code", string user = "u1", params (string Key, string Value)[] args) => new()
    {
        Tool = tool,
        Client = client,
        User = user,
        Project = "1626",
        IsWrite = write,
        Success = success,
        Status = status,
        Time = time,
        Arguments = args.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase)
    };

    [Fact]
    public void ExtractArguments_KeepsAllowListedScalarsOnly()
    {
        var args = new Dictionary<string, object?>
        {
            ["tagTypeId"] = 712345L,
            ["offsetMm"] = JValue.CreateString("2700"),
            ["category"] = "Fire Alarm Devices",
            ["text"] = "free text never enters",
            ["familyName"] = "C:\\families\\x.rfa",
            ["typeName"] = new string('x', 80),
            ["viewName"] = new JArray("a", "b"),
            ["typeId"] = 0L
        };

        var kept = WorkflowObserver.ExtractArguments(args);

        Assert.Equal(new[] { "category", "offsetMm", "tagTypeId" }, kept.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("712345", kept["tagTypeId"]);
    }

    [Fact]
    public void Default_IsEmittedAfterSupportAcrossSessions_Once()
    {
        var observer = new WorkflowObserver();
        var state = new ObserverState();
        var arg = ("tagTypeId", "712345");

        Assert.Empty(observer.Observe(state, Call("revit_place_tags", Start, args: arg), 3, 2));
        Assert.Empty(observer.Observe(state, Call("revit_place_tags", Start.AddMinutes(5), args: arg), 3, 2));
        // Third sighting, but still one session: not yet.
        Assert.Empty(observer.Observe(state, Call("revit_place_tags", Start.AddMinutes(10), args: arg), 3, 2));
        // A later session.
        var emitted = observer.Observe(state, Call("revit_place_tags", Start.AddHours(3), args: arg), 3, 2);

        var p = Assert.Single(emitted);
        Assert.Equal(ObservedPattern.KindDefault, p.Kind);
        Assert.Equal("defaults/place_tags/tagtypeid", p.Topic);
        Assert.Equal("In this project revit_place_tags is called with tagTypeId = 712345.", p.Value);
        Assert.Equal("712345", p.Match!.Value);
        Assert.Empty(observer.Observe(state, Call("revit_place_tags", Start.AddHours(4), args: arg), 3, 2));
    }

    [Fact]
    public void Default_IgnoresReadsFailuresAndValuesThatAreNotDominant()
    {
        var observer = new WorkflowObserver();
        var state = new ObserverState();
        for (var i = 0; i < 9; i++)
        {
            var t = Start.AddHours(i);
            Assert.Empty(observer.Observe(state, Call("revit_get_elements_info", t, write: false, args: ("category", "Doors")), 3, 2));
            Assert.Empty(observer.Observe(state, Call("revit_place_tags", t, success: false, args: ("tagTypeId", "1111")), 3, 2));
            // Three values in turn: each is seen often enough, but none is 60 % of the calls with this argument.
            Assert.Empty(observer.Observe(state, Call("revit_set_elevation", t, args: ("elevationMm", (2400 + i % 3 * 100).ToString())), 3, 2));
        }
    }

    [Fact]
    public void Recipe_IsTheChainAfterARoute_SeenInTwoSessions()
    {
        var observer = new WorkflowObserver();
        var state = new ObserverState();
        List<ObservedPattern> last = new();
        for (var session = 0; session < 3; session++)
        {
            var t = Start.AddHours(session * 2);
            var route = Call("revit_graph_route", t, write: false);
            route.RouteKey = WorkflowObserver.RouteKeyOf("category", "Fire Alarm Devices", "tag detectors");
            observer.Observe(state, route, 3, 2);
            observer.Observe(state, Call("revit_graph_query", t.AddMinutes(1), write: false), 3, 2);
            observer.Observe(state, Call("revit_get_elements_info", t.AddMinutes(2), write: false), 3, 2);
            observer.Observe(state, Call("revit_place_tags", t.AddMinutes(3)), 3, 2);
            // The next route closes the chain.
            var next = Call("revit_graph_route", t.AddMinutes(4), write: false);
            last = observer.Observe(state, next, 3, 2);
        }

        var recipe = Assert.Single(last, p => p.Kind == ObservedPattern.KindRecipe);
        Assert.Equal("workflow/category/fire-alarm-devices", recipe.Topic);
        Assert.Contains("revit_graph_route → revit_graph_query → revit_get_elements_info → revit_place_tags", recipe.Value);
        Assert.Equal(new[] { "revit_graph_route", "revit_graph_query", "revit_get_elements_info", "revit_place_tags" }, recipe.Requires);
    }

    [Fact]
    public void Correction_IsARejectionFollowedByAnAcceptedValue()
    {
        var observer = new WorkflowObserver();
        var state = new ObserverState();
        Assert.Empty(observer.Observe(state, Call("revit_set_elevation", Start, status: WorkflowObserver.RejectedStatus, success: false, args: ("elevationMm", "2400")), 3, 2));

        var emitted = observer.Observe(state, Call("revit_set_elevation", Start.AddMinutes(2), args: ("elevationMm", "2700")), 3, 2);

        var p = Assert.Single(emitted);
        Assert.Equal(ObservedPattern.KindCorrection, p.Kind);
        Assert.Equal("revit_set_elevation: elevationMm = 2400 was rejected; 2700 was accepted.", p.Value);
        Assert.Equal("2700", p.Match!.Value);
    }

    [Fact]
    public void RouteKey_FallsBackToTheIntent()
    {
        Assert.Equal("category/doors", WorkflowObserver.RouteKeyOf("category", "Doors", "whatever"));
        Assert.Equal("room/devices-in-room-1.12", WorkflowObserver.RouteKeyOf("room", null, "devices in room 1.12 please now"));
        Assert.Null(WorkflowObserver.RouteKeyOf(null, null, "  "));
    }
}

public sealed class MemoryPhase2ServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "memory-p2-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySettings _settings = new() { ObserverMinSupport = 2, ObserverMinSessions = 1 };
    private readonly string _projectRoot;
    private readonly string _modelPath;
    private readonly HashSet<string> _tools = new(StringComparer.OrdinalIgnoreCase) { "revit_graph_route", "revit_place_tags", "revit_set_elevation" };
    private readonly MemoryService _service;

    public MemoryPhase2ServiceTests()
    {
        _projectRoot = Path.Combine(_root, "projects", "P1626");
        Directory.CreateDirectory(Path.Combine(_projectRoot, ".rktools"));
        Directory.CreateDirectory(Path.Combine(_projectRoot, "EN"));
        _modelPath = Path.Combine(_projectRoot, "EN", "1626_PP_EN.rvt");
        _service = New("personal");
    }

    private MemoryService New(string personal) =>
        new(Path.Combine(_root, personal), Path.Combine(_root, "mirror-" + personal), () => _settings, watch: false, toolExists: _tools.Contains);

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

    private ObservedCall Call(string tool, string user = "u1", bool success = true, string? status = null, params (string Key, string Value)[] args) => new()
    {
        Tool = tool,
        Client = "Claude Code",
        User = user,
        Project = "1626",
        ModelPath = _modelPath,
        IsWrite = true,
        Success = success,
        Status = status,
        Time = DateTimeOffset.UtcNow,
        Arguments = args.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase)
    };

    private static MemoryRememberRequest Note(string value, string key = "tags/detector-tag-type", string scope = MemoryScopes.Project) => new()
    {
        Key = key,
        Value = value,
        Reason = "The client's drawing standard shows the detector number only.",
        Scope = scope
    };

    [Fact]
    public void Company_WritesAProposal_AndIsReadFromTheConfiguredFolder()
    {
        var result = _service.Remember(Ctx(), Note("Detector tags always show the loop number.", scope: MemoryScopes.Company));

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(result.ProposalPath));
        var proposal = JObject.Parse(File.ReadAllText(result.ProposalPath!));
        Assert.Equal("company/tags/detector-tag-type", (string?)proposal["key"]);
        Assert.Null((string?)proposal["applyTo"]);

        // The administrator applies it: the entry text saved in the company folder.
        var company = Path.Combine(_root, "company");
        Directory.CreateDirectory(company);
        File.WriteAllText(Path.Combine(company, (string)proposal["fileName"]!), (string)proposal["entry"]!);
        _settings.CompanyFolder = company;

        var hits = _service.Recall(Ctx(), new MemoryRecallOptions { Query = "loop number" }).Hits;
        Assert.Equal(MemoryScopes.Company, Assert.Single(hits).Entry.Scope);
    }

    [Fact]
    public void Hints_AreOnByDefault_AndShowOnlyConfirmedOrOwnCandidates()
    {
        _service.Remember(Ctx("u1"), Note("Detector tags use tag A always."));
        Assert.True(new MemorySettings().Hints);

        _settings.Hints = false;
        Assert.Null(_service.Hints(Ctx("u1"), "detector tags"));

        _settings.Hints = true;
        Assert.Empty(_service.Hints(Ctx("u2"), "detector tags")!.Hits);
        Assert.Single(_service.Hints(Ctx("u1"), "detector tags")!.Hits);

        // A second person writing the same confirms it for everyone.
        _service.Remember(Ctx("u2"), Note("Detector tags use tag A always."));
        Assert.Equal(MemoryStates.Confirmed, Assert.Single(_service.Hints(Ctx("u3"), "detector tags")!.Hits).Entry.State);
    }

    [Fact]
    public void Feedback_CountsUseAndSuccess_ForAProseEntry()
    {
        _service.Remember(Ctx(), Note("Detector tags use ATS_Tag_Andur_Nr, not the default tag."));
        _service.Recall(Ctx(), new MemoryRecallOptions { Query = "detector tag" });

        Assert.Equal(0, _service.Feedback(Call("revit_place_tags", args: ("tagType", "Other_Tag"))));
        Assert.Equal(1, _service.Feedback(Call("revit_place_tags", args: ("tagType", "ATS_Tag_Andur_Nr"))));
        // Counted once per showing.
        Assert.Equal(0, _service.Feedback(Call("revit_place_tags", args: ("tagType", "ATS_Tag_Andur_Nr"))));

        var e = Assert.Single(_service.List(Ctx(), "project", null, null, 0, 10).Hits).Entry;
        Assert.Equal(1, e.Counters.Used);
        Assert.Equal(1, e.Counters.Worked);
        Assert.NotNull(e.Counters.LastUsed);
    }

    [Fact]
    public void BasedOn_CreditsTheEntriesANoteReliedOn_WorkedOnlyWhenApproved()
    {
        var first = _service.Remember(Ctx(), Note("Detector tags use tag A always.")).Entry!;
        var unapproved = _service.Remember(Ctx(), new MemoryRememberRequest
        {
            Key = "tags/sounder-tag-type",
            Value = "Sounder tags follow the detector tag style.",
            Reason = "Same legend as the detectors.",
            BasedOn = { first.Id, "not-an-id" }
        });
        Assert.Equal(1, unapproved.Credited);
        var e = _service.List(Ctx(), "project", null, null, 0, 10).Hits.Single(h => h.Entry.Id == first.Id).Entry;
        Assert.Equal((1, 0), (e.Counters.Used, e.Counters.Worked));

        // Approved in Revit (not Direct Edit): the note was reviewed, so what it relied on worked.
        _service.Remember(Ctx(), new MemoryRememberRequest
        {
            Key = "tags/beacon-tag-type",
            Value = "Beacon tags follow the detector tag style.",
            Reason = "Same legend as the detectors.",
            BasedOn = { first.Id },
            Approved = true
        });
        e = _service.List(Ctx(), "project", null, null, 0, 10).Hits.Single(h => h.Entry.Id == first.Id).Entry;
        Assert.Equal((2, 1), (e.Counters.Used, e.Counters.Worked));
    }

    [Fact]
    public void ApprovedProjectNote_IsConfirmed_AndReachesOtherUsersHints()
    {
        _settings.Hints = true;
        var unapproved = _service.Remember(Ctx("u1"), Note("Detector tags use tag A always."));
        Assert.Equal(MemoryStates.Candidate, unapproved.Entry!.State);
        Assert.Contains("candidate", unapproved.Message);
        Assert.Empty(_service.Hints(Ctx("u2"), "detector tags")!.Hits);

        // The same note approved in Revit confirms the existing entry.
        var approved = _service.Remember(Ctx("u1"), new MemoryRememberRequest
        {
            Key = "tags/detector-tag-type", Value = "Detector tags use tag A always.", Reason = "Client standard.", Approved = true
        });
        Assert.Equal(MemoryWriteKind.Reaffirm, approved.Kind);
        Assert.Contains("confirmed", approved.Message);
        Assert.Equal(MemoryStates.Confirmed, Assert.Single(_service.Hints(Ctx("u2"), "detector tags")!.Hits).Entry.State);

        // A new approved value is confirmed straight away; a personal note never is.
        var replaced = _service.Remember(Ctx("u2"), new MemoryRememberRequest
        {
            Key = "tags/detector-tag-type", Value = "Detector tags use tag B since the client's new legend.", Reason = "Client change.", Approved = true
        });
        Assert.Equal(MemoryStates.Confirmed, replaced.Entry!.State);
        var personal = _service.Remember(Ctx("u1"), new MemoryRememberRequest
        {
            Key = "tags/my-side", Value = "I place detector tags on the right side.", Reason = "Habit.", Scope = MemoryScopes.Personal, Approved = true
        });
        Assert.Equal(MemoryStates.Candidate, personal.Entry!.State);
    }

    [Fact]
    public void ApprovedFlipFlop_StaysACandidate()
    {
        Remember("u1", "Detector tags use tag A always.");
        Remember("u2", "Detector tags use tag B always.");
        var back = Remember("u1", "Detector tags use tag A always.");
        Assert.Contains(MemoryPolicy.FlagFlipFlop, back.Entry!.Flags);
        Assert.Equal(MemoryStates.Candidate, back.Entry.State);

        MemoryRememberResult Remember(string user, string value) => _service.Remember(Ctx(user), new MemoryRememberRequest
        {
            Key = "tags/detector-tag-type", Value = value, Reason = "Client standard.", Approved = true
        });
    }

    [Fact]
    public void Observer_WritesAPersonalCandidate_AndPromotesWhenASecondUserShowsTheSamePattern()
    {
        var arg = ("tagTypeId", "712345");
        _service.ProcessObserved(Call("revit_place_tags", "u1", args: arg));
        var emitted = _service.ProcessObserved(Call("revit_place_tags", "u1", args: arg));
        Assert.Single(emitted);

        var personal = _service.List(Ctx("u1"), "personal", null, null, 0, 10).Hits;
        var mine = Assert.Single(personal).Entry;
        Assert.Equal(MemorySources.Observed, mine.Source);
        Assert.Equal("personal/1626/defaults/place_tags/tagtypeid", mine.Key);
        Assert.Equal("revit_place_tags", mine.Match!.Tool);
        Assert.Empty(_service.List(Ctx(), "project", null, null, 0, 10).Hits);

        // Another colleague, on another machine (own personal folder), shows the same default.
        using (var other = New("personal-u2"))
        {
            other.ProcessObserved(Call("revit_place_tags", "u2", args: arg));
            other.ProcessObserved(Call("revit_place_tags", "u2", args: arg));
        }

        var project = Assert.Single(_service.List(Ctx(), "project", null, null, 0, 10).Hits).Entry;
        Assert.Equal("project/1626/defaults/place_tags/tagtypeid", project.Key);
        Assert.Equal(new[] { "u1", "u2" }, project.Authors.OrderBy(a => a));
        Assert.Equal(MemoryStates.Confirmed, project.State);
        Assert.True(File.Exists(Path.Combine(_projectRoot, ".rktools", "memory", MemoryService.ObservedFolderName, "u1.json")));
    }

    [Fact]
    public void Observer_MatchFeedback_CountsAContradiction()
    {
        var arg = ("tagTypeId", "712345");
        _service.ProcessObserved(Call("revit_place_tags", args: arg));
        _service.ProcessObserved(Call("revit_place_tags", args: arg));

        _service.Recall(Ctx(), new MemoryRecallOptions { Query = "place tags tagTypeId" }, MemoryScopes.Personal);
        _service.ProcessObserved(Call("revit_place_tags", args: ("tagTypeId", "999999")));

        var e = Assert.Single(_service.List(Ctx(), "personal", null, null, 0, 10).Hits).Entry;
        Assert.Equal(1, e.Counters.Contradicted);
        Assert.Equal(0, e.Counters.Used);
    }

    [Fact]
    public void Observer_StateSurvivesARestart_AndCanBeSwitchedOff()
    {
        var arg = ("tagTypeId", "712345");
        _service.ProcessObserved(Call("revit_place_tags", args: arg));
        _service.FlushObserver();

        using (var restarted = New("personal"))
            Assert.Single(restarted.ProcessObserved(Call("revit_place_tags", args: arg)));

        _settings.ObserverEnabled = false;
        using var off = New("personal-off");
        off.ProcessObserved(Call("revit_place_tags", args: arg));
        Assert.Empty(off.ProcessObserved(Call("revit_place_tags", args: arg)));
    }
}
