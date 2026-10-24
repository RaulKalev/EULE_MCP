using System.IO;
using Newtonsoft.Json;

namespace RevitMCP.Addin.Memory.Hosting;

public static class MemoryReviewActions
{
    public const string Confirm = "confirm";
    public const string Retire = "retire";
    public const string Promote = "promote";
    public const string Accept = "accept";
    public const string Reject = "reject";

    public static readonly string[] All = { Confirm, Retire, Promote, Accept, Reject };
}

public sealed class MemoryReviewResult
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>Entries whose stored state or flags changed.</summary>
    public List<string> Changed { get; set; } = new();

    /// <summary>The entry or proposal file the action produced (merge result, company proposal).</summary>
    public string? Produced { get; set; }
}

/// <summary>Everything the review page shows (docs/shared-memory.md, "Human review").</summary>
public sealed class MemoryReviewData
{
    public string? Project { get; set; }
    public string User { get; set; } = string.Empty;
    public DateTimeOffset Now { get; set; }

    /// <summary>All entries of the project, company and personal tiers, history included.</summary>
    public List<MemoryHit> Entries { get; set; } = new();

    /// <summary>Consolidation proposals of the project tier: pending first, then the latest decided.</summary>
    public List<MemoryProposal> Proposals { get; set; } = new();
    public List<MemoryTierHits> Tiers { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    // Digest
    public List<MemoryHit> Unused { get; set; } = new();
    public List<MemoryHit> Contradicted { get; set; } = new();
    public List<MemoryHit> Flagged { get; set; } = new();
    public List<MemoryHit> Waiting { get; set; } = new();
}

/// <summary>
/// Human review (docs/shared-memory.md, "Human review"): confirm, retire and promote entries, accept or reject
/// consolidation proposals, forget. Every one of these is reached through an approval-gated tool (the review page's
/// <c>revit_memory_review</c>, or <c>revit_memory_forget</c>), so the person at the Revit screen decides. Company
/// memory is never changed here: an administrator applies proposals to it.
/// </summary>
public sealed partial class MemoryService
{
    public const string FlagPromoted = "promoted";

    /// <summary>Retired by a person (review page, forget, accepted proposal): the observer never brings it back.</summary>
    public const string FlagRetiredByReview = "retired-by-review";
    public const string PromotionExtension = ".promotion.proposal.json";
    public const int MaxReviewIds = 100;
    public static readonly TimeSpan UnusedAfter = TimeSpan.FromDays(30);

    public MemoryReviewResult Review(MemoryContext context, string action, IReadOnlyList<string> ids, string? proposalId)
    {
        if (!Settings.Enabled) return Fail("not_available", "Shared memory is switched off (memory.enabled=false).");
        action = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (!MemoryReviewActions.All.Contains(action))
            return Fail("validation_failed", $"action must be one of {string.Join(", ", MemoryReviewActions.All)}.");

        if (action is MemoryReviewActions.Accept or MemoryReviewActions.Reject)
            return DecideProposal(context, proposalId, action == MemoryReviewActions.Accept);

        var wanted = ids.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0 || wanted.Count > MaxReviewIds) return Fail("validation_failed", $"Name 1 to {MaxReviewIds} entry ids.");

        lock (_writeGate)
        {
            var found = Locate(context, wanted, out var problem);
            if (found == null) return Fail("not_found", problem!);
            if (action == MemoryReviewActions.Promote)
                return PromoteEntries(context, found, MemoryPromotionTargets.Company, null, null);
            return SetStored(context, found, action == MemoryReviewActions.Confirm ? MemoryStates.Confirmed : MemoryStates.Retired);
        }
    }

    /// <summary>revit_memory_forget: retires entries (history stays; nothing is deleted).</summary>
    public MemoryReviewResult Forget(MemoryContext context, IReadOnlyList<string> ids) =>
        Review(context, MemoryReviewActions.Retire, ids, null);

    // ── Entries ──────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Located
    {
        public MemoryCache Cache = null!;
        public MemorySnapshot Snapshot = null!;
        public MemoryEntry Entry = null!;
    }

