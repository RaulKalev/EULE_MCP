namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Pure-logic loop classification for fire alarm circuits.
/// No Revit API dependency — safe to unit-test directly.
/// </summary>
public static class FireAlarmLoopClassifier
{
    /// <summary>
    /// Loop name given to devices whose loop parameter is empty. Such a group is not a real
    /// loop or line, so it is never classified by its device types.
    /// </summary>
    public const string NoLoopName = "(No Loop)";

    /// <summary>
    /// Classifies a fire alarm loop/line based on its name and the device types it contains.
    /// </summary>
    /// <param name="loopName">The loop/line name (Ahela nr. value).</param>
    /// <param name="deviceTypes">Device type strings for all devices on this loop.</param>
    /// <returns>
    /// "Unknown"                 — the <see cref="NoLoopName"/> bucket (devices without a loop).<br/>
    /// "ConventionalSounderLine" — loop name contains '#', or devices include sirens/flashers.<br/>
    /// "ModuleLoop"              — devices include I/O module keywords.<br/>
    /// "AddressableLoop"         — loop name contains "Ahel" (case-insensitive).<br/>
    /// "Unknown"                 — none of the above matched.
    /// </returns>
    public static string Classify(string loopName, IEnumerable<string> deviceTypes)
    {
        if (IsNoLoop(loopName))
            return "Unknown";

        if (loopName.Contains('#'))
            return "ConventionalSounderLine";

        var types = deviceTypes.Select(t => t.ToLowerInvariant()).ToList();

        if (types.Any(IsSounderLower))
            return "ConventionalSounderLine";

        if (types.Any(t => t.Contains("moodul") || t.Contains("sisend") || t.Contains("väljund")
                        || t.Contains("sim") || t.Contains("som")))
            return "ModuleLoop";

        if (loopName.Contains("Ahel", StringComparison.OrdinalIgnoreCase))
            return "AddressableLoop";

        return "Unknown";
    }

    /// <summary>True for the bucket of devices that have no loop value.</summary>
    public static bool IsNoLoop(string? loopName) =>
        string.IsNullOrWhiteSpace(loopName) || string.Equals(loopName, NoLoopName, StringComparison.Ordinal);

    /// <summary>
    /// True when a device type names a sounder or flasher (sireen, vilkur, alarmseade). Only these
    /// devices draw the sounder current on a conventional sounder line; detectors and call points do not.
    /// </summary>
    public static bool IsSounder(string? deviceType) =>
        !string.IsNullOrWhiteSpace(deviceType) && IsSounderLower(deviceType!.ToLowerInvariant());

    /// <summary>Number of device types in <paramref name="deviceTypes"/> that are sounders.</summary>
    public static int CountSounders(IEnumerable<string?> deviceTypes) => deviceTypes.Count(IsSounder);

    private static bool IsSounderLower(string t) =>
        t.Contains("sireen") || t.Contains("vilkur") || t.Contains("alarmseade");
}
