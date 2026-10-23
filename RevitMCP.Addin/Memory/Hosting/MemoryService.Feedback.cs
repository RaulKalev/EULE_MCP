namespace RevitMCP.Addin.Memory.Hosting;

/// <summary>
/// Hint feedback (docs/shared-memory.md, "Learning from users' workflows"): an entry shown by a recall or a route hint
/// is watched for the next calls of the same user and client. A call that matches it counts as <c>used</c>, and as
/// <c>worked</c> when it succeeds or is approved; a rejected one, or an observed default called with another value,
/// counts as <c>contradicted</c>. Counts go to the user's own tally file of the entry's tier.
/// </summary>
public sealed partial class MemoryService
{
    /// <summary>How long, and for how many calls, a shown entry is watched.</summary>
    public static readonly TimeSpan FeedbackWindow = TimeSpan.FromMinutes(30);
    public const int FeedbackCalls = 15;
    private const int MinMatchLength = 3;

    private sealed class PendingHint
    {
        public string Id = string.Empty;
        public MemoryCache Cache = null!;
        public string FoldedValue = string.Empty;
        public MemoryMatch? Match;
        public DateTimeOffset Expires;
        public int CallsLeft;
    }

    private readonly object _feedbackGate = new();
    private readonly Dictionary<string, List<PendingHint>> _pending = new(StringComparer.OrdinalIgnoreCase);

    private static string FeedbackKey(string user, string? client) => user + "|" + (client ?? string.Empty);

    private void RecordShown(MemoryContext context, List<MemoryHit> hits, TierRead read)
    {
        if (hits.Count == 0) return;
        var user = UserOf(context);
        var watch = new List<PendingHint>();
        foreach (var (cache, snapshot, scope) in read.Snapshots)
        {
            var ids = new HashSet<string>(snapshot.Entries.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
            var shown = hits.Where(h => ids.Contains(h.Entry.Id)).ToList();
            if (shown.Count == 0) continue;
            // The company folder is usually read-only for users; its usage simply goes uncounted then.
            if (!cache.Store.AddShown(user, shown.Select(h => h.Entry.Id)) && scope != MemoryScopes.Company)
                read.Result.Warnings.Add($"The usage tally in {snapshot.Folder} could not be updated.");
            cache.Invalidate();
            watch.AddRange(shown.Select(h => new PendingHint
            {
                Id = h.Entry.Id,
                Cache = cache,
                FoldedValue = MemoryRecall.Fold(h.Entry.Value),
                Match = h.Entry.Match,
                Expires = context.Now + FeedbackWindow,
                CallsLeft = FeedbackCalls
            }));
        }

        lock (_feedbackGate)
        {
            var key = FeedbackKey(user, context.Client);
            if (!_pending.TryGetValue(key, out var list)) _pending[key] = list = new List<PendingHint>();
            list.RemoveAll(p => watch.Any(w => w.Id == p.Id));
            list.AddRange(watch);
        }
    }

    /// <summary>
    /// Checks one completed call against the entries recently shown to the same user and client. Returns how many
    /// entries it counted for (tests and diagnostics).
    /// </summary>
    public int Feedback(ObservedCall call)
    {
        if (WorkflowObserver.IsIgnored(call.Tool) || call.Tool.StartsWith("revit_graph_", StringComparison.OrdinalIgnoreCase)) return 0;

        var deltas = new Dictionary<MemoryCache, Dictionary<string, MemoryCounters>>();
        lock (_feedbackGate)
        {
            if (!_pending.TryGetValue(FeedbackKey(call.User, call.Client), out var list)) return 0;
            var values = call.Arguments.Values.Where(v => v.Length >= MinMatchLength).Select(MemoryRecall.Fold).ToList();
            foreach (var hint in list.ToList())
            {
                if (hint.Expires < call.Time || hint.CallsLeft <= 0)
                {
                    list.Remove(hint);
                    continue;
                }
                hint.CallsLeft--;

                var delta = Evaluate(hint, call, values);
                if (delta == null) continue;
                list.Remove(hint);
                if (!deltas.TryGetValue(hint.Cache, out var byId)) deltas[hint.Cache] = byId = new Dictionary<string, MemoryCounters>(StringComparer.OrdinalIgnoreCase);
                byId[hint.Id] = delta;
            }
        }

        foreach (var pair in deltas)
        {
            pair.Key.Store.AddTallies(call.User, pair.Value);
            pair.Key.Invalidate();
        }
        return deltas.Sum(d => d.Value.Count);
    }

    private static MemoryCounters? Evaluate(PendingHint hint, ObservedCall call, List<string> foldedValues)
    {
        if (hint.Match != null)
        {
            if (!string.Equals(call.Tool, hint.Match.Tool, StringComparison.OrdinalIgnoreCase) ||
                !call.Arguments.TryGetValue(hint.Match.Argument, out var value))
                return null;
            return string.Equals(value, hint.Match.Value, StringComparison.OrdinalIgnoreCase)
                ? Outcome(call)
                : new MemoryCounters { Contradicted = 1 };
        }

        // A prose entry is used when the call passes a value the entry names (a type, a category, an offset).
        return foldedValues.Any(v => hint.FoldedValue.Contains(v)) ? Outcome(call) : null;
    }

    private static MemoryCounters Outcome(ObservedCall call) => new()
    {
        Used = 1,
        Worked = call.Success ? 1 : 0,
        Contradicted = call.Rejected ? 1 : 0,
        LastUsed = call.Time
    };

    /// <summary>remember basedOn=[ids]: the note relied on those entries, so each counts as used and worked.</summary>
    private int Credit(MemoryContext context, List<string>? ids)
    {
        if (ids == null || ids.Count == 0) return 0;
        var wanted = new HashSet<string>(ids.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()), StringComparer.OrdinalIgnoreCase);
        var credited = 0;
        foreach (var scope in new[] { MemoryScopes.Project, MemoryScopes.Company, MemoryScopes.Personal })
        {
            var cache = CacheFor(scope, context, out _);
            if (cache == null) continue;
            var found = cache.Read().Entries.Where(e => wanted.Contains(e.Id)).Select(e => e.Id).ToList();
            if (found.Count == 0) continue;
            cache.Store.AddTallies(UserOf(context), found.ToDictionary(
                id => id,
                _ => new MemoryCounters { Used = 1, Worked = 1, LastUsed = context.Now },
                StringComparer.OrdinalIgnoreCase));
            cache.Invalidate();
            credited += found.Count;
        }
        return credited;
    }
}