    /// <summary>Finds each id in the project or personal tier; company entries and unknown ids are refused.</summary>
    private List<Located>? Locate(MemoryContext context, List<string> ids, out string? problem)
    {
        problem = null;
        var found = new List<Located>();
        var remaining = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        foreach (var scope in new[] { MemoryScopes.Project, MemoryScopes.Personal, MemoryScopes.Company })
        {
            var cache = CacheFor(scope, context, out _);
            if (cache == null || remaining.Count == 0) continue;
            var snapshot = Read(cache, context.Now);
            foreach (var e in snapshot.Entries.Where(e => remaining.Contains(e.Id)).ToList())
            {
                if (scope == MemoryScopes.Company)
                {
                    problem = $"Entry {e.Id} is company memory, which an administrator changes.";
                    return null;
                }
                found.Add(new Located { Cache = cache, Snapshot = snapshot, Entry = e });
                remaining.Remove(e.Id);
            }
        }
        if (remaining.Count > 0)
        {
            problem = $"No entry with id {string.Join(", ", remaining)} in this project's or your personal memory.";
            return null;
        }
        return found;
    }

    private MemoryReviewResult SetStored(MemoryContext context, List<Located> found, string state)
    {
        var result = new MemoryReviewResult { Success = true };
        foreach (var f in found)
        {
            var stored = OnDisk(f.Snapshot, f.Entry);
            stored.State = state;
            // A person settling the value ends the disagreement.
            if (state == MemoryStates.Confirmed) stored.Flags.Remove(MemoryPolicy.FlagFlipFlop);
            if (state == MemoryStates.Retired && !stored.Flags.Contains(FlagRetiredByReview)) stored.Flags.Add(FlagRetiredByReview);
            stored.Updated = context.Now;
            f.Cache.Store.Write(stored);
            f.Cache.Invalidate();
            result.Changed.Add(stored.Id);
        }
        result.Message = state == MemoryStates.Confirmed
            ? $"Confirmed {found.Count} entr{(found.Count == 1 ? "y" : "ies")}."
            : $"Retired {found.Count} entr{(found.Count == 1 ? "y" : "ies")}; they stay as history.";
        return result;
    }

    /// <summary>
    /// Entries leave memory for the company tier, a skill setting or a standard: a proposal file for an administrator,
    /// and the entries get the <c>promoted</c> flag (they stay in use until the company copy exists).
    /// </summary>
    private MemoryReviewResult PromoteEntries(MemoryContext context, List<Located> found, string target, string? value, string? reason)
    {
        var first = found[0].Entry;
        var topic = MemoryConsolidation.TopicOf(first);
        var text = MemoryEntryParser.OneLine(string.IsNullOrWhiteSpace(value) ? first.Value : value);
        var why = MemoryEntryParser.OneLine(string.IsNullOrWhiteSpace(reason)
            ? $"Promoted from project {first.Project ?? "memory"}: {first.Reason}"
            : reason);
        var max = Settings.ValueMaxLength;
        if (why.Length > max) why = why.Substring(0, max - 1).TrimEnd() + "…";

        string produced;
        if (target == MemoryPromotionTargets.Company)
        {
            var proposal = Propose(context, new MemoryNote
            {
                Scope = MemoryScopes.Company,
                Key = MemoryPolicy.FullKey(MemoryScopes.Company, null, topic),
                Value = text,
                Reason = why,
                Tags = first.Tags,
                Source = MemorySources.Human,
                Authors = new List<string> { UserOf(context) },
                State = MemoryStates.Confirmed
            });
            if (!proposal.Success) return Fail(proposal.Status ?? "failed", proposal.Message);
            produced = proposal.ProposalPath!;
        }
        else
        {
            var payload = new
            {
                kind = "memory.promotion",
                target,
                topic,
                text,
                reason = why,
                proposedAt = context.Now,
                proposedBy = UserOf(context),
                fromProject = Blank(context.Project),
                fromEntries = found.Select(f => new { id = f.Entry.Id, key = f.Entry.Key, value = f.Entry.Value, reason = f.Entry.Reason }).ToList(),
                howToApply = target == MemoryPromotionTargets.Skill
                    ? "Add the text as a setting of the matching company master skill (see docs/skills.md), then retire the entries."
                    : "Add the text to the matching standards document (see docs/standards-lookup.md), then retire the entries."
            };
            try
            {
                produced = MemoryStore.AtomicWrite(ProposalsFolder, Guid.NewGuid().ToString("N") + PromotionExtension,
                    JsonConvert.SerializeObject(payload, Formatting.Indented));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail("not_available", "The promotion proposal could not be written: " + ex.Message);
            }
        }

        var result = new MemoryReviewResult { Success = true, Produced = produced };
        foreach (var f in found)
        {
            var stored = OnDisk(f.Snapshot, f.Entry);
            if (!stored.Flags.Contains(FlagPromoted)) stored.Flags.Add(FlagPromoted);
            stored.Updated = context.Now;
            f.Cache.Store.Write(stored);
            f.Cache.Invalidate();
            result.Changed.Add(stored.Id);
        }
        result.Message = $"Proposed for the {target} ({produced}); an administrator applies it.";
        return result;
    }

