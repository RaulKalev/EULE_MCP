namespace RevitMCP.Addin.Plans;

/// <summary>
/// Plans of this Revit session (#99), in memory only, with their state machine:
/// Active → Exhausted (counters used up) | Expired (time) | Revoked (user, panic stop) | Closed (agent).
/// Exhausted plans still answer status calls; nothing runs under them. Thread-safe. Pure — no Revit API.
/// </summary>
public sealed class PlanRegistry
{
    public const int MaxEndedPlans = 50;

    private readonly object _gate = new();
    private readonly Dictionary<string, Plan> _plans = new(StringComparer.Ordinal);

    /// <summary>Raised after any state or counter change (from whatever thread made it).</summary>
    public event Action? Changed;

    public void Add(Plan plan)
    {
        lock (_gate)
        {
            _plans[plan.Id] = plan;
            PruneEnded();
        }
        Changed?.Invoke();
    }

    public Plan? Get(string? id)
    {
        if (!PlanIds.IsValid(id)) return null;
        lock (_gate) return _plans.TryGetValue(id!, out var plan) ? plan : null;
    }

    public bool Contains(string id)
    {
        lock (_gate) return _plans.ContainsKey(id);
    }

    /// <summary>Newest first.</summary>
    public List<Plan> All()
    {
        lock (_gate) return _plans.Values.OrderByDescending(p => p.CreatedAt).ToList();
    }

    /// <summary>
    /// Null when calls may run under the plan now, otherwise why not (state, expiry). Expires the plan on the way.
    /// </summary>
    public string? CheckUsable(Plan plan, DateTimeOffset now)
    {
        bool expired;
        lock (_gate)
        {
            expired = plan.State == PlanState.Active && plan.ExpiresAt is { } at && now >= at;
            if (expired) End(plan, PlanState.Expired, "the plan's time ran out", now);
        }
        if (expired) Changed?.Invoke();
        lock (_gate)
        {
            return plan.State switch
            {
                PlanState.Active => null,
                PlanState.Exhausted => $"plan {plan.Id} is exhausted ({plan.StateReason})",
                _ => $"plan {plan.Id} is {plan.State.ToString().ToLowerInvariant()}" + (plan.StateReason != null ? $" ({plan.StateReason})" : string.Empty)
            };
        }
    }

    /// <summary>Counts one authorized call that stayed inside the envelope; exhausts the plan at its limits.</summary>
    public void RecordCall(Plan plan, PostCheckResult check)
    {
        lock (_gate)
        {
            plan.Calls++;
            plan.Touched.UnionWith(check.Counted);
            plan.Added += check.Added;
            plan.Modified += check.Modified;
            if (plan.State == PlanState.Active)
            {
                if (plan.Touched.Count >= plan.Envelope.MaxElementsTotal)
                    End(plan, PlanState.Exhausted, $"{plan.Touched.Count} of {plan.Envelope.MaxElementsTotal} elements used", null);
                else if (plan.Calls >= plan.Envelope.MaxCalls)
                    End(plan, PlanState.Exhausted, $"{plan.Calls} of {plan.Envelope.MaxCalls} calls used", null);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>A rolled-back call still uses one of the plan's calls, so a looping agent runs out.</summary>
    public void RecordViolation(Plan plan, PlanViolation violation)
    {
        lock (_gate)
        {
            plan.Violations.Add(violation);
            if (violation.Kind == "violated")
            {
                plan.Calls++;
                if (plan.State == PlanState.Active && plan.Calls >= plan.Envelope.MaxCalls)
                    End(plan, PlanState.Exhausted, $"{plan.Calls} of {plan.Envelope.MaxCalls} calls used", null);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Revoke (user) or Close (agent). False when the plan was already ended.</summary>
    public bool Finish(Plan plan, PlanState state, string reason, DateTimeOffset now)
    {
        bool changed;
        lock (_gate)
        {
            changed = !plan.IsEnded;
            if (changed) End(plan, state, reason, now);
        }
        if (changed) Changed?.Invoke();
        return changed;
    }

    /// <summary>Revokes every plan that is not ended (panic stop). Returns them.</summary>
    public List<Plan> RevokeAll(string reason, DateTimeOffset now)
    {
        List<Plan> revoked;
        lock (_gate)
        {
            revoked = _plans.Values.Where(p => !p.IsEnded).ToList();
            foreach (var p in revoked) End(p, PlanState.Revoked, reason, now);
        }
        if (revoked.Count > 0) Changed?.Invoke();
        return revoked;
    }

    /// <summary>Expires plans whose time ran out (the UI timer calls this). Returns them.</summary>
    public List<Plan> ExpireDue(DateTimeOffset now)
    {
        List<Plan> expired;
        lock (_gate)
        {
            expired = _plans.Values.Where(p => p.State is PlanState.Active or PlanState.Exhausted && p.ExpiresAt is { } at && now >= at).ToList();
            foreach (var p in expired) End(p, PlanState.Expired, "the plan's time ran out", now);
        }
        if (expired.Count > 0) Changed?.Invoke();
        return expired;
    }

    private static void End(Plan plan, PlanState state, string reason, DateTimeOffset? now)
    {
        plan.State = state;
        plan.StateReason = reason;
        if (state != PlanState.Exhausted) plan.EndedAt = now ?? DateTimeOffset.Now;
    }

    private void PruneEnded()
    {
        var ended = _plans.Values.Where(p => p.IsEnded).OrderByDescending(p => p.EndedAt ?? p.CreatedAt).Skip(MaxEndedPlans).ToList();
        foreach (var p in ended) _plans.Remove(p.Id);
    }
}
