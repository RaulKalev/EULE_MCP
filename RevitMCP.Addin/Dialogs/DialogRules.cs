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
/// Auto-answers for Revit dialogs that pop up while the connector runs a tool (opening a model with missing links,
/// a missing third-party updater, …). Unattended work otherwise stops at the first dialog. Read from <c>dialogs</c>
/// in the per-user config:
/// <code>
/// "dialogs": {
///   "autoAnswer": true,
///   "unknown": "show",                       // or "cancel"
///   "rules": { "TaskDialog_Unresolved_References": 1002, "Some_Dialog_Id": "show" }
/// }
/// </code>
/// A rule maps a Revit dialog id to the result code Revit should get (1 OK, 2 Cancel, 6 Yes, 7 No, 8 Close,
/// 1001–1004 the first to fourth command link of a task dialog), or to "show" to leave it to the user. The built-in
/// <see cref="Defaults"/> apply unless a rule overrides them. Dialogs outside connector work are never touched.
/// The ids and answers of every dialog seen during connector work are logged (revit_get_dialog_log), which is how a
/// new rule is found. Pure logic, no Revit API.
/// </summary>
public sealed class DialogRules
{
    public const string Key = "dialogs";
    public const int Ok = 1, Cancel = 2, Yes = 6, No = 7, Close = 8;
    public const int CommandLink1 = 1001, CommandLink2 = 1002, CommandLink3 = 1003, CommandLink4 = 1004;
    private const int ShowMarker = -1;

    /// <summary>
    /// Answers that keep a model opening unattended. Verify in the dialog log on the first run: dialog ids and the
    /// order of command links can differ between Revit versions and languages.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> Defaults = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        // "Revit could not find or read N references" → "Ignore and continue opening the project".
        ["TaskDialog_Unresolved_References"] = CommandLink2,
        // A model saved with an add-in's updater that is not installed → "Continue working with the file".
        ["TaskDialog_Missing_Third_Party_Updater"] = CommandLink1,
        ["TaskDialog_Missing_Third_Party_Updaters"] = CommandLink1
    };

    public static readonly DialogRules Off = new(false, false, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), Array.Empty<string>());

    public bool AutoAnswer { get; }
    public bool CancelUnknown { get; }
    /// <summary>Effective rules: defaults merged with the config's; "show" entries are kept as <see cref="ShowMarker"/>.</summary>
    public IReadOnlyDictionary<string, int> Rules { get; }
    public IReadOnlyList<string> Problems { get; }

    private DialogRules(bool autoAnswer, bool cancelUnknown, IReadOnlyDictionary<string, int> rules, IReadOnlyList<string> problems)
    {
        AutoAnswer = autoAnswer;
        CancelUnknown = cancelUnknown;
        Rules = rules;
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

        var rules = new Dictionary<string, int>(Defaults, StringComparer.OrdinalIgnoreCase);
        if (dialogs["rules"] is JsonObject configured)
        {
            foreach (var pair in configured)
            {
                var id = pair.Key.Trim();
                if (id.Length == 0) continue;
                if (pair.Value is JsonValue v && v.TryGetValue<int>(out var code) && code > 0)
                    rules[id] = code;
                else if (pair.Value is JsonValue s && s.TryGetValue<string>(out var text) &&
                         string.Equals(text, "show", StringComparison.OrdinalIgnoreCase))
                    rules[id] = ShowMarker;
                else
                    problems.Add($"dialogs.rules.{id} must be a positive result code or \"show\"; ignored.");
            }
        }

        return new DialogRules(autoAnswer, cancelUnknown, rules, problems);
    }

    /// <summary>What to do with a dialog; <paramref name="duringConnectorWork"/> is true while a tool or job step runs.</summary>
    public DialogDecision Decide(string? dialogId, bool duringConnectorWork)
    {
        if (!duringConnectorWork) return new(DialogAction.Show, 0, "Not during connector work.");
        if (!AutoAnswer) return new(DialogAction.Show, 0, "Auto-answer is off (dialogs.autoAnswer).");
        var id = dialogId ?? string.Empty;
        if (Rules.TryGetValue(id, out var code))
        {
            return code == ShowMarker
                ? new(DialogAction.Show, 0, $"Rule for '{id}' says show.")
                : new(DialogAction.Answer, code, $"Rule for '{id}' answers {code} ({Describe(code)}).");
        }
        return CancelUnknown
            ? new(DialogAction.Cancel, Cancel, $"No rule for '{id}'; dismissed with Cancel (dialogs.unknown=cancel).")
            : new(DialogAction.Show, 0, $"No rule for '{id}'; left for the user (dialogs.unknown=show).");
    }

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

/// <summary>One dialog seen while the connector was working.</summary>
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
