using System.Text.Json.Nodes;

namespace RevitMCP.Addin.Approval;

/// <summary>
/// Worker mode pre-approvals: the document operations an unattended agent needs to open a model, work in it, sync and
/// close it without anyone clicking Approve. Read from <c>approval.worker</c> in the per-user config only (like
/// <see cref="DirectEditDefault"/>): a company or project config cannot switch approvals off.
/// <code>
/// "approval": { "worker": {
///   "enabled": true,
///   "operations": ["open", "sync", "close", "relinquish", "reloadLatest", "checkout"],
///   "folders": ["P:\\Projektid"]
/// } }
/// </code>
/// Nothing is pre-approved unless <c>enabled</c> is true AND at least one folder is listed. Every pre-approval is
/// limited to models whose file (or, for a workshared local, its central) lies in one of those folders. Operations
/// on an existing document additionally need it to have been opened by the connector in this Revit session
/// (<see cref="WorkerDocumentRegistry"/>), so the user's own documents are never synced or closed without a click.
/// Pure logic, no Revit API, so it is unit-tested.
/// </summary>
public sealed class WorkerPolicy
{
    public const string Key = "worker";

    public const string OpOpen = "open";
    public const string OpSync = "sync";
    public const string OpClose = "close";
    public const string OpRelinquish = "relinquish";
    public const string OpReloadLatest = "reloadLatest";
    public const string OpCheckout = "checkout";

    /// <summary>Operation names accepted in <c>operations</c>; matching is case-insensitive.</summary>
    public static readonly IReadOnlyList<string> KnownOperations =
        new[] { OpOpen, OpSync, OpClose, OpRelinquish, OpReloadLatest, OpCheckout };

