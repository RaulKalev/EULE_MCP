using System.IO;
using System.Text.Json;
using Autodesk.Revit.UI.Events;
using RevitMCP.Addin.Configuration;

namespace RevitMCP.Addin.Dialogs;

/// <summary>
/// Revit's <c>DialogBoxShowing</c> handler: while the connector runs a tool or a job step, answers dialogs by the
/// <see cref="DialogRules"/> of the user config, so an unattended open, sync or close does not hang on a dialog.
/// Every dialog seen during connector work is recorded (<see cref="DialogJournal"/>, revit_get_dialog_log) and
/// appended to %LOCALAPPDATA%\RevitMCP\dialogs.jsonl. Dialogs the user meets in their own work are never touched.
/// Some Revit windows (licensing, crash reports, add-in errors) do not raise this event and cannot be answered here.
/// </summary>
public static class DialogAutoAnswer
{
    private static readonly object Gate = new();
    private static DialogRules _cached = DialogRules.Off;
    private static DateTime _cachedStamp = DateTime.MinValue;
    private static string? _cachedPath;

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMCP", "dialogs.jsonl");

    /// <summary>The current rules from the user config; <see cref="DialogRules.Off"/> when absent or unreadable.</summary>
    public static DialogRules Current
    {
        get
        {
            try
            {
                var (path, _) = ConfigPathResolver.Resolve(ConfigPathResolver.ScopeUser);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return DialogRules.Off;
                var stamp = File.GetLastWriteTimeUtc(path);
                lock (Gate)
                {
                    if (path == _cachedPath && stamp == _cachedStamp) return _cached;
                    var (config, _) = new JsonConfigService().Read(path!, createIfMissing: false);
                    _cached = DialogRules.FromConfig(config);
                    _cachedStamp = stamp;
                    _cachedPath = path;
                    return _cached;
                }
            }
            catch
            {
                return DialogRules.Off;
            }
        }
    }

    /// <summary>Subscribed in App.OnStartup to <c>UIControlledApplication.DialogBoxShowing</c>.</summary>
    public static void OnDialogBoxShowing(object? sender, DialogBoxShowingEventArgs e)
    {
        try
        {
            var inWork = DialogJournal.InConnectorWork;
            if (!inWork) return;   // the user's own dialogs: never touched, never logged

            var dialogId = e.DialogId ?? string.Empty;
            var decision = Current.Decide(dialogId, inWork);
            var accepted = false;
            if (decision.Action != DialogAction.Show)
            {
                try { accepted = e.OverrideResult(decision.Result); }
                catch { accepted = false; }
            }

            var (tool, requestId, client) = DialogJournal.Context;
            var record = new DialogRecord
            {
                Time = DateTimeOffset.Now,
                DialogId = dialogId,
                Kind = e switch
                {
                    TaskDialogShowingEventArgs => "TaskDialog",
                    MessageBoxShowingEventArgs => "MessageBox",
                    _ => "Dialog"
                },
                Message = e switch
                {
                    TaskDialogShowingEventArgs t => t.Message ?? string.Empty,
                    MessageBoxShowingEventArgs m => m.Message ?? string.Empty,
                    _ => string.Empty
                },
                Action = decision.Action.ToString(),
                Result = decision.Action == DialogAction.Show ? null : decision.Result,
                Accepted = accepted,
                Reason = decision.Reason,
                Tool = tool,
                RequestId = requestId,
                Client = client
            };
            DialogJournal.Record(record);
            AppendToLog(record);
        }
        catch
        {
            // A failure here must never break Revit's dialog handling.
        }
    }

    private static void AppendToLog(DialogRecord record)
    {
        var line = JsonSerializer.Serialize(record);
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                lock (Gate) File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch
            {
            }
        });
    }
}
