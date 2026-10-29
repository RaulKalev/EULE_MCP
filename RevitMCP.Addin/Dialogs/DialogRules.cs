using System.Text.Json.Nodes;

namespace RevitMCP.Addin.Dialogs;

/// <summary>What the auto-answer handler does with one dialog.</summary>
public enum DialogAction
{
    /// <summary>Not touched: Revit shows it (outside connector work, or no rule and unknown=show).</summary>
    Show,
    /// <summary>Answered with the rule's result code.</summary>
    Answer,
    /// <summary>No rule and unknown=cancel: dismissed with Cancel so the call fails instead of hanging.</summary>
    Cancel
}

public readonly record struct DialogDecision(DialogAction Action, int Result, string Reason);

/// <summary>
/// Auto-answers for Revit dialogs (opening a model with missing links, a missing third-party updater, "Some entities
/// were lost during import", …). Unattended work otherwise stops at the first dialog. Read from <c>dialogs</c> in the
/// per-user config:
/// <code>
/// "dialogs": {
///   "autoAnswer": true,
///   "scope": "connector",                    // or "always": rules also answer dialogs in the user's own work
///   "unknown": "show",                       // or "cancel" (connector work only)
///   "rules": { "TaskDialog_Unresolved_References": 1002, "Some_Dialog_Id": "show" },
///   "messageRules": { "Some entities were lost during import": 8 }
/// }
/// </code>
/// A rule maps a Revit dialog id — or, in <c>messageRules</c>, a piece of the dialog's text (case-insensitive) — to
/// the result code Revit should get (1 OK, 2 Cancel, 6 Yes, 7 No, 8 Close, 1001–1004 the first to fourth command link
/// of a task dialog), or to "show" to leave it to the user. An id rule wins over a message rule. The built-in
/// <see cref="Defaults"/> and <see cref="DefaultMessages"/> apply unless overridden. Pure logic, no Revit API.
/// </summary>
public sealed class DialogRules
{
    public const string Key = "dialogs";
    public const int Ok = 1, Cancel = 2, Yes = 6, No = 7, Close = 8;
    public const int CommandLink1 = 1001, CommandLink2 = 1002, CommandLink3 = 1003, CommandLink4 = 1004;
    private const int ShowMarker = -1;