    /// <summary>Tool name → operation. Only these tools can ever be pre-approved.</summary>
    public static readonly IReadOnlyDictionary<string, string> ToolOperations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["revit_open_document"] = OpOpen,
        ["revit_sync_with_central"] = OpSync,
        ["revit_close_document"] = OpClose,
        ["revit_relinquish"] = OpRelinquish,
        ["revit_reload_latest"] = OpReloadLatest,
        ["revit_checkout_elements"] = OpCheckout
    };

    public static readonly WorkerPolicy Disabled = new(false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    public bool Enabled { get; }
    public IReadOnlyList<string> Operations { get; }
    public IReadOnlyList<string> Folders { get; }
    /// <summary>Problems found while reading the config (unknown operations, bad folders); shown in the status.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>True when the policy can pre-approve anything at all.</summary>
    public bool IsActive => Enabled && Operations.Count > 0 && Folders.Count > 0;

    private WorkerPolicy(bool enabled, IReadOnlyList<string> operations, IReadOnlyList<string> folders, IReadOnlyList<string> problems)
    {
        Enabled = enabled;
        Operations = operations;
        Folders = folders;
        Problems = problems;
    }

    /// <summary>Reads <c>approval.worker</c> from a user config. Missing or malformed → <see cref="Disabled"/>.</summary>
    public static WorkerPolicy FromConfig(JsonObject? config)
    {
        if (config?["approval"] is not JsonObject approval) return Disabled;
        if (approval[Key] is not JsonObject worker) return Disabled;

        var problems = new List<string>();
        var enabled = worker["enabled"] is JsonValue e && e.TryGetValue<bool>(out var on) && on;

        var operations = new List<string>();
        if (worker["operations"] is JsonArray ops)
        {
            foreach (var node in ops)
            {
                var name = node is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : string.Empty;
                var known = KnownOperations.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
                if (known == null) problems.Add($"Unknown worker operation '{name}' (known: {string.Join(", ", KnownOperations)}).");
                else if (!operations.Contains(known)) operations.Add(known);
            }
        }

        var folders = new List<string>();
        if (worker["folders"] is JsonArray dirs)
        {
            foreach (var node in dirs)
            {
                var raw = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;
                var folder = NormalizeFolder(raw);
                if (folder.Length < 3) problems.Add($"Worker folder '{raw}' is not a usable absolute folder; ignored.");
                else if (!folders.Contains(folder)) folders.Add(folder);
            }
        }

        if (enabled && folders.Count == 0) problems.Add("Worker mode is enabled but no folders are listed, so nothing is pre-approved.");
        return new WorkerPolicy(enabled, operations, folders, problems);
    }

    /// <summary>Facts about one call that the policy decides on, gathered by <see cref="WorkerPreapproval"/>.</summary>
    public sealed class Call
    {
        public string ToolName { get; init; } = string.Empty;
        /// <summary>revit_open_document: the path passed. Other tools: the target document's file path.</summary>
        public string? Path { get; init; }
        /// <summary>Central path of a workshared target, else empty.</summary>
        public string? CentralPath { get; init; }
        /// <summary>The target was opened by the connector in this session (ignored for open).</summary>
        public bool OpenedByConnector { get; init; }
        /// <summary>The call targets every open document (<c>all=true</c>).</summary>
        public bool AllDocuments { get; init; }
        /// <summary>revit_open_document with activate=true (takes over the user's window).</summary>
        public bool Activate { get; init; }
        /// <summary>revit_close_document: save=true.</summary>
        public bool Save { get; init; }
        /// <summary>The target has unsaved changes.</summary>
        public bool IsModified { get; init; }
    }

    /// <summary>
    /// Whether the call runs without a prompt. <paramref name="reason"/> explains a refusal (null when the tool is not a
    /// worker operation at all, so callers need not mention worker mode) or names the rule that approved it.
    /// </summary>
    public bool Covers(Call call, out string? reason)
    {
        reason = null;
        if (!ToolOperations.TryGetValue(call.ToolName, out var op)) return false;
        if (!IsActive) return false;
        if (!Operations.Contains(op))
        {
            reason = $"Worker mode does not pre-approve '{op}' (approval.worker.operations).";
            return false;
        }
        if (call.AllDocuments)
        {
            reason = "Worker mode never pre-approves all=true; name the document.";
            return false;
        }
        if (op == OpOpen && call.Activate)
        {
            reason = "Worker mode pre-approves opening only without activating (activate=false).";
            return false;
        }
        if (op != OpOpen && !call.OpenedByConnector)
        {
            reason = "Worker mode only pre-approves documents the connector opened in this Revit session.";
            return false;
        }
        if (op == OpClose && call.IsModified && !call.Save)
        {
            reason = "Worker mode never pre-approves closing with unsaved changes; pass save=true.";
            return false;
        }

        var folder = MatchingFolder(call.Path) ?? MatchingFolder(call.CentralPath);
        if (folder == null)
        {
            reason = "The model is outside the worker folders (approval.worker.folders).";
            return false;
        }

        reason = $"Pre-approved by worker mode ('{op}' in {folder}).";
        return true;
    }

    /// <summary>The configured folder that contains <paramref name="path"/>, or null.</summary>
    public string? MatchingFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = NormalizePath(path!);
        if (normalized.Contains("\\..\\") || normalized.EndsWith("\\..")) return null;
        foreach (var folder in Folders)
            if (normalized.StartsWith(folder + "\\", StringComparison.Ordinal)) return folder;
        return null;
    }

    public static string NormalizePath(string path) =>
        path.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();

    private static string NormalizeFolder(string folder)
    {
        var normalized = NormalizePath(folder ?? string.Empty);
        if (normalized.Contains("\\..")) return string.Empty;
        var rooted = normalized.StartsWith("\\\\") || (normalized.Length >= 2 && normalized[1] == ':');
        return rooted ? normalized : string.Empty;
    }
}

/// <summary>
/// Documents the connector opened in this Revit session (by normalized file path). Worker mode only pre-approves
/// sync, close, relinquish, reload latest and checkout on these. Cleared when Revit restarts.
/// </summary>
public static class WorkerDocumentRegistry
{
    private static readonly HashSet<string> Paths = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static void Add(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (Gate) Paths.Add(WorkerPolicy.NormalizePath(path!));
    }

    public static void Remove(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (Gate) Paths.Remove(WorkerPolicy.NormalizePath(path!));
    }

    public static bool Contains(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        lock (Gate) return Paths.Contains(WorkerPolicy.NormalizePath(path!));
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate) return Paths.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>For tests.</summary>
    internal static void Clear()
    {
        lock (Gate) Paths.Clear();
    }
}