    // ── Proposals ────────────────────────────────────────────────────────────────────────────────────────

    private MemoryReviewResult DecideProposal(MemoryContext context, string? proposalId, bool accept)
    {
        if (!MemoryProposalStore.IsValidId(proposalId)) return Fail("validation_failed", "Name the proposal.");
        var cache = CacheFor(MemoryScopes.Project, context, out var unavailable);
        if (cache == null) return Fail("not_available", unavailable!);
        var store = new MemoryProposalStore(cache.Store.Folder);

        lock (_writeGate)
        {
            var proposal = store.Get(proposalId!);
            if (proposal == null) return Fail("not_found", $"No proposal {proposalId} in this project's memory.");
            if (proposal.Status != MemoryProposalStatus.Pending) return Fail("already_decided", $"The proposal was already {proposal.Status}.");

            MemoryReviewResult result;
            if (!accept)
                result = new MemoryReviewResult { Success = true, Message = "Proposal rejected; nothing changed." };
            else
            {
                result = Apply(context, cache, proposal);
                if (!result.Success) return result;
            }

            proposal.Status = accept ? MemoryProposalStatus.Accepted : MemoryProposalStatus.Rejected;
            proposal.DecidedAt = context.Now;
            proposal.DecidedBy = UserOf(context);
            proposal.Result = result.Produced;
            store.Save(proposal);
            return result;
        }
    }

