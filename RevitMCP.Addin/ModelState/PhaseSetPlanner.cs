using System.Globalization;

namespace RevitMCP.Addin.ModelState;

/// <summary>A project phase: its id, name and position in the phase sequence (1 = first).</summary>
public sealed class PhaseInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Sequence { get; set; }
}

/// <summary>What the phase writer knows about one element before it changes it (read on the API thread).</summary>
public sealed class PhaseElementState
{
    public long Id { get; set; }
    public bool Exists { get; set; } = true;
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>The element has PHASE_CREATED / PHASE_DEMOLISHED (annotations, views and types do not).</summary>
    public bool HasPhaseParameters { get; set; }
    public bool CreatedReadOnly { get; set; }
    public bool DemolishedReadOnly { get; set; }
    /// <summary>Why the parameters cannot be written (group member, follows its host), when known.</summary>
    public string? ReadOnlyReason { get; set; }

    public PhaseInfo? Created { get; set; }
    public PhaseInfo? Demolished { get; set; }
}

public enum PhaseTargetKind { Keep, Set, Clear }

/// <summary>What to do with one of the two phase parameters.</summary>
public sealed class PhaseTarget
{
    public PhaseTargetKind Kind { get; set; } = PhaseTargetKind.Keep;
    public PhaseInfo? Phase { get; set; }

    public static readonly PhaseTarget Keep = new();
}

/// <summary>One preview / result row of revit_set_phase.</summary>
public sealed class PhaseSetRow
{
    public long Id { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? CreatedBefore { get; set; }
    public string? CreatedAfter { get; set; }
    public string? DemolishedBefore { get; set; }
    public string? DemolishedAfter { get; set; }

    /// <summary>ready | unchanged | skipped | read_only | phase_order_invalid | not_found, then set | failed | rolled_back.</summary>
    public string Status { get; set; } = PhaseSetPlanner.StatusReady;
    public string? Reason { get; set; }

    public bool SetCreated { get; set; }
    public long? NewCreatedId { get; set; }
    public bool SetDemolished { get; set; }
    /// <summary>The new demolished phase id; null with <see cref="SetDemolished"/> = clear (not demolished).</summary>
    public long? NewDemolishedId { get; set; }
    /// <summary>Write the demolished phase before the created one, so no intermediate state is out of order.</summary>
    public bool DemolishedFirst { get; set; }

    /// <summary>A row that makes an atomic request fail as a whole.</summary>
    public bool IsBlocking => Status is PhaseSetPlanner.StatusReadOnly or PhaseSetPlanner.StatusOrderInvalid or PhaseSetPlanner.StatusNotFound;
    public bool WillChange => Status == PhaseSetPlanner.StatusReady;
}

/// <summary>
/// Validation and before/after rows for revit_set_phase (#109). Pure: the tools read <see cref="PhaseElementState"/>s
/// on the API thread, this decides what each element would become. Created and demolished in the same phase is
/// allowed (Revit shows it as Temporary); demolished before created is <c>phase_order_invalid</c>.
/// </summary>
public static class PhaseSetPlanner
{
    public const int MaxElements = 2000;
    public const string NoneKeyword = "none";

    public const string StatusReady = "ready";
    public const string StatusUnchanged = "unchanged";
    public const string StatusSkipped = "skipped";
    public const string StatusReadOnly = "read_only";
    public const string StatusOrderInvalid = "phase_order_invalid";
    public const string StatusNotFound = "not_found";
    public const string StatusSet = "set";
    public const string StatusFailed = "failed";
    public const string StatusRolledBack = "rolled_back";

    public const string PhaseNotFound = "phase_not_found";

