namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Pure-logic loop classification for fire alarm circuits.
/// No Revit API dependency — safe to unit-test directly.
/// </summary>
public static class FireAlarmLoopClassifier
{
    /// <summary>
    /// Classifies a fire alarm loop/line based on its name and the device types it contains.
    /// </summary>
    /// <param name="loopName">The loop/line name (Ahela nr. value).</param>
    /// <param name="deviceTypes">Device type strings for all devices on this loop.</param>
    /// <returns>
    /// "ConventionalSounderLine" — loop name contains '#', or devices include sirens/flashers.<br/>
    /// "ModuleLoop"              — devices include I/O module keywords.<br/>
    /// "AddressableLoop"         — loop name contains "Ahel" (case-insensitive).<br/>
    /// "Unknown"                 — none of the above matched.
    /// </returns>
    public static string Classify(string loopName, IEnumerable<string> deviceTypes)
    {
        if (loopName.Contains('#'))
            return "ConventionalSounderLine";

        var types = deviceTypes.Select(t => t.ToLowerInvariant()).ToList();

        if (types.Any(t => t.Contains("sireen") || t.Contains("vilkur") || t.Contains("alarmseade")))
            return "ConventionalSounderLine";

        if (types.Any(t => t.Contains("moodul") || t.Contains("sisend") || t.Contains("väljund")
                        || t.Contains("sim") || t.Contains("som")))
            return "ModuleLoop";

        if (loopName.Contains("Ahel", StringComparison.OrdinalIgnoreCase))
            return "AddressableLoop";

        return "Unknown";
    }
}