    /// <summary>
    /// Answers by dialog id that keep a model opening unattended. Verify in the dialog log on the first run: dialog
    /// ids and the order of command links can differ between Revit versions and languages.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> Defaults = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        // "Revit could not find or read N references" → "Ignore and continue opening the project".
        ["TaskDialog_Unresolved_References"] = CommandLink2,
        // A model saved with an add-in's updater that is not installed → "Continue working with the file".
        ["TaskDialog_Missing_Third_Party_Updater"] = CommandLink1,
        ["TaskDialog_Missing_Third_Party_Updaters"] = CommandLink1
    };

    /// <summary>Answers by message text, for informational dialogs whose id is not known in advance.</summary>
    public static readonly IReadOnlyDictionary<string, int> DefaultMessages = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        // DWG/CAD links with entities Revit cannot import; shown once per link when a model opens. Close only.
        ["Some entities were lost during import"] = Close,
        // DWG/CAD links with coordinates or values Revit cannot hold. Close only.
        ["Some numerical data within the imported file was out of range"] = Close
    };

    public static readonly DialogRules Off = new(false, false, false,
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase), Array.Empty<string>());

    /// <summary>Message fragments from the config; they are matched before the built-in ones.</summary>
    private readonly HashSet<string> _configuredMessages;

    public bool AutoAnswer { get; }
    public bool CancelUnknown { get; }
    /// <summary>scope=always: rules also answer dialogs outside connector work (never <c>unknown=cancel</c>).</summary>
    public bool AlwaysScope { get; }
    /// <summary>Effective id rules: defaults merged with the config's; "show" entries are kept as <see cref="ShowMarker"/>.</summary>
    public IReadOnlyDictionary<string, int> Rules { get; }
    /// <summary>Effective message rules (text fragment → result), same convention.</summary>
    public IReadOnlyDictionary<string, int> MessageRules { get; }
    public IReadOnlyList<string> Problems { get; }

    private DialogRules(bool autoAnswer, bool cancelUnknown, bool alwaysScope, IReadOnlyDictionary<string, int> rules,
        IReadOnlyDictionary<string, int> messageRules, HashSet<string> configuredMessages, IReadOnlyList<string> problems)
    {
        _configuredMessages = configuredMessages;
        AutoAnswer = autoAnswer;
        CancelUnknown = cancelUnknown;
        AlwaysScope = alwaysScope;
        Rules = rules;
        MessageRules = messageRules;
        Problems = problems;
    }

    public static DialogRules FromConfig(JsonObject? config)
    {
        if (config?[Key] is not JsonObject dialogs) return Off;
        var problems = new List<string>();
        var autoAnswer = dialogs["autoAnswer"] is JsonValue a && a.TryGetValue<bool>(out var on) && on;

        var cancelUnknown = false;
        if (dialogs["unknown"] is JsonValue u && u.TryGetValue<string>(out var unknown))
        {
            if (string.Equals(unknown, "cancel", StringComparison.OrdinalIgnoreCase)) cancelUnknown = true;
            else if (!string.Equals(unknown, "show", StringComparison.OrdinalIgnoreCase))
                problems.Add($"dialogs.unknown must be \"show\" or \"cancel\", not \"{unknown}\"; using \"show\".");
        }

        var always = false;
        if (dialogs["scope"] is JsonValue sc && sc.TryGetValue<string>(out var scope))
        {
            if (string.Equals(scope, "always", StringComparison.OrdinalIgnoreCase)) always = true;
            else if (!string.Equals(scope, "connector", StringComparison.OrdinalIgnoreCase))
                problems.Add($"dialogs.scope must be \"connector\" or \"always\", not \"{scope}\"; using \"connector\".");
        }

        var rules = new Dictionary<string, int>(Defaults, StringComparer.OrdinalIgnoreCase);
        ReadRules(dialogs["rules"] as JsonObject, rules, "rules", problems);
        var configured = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        ReadRules(dialogs["messageRules"] as JsonObject, configured, "messageRules", problems);
        var messages = new Dictionary<string, int>(DefaultMessages, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in configured) messages[pair.Key] = pair.Value;

        return new DialogRules(autoAnswer, cancelUnknown, always, rules, messages,
            new HashSet<string>(configured.Keys, StringComparer.OrdinalIgnoreCase), problems);
    }

    private static void ReadRules(JsonObject? configured, Dictionary<string, int> target, string name, List<string> problems)
    {
        if (configured == null) return;
        foreach (var pair in configured)
        {
            var key = pair.Key.Trim();
            if (key.Length == 0) continue;
            if (pair.Value is JsonValue v && v.TryGetValue<int>(out var code) && code > 0)
                target[key] = code;
            else if (pair.Value is JsonValue s && s.TryGetValue<string>(out var text) &&
                     string.Equals(text, "show", StringComparison.OrdinalIgnoreCase))
                target[key] = ShowMarker;
            else
                problems.Add($"dialogs.{name}.{key} must be a positive result code or \"show\"; ignored.");
        }
    }

    /// <summary>
    /// What to do with a dialog. <paramref name="duringConnectorWork"/> is true while a tool or job step runs; outside
    /// it only <c>scope=always</c> answers, and only by an explicit rule.
    /// </summary>
    public DialogDecision Decide(string? dialogId, string? message, bool duringConnectorWork)
    {
        if (!AutoAnswer) return new(DialogAction.Show, 0, "Auto-answer is off (dialogs.autoAnswer).");
        if (!duringConnectorWork && !AlwaysScope) return new(DialogAction.Show, 0, "Not during connector work.");

        var id = dialogId ?? string.Empty;
        if (Rules.TryGetValue(id, out var code)) return FromRule(code, $"Rule for '{id}'");

        var text = message ?? string.Empty;
        if (text.Length > 0)
        {
            // A configured fragment beats a built-in one; then the longest wins, so a specific rule beats a general one.
            var match = MessageRules
                .Where(r => text.IndexOf(r.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(r => _configuredMessages.Contains(r.Key))
                .ThenByDescending(r => r.Key.Length)
                .Select(r => (KeyValuePair<string, int>?)r)
                .FirstOrDefault();
            if (match is { } m) return FromRule(m.Value, $"Message rule \"{m.Key}\"");
        }

        if (!duringConnectorWork) return new(DialogAction.Show, 0, $"No rule for '{id}'.");
        return CancelUnknown
            ? new(DialogAction.Cancel, Cancel, $"No rule for '{id}'; dismissed with Cancel (dialogs.unknown=cancel).")
            : new(DialogAction.Show, 0, $"No rule for '{id}'; left for the user (dialogs.unknown=show).");
    }

    /// <summary>Backward-compatible overload without the message.</summary>
    public DialogDecision Decide(string? dialogId, bool duringConnectorWork) => Decide(dialogId, null, duringConnectorWork);

    private static DialogDecision FromRule(int code, string what) => code == ShowMarker
        ? new(DialogAction.Show, 0, $"{what} says show.")
        : new(DialogAction.Answer, code, $"{what} answers {code} ({Describe(code)}).");

    /// <summary>
    /// Results to try in order when Revit refuses an answer: a one-button dialog may take OK where Close was asked,
    /// or the reverse.
    /// </summary>
    public static IReadOnlyList<int> Fallbacks(int code) => code switch
    {
        Close => new[] { Ok, Cancel },
        Ok => new[] { Close },
        _ => Array.Empty<int>()
    };

    public static string Describe(int code) => code switch
    {
        Ok => "OK",
        Cancel => "Cancel",
        Yes => "Yes",
        No => "No",
        Close => "Close",
        >= CommandLink1 and <= CommandLink4 => $"command link {code - 1000}",
        _ => "custom result"
    };
}

/// <summary>One dialog seen while the connector was working, or answered by a scope=always rule.</summary>
public sealed class DialogRecord
{
    public DateTimeOffset Time { get; init; }
    public string DialogId { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public int? Result { get; init; }
    public bool Accepted { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string Tool { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string Client { get; init; } = string.Empty;
    /// <summary>False for a dialog answered in the user's own work (scope=always).</summary>
    public bool DuringConnectorWork { get; init; } = true;

    /// <summary>One line for a tool result's warnings.</summary>
    public string ToWarning()
    {
        var text = Message.Length > 160 ? Message.Substring(0, 157) + "…" : Message;
        var what = Action switch
        {
            nameof(DialogAction.Answer) => Accepted ? $"answered {Result} ({DialogRules.Describe(Result ?? 0)})" : $"answer {Result} was refused by Revit",
            nameof(DialogAction.Cancel) => Accepted ? "dismissed with Cancel" : "Cancel was refused by Revit",
            _ => "shown to the user"
        };
        return $"Revit dialog '{DialogId}' {what}" + (text.Length > 0 ? $": {text}" : ".");
    }
}

/// <summary>
/// Marks connector work on the Revit API thread (a tool or job step running) and collects the dialogs seen during
/// it: a bounded history for revit_get_dialog_log and the current call's dialogs for its warnings.
/// </summary>
public static class DialogJournal
{
    private const int MaxHistory = 200;
    private static readonly object Gate = new();
    private static readonly LinkedList<DialogRecord> History = new();
    private static readonly List<DialogRecord> Current = new();
    private static int _depth;
    private static string _tool = string.Empty, _requestId = string.Empty, _client = string.Empty;

    public static bool InConnectorWork
    {
        get { lock (Gate) return _depth > 0; }
    }

    public static (string Tool, string RequestId, string Client) Context
    {
        get { lock (Gate) return (_tool, _requestId, _client); }
    }

    /// <summary>Starts connector work; dispose to end it. Nested scopes keep the outer context.</summary>
    public static IDisposable Enter(string tool, string requestId, string client)
    {
        lock (Gate)
        {
            if (_depth == 0)
            {
                _tool = tool;
                _requestId = requestId;
                _client = client;
                Current.Clear();
            }
            _depth++;
        }
        return new Scope();
    }

    public static void Record(DialogRecord record)
    {
        lock (Gate)
        {
            History.AddFirst(record);
            while (History.Count > MaxHistory) History.RemoveLast();
            if (_depth > 0) Current.Add(record);
        }
    }

    /// <summary>The dialogs recorded during the current outermost scope, cleared.</summary>
    public static IReadOnlyList<DialogRecord> DrainCurrent()
    {
        lock (Gate)
        {
            var list = Current.ToList();
            Current.Clear();
            return list;
        }
    }

    public static IReadOnlyList<DialogRecord> Recent(int max)
    {
        lock (Gate) return History.Take(Math.Max(1, max)).ToList();
    }

    /// <summary>For tests.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            History.Clear();
            Current.Clear();
            _depth = 0;
        }
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Gate)
            {
                if (_depth > 0) _depth--;
                if (_depth == 0) _tool = _requestId = _client = string.Empty;
            }
        }
    }
}
