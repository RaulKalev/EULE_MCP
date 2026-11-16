using Newtonsoft.Json;

namespace RevitMCP.Addin.LinkOperations;

/// <summary>
/// States of a link operation (#163). Plain strings so the durable file, the bridge's status answer and the tool
/// results all read the same. <c>outcome_unknown</c> is deliberately neither active nor finished: it blocks a new
/// request for the same source and only a verification against the live document (late completion, or
/// <c>revit_reconcile_link_operation</c>) can move it on. Pure.
/// </summary>
public static class LinkOperationStates
{
    public const string Queued = "queued";
    public const string Starting = "starting";
    public const string Converting = "converting";
    public const string Verifying = "verifying";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string OutcomeUnknown = "outcome_unknown";
    public const string Cancelled = "cancelled";

    public static readonly string[] All =
        { Queued, Starting, Converting, Verifying, Completed, Failed, OutcomeUnknown, Cancelled };

    /// <summary>Queued, or running in some step: the operation holds the lock for its source.</summary>
    public static bool IsActive(string state) =>
        state == Queued || state == Starting || state == Converting || state == Verifying;

    /// <summary>Nothing more will happen to this operation.</summary>
    public static bool IsTerminal(string state) =>
        state == Completed || state == Failed || state == Cancelled;

    /// <summary>Revit may be working on it right now (not merely waiting in the queue).</summary>
    public static bool IsRunning(string state) =>
        state == Starting || state == Converting || state == Verifying;

    /// <summary>A new request for the same source and document must not start while this is true.</summary>
    public static bool BlocksNewRequests(string state) => IsActive(state) || state == OutcomeUnknown;
}

public static class LinkOperationTypes
{
    public const string Link = "link";
    public const string Refresh = "refresh";
}

public static class LinkKinds
{
    public const string Ifc = "ifc";
    public const string Rvt = "rvt";
}

/// <summary>Operation ids: <c>lop-</c> and 12 hex digits. Used in file names, so only this shape is accepted.</summary>
public static class LinkOperationIds
{
    public const string Prefix = "lop-";

    public static string New() => Prefix + Guid.NewGuid().ToString("N").Substring(0, 12);

    public static bool IsValid(string? id)
    {
        if (id == null || id.Length != Prefix.Length + 12 || !id.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        for (var i = Prefix.Length; i < id.Length; i++)
        {
            var c = id[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
        return true;
    }
}

public sealed class LinkOperationEvent
{
    public DateTimeOffset At { get; set; }
    public string State { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}

/// <summary>What is known about the IFC conversion cache (<c>.ifc.RVT</c>) of an operation.</summary>
public sealed class LinkCacheFacts
{
    public string Path { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public long Bytes { get; set; }
    public DateTimeOffset? LastWriteUtc { get; set; }
    /// <summary>True when the cache is newer than the IFC, so Revit reuses it instead of converting again.</summary>
    public bool Fresh { get; set; }
}

public sealed class LinkOperationResult
{
    public long? LinkTypeId { get; set; }
    public List<long> InstanceIds { get; set; } = new();
    /// <summary>The path Revit reports for the link type after the operation.</summary>
    public string? LinkPath { get; set; }
    public string? LinkStatus { get; set; }
    public int? LinkedElementCount { get; set; }
    public bool Verified { get; set; }
    public List<string> Checks { get; set; } = new();
    public LinkCacheFacts? Cache { get; set; }
    public List<string> Notes { get; set; } = new();
}

/// <summary>
/// One IFC / RVT link request and everything the connector knows about it, rewritten after every change in
/// <c>%LOCALAPPDATA%\RevitMCP\LinkOperations\&lt;id&gt;.json</c>. It is written BEFORE Revit starts the conversion, so
/// the bridge can answer status from the file while the Revit API thread is blocked, and a Revit restart leaves a
/// record to reconcile. Pure.
/// </summary>
public sealed class LinkOperation
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    /// <summary>link (a new link) or refresh (an existing link through the reload path).</summary>
    public string Type { get; set; } = LinkOperationTypes.Link;
    public string Kind { get; set; } = LinkKinds.Ifc;
    public string SourcePath { get; set; } = string.Empty;
    /// <summary>The lock key's source part: <see cref="LinkSourcePath.Canonical"/> of the path.</summary>
    public string CanonicalSource { get; set; } = string.Empty;
    public string CachePath { get; set; } = string.Empty;
    /// <summary>The lock key's document part: <see cref="LinkSourcePath.TargetKey"/>.</summary>
    public string TargetKey { get; set; } = string.Empty;
    public string TargetTitle { get; set; } = string.Empty;
    /// <summary>What the operation passes back to Revit to find the document: its path, else its title.</summary>
    public string TargetSelector { get; set; } = string.Empty;
    /// <summary>origin or shared.</summary>
    public string Positioning { get; set; } = "origin";
    public bool Pin { get; set; }
    public bool RegenerateCache { get; set; }
    /// <summary>For a refresh: the type id of the link being refreshed.</summary>
    public long? ExistingLinkTypeId { get; set; }

    public string State { get; set; } = LinkOperationStates.Queued;
    public string Message { get; set; } = string.Empty;
    /// <summary>What the running step is doing, in words; never a percentage.</summary>
    public string Phase { get; set; } = string.Empty;

    public string RequestedBy { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset StateChangedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>Set once the blocking Revit call is about to start. After this a "not started" outcome is impossible.</summary>
    public DateTimeOffset? ConversionStartedAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }

    /// <summary>The Revit process and add-in load that own the operation.</summary>
    public int ProcessId { get; set; }
    public string SessionId { get; set; } = string.Empty;

    public bool CancelRequested { get; set; }
    public DateTimeOffset? CancelRequestedAt { get; set; }

    /// <summary>Hands one dispatch to the run tool, which refuses any other.</summary>
    public string DispatchToken { get; set; } = string.Empty;
    /// <summary>How often the dispatch was refused before Revit started it (busy). Never counts conversion attempts.</summary>
    public int DispatchAttempts { get; set; }
    public DateTimeOffset? NotBefore { get; set; }

    /// <summary>True when only a person can say what happened.</summary>
    public bool NeedsReview { get; set; }
    public string? ReviewHint { get; set; }
    /// <summary>How an outcome_unknown was settled: late_completion | reconcile.</summary>
    public string? ResolvedBy { get; set; }

    public LinkOperationResult Result { get; set; } = new();
    public List<LinkOperationEvent> History { get; set; } = new();

    [JsonIgnore]
    public bool IsOpen => LinkOperationStates.BlocksNewRequests(State);

    /// <summary>Time spent so far (or in total once it ended), measured, never estimated.</summary>
    public TimeSpan Elapsed(DateTimeOffset now)
    {
        var from = StartedAt ?? CreatedAt;
        var to = FinishedAt ?? now;
        return to > from ? to - from : TimeSpan.Zero;
    }
}