    /// <summary>A phase by element id or by name (exact, then case-insensitive).</summary>
    public static PhaseInfo? ResolvePhase(string? text, IReadOnlyList<PhaseInfo> phases)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text!.Trim();
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            var byId = phases.FirstOrDefault(p => p.Id == id);
            if (byId != null) return byId;
        }
        return phases.FirstOrDefault(p => string.Equals(p.Name, trimmed, StringComparison.Ordinal))
               ?? phases.FirstOrDefault(p => string.Equals(p.Name?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses phaseCreated / phaseDemolished. Empty keeps a parameter, <c>none</c> clears the demolished phase.
    /// Returns false with <paramref name="status"/> (<c>phase_not_found</c>, <c>phase_order_invalid</c>,
    /// <c>validation_failed</c>) and a message when the request cannot be planned.
    /// </summary>
    public static bool TryParseTargets(
        string? phaseCreated, string? phaseDemolished, IReadOnlyList<PhaseInfo> phases,
        out PhaseTarget created, out PhaseTarget demolished, out string? status, out string? error)
    {
        created = PhaseTarget.Keep;
        demolished = PhaseTarget.Keep;
        status = null;
        error = null;

        if (!string.IsNullOrWhiteSpace(phaseCreated))
        {
            if (IsNone(phaseCreated))
            {
                status = "validation_failed";
                error = "phaseCreated cannot be 'none': every element is created in some phase. Name a phase.";
                return false;
            }
            var phase = ResolvePhase(phaseCreated, phases);
            if (phase == null)
            {
                status = PhaseNotFound;
                error = $"Phase '{phaseCreated!.Trim()}' (phaseCreated) was not found. Phases: {Available(phases)}.";
                return false;
            }
            created = new PhaseTarget { Kind = PhaseTargetKind.Set, Phase = phase };
        }

        if (!string.IsNullOrWhiteSpace(phaseDemolished))
        {
            if (IsNone(phaseDemolished))
            {
                demolished = new PhaseTarget { Kind = PhaseTargetKind.Clear };
            }
            else
            {
                var phase = ResolvePhase(phaseDemolished, phases);
                if (phase == null)
                {
                    status = PhaseNotFound;
                    error = $"Phase '{phaseDemolished!.Trim()}' (phaseDemolished) was not found. Phases: {Available(phases)}, or none.";
                    return false;
                }
                demolished = new PhaseTarget { Kind = PhaseTargetKind.Set, Phase = phase };
            }
        }

        if (created.Kind == PhaseTargetKind.Keep && demolished.Kind == PhaseTargetKind.Keep)
        {
            status = "validation_failed";
            error = "Nothing to set: give phaseCreated, phaseDemolished (a phase or none), or both.";
            return false;
        }

        if (created.Phase != null && demolished.Phase != null && demolished.Phase.Sequence < created.Phase.Sequence)
        {
            status = StatusOrderInvalid;
            error = $"phaseDemolished '{demolished.Phase.Name}' comes before phaseCreated '{created.Phase.Name}' in the phase sequence.";
            return false;
        }
        return true;
    }

    public static bool IsNone(string? text) =>
        text != null && (string.Equals(text.Trim(), NoneKeyword, StringComparison.OrdinalIgnoreCase) || text.Trim() == "-");

    /// <summary>"Existing (1), New Construction (2)" for messages and the phase_not_found data.</summary>
    public static string Available(IReadOnlyList<PhaseInfo> phases) =>
        phases.Count == 0 ? "(none)" : string.Join(", ", phases.OrderBy(p => p.Sequence).Select(p => $"{p.Name} ({p.Sequence})"));

    public static List<PhaseSetRow> Plan(IReadOnlyList<PhaseElementState> elements, PhaseTarget created, PhaseTarget demolished)
    {
        var rows = new List<PhaseSetRow>(elements.Count);
        foreach (var e in elements)
            rows.Add(PlanOne(e, created, demolished));
        return rows;
    }

    public static PhaseSetRow PlanOne(PhaseElementState e, PhaseTarget created, PhaseTarget demolished)
    {
        var row = new PhaseSetRow
        {
            Id = e.Id,
            Category = e.Category,
            Name = e.Name,
            CreatedBefore = e.Created?.Name,
            DemolishedBefore = e.Demolished?.Name
        };

        if (!e.Exists)
        {
            row.Status = StatusNotFound;
            row.Reason = "The element does not exist in this document.";
            return row;
        }
        if (!e.HasPhaseParameters)
        {
            row.Status = StatusSkipped;
            row.Reason = "The element has no phase parameters (annotation, view, type or another element Revit does not phase).";
            return row;
        }

        var setCreated = created.Kind == PhaseTargetKind.Set;
        var setDemolished = demolished.Kind != PhaseTargetKind.Keep;
        var newCreated = setCreated ? created.Phase : e.Created;
        var newDemolished = demolished.Kind switch
        {
            PhaseTargetKind.Set => demolished.Phase,
            PhaseTargetKind.Clear => null,
            _ => e.Demolished
        };
        row.CreatedAfter = newCreated?.Name;
        row.DemolishedAfter = newDemolished?.Name;

        var createdChanges = setCreated && newCreated?.Id != e.Created?.Id;
        var demolishedChanges = setDemolished && newDemolished?.Id != e.Demolished?.Id;

        if ((createdChanges && e.CreatedReadOnly) || (demolishedChanges && e.DemolishedReadOnly))
        {
            row.Status = StatusReadOnly;
            row.Reason = e.ReadOnlyReason ??
                         $"{(createdChanges && e.CreatedReadOnly ? "Phase Created" : "Phase Demolished")} is read-only on this element.";
            return row;
        }

        if (newCreated != null && newDemolished != null && newDemolished.Sequence < newCreated.Sequence)
        {
            row.Status = StatusOrderInvalid;
            row.Reason = $"It would be demolished in '{newDemolished.Name}' ({newDemolished.Sequence}) before it is created in " +
                         $"'{newCreated.Name}' ({newCreated.Sequence}).";
            return row;
        }

        if (!createdChanges && !demolishedChanges)
        {
            row.Status = StatusUnchanged;
            row.Reason = "Already in the requested phases.";
            return row;
        }

        row.Status = StatusReady;
        row.SetCreated = createdChanges;
        row.NewCreatedId = createdChanges ? newCreated?.Id : null;
        row.SetDemolished = demolishedChanges;
        row.NewDemolishedId = demolishedChanges ? newDemolished?.Id : null;
        // Moving the created phase past the current demolished one is only valid once demolished has moved too.
        row.DemolishedFirst = createdChanges && demolishedChanges && e.Demolished != null && newCreated != null &&
                              newCreated.Sequence > e.Demolished.Sequence;
        return row;
    }

    /// <summary>True when an atomic request must be refused before a transaction opens.</summary>
    public static bool ShouldReject(bool atomic, IEnumerable<PhaseSetRow> rows) => atomic && rows.Any(r => r.IsBlocking);
}
