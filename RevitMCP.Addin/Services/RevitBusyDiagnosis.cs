namespace RevitMCP.Addin.Services;

/// <summary>
/// What the connector knows about the Revit API thread without being on it. Captured from the pipe
/// thread when a request has to wait, so it must never require a Revit API context.
/// </summary>
public sealed class RevitBusySnapshot
{
    public DateTime NowUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Last time the Revit API thread ran our ExternalEvent (null = never since start).</summary>
    public DateTime? LastExecuteUtc { get; set; }

    /// <summary>Tool currently executing on the Revit API thread, if any.</summary>
    public string? ActiveTool { get; set; }
    public DateTime? ActiveSinceUtc { get; set; }

    /// <summary>Requests waiting for the Revit API thread.</summary>
    public int QueueLength { get; set; }

    /// <summary>False when Revit's main window is disabled, which is what a modal dialog does. Null = unknown.</summary>
    public bool? MainWindowEnabled { get; set; }

    /// <summary>Title of the visible dialog owned by Revit's main window, when one was found.</summary>
    public string? ModalDialogTitle { get; set; }

    /// <summary>False when Revit's main window does not answer window messages (UI thread blocked). Null = unknown.</summary>
    public bool? MainWindowResponding { get; set; }

    public double? SecondsSinceLastExecute =>
        LastExecuteUtc.HasValue ? Math.Max(0, (NowUtc - LastExecuteUtc.Value).TotalSeconds) : null;

    public double? ActiveSeconds =>
        ActiveSinceUtc.HasValue ? Math.Max(0, (NowUtc - ActiveSinceUtc.Value).TotalSeconds) : null;
}

/// <summary>
/// Turns a <see cref="RevitBusySnapshot"/> into a short, specific reason for why Revit is not taking
/// requests (#87), so callers get "a modal dialog is open" instead of a bare timeout.
/// </summary>
public static class RevitBusyDiagnosis
{
    public const string BusyStatus = "revit_busy";

    /// <summary>Machine-readable cause: modal_dialog | executing_tool | ui_not_responding | not_idle | unknown.</summary>
    public static string Cause(RevitBusySnapshot s)
    {
        if (s.MainWindowEnabled == false || !string.IsNullOrWhiteSpace(s.ModalDialogTitle)) return "modal_dialog";
        if (!string.IsNullOrEmpty(s.ActiveTool)) return "executing_tool";
        if (s.MainWindowResponding == false) return "ui_not_responding";
        if (s.QueueLength > 0) return "not_idle";
        return "unknown";
    }

    public static string Describe(RevitBusySnapshot s)
    {
        var reason = Cause(s) switch
        {
            "modal_dialog" => string.IsNullOrWhiteSpace(s.ModalDialogTitle)
                ? "a modal dialog is open in Revit"
                : $"a modal dialog is open in Revit: '{s.ModalDialogTitle}'",
            "executing_tool" => $"Revit is still executing {s.ActiveTool}" +
                                (s.ActiveSeconds.HasValue ? $" (running for {Seconds(s.ActiveSeconds.Value)})" : string.Empty),
            "ui_not_responding" => "Revit's main window is not responding (a long operation such as synchronize, load, " +
                                   "regenerate or save is probably running)",
            "not_idle" => "Revit is not idle, so it is not processing add-in requests (an active command or edit mode, " +
                          "such as placing a component or editing a sketch, or a background operation)",
            _ => "Revit did not process the request in time"
        };

        var details = new List<string>();
        if (s.QueueLength > 0)
            details.Add($"{s.QueueLength} request{(s.QueueLength == 1 ? "" : "s")} waiting");
        if (s.SecondsSinceLastExecute.HasValue)
            details.Add($"last processed a request {Seconds(s.SecondsSinceLastExecute.Value)} ago");
        else
            details.Add("has not processed a request since the connector started");

        return $"{Capitalize(reason)}; {string.Join(", ", details)}.";
    }

    public static string Hint(RevitBusySnapshot s) => Cause(s) switch
    {
        "modal_dialog" => "Close the dialog in Revit, then retry.",
        "executing_tool" => "Wait for the running request to finish, then retry.",
        "ui_not_responding" => "Wait for Revit to finish, then retry. revit_get_connection_status answers even while Revit is busy.",
        "not_idle" => "Finish or cancel (Esc) the active Revit command, then retry.",
        _ => "Retry in a moment. revit_get_connection_status answers even while Revit is busy."
    };

    private static string Seconds(double seconds) =>
        seconds >= 120 ? $"{seconds / 60:0} min" : $"{seconds:0} s";

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
