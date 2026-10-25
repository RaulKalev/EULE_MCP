using System.Globalization;
using System.Security.Cryptography;

namespace RevitMCP.Addin.Plans;

public enum PlanState
{
    Proposed,
    Active,
    Exhausted,
    Expired,
    Revoked,
    Closed
}

/// <summary>
/// What an approved plan may do without further prompts (#99). Every limit is enforced mechanically: before a
/// call from its arguments, after it from the ids Revit reports as changed.
/// </summary>
public sealed class PlanEnvelope
{
    public const int MaxElementIds = 20_000;
    public const int MaxExpiresMinutes = 480;

    /// <summary>Write tools the plan may run without a prompt. Never a DestructiveRequiresManualApproval tool.</summary>
    public List<string> Tools { get; set; } = new();

    /// <summary>Explicit allow list; empty = not restricted by id.</summary>
    public HashSet<long> ElementIds { get; set; } = new();

    /// <summary>Allowed categories of touched elements (display or OST_ names); empty = not restricted.</summary>
    public HashSet<string> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<long> LevelIds { get; set; } = new();

    /// <summary>View-specific work (tags, text, detail items) only in these views; empty = any view.</summary>
    public HashSet<long> ViewIds { get; set; } = new();

    public HashSet<string> Worksets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool AllowCreate { get; set; } = true;
    public bool AllowModify { get; set; } = true;
    public int MaxElementsPerCall { get; set; } = 500;
    public int MaxElementsTotal { get; set; } = 2000;
    public int MaxCalls { get; set; } = 100;
    public int ExpiresMinutes { get; set; } = 120;

    /// <summary>False: incidental categories (tags, dimensions, wires, circuits…) changed as a side effect do not count.</summary>
    public bool Strict { get; set; }
}

public sealed class PlanViolation
{
    public DateTimeOffset At { get; set; }
    public string Tool { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public List<long> Ids { get; set; } = new();

    /// <summary>"fallback" (the call waited for approval) or "violated" (the call ran and was rolled back).</summary>
    public string Kind { get; set; } = "violated";
}

/// <summary>One plan: intent, envelope, binding to a document and its counters. Pure — no Revit API.</summary>
public sealed class Plan
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<string> Steps { get; set; } = new();
    public PlanEnvelope Envelope { get; set; } = new();
    public string ClientName { get; set; } = string.Empty;

    /// <summary>The Revit document the plan was approved for (typed object so the model stays testable).</summary>
    public object? DocumentToken { get; set; }
    public string DocumentTitle { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string? CheckpointId { get; set; }

    public PlanState State { get; set; } = PlanState.Proposed;
    public string? StateReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    public int Calls { get; set; }
    public HashSet<long> Touched { get; set; } = new();
    public int Added { get; set; }
    public int Modified { get; set; }
    public int Deleted { get; set; }
    public List<PlanViolation> Violations { get; set; } = new();

    public bool IsActive => State == PlanState.Active;
    public bool IsEnded => State is PlanState.Expired or PlanState.Revoked or PlanState.Closed;
}

/// <summary>Plan ids: "plan-" + 6 hex. Pure.</summary>
public static class PlanIds
{
    public const string Prefix = "plan-";

    public static string NewId()
    {
        var bytes = new byte[3];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return Prefix + string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    public static bool IsValid(string? id)
    {
        if (id == null || id.Length != Prefix.Length + 6 || !id.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        for (var i = Prefix.Length; i < id.Length; i++)
        {
            var c = id[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
        return true;
    }
}