    private MemoryReviewResult Apply(MemoryContext context, MemoryCache cache, MemoryProposal proposal)
    {
        var snapshot = Read(cache, context.Now);
        var entries = snapshot.Entries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        // A promotion may name entries of other projects too; only this project's are touched here.
        if (proposal.Kind == MemoryProposalKinds.Promote)
            proposal.EntryIds = proposal.EntryIds.Where(entries.ContainsKey).ToList();
        var problems = MemoryProposalRules.Check(proposal, entries, Settings.ValueMaxLength);
        if (problems.Count > 0) return Fail("validation_failed", "The proposal no longer applies: " + problems[0]);

        var found = proposal.EntryIds.Select(id => new Located { Cache = cache, Snapshot = snapshot, Entry = entries[id] }).ToList();
        switch (proposal.Kind)
        {
            case MemoryProposalKinds.Retire:
                return SetStored(context, found, MemoryStates.Retired);
            case MemoryProposalKinds.Promote:
                return PromoteEntries(context, found, proposal.Target ?? MemoryPromotionTargets.Company, proposal.Value, proposal.Reason);
        }

        // Merge or rewrite: one entry with the proposed text, confirmed because a person accepted it.
        var first = found[0].Entry;
        var topic = string.IsNullOrWhiteSpace(proposal.Key) ? MemoryConsolidation.TopicOf(first) : MemoryPolicy.NormalizeTopic(proposal.Key, first.Project)!;
        var authors = found.SelectMany(f => f.Entry.Authors.Append(f.Entry.Author))
            .Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var written = Write(cache, new MemoryNote
        {
            Scope = first.Scope,
            Project = first.Project,
            Key = MemoryPolicy.FullKey(first.Scope, first.Project, topic),
            Value = proposal.Value!,
            Reason = proposal.Reason!,
            Tags = found.SelectMany(f => f.Entry.Tags).Distinct().Take(MemoryPolicy.MaxTags).ToList(),
            Stage = first.Stage,
            Source = MemorySources.Agent,
            Authors = authors,
            State = MemoryStates.Confirmed
        }, context);
        if (!written.Success) return Fail(written.Status ?? "failed", written.Message);

        var resultId = written.Entry!.Id;
        var after = Read(cache, context.Now);
        var produced = after.Entries.First(e => e.Id == resultId);
        var others = found.Where(f => !string.Equals(f.Entry.Id, resultId, StringComparison.OrdinalIgnoreCase)).ToList();
        var stored = OnDisk(after, produced);
        stored.State = MemoryStates.Confirmed;
        stored.Flags.Remove(MemoryPolicy.FlagFlipFlop);
        cache.Store.Write(stored);
        foreach (var f in others)
        {
            var current = after.Entries.FirstOrDefault(e => e.Id == f.Entry.Id);
            if (current == null || current.IsRetired) continue;
            var old = OnDisk(after, current);
            old.State = MemoryStates.Retired;
            if (!old.Flags.Contains(FlagRetiredByReview)) old.Flags.Add(FlagRetiredByReview);
            old.Updated = context.Now;
            cache.Store.Write(old);
        }
        cache.Invalidate();
        return new MemoryReviewResult
        {
            Success = true,
            Produced = resultId,
            Changed = found.Select(f => f.Entry.Id).Append(resultId).Distinct().ToList(),
            Message = proposal.Kind == MemoryProposalKinds.Merge
                ? $"Merged {found.Count} entries into '{stored.Key}'."
                : $"Rewrote '{stored.Key}'."
        };
    }

    // ── Review data ──────────────────────────────────────────────────────────────────────────────────────

    public MemoryReviewData ReviewData(MemoryContext context)
    {
        var read = ReadTiers(context, "all");
        var recall = context.ForRecall(Settings.StaleAfterVersions);
        var data = new MemoryReviewData { Project = context.Project, User = UserOf(context), Now = context.Now, Tiers = read.Result.Tiers, Warnings = read.Result.Warnings };
        data.Entries = read.Snapshots
            .SelectMany(s => s.Snapshot.Entries)
            .Where(e => MemoryRecall.InProject(e, context.Project))
            .OrderBy(e => e.IsRetired)
            .ThenByDescending(e => e.Updated)
            .Select(e =>
            {
                var (stale, reason) = MemoryRecall.Staleness(e, recall);
                return new MemoryHit { Entry = e, Stale = stale, StaleReason = reason };
            })
            .ToList();

        var project = read.Snapshots.FirstOrDefault(s => s.Scope == MemoryScopes.Project);
        if (project.Cache != null)
        {
            var all = new MemoryProposalStore(project.Cache.Store.Folder).List();
            data.Proposals = all.Where(p => p.Status == MemoryProposalStatus.Pending)
                .Concat(all.Where(p => p.Status != MemoryProposalStatus.Pending).Take(20))
                .ToList();
        }

        var active = data.Entries.Where(h => !h.Entry.IsRetired && h.Entry.Scope != MemoryScopes.Company).ToList();
        data.Unused = active.Where(h => h.Entry.State == MemoryStates.Dormant ||
                                        (context.Now - h.Entry.Created >= UnusedAfter &&
                                         (h.Entry.Counters.LastUsed == null || context.Now - h.Entry.Counters.LastUsed >= UnusedAfter))).ToList();
        data.Contradicted = active.Where(h => h.Entry.Counters.Contradicted > 0).ToList();
        data.Flagged = active.Where(h => h.Entry.Flags.Contains(MemoryPolicy.FlagFlipFlop)).ToList();
        data.Waiting = active.Where(h => h.Entry.State == MemoryStates.Candidate).ToList();
        return data;
    }

    private static MemoryReviewResult Fail(string status, string message) => new() { Status = status, Message = message };
}
