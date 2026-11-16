using System.IO;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.LinkOperations;
using RevitMCP.Addin.Plans;
using RevitMCP.Addin.Services;
using RevitMCP.Bridge;
using RevitMCP.Core.Models;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#163: IFC-first model linking with durable, non-retrying operations.</summary>
public sealed class LinkOperationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "revitmcp-linkops-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private LinkOperationStore NewStore() => new(_folder);

    private LinkOperationService NewService(LinkOperationStore store, Func<McpToolRequest, int, Task<McpToolResult>> dispatch,
        string session = "S1", int pid = 100, Func<int, bool>? alive = null)
    {
        var service = new LinkOperationService(store, dispatch, session, pid, () => _now, alive ?? (_ => true),
            async span => { _now += span; await Task.Yield(); });
        return service;
    }

    private static LinkAdmitInput Input(string path = @"C:\Models\Arch.ifc", string target = "C:\\PROJECTS\\MAIN.RVT", string type = LinkOperationTypes.Link) => new()
    {
        Type = type,
        Kind = LinkSourcePath.KindOf(path) ?? LinkKinds.Ifc,
        SourcePath = path,
        TargetSelector = target,
        TargetTitle = "Main",
        TargetKey = LinkSourcePath.TargetKey(target, "Main"),
        RequestedBy = "test",
        RequestId = "req-" + Guid.NewGuid().ToString("N").Substring(0, 6)
    };

    private static LinkVerdict Verdict(string outcome, string reason = "ok") => new() { Outcome = outcome, Reason = reason, Checks = { "link type: found" } };

    private static McpToolResult Ok() => new() { Success = true };
    private static McpToolResult Failed(string status, string message = "") => new() { Success = false, Status = status, Message = message };

    private static string Arg(McpToolRequest request, string key) => request.Arguments[key]?.ToString() ?? string.Empty;

    // ----- identity ---------------------------------------------------------------------------------------------

    [Fact]
    public void SourceIdentityIgnoresCaseSlashesQuotesAndTheCacheSuffix()
    {
        var plain = LinkSourcePath.Canonical(@"C:\Models\Arch.ifc");
        Assert.Equal(plain, LinkSourcePath.Canonical("\"c:/models/ARCH.IFC\""));
        Assert.Equal(plain, LinkSourcePath.Canonical(@"C:\Models\Arch.ifc.RVT"));
        Assert.Equal(plain, LinkSourcePath.Canonical(@"\\?\C:\Models\Arch.ifc"));
        Assert.NotEqual(plain, LinkSourcePath.Canonical(@"C:\Models\Struct.ifc"));
        Assert.True(LinkSourcePath.SameSource(@"C:\Models\Arch.ifc.RVT", @"c:\models\arch.ifc"));
        Assert.False(LinkSourcePath.SameSource("", @"c:\models\arch.ifc"));
    }

    [Theory]
    [InlineData(@"C:\a\b.ifc", "ifc")]
    [InlineData(@"C:\a\b.IFC", "ifc")]
    [InlineData(@"C:\a\b.rvt", "rvt")]
    [InlineData(@"C:\a\b.ifc.RVT", null)]
    [InlineData(@"C:\a\b.dwg", null)]
    [InlineData(@"C:\a\b.ifczip", null)]
    public void KindComesFromTheExtension(string path, string? expected) => Assert.Equal(expected, LinkSourcePath.KindOf(path));

    [Fact]
    public void TargetKeyUsesThePathElseTheTitle()
    {
        Assert.Equal(LinkSourcePath.TargetKey(@"C:\P\Main.rvt", "x"), LinkSourcePath.TargetKey(@"c:/p/main.RVT", "y"));
        Assert.Equal("TITLE:UNSAVED", LinkSourcePath.TargetKey("", " Unsaved "));
    }

    [Fact]
    public void OperationIdsAreSafeFileNames()
    {
        Assert.True(LinkOperationIds.IsValid(LinkOperationIds.New()));
        Assert.False(LinkOperationIds.IsValid("lop-../../etc"));
        Assert.False(LinkOperationIds.IsValid("lop-ABCDEF123456"));
        Assert.False(LinkOperationIds.IsValid(null));
    }

    // ----- state machine ----------------------------------------------------------------------------------------

    private static LinkOperation Op(string state, bool conversionStarted = false) => new()
    {
        Id = LinkOperationIds.New(),
        State = state,
        ConversionStartedAt = conversionStarted ? DateTimeOffset.Now : null,
        CanonicalSource = "C:\\A.IFC",
        TargetKey = "T"
    };

    [Theory]
    [InlineData("queued", "starting", false, true)]
    [InlineData("queued", "cancelled", false, true)]
    [InlineData("queued", "converting", false, false)]
    [InlineData("starting", "converting", false, true)]
    [InlineData("starting", "queued", false, true)]            // never reached the conversion: a busy dispatch goes back
    [InlineData("starting", "queued", true, false)]            // reached it: never back
    [InlineData("converting", "queued", true, false)]
    [InlineData("converting", "starting", true, false)]
    [InlineData("converting", "cancelled", true, false)]       // a running conversion is not cancelled
    [InlineData("converting", "verifying", true, true)]
    [InlineData("converting", "outcome_unknown", true, true)]
    [InlineData("verifying", "completed", true, true)]
    [InlineData("verifying", "failed", true, true)]
    [InlineData("outcome_unknown", "completed", true, true)]
    [InlineData("outcome_unknown", "failed", true, true)]
    [InlineData("outcome_unknown", "queued", true, false)]
    [InlineData("outcome_unknown", "starting", true, false)]
    [InlineData("outcome_unknown", "converting", true, false)]
    [InlineData("completed", "failed", true, false)]
    [InlineData("failed", "starting", true, false)]
    [InlineData("failed", "queued", true, false)]
    [InlineData("cancelled", "queued", false, false)]
    public void TransitionsFollowTheNeverRetryRule(string from, string to, bool conversionStarted, bool allowed) =>
        Assert.Equal(allowed, LinkOperationRules.CanTransition(Op(from, conversionStarted), to));

    [Fact]
    public void NoStateEverGoesBackToQueuedOnceConverting()
    {
        foreach (var from in new[] { "converting", "verifying", "outcome_unknown", "completed", "failed", "cancelled" })
            foreach (var to in new[] { "queued", "starting" })
                Assert.False(LinkOperationRules.CanTransition(Op(from, true), to), $"{from} -> {to}");
    }

    [Fact]
    public void TransitionStampsTimesHistoryAndReviewFlag()
    {
        var op = Op("queued");
        op.CreatedAt = _now;
        Assert.True(LinkOperationRules.Transition(op, "starting", "handed", _now));
        Assert.Equal(_now, op.StartedAt);
        Assert.True(LinkOperationRules.Transition(op, "converting", "converting", _now.AddSeconds(5)));
        Assert.Equal(_now.AddSeconds(5), op.ConversionStartedAt);
        Assert.True(LinkOperationRules.Transition(op, "outcome_unknown", "lost", _now.AddMinutes(20)));
        Assert.True(op.NeedsReview);
        Assert.Null(op.FinishedAt);
        Assert.True(LinkOperationRules.Transition(op, "completed", "late", _now.AddMinutes(25)));
        Assert.False(op.NeedsReview);
        Assert.Equal(_now.AddMinutes(25), op.FinishedAt);
        Assert.False(LinkOperationRules.Transition(op, "failed", "no", _now));
        Assert.Equal("completed", op.State);
        Assert.Equal(new[] { "starting", "converting", "outcome_unknown", "completed" }, op.History.Select(h => h.State));
    }

    [Fact]
    public void StateGroupsAreConsistent()
    {
        foreach (var state in LinkOperationStates.All)
        {
            Assert.False(LinkOperationStates.IsActive(state) && LinkOperationStates.IsTerminal(state));
            Assert.Equal(LinkOperationStates.IsActive(state) || state == "outcome_unknown", LinkOperationStates.BlocksNewRequests(state));
        }
        Assert.False(LinkOperationStates.IsTerminal("outcome_unknown"));
    }

    // ----- admission --------------------------------------------------------------------------------------------

    [Fact]
    public void AdmissionReturnsTheOpenOperationForTheSameSourceAndDocument()
    {
        var open = Op("converting", true);
        var same = LinkOperationRules.Admit(new[] { open }, "C:\\A.IFC", "T", LinkOperationTypes.Link);
        Assert.Equal(AdmissionOutcome.AlreadyInProgress, same.Outcome);
        Assert.Equal(open.Id, same.Existing!.Id);
        Assert.Contains("nothing new was started", same.Message);

        Assert.Equal(AdmissionOutcome.New, LinkOperationRules.Admit(new[] { open }, "C:\\B.IFC", "T", LinkOperationTypes.Link).Outcome);
        Assert.Equal(AdmissionOutcome.New, LinkOperationRules.Admit(new[] { open }, "C:\\A.IFC", "OTHER", LinkOperationTypes.Link).Outcome);
        // A refresh next to a running link of the same file is the same file: one operation.
        Assert.Equal(AdmissionOutcome.AlreadyInProgress, LinkOperationRules.Admit(new[] { open }, "C:\\A.IFC", "T", LinkOperationTypes.Refresh).Outcome);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void AFinishedOperationDoesNotBlockANewRequest(string state) =>
        Assert.Equal(AdmissionOutcome.New, LinkOperationRules.Admit(new[] { Op(state, true) }, "C:\\A.IFC", "T", LinkOperationTypes.Link).Outcome);

    [Fact]
    public void AnUnknownOutcomeBlocksAndAsksForReview()
    {
        var decision = LinkOperationRules.Admit(new[] { Op("outcome_unknown", true) }, "C:\\A.IFC", "T", LinkOperationTypes.Link);
        Assert.Equal(AdmissionOutcome.NeedsReview, decision.Outcome);
        Assert.Contains("revit_reconcile_link_operation", decision.Message);
    }

    [Fact]
    public void AdmissionBoundsTheOpenOperations()
    {
        var open = Enumerable.Range(0, LinkOperationRules.MaxOpenOperations).Select(i => { var o = Op("queued"); o.CanonicalSource = "C:\\F" + i; return o; }).ToList();
        Assert.Equal(AdmissionOutcome.QueueFull, LinkOperationRules.Admit(open, "C:\\NEW.IFC", "T", LinkOperationTypes.Link).Outcome);
    }

    // ----- dispatch meaning, restart, liveness ------------------------------------------------------------------

    [Theory]
    [InlineData("starting", false, "revit_busy", 0, DispatchFollowUp.Requeue)]
    [InlineData("starting", false, "queue_full", 3, DispatchFollowUp.Requeue)]
    [InlineData("starting", false, "revit_busy", 11, DispatchFollowUp.FailNotStarted)]
    [InlineData("starting", false, "validation_failed", 0, DispatchFollowUp.FailRefused)]
    [InlineData("starting", false, "request_timeout", 0, DispatchFollowUp.MarkUnknown)]
    [InlineData("starting", false, "request_cancelled", 0, DispatchFollowUp.MarkUnknown)]
    [InlineData("starting", true, null, 0, DispatchFollowUp.MarkUnknown)]
    [InlineData("converting", false, "request_timeout", 0, DispatchFollowUp.MarkUnknown)]
    [InlineData("converting", false, "revit_busy", 0, DispatchFollowUp.MarkUnknown)]     // busy never requeues a conversion
    [InlineData("verifying", false, "request_cancelled", 0, DispatchFollowUp.MarkUnknown)]
    [InlineData("completed", true, null, 0, DispatchFollowUp.None)]
    [InlineData("failed", false, "request_timeout", 0, DispatchFollowUp.None)]
    [InlineData("outcome_unknown", false, "request_timeout", 0, DispatchFollowUp.None)]
    public void ADispatchResultNeverSchedulesAConversionRetry(string state, bool success, string? status, int attempts, DispatchFollowUp expected)
    {
        var op = Op(state);
        op.DispatchAttempts = attempts;
        Assert.Equal(expected, LinkOperationRules.ClassifyDispatch(op, success, status, 12));
    }

    [Fact]
    public void AStartingOperationThatReachedTheConversionIsNeverRequeued()
    {
        var op = Op("starting", conversionStarted: true);
        Assert.Equal(DispatchFollowUp.MarkUnknown, LinkOperationRules.ClassifyDispatch(op, false, "revit_busy", 12));
    }

    [Fact]
    public void RecoveryNeverQueuesAgain()
    {
        Assert.Equal("cancelled", LinkOperationRules.RecoveryTarget(Op("queued")));
        Assert.Equal("failed", LinkOperationRules.RecoveryTarget(Op("starting")));
        Assert.Equal("outcome_unknown", LinkOperationRules.RecoveryTarget(Op("starting", true)));
        Assert.Equal("outcome_unknown", LinkOperationRules.RecoveryTarget(Op("converting", true)));
        Assert.Equal("outcome_unknown", LinkOperationRules.RecoveryTarget(Op("verifying", true)));
        Assert.Null(LinkOperationRules.RecoveryTarget(Op("completed", true)));
        Assert.Null(LinkOperationRules.RecoveryTarget(Op("outcome_unknown", true)));
        Assert.Contains("not started again", LinkOperationRules.RecoveryNote(Op("converting", true)));
    }

    [Fact]
    public void LivenessComesFromTheProcessAndTheHeartbeat()
    {
        var op = Op("converting", true);
        op.ProcessId = 4242;
        op.StateChangedAt = _now;
        op.HeartbeatAt = _now;

        var healthy = LinkOperationRules.Assess(op, _now.AddSeconds(20), _ => true, TimeSpan.FromSeconds(90));
        Assert.Equal("converting", healthy.EffectiveState);
        Assert.False(healthy.HeartbeatStale);

        // A silent add-in is a warning, not a verdict: Revit may simply be hung, and the conversion may still finish.
        var quiet = LinkOperationRules.Assess(op, _now.AddMinutes(10), _ => true, TimeSpan.FromSeconds(90));
        Assert.Equal("converting", quiet.EffectiveState);
        Assert.True(quiet.HeartbeatStale);
        Assert.Contains("do not start the link again", quiet.Reason);

        var gone = LinkOperationRules.Assess(op, _now.AddMinutes(10), _ => false, TimeSpan.FromSeconds(90));
        Assert.Equal("outcome_unknown", gone.EffectiveState);
        Assert.False(gone.OwnerAlive);

        var queuedGone = Op("queued");
        queuedGone.ProcessId = 1;
        Assert.Equal("cancelled", LinkOperationRules.Assess(queuedGone, _now, _ => false, TimeSpan.FromSeconds(90)).EffectiveState);

        var finished = Op("completed", true);
        Assert.Equal("completed", LinkOperationRules.Assess(finished, _now, _ => false, TimeSpan.FromSeconds(90)).EffectiveState);
    }

    [Fact]
    public void BusyNoteNamesTheRunningOperationAndTellsOthersNotToRetry()
    {
        var running = Op("converting", true);
        running.SourcePath = @"C:\Models\Big.ifc";
        running.StartedAt = _now;
        var queued = Op("queued");
        var note = LinkOperationRules.BusyNote(new[] { running, queued }, _now.AddMinutes(4));
        Assert.Contains("Big.ifc", note);
        Assert.Contains("4 min", note);
        Assert.Contains("1 more queued", note);
        Assert.Contains("do not retry", note);
        Assert.Null(LinkOperationRules.BusyNote(new[] { queued }, _now));
    }

    [Fact]
    public void DispatchBackoffGrowsToAMinute()
    {
        Assert.Equal(new[] { 5, 10, 20, 40, 60, 60 }, Enumerable.Range(1, 6).Select(i => (int)LinkOperationRules.DispatchBackoff(i).TotalSeconds));
    }

    // ----- verification -----------------------------------------------------------------------------------------

    private static LinkVerificationFacts GoodFacts() => new()
    {
        LinkTypeFound = true,
        LinkTypeId = 77,
        LinkPath = @"C:\Models\Arch.ifc.RVT",
        LinkStatus = "Loaded",
        InstanceIds = new List<long> { 78 },
        LinkedElementCount = 1200,
        Kind = LinkKinds.Ifc,
        Cache = new LinkCacheFacts { Path = @"C:\Models\Arch.ifc.RVT", Exists = true, Bytes = 5_000_000 }
    };

    [Fact]
    public void AVerifiedLinkIsLoadedPointsAtTheFileAndHasContent()
    {
        var verdict = LinkVerification.Decide(GoodFacts(), @"C:\Models\Arch.ifc");
        Assert.Equal("completed", verdict.Outcome);
        Assert.True(verdict.Verified);
        Assert.Contains("status: Loaded", verdict.Checks);
    }

    [Fact]
    public void ACacheFileAloneIsNotASuccess()
    {
        var facts = GoodFacts();
        facts.LinkTypeFound = false;
        var verdict = LinkVerification.Decide(facts, @"C:\Models\Arch.ifc");
        Assert.Equal("failed", verdict.Outcome);
        Assert.Contains(verdict.Notes, n => n.Contains("kept"));
    }

    [Fact]
    public void EveryMissingPieceFailsOrLeavesTheOutcomeOpen()
    {
        var wrongPath = GoodFacts(); wrongPath.LinkPath = @"C:\Models\Other.ifc";
        Assert.Equal("failed", LinkVerification.Decide(wrongPath, @"C:\Models\Arch.ifc").Outcome);

        var unloaded = GoodFacts(); unloaded.LinkStatus = "Unloaded";
        Assert.Equal("failed", LinkVerification.Decide(unloaded, @"C:\Models\Arch.ifc").Outcome);

        var noInstance = GoodFacts(); noInstance.InstanceIds.Clear();
        var partial = LinkVerification.Decide(noInstance, @"C:\Models\Arch.ifc");
        Assert.Equal("failed", partial.Outcome);
        Assert.Contains("partial", partial.Reason);

        var empty = GoodFacts(); empty.LinkedElementCount = 0;
        Assert.Equal("failed", LinkVerification.Decide(empty, @"C:\Models\Arch.ifc").Outcome);

        var emptyCache = GoodFacts(); emptyCache.Cache!.Bytes = 0;
        Assert.Contains("partial conversion", LinkVerification.Decide(emptyCache, @"C:\Models\Arch.ifc").Reason);

        var closed = GoodFacts(); closed.DocumentAvailable = false;
        Assert.Equal("outcome_unknown", LinkVerification.Decide(closed, @"C:\Models\Arch.ifc").Outcome);

        var unreadable = GoodFacts(); unreadable.LinkedElementCount = null;
        var unchecked_ = LinkVerification.Decide(unreadable, @"C:\Models\Arch.ifc");
        Assert.Equal("completed", unchecked_.Outcome);
        Assert.Contains(unchecked_.Notes, n => n.Contains("not checked"));
    }

    [Fact]
    public void AnRvtLinkNeedsNoCache()
    {
        var facts = GoodFacts();
        facts.Kind = LinkKinds.Rvt;
        facts.Cache = null;
        facts.LinkPath = @"C:\Models\Struct.rvt";
        Assert.Equal("completed", LinkVerification.Decide(facts, @"C:\Models\Struct.rvt").Outcome);
    }

    // ----- planner ----------------------------------------------------------------------------------------------

    private LinkPlanContext Context(IEnumerable<ExistingLinkInfo>? links = null, IEnumerable<LinkOperation>? ops = null,
        Func<string, bool>? exists = null, Func<string, string?>? policy = null) => new()
    {
        TargetKey = "T",
        FileExists = exists ?? (_ => true),
        ValidateRead = policy ?? (_ => null),
        CacheOf = p => new LinkCacheFacts { Path = p, Exists = false },
        ExistingLinks = (links ?? Array.Empty<ExistingLinkInfo>()).ToList(),
        Operations = (ops ?? Array.Empty<LinkOperation>()).ToList()
    };

    private static List<LinkModelRequest> Parse(string json, out List<string> errors)
    {
        errors = new List<string>();
        return LinkModelsPlanner.Parse(new Dictionary<string, object?> { ["files"] = JArray.Parse(json) }, errors);
    }

    [Fact]
    public void ParsesFilesPathsAndPerFileOverrides()
    {
        var args = new Dictionary<string, object?>
        {
            ["files"] = JArray.Parse("[{\"path\":\"C:\\\\a\\\\x.ifc\",\"positioning\":\"shared\",\"pin\":true},{\"path\":\"C:\\\\a\\\\y.rvt\"}]"),
            ["paths"] = new[] { "C:\\a\\z.ifc" },
            ["ifExists"] = "refresh",
            ["positioning"] = "origin"
        };
        var errors = new List<string>();
        var parsed = LinkModelsPlanner.Parse(args, errors);
        Assert.Empty(errors);
        Assert.Equal(3, parsed.Count);
        Assert.Equal("shared", parsed[0].Positioning);
        Assert.True(parsed[0].Pin);
        Assert.Equal("origin", parsed[1].Positioning);
        Assert.Equal("refresh", parsed[2].IfExists);
    }

    [Fact]
    public void RefusesNoFilesAndTooManyFiles()
    {
        var errors = new List<string>();
        LinkModelsPlanner.Parse(new Dictionary<string, object?>(), errors);
        Assert.Contains("files is required", errors.Single());

        errors.Clear();
        var many = new JArray(Enumerable.Range(0, LinkModelsPlanner.MaxFiles + 1).Select(i => new JObject { ["path"] = $"C:\\m\\{i}.ifc" }));
        LinkModelsPlanner.Parse(new Dictionary<string, object?> { ["files"] = many }, errors);
        Assert.Contains("At most", errors.Single());
    }

    [Theory]
    [InlineData("[{\"path\":\"models\\\\a.ifc\"}]", "path_not_absolute")]
    [InlineData("[{\"path\":\"C:\\\\m\\\\a.dwg\"}]", "revit_link_dwg")]
    [InlineData("[{\"path\":\"C:\\\\m\\\\a.ifc.RVT\"}]", "conversion cache")]
    [InlineData("[{\"path\":\"C:\\\\m\\\\a.ifczip\"}]", "unsupported_format")]
    [InlineData("[{\"path\":\"C:\\\\m\\\\a.ifc\",\"kind\":\"rvt\"}]", "kind_mismatch")]
    [InlineData("[{\"path\":\"C:\\\\m\\\\a.ifc\",\"positioning\":\"center\"}]", "unsupported_positioning")]
    [InlineData("[{\"path\":\"C:\\\\m\\\\a.ifc\",\"ifExists\":\"replace\"}]", "invalid_ifExists")]
    public void InvalidFilesAreRefusedWithAReason(string json, string expected)
    {
        var plan = LinkModelsPlanner.Plan(Parse(json, out _), Context());
        var item = Assert.Single(plan);
        Assert.Equal(LinkPlanActions.Invalid, item.Action);
        Assert.Contains(expected, item.Reason);
    }

    [Fact]
    public void MissingFilesAndDisallowedPathsAreInvalid()
    {
        var requests = Parse("[{\"path\":\"C:\\\\m\\\\a.ifc\"}]", out _);
        Assert.Contains("file_not_found", LinkModelsPlanner.Plan(requests, Context(exists: _ => false)).Single().Reason);
        Assert.Contains("path_not_allowed", LinkModelsPlanner.Plan(requests, Context(policy: _ => "Network paths are not permitted")).Single().Reason);
    }

    [Fact]
    public void NewFilesLinkAndCarryTheConversionNotes()
    {
        var plan = LinkModelsPlanner.Plan(Parse("[{\"path\":\"C:\\\\m\\\\a.ifc\"},{\"path\":\"C:\\\\m\\\\b.rvt\"}]", out _), Context());
        Assert.All(plan, p => Assert.Equal(LinkPlanActions.Link, p.Action));
        Assert.Equal(@"C:\m\a.ifc.RVT", plan[0].CachePath);
        Assert.Contains(plan[0].Notes, n => n.Contains("minutes"));
        Assert.Contains(plan[0].Notes, n => n.Contains("one at a time"));
        Assert.Equal("", plan[1].CachePath);
    }

    [Fact]
    public void AFreshCacheMeansNoConversion()
    {
        var context = Context();
        context.CacheOf = p => new LinkCacheFacts { Path = p, Exists = true, Bytes = 10, Fresh = true };
        var item = LinkModelsPlanner.Plan(Parse("[{\"path\":\"C:\\\\m\\\\a.ifc\"}]", out _), context).Single();
        Assert.Contains(item.Notes, n => n.Contains("no conversion"));
        var again = LinkModelsPlanner.Plan(Parse("[{\"path\":\"C:\\\\m\\\\a.ifc\",\"regenerateIfcCache\":true}]", out _), context).Single();
        Assert.Contains(again.Notes, n => n.Contains("minutes"));
    }

    [Fact]
    public void AnExistingLinkIsSkippedOrRefreshedNeverLinkedTwice()
    {
        var existing = new ExistingLinkInfo { TypeId = 9, Name = "a.ifc", Path = @"C:\M\A.IFC.RVT", Kind = LinkKinds.Ifc, Status = "Loaded", InstanceCount = 1 };
        var skip = LinkModelsPlanner.Plan(Parse("[{\"path\":\"C:\\\\m\\\\a.ifc\"}]", out _), Context(links: new[] { existing })).Single();
        Assert.Equal(LinkPlanActions.Skip, skip.Action);
        Assert.Contains("already_linked", skip.Reason);

        var refresh = LinkModelsPlanner.Plan(Parse("[{\"path\":\"C:\\\\m\\\\a.ifc\",\"ifExists\":\"refresh\"}]", out _), Context(links: new[] { existing })).Single();
        Assert.Equal(LinkPlanActions.Refresh, refresh.Action);
        Assert.Equal(9, refresh.ExistingLink!.TypeId);
        Assert.Contains(refresh.Notes, n => n.Contains("revit_reload_links_from"));

        var none = LinkModelsPlanner.Plan(Parse("[{\"path\":\"C:\\\\m\\\\b.ifc\",\"ifExists\":\"refresh\"}]", out _), Context(links: new[] { existing })).Single();
        Assert.Equal(LinkPlanActions.Link, none.Action);
    }

    [Fact]
    public void AnOpenOperationAnswersWithItsId()
    {
        var open = Op("converting", true);
        open.CanonicalSource = LinkSourcePath.Canonical(@"C:\m\a.ifc");
        open.TargetKey = "T";
        var item = LinkModelsPlanner.Plan(Parse("[{\"path\":\"C:\\\\m\\\\A.IFC\"}]", out _), Context(ops: new[] { open })).Single();
        Assert.Equal(LinkPlanActions.InProgress, item.Action);
        Assert.Equal(open.Id, item.Operation!.Id);
        Assert.False(item.WillRun);
    }

    [Fact]
    public void TheSameFileTwiceInOneCallIsOneOperation()
    {
        var plan = LinkModelsPlanner.Plan(Parse("[{\"path\":\"C:\\\\m\\\\a.ifc\"},{\"path\":\"c:/m/A.IFC\"}]", out _), Context());
        Assert.Equal(LinkPlanActions.Link, plan[0].Action);
        Assert.Equal(LinkPlanActions.Invalid, plan[1].Action);
        Assert.Contains("duplicate_in_request", plan[1].Reason);
    }

    // ----- store ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheStoreRoundTripsAndUpdatesUnderOneLock()
    {
        var store = NewStore();
        var op = new LinkOperation { Id = LinkOperationIds.New(), SourcePath = @"C:\a.ifc", CreatedAt = _now, RequestId = "r1" };
        Assert.True(store.Create(op));
        Assert.False(store.Create(op));

        var updated = store.Update(op.Id, o => { o.Phase = "converting"; return true; });
        Assert.Equal("converting", updated!.Phase);
        Assert.Equal("converting", NewStore().Get(op.Id)!.Phase);   // a second reader (the bridge) sees it
        Assert.Single(store.FindByRequest("r1"));
        Assert.Empty(store.FindByRequest("nope"));
        Assert.Null(store.Get("lop-zzzzzzzzzzzz"));
        Assert.Null(store.Update("../x", _ => true));
    }

    [Fact]
    public void ConcurrentUpdatesDoNotLoseChanges()
    {
        var store = NewStore();
        var op = new LinkOperation { Id = LinkOperationIds.New(), CreatedAt = _now };
        store.Create(op);
        Parallel.For(0, 40, i => store.Update(op.Id, o => { o.DispatchAttempts++; return true; }));
        Assert.Equal(40, store.Get(op.Id)!.DispatchAttempts);
    }

    [Fact]
    public void ACancelRequestIsAMarkerFileTheBridgeCanWriteSafely()
    {
        var store = NewStore();
        var op = new LinkOperation { Id = LinkOperationIds.New(), CreatedAt = _now, State = "queued" };
        store.Create(op);
        Assert.False(store.IsCancelRequested(op.Id));
        Assert.True(store.RequestCancel(op.Id));
        Assert.True(store.IsCancelRequested(op.Id));
        Assert.Equal("queued", store.Get(op.Id)!.State);   // the record itself is untouched

        var done = new LinkOperation { Id = LinkOperationIds.New(), CreatedAt = _now, State = "completed" };
        store.Create(done);
        Assert.False(store.RequestCancel(done.Id));
        Assert.False(store.RequestCancel("lop-000000000000"));
    }

    [Fact]
    public void FinishedRecordsArePrunedOpenOnesNever()
    {
        var store = NewStore();
        var open = new LinkOperation { Id = LinkOperationIds.New(), CreatedAt = _now.AddYears(-1), State = "outcome_unknown" };
        store.Create(open);
        for (var i = 0; i < LinkOperationStore.MaxFinishedKept + 5; i++)
            store.Create(new LinkOperation { Id = LinkOperationIds.New(), CreatedAt = _now.AddMinutes(i), State = "completed" });
        Assert.NotNull(store.Get(open.Id));
        Assert.Equal(LinkOperationStore.MaxFinishedKept + 1, store.LoadAll().Count);
    }

    [Fact]
    public void AnUnreadableRecordIsSkipped()
    {
        var store = NewStore();
        store.Create(new LinkOperation { Id = LinkOperationIds.New(), CreatedAt = _now });
        File.WriteAllText(Path.Combine(_folder, "lop-aaaaaaaaaaaa.json"), "{ not json");
        Assert.Single(store.LoadAll());
    }

    // ----- service: exactly once, serial, never retried ---------------------------------------------------------

    /// <summary>The Revit side as the run tool does it: claim, persist converting, "convert", verify, finish.</summary>
    private sealed class FakeRevit
    {
        public int Dispatches;
        public int Conversions;
        public int MaxConcurrent;
        private int _current;
        public Func<LinkOperation, Task<string>>? Convert;     // returns "completed" | "failed" | "hang"
        public List<string> Order { get; } = new();

        public Func<McpToolRequest, int, Task<McpToolResult>> Dispatch(Func<LinkOperationService> service) => async (request, _) =>
        {
            Interlocked.Increment(ref Dispatches);
            var svc = service();
            var op = svc.ClaimForRun(Arg(request, "operationId"), Arg(request, "dispatchToken"));
            if (op == null) return Failed("validation_failed", "not claimable");
            var now = Interlocked.Increment(ref _current);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            try
            {
                Assert.True(svc.BeginConversion(op.Id, "converting"));
                Interlocked.Increment(ref Conversions);
                lock (Order) Order.Add(op.SourcePath);
                var outcome = Convert == null ? "completed" : await Convert(op);
                if (outcome == "hang") return Failed("request_timeout", "timed out");
                svc.BeginVerification(op.Id);
                svc.Finish(op.Id, Verdict(outcome), r => r.LinkTypeId = 7);
                return outcome == "completed" ? Ok() : Failed("operation_failed", "failed");
            }
            finally { Interlocked.Decrement(ref _current); }
        };
    }

    [Fact]
    public async Task AnAdmittedOperationIsOnDiskBeforeAnythingRunsAndRunsOnce()
    {
        var store = NewStore();
        var fake = new FakeRevit();
        LinkOperationService service = null!;
        service = NewService(store, fake.Dispatch(() => service));

        var admission = service.Admit(Input());
        Assert.True(admission.Accepted);
        Assert.Equal("queued", NewStore().Get(admission.Operation!.Id)!.State);   // visible to another process first
        Assert.Equal(0, fake.Dispatches);

        await service.RunUntilIdleAsync();

        var done = store.Get(admission.Operation.Id)!;
        Assert.Equal("completed", done.State);
        Assert.True(done.Result.Verified);
        Assert.Equal(1, fake.Dispatches);
        Assert.Equal(1, fake.Conversions);
        Assert.Equal(new[] { "starting", "converting", "verifying", "completed" }, done.History.Skip(1).Select(h => h.State));
    }

    [Fact]
    public void TwoClientsRacingGetOneOperation()
    {
        var store = NewStore();
        var service = NewService(store, (_, _) => Task.FromResult(Ok()));
        var results = new LinkAdmission[16];
        Parallel.For(0, results.Length, i => results[i] = service.Admit(Input(@"C:\Models\Arch.ifc")));

        Assert.Equal(1, results.Count(r => r.Accepted));
        Assert.Equal(results.Length - 1, results.Count(r => r.Status == "already_in_progress"));
        Assert.Single(results.Select(r => r.Operation!.Id).Distinct());
        Assert.Single(store.LoadAll());
    }

    [Fact]
    public void AReconnectingAgentGetsTheSameOperationBackWhateverTheSpelling()
    {
        var service = NewService(NewStore(), (_, _) => Task.FromResult(Ok()));
        var first = service.Admit(Input(@"C:\Models\Arch.ifc"));
        var again = service.Admit(Input("c:/models/ARCH.IFC"));
        Assert.Equal("already_in_progress", again.Status);
        Assert.Equal(first.Operation!.Id, again.Operation!.Id);
        Assert.Equal("accepted", service.Admit(Input(@"C:\Models\Struct.ifc")).Status);
        Assert.Equal("accepted", service.Admit(Input(@"C:\Models\Arch.ifc", target: @"C:\Projects\Other.rvt")).Status);
    }

    [Fact]
    public async Task OperationsRunOneAtATimeInRequestOrder()
    {
        var store = NewStore();
        var fake = new FakeRevit { Convert = async _ => { await Task.Yield(); return "completed"; } };
        LinkOperationService service = null!;
        service = NewService(store, fake.Dispatch(() => service));
        foreach (var name in new[] { "A", "B", "C", "D" })
        {
            service.Admit(Input($@"C:\Models\{name}.ifc"));
            _now += TimeSpan.FromSeconds(1);
        }

        await service.RunUntilIdleAsync();

        Assert.Equal(1, fake.MaxConcurrent);
        Assert.Equal(new[] { @"C:\Models\A.ifc", @"C:\Models\B.ifc", @"C:\Models\C.ifc", @"C:\Models\D.ifc" }, fake.Order);
        Assert.All(store.LoadAll(), o => Assert.Equal("completed", o.State));
    }

    [Fact]
    public async Task ATimeoutDuringTheConversionIsOutcomeUnknownAndNeverRetried()
    {
        var store = NewStore();
        var fake = new FakeRevit { Convert = _ => Task.FromResult("hang") };
        LinkOperationService service = null!;
        service = NewService(store, fake.Dispatch(() => service));
        var id = service.Admit(Input()).Operation!.Id;

        await service.RunUntilIdleAsync();
        await service.RunUntilIdleAsync();

        var op = store.Get(id)!;
        Assert.Equal("outcome_unknown", op.State);
        Assert.True(op.NeedsReview);
        Assert.Equal(1, fake.Dispatches);   // not offered again
        Assert.Equal(1, fake.Conversions);
        Assert.Contains("NOT started again", op.Message);

        // A client that retries gets told to reconcile, not a second conversion.
        var retry = service.Admit(Input());
        Assert.Equal("needs_review", retry.Status);
        Assert.Equal(id, retry.Operation!.Id);
        await service.RunUntilIdleAsync();
        Assert.Equal(1, fake.Dispatches);
    }

    [Fact]
    public async Task ALateFinishSettlesAnUnknownOutcomeWithoutAnotherConversion()
    {
        var store = NewStore();
        var fake = new FakeRevit { Convert = _ => Task.FromResult("hang") };
        LinkOperationService service = null!;
        service = NewService(store, fake.Dispatch(() => service));
        var id = service.Admit(Input()).Operation!.Id;
        await service.RunUntilIdleAsync();
        Assert.Equal("outcome_unknown", store.Get(id)!.State);

        // Revit comes back and the conversion that was still running completes.
        service.BeginVerification(id);   // no-op from outcome_unknown
        service.Finish(id, Verdict("completed"), r => r.LinkTypeId = 5);

        var op = store.Get(id)!;
        Assert.Equal("completed", op.State);
        Assert.Equal("late_completion", op.ResolvedBy);
        Assert.False(op.NeedsReview);
        Assert.Equal(1, fake.Conversions);
    }

    [Fact]
    public async Task ABusyRevitThatNeverStartedTheRunToolIsOfferedAgainButNeverConvertedTwice()
    {
        var store = NewStore();
        var calls = 0;
        var fake = new FakeRevit();
        LinkOperationService service = null!;
        var inner = fake.Dispatch(() => service);
        service = NewService(store, (request, ms) =>
            ++calls <= 2 ? Task.FromResult(Failed("revit_busy", "Revit is busy; nothing was executed.")) : inner(request, ms));
        var id = service.Admit(Input()).Operation!.Id;

        await service.RunUntilIdleAsync();

        var op = store.Get(id)!;
        Assert.Equal("completed", op.State);
        Assert.Equal(3, calls);
        Assert.Equal(2, op.DispatchAttempts);
        Assert.Equal(1, fake.Conversions);
    }

    [Fact]
    public async Task ARevitThatStaysBusyFailsTheOperationWithNothingStarted()
    {
        var store = NewStore();
        var calls = 0;
        var service = NewService(store, (_, _) => { calls++; return Task.FromResult(Failed("revit_busy")); });
        service.MaxDispatchAttempts = 4;
        var id = service.Admit(Input()).Operation!.Id;

        await service.RunUntilIdleAsync();

        var op = store.Get(id)!;
        Assert.Equal("failed", op.State);
        Assert.Equal(4, calls);
        Assert.Contains("Nothing was converted", op.Message);
        Assert.Null(op.ConversionStartedAt);
    }

    [Fact]
    public async Task AFailedConversionEndsFailedAndOnlyANewRequestMakesANewOperation()
    {
        var store = NewStore();
        var fake = new FakeRevit { Convert = _ => Task.FromResult("failed") };
        LinkOperationService service = null!;
        service = NewService(store, fake.Dispatch(() => service));
        var first = service.Admit(Input()).Operation!.Id;
        await service.RunUntilIdleAsync();
        Assert.Equal("failed", store.Get(first)!.State);
        Assert.Equal(1, fake.Dispatches);

        // Nothing retried by itself; a deliberate new request is a new operation.
        await service.RunUntilIdleAsync();
        Assert.Equal(1, fake.Dispatches);
        var second = service.Admit(Input());
        Assert.True(second.Accepted);
        Assert.NotEqual(first, second.Operation!.Id);
    }

    [Fact]
    public async Task ADispatchThatThrowsIsAnUnknownOutcomeNotARetry()
    {
        var store = NewStore();
        var calls = 0;
        LinkOperationService service = null!;
        service = NewService(store, (request, _) =>
        {
            calls++;
            var op = service.ClaimForRun(Arg(request, "operationId"), Arg(request, "dispatchToken"))!;
            service.BeginConversion(op.Id, "converting");
            throw new IOException("pipe broken");
        });
        var id = service.Admit(Input()).Operation!.Id;
        await service.RunUntilIdleAsync();
        Assert.Equal("outcome_unknown", store.Get(id)!.State);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void TheRunToolRefusesAWrongTokenAndAnOperationThatIsNotStarting()
    {
        var store = NewStore();
        var service = NewService(store, (_, _) => Task.FromResult(Ok()));
        var id = service.Admit(Input()).Operation!.Id;
        Assert.Null(service.ClaimForRun(id, "guess"));                       // still queued
        store.Update(id, o => LinkOperationRules.Transition(o, "starting", "x", _now) && (o.DispatchToken = "tok") != null);
        Assert.Null(service.ClaimForRun(id, "wrong"));
        Assert.Null(service.ClaimForRun(id, ""));
        Assert.NotNull(service.ClaimForRun(id, "tok"));
        Assert.True(service.BeginConversion(id, "converting"));
        Assert.Null(service.ClaimForRun(id, "tok"));                         // a second delivery cannot start it again
    }

    // ----- restart and reconciliation ---------------------------------------------------------------------------

    [Fact]
    public void AfterARestartRunningWorkIsUnknownQueuedWorkIsCancelledAndNothingStartsAgain()
    {
        var store = NewStore();
        var before = NewService(store, (_, _) => Task.FromResult(Ok()), "OLD", 100);
        var converting = before.Admit(Input(@"C:\m\a.ifc")).Operation!.Id;
        var queued = before.Admit(Input(@"C:\m\b.ifc")).Operation!.Id;
        var starting = before.Admit(Input(@"C:\m\c.ifc")).Operation!.Id;
        store.Update(converting, o => LinkOperationRules.Transition(o, "starting", "s", _now) && LinkOperationRules.Transition(o, "converting", "c", _now));
        store.Update(starting, o => LinkOperationRules.Transition(o, "starting", "s", _now));

        var calls = 0;
        var after = NewService(store, (_, _) => { calls++; return Task.FromResult(Ok()); }, "NEW", 200, alive: pid => pid != 100);
        var changed = after.RecoverOrphans();

        Assert.Equal(3, changed.Count);
        Assert.Equal("outcome_unknown", store.Get(converting)!.State);
        Assert.True(store.Get(converting)!.NeedsReview);
        Assert.Contains("revit_reconcile_link_operation", store.Get(converting)!.ReviewHint);
        Assert.Equal("cancelled", store.Get(queued)!.State);
        Assert.Equal("failed", store.Get(starting)!.State);

        after.Kick();
        Assert.Equal(0, calls);
        Assert.Equal("needs_review", after.Admit(Input(@"C:\m\a.ifc")).Status);
        Assert.Equal("accepted", after.Admit(Input(@"C:\m\b.ifc")).Status);   // cancelled work can be asked for again, by a person
    }

    [Fact]
    public void AnOperationOfAnotherLiveRevitIsLeftAlone()
    {
        var store = NewStore();
        var other = NewService(store, (_, _) => Task.FromResult(Ok()), "OTHER", 100);
        var id = other.Admit(Input()).Operation!.Id;
        store.Update(id, o => LinkOperationRules.Transition(o, "starting", "s", _now) && LinkOperationRules.Transition(o, "converting", "c", _now));

        var mine = NewService(store, (_, _) => Task.FromResult(Ok()), "MINE", 200, alive: _ => true);
        Assert.Empty(mine.RecoverOrphans());
        Assert.Equal("converting", store.Get(id)!.State);
        Assert.Equal("already_in_progress", mine.Admit(Input()).Status);   // two Revit instances racing: still one conversion
    }

    [Fact]
    public void ReconcileResolvesAnUnknownOutcomeOnlyThroughAVerification()
    {
        var store = NewStore();
        var service = NewService(store, (_, _) => Task.FromResult(Ok()));
        var id = service.Admit(Input()).Operation!.Id;
        store.Update(id, o => LinkOperationRules.Transition(o, "starting", "s", _now) && LinkOperationRules.Transition(o, "converting", "c", _now));
        service.MarkUnknown(id, "lost");

        // Document closed: stays unknown for a person.
        var stillUnknown = service.Finish(id, LinkVerification.Decide(new LinkVerificationFacts { DocumentAvailable = false }, @"C:\Models\Arch.ifc"), null, "reconcile");
        Assert.Equal("outcome_unknown", stillUnknown!.State);
        Assert.True(stillUnknown.NeedsReview);

        // Document open and the link is there: completed, by reconcile.
        var settled = service.Finish(id, LinkVerification.Decide(GoodFacts(), @"C:\Models\Arch.ifc"), r => r.LinkTypeId = 77, "reconcile");
        Assert.Equal("completed", settled!.State);
        Assert.Equal("reconcile", settled.ResolvedBy);
        Assert.Equal(77, settled.Result.LinkTypeId);
    }

    // ----- cancel -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task CancellingQueuedWorkDropsItWithoutTouchingRevit()
    {
        var store = NewStore();
        var calls = 0;
        var service = NewService(store, (_, _) => { calls++; return Task.FromResult(Ok()); });
        var id = service.Admit(Input()).Operation!.Id;

        var outcome = service.RequestCancel(id);
        Assert.Equal("cancelled", outcome.Status);
        await service.RunUntilIdleAsync();

        Assert.Equal("cancelled", store.Get(id)!.State);
        Assert.Equal(0, calls);
        Assert.Equal("already_finished", service.RequestCancel(id).Status);
        Assert.Equal("not_found", service.RequestCancel("lop-000000000000").Status);
    }

    [Fact]
    public async Task CancellingARunningConversionOnlyMarksItAndNeverInterruptsIt()
    {
        var store = NewStore();
        var gate = new TaskCompletionSource<bool>();
        var fake = new FakeRevit { Convert = async _ => { await gate.Task; return "completed"; } };
        LinkOperationService service = null!;
        service = NewService(store, fake.Dispatch(() => service));
        var id = service.Admit(Input()).Operation!.Id;

        var run = service.RunUntilIdleAsync();
        while (store.Get(id)!.State != "converting") await Task.Delay(5);

        var outcome = service.RequestCancel(id);
        Assert.Equal("cancel_requested", outcome.Status);
        Assert.Contains("not interrupted", outcome.Message);
        var marked = store.Get(id)!;
        Assert.Equal("converting", marked.State);
        Assert.True(marked.CancelRequested);

        gate.SetResult(true);
        await run;
        Assert.Equal("completed", store.Get(id)!.State);   // it finished and was verified like any other
    }

    [Fact]
    public void ACancelMarkerFromTheBridgeIsAppliedByTheSweep()
    {
        var store = NewStore();
        var service = NewService(store, (_, _) => Task.FromResult(Ok()));
        var queued = service.Admit(Input(@"C:\m\a.ifc")).Operation!.Id;
        store.RequestCancel(queued);   // what revit_request_link_cancel does, in another process
        service.Tick();
        Assert.Equal("cancelled", store.Get(queued)!.State);
    }

    [Fact]
    public void TheHeartbeatTouchesOnlyActiveOperationsOfThisSession()
    {
        var store = NewStore();
        var service = NewService(store, (_, _) => Task.FromResult(Ok()));
        var id = service.Admit(Input()).Operation!.Id;
        _now += TimeSpan.FromSeconds(30);
        service.Heartbeat();
        Assert.Equal(_now, store.Get(id)!.HeartbeatAt);

        service.Finish(id, Verdict("failed"), null);   // illegal from queued: unchanged
        store.Update(id, o => LinkOperationRules.Transition(o, "cancelled", "x", _now));
        _now += TimeSpan.FromSeconds(30);
        service.Heartbeat();
        Assert.NotEqual(_now, store.Get(id)!.HeartbeatAt);
    }

    // ----- inline (the reload path) -----------------------------------------------------------------------------

    [Fact]
    public async Task AnInlineReloadHoldsTheLockAndTheQueueWaitsForIt()
    {
        var store = NewStore();
        var fake = new FakeRevit();
        LinkOperationService service = null!;
        service = NewService(store, fake.Dispatch(() => service));

        var reload = service.Admit(Input(@"C:\m\a.ifc", type: LinkOperationTypes.Refresh), inline: true);
        Assert.True(reload.Accepted);
        Assert.Equal("starting", reload.Operation!.State);
        Assert.Equal("already_in_progress", service.Admit(Input(@"C:\m\a.ifc")).Status);   // a link of the same file is refused meanwhile

        var other = service.Admit(Input(@"C:\m\b.ifc")).Operation!.Id;
        var run = service.RunUntilIdleAsync();
        await Task.Delay(30);
        Assert.Equal("queued", store.Get(other)!.State);   // waits: one conversion at a time

        Assert.True(service.BeginConversion(reload.Operation.Id, "reloading"));
        service.BeginVerification(reload.Operation.Id);
        service.Finish(reload.Operation.Id, Verdict("completed"));
        await run;
        Assert.Equal("completed", store.Get(other)!.State);
        Assert.Equal(1, fake.Conversions);
    }

    [Fact]
    public void AnInlineOperationThatNeverBeganCanBeWithdrawn()
    {
        var store = NewStore();
        var service = NewService(store, (_, _) => Task.FromResult(Ok()));
        var id = service.Admit(Input(type: LinkOperationTypes.Refresh), inline: true).Operation!.Id;
        service.WithdrawInline(id);
        Assert.Equal("cancelled", store.Get(id)!.State);

        var running = service.Admit(Input(@"C:\m\z.ifc", type: LinkOperationTypes.Refresh), inline: true).Operation!.Id;
        service.BeginConversion(running, "reloading");
        service.WithdrawInline(running);
        Assert.Equal("converting", store.Get(running)!.State);   // a running one is never withdrawn
    }

    // ----- status view ------------------------------------------------------------------------------------------

    [Fact]
    public void StatusReportsMeasuredTimeAndNoPercentage()
    {
        var op = Op("converting", true);
        op.SourcePath = @"C:\Models\Big.ifc";
        op.StartedAt = _now;
        op.Phase = "converting the IFC";
        var liveness = LinkOperationRules.Assess(op, _now.AddMinutes(7), _ => true, TimeSpan.FromMinutes(5));
        var json = JObject.FromObject(LinkOperationStatusView.Describe(op, _now.AddMinutes(7), liveness, null));

        Assert.Equal("7 min 0 s", (string?)json["elapsed"]);
        Assert.Equal(420, (long?)json["elapsedSeconds"]);
        Assert.Equal("never", (string?)json["automaticRetry"]);
        Assert.False((bool)json["ready"]!);
        Assert.Null(json.Properties().FirstOrDefault(p => p.Name.Contains("percent", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("Do NOT request the link again", (string?)json["next"]);
    }

    [Fact]
    public void ReadyMeansCompletedAndVerified()
    {
        var done = Op("completed", true);
        done.Result.Verified = true;
        Assert.True((bool)JObject.FromObject(LinkOperationStatusView.Describe(done, _now, new LinkOperationLiveness { EffectiveState = "completed" }, null))["ready"]!);

        done.Result.Verified = false;
        Assert.False((bool)JObject.FromObject(LinkOperationStatusView.Describe(done, _now, new LinkOperationLiveness { EffectiveState = "completed" }, null))["ready"]!);

        var unknown = Op("outcome_unknown", true);
        var view = JObject.FromObject(LinkOperationStatusView.Describe(unknown, _now, new LinkOperationLiveness { EffectiveState = "outcome_unknown" }, null));
        Assert.True((bool)view["needsReview"]!);
        Assert.Contains("Do NOT link again", (string?)view["next"]);
    }

    [Fact]
    public void QueuePositionsAreOldestFirst()
    {
        var a = Op("queued"); a.CreatedAt = _now;
        var b = Op("queued"); b.CreatedAt = _now.AddSeconds(5);
        var running = Op("converting", true);
        var positions = LinkOperationStatusView.QueuePositions(new[] { b, running, a });
        Assert.Equal(1, positions[a.Id]);
        Assert.Equal(2, positions[b.Id]);
        Assert.False(positions.ContainsKey(running.Id));
    }

    [Theory]
    [InlineData(5, "5 s")]
    [InlineData(95, "1 min 35 s")]
    [InlineData(3600, "60 min 0 s")]
    [InlineData(9000, "2 h 30 min")]
    public void ElapsedTimeReadsPlainly(int seconds, string expected) =>
        Assert.Equal(expected, LinkOperationRules.FormatElapsed(TimeSpan.FromSeconds(seconds)));

    // ----- the tool surface --------------------------------------------------------------------------------------

    [Fact]
    public void TheSurfaceIsInPlace()
    {
        var addin = ToolSourceScan.Addin.Value;
        Assert.Equal("ReadOnly", addin["revit_preview_link_models"].Permission);
        Assert.Equal("RequiresApproval", addin["revit_link_models"].Permission);
        Assert.Equal("RequiresApproval", addin["revit_link_operation_run"].Permission);
        Assert.Equal("DirectEdit", addin["revit_reconcile_link_operation"].Permission);

        var bridge = ToolSourceScan.Bridge.Value;
        Assert.True(Assert.Single(bridge, b => b.Name == "revit_preview_link_models").ReadOnly);
        Assert.False(Assert.Single(bridge, b => b.Name == "revit_link_models").ReadOnly);
        Assert.True(Assert.Single(bridge, b => b.Name == "revit_link_operation_status").ReadOnly);
        Assert.True(Assert.Single(bridge, b => b.Name == "revit_list_link_operations").ReadOnly);
        Assert.False(Assert.Single(bridge, b => b.Name == "revit_request_link_cancel").ReadOnly);
        Assert.False(Assert.Single(bridge, b => b.Name == "revit_reconcile_link_operation").ReadOnly);

        // The carrier is internal: the bridge never advertises it.
        Assert.DoesNotContain(bridge, b => b.Name == "revit_link_operation_run");
        Assert.DoesNotContain(bridge, b => b.Targets.Contains("revit_link_operation_run"));

        foreach (var name in new[] { "revit_preview_link_models", "revit_link_models", "revit_link_operation_status", "revit_list_link_operations", "revit_request_link_cancel", "revit_reconcile_link_operation" })
            Assert.Equal("documents", BridgeToolGroups.GroupOf(name));
    }

    [Fact]
    public void StatusListAndCancelNeverTouchRevit()
    {
        // The whole point of #163's status route: these three run in the bridge on the files and never send to the add-in.
        foreach (var name in new[] { "revit_link_operation_status", "revit_list_link_operations", "revit_request_link_cancel" })
        {
            var tool = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == name);
            Assert.Empty(tool.Targets);
            Assert.DoesNotContain("pipeClient.SendAsync", tool.Source);
        }
    }

    [Fact]
    public void LinkingCreatesElementsAndRunsLong()
    {
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_link_models"));
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_link_operation_run"));
        Assert.Equal(PlanToolKind.Modifies, PlanToolClassification.Classify("revit_reconcile_link_operation"));
        Assert.True(LongRunningTools.IsLongRunning("revit_link_operation_run"));
        Assert.True(LongRunningTools.IsLongRunning("revit_reload_links_from"));
    }

    [Fact]
    public void TheReloadPathIsReusedNotDuplicated()
    {
        // ifExists=refresh goes through LinkReloadService, and revit_reload_links_from records its pairs as operations.
        var root = ToolSourceScan.Root();
        var executor = File.ReadAllText(Path.Combine(root, "RevitMCP.Addin", "Tools", "Documents", "LinkModelsExecutor.cs"));
        Assert.Contains("LinkReloadService.Reload(", executor);
        var reload = File.ReadAllText(Path.Combine(root, "RevitMCP.Addin", "Tools", "Documents", "ReloadLinksTools.cs"));
        Assert.Contains("LinkOperationService.Current", reload);
        Assert.Contains("LinkModelsExecutor.FinishReload", reload);
    }

    [Fact]
    public void NothingInTheLinkOperationCodeRetriesAConversion()
    {
        // A tripwire for the issue's central rule: no retry loop around the conversion, and the run path never reads the
        // caller's cancellation token once it is converting.
        var root = ToolSourceScan.Root();
        var executor = File.ReadAllText(Path.Combine(root, "RevitMCP.Addin", "Tools", "Documents", "LinkModelsExecutor.cs"));
        var run = executor.Substring(executor.IndexOf("public static McpToolResult Run(", StringComparison.Ordinal));
        run = run.Substring(0, run.IndexOf("private static McpToolResult ResultOf", StringComparison.Ordinal));
        Assert.DoesNotContain("ThrowIfCancellationRequested", run);
        Assert.DoesNotContain("CancellationToken", run);
        foreach (var file in Directory.GetFiles(Path.Combine(root, "RevitMCP.Addin", "LinkOperations"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Retry(", text);
            Assert.DoesNotMatch(@"for\s*\(.*attempt", text);
        }
    }
}
