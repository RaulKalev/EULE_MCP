namespace RevitMCP.Addin.ModelFamilies;

/// <summary>
/// The wire units of connector property values (#174). A number is in the display unit of its parameter's spec
/// (<see cref="DefaultUnits"/>: volts for a voltage, VA for an apparent load, mm for a length…); <c>{value, unit}</c>
/// gives an explicit unit, which <see cref="ToDefault"/> scales to the display unit. The Revit side then converts the
/// display unit to internal units with UnitUtils. Pure.
/// </summary>
public static class ModelFamilyConnectorUnits
{
    /// <summary>
    /// The spec keys this tool knows (the Revit side maps SpecTypeId members to them) and the display unit of each.
    /// <c>number</c> has no unit.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultUnits = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["length"] = "mm",
        ["electricalPotential"] = "V",
        ["apparentPower"] = "VA",
        ["electricalPower"] = "W",
        ["current"] = "A",
        ["airFlow"] = "L/s",
        ["flow"] = "L/s",
        ["angle"] = "deg",
        ["hvacPressure"] = "Pa",
        ["pipingPressure"] = "Pa",
        ["number"] = string.Empty
    };

    /// <summary>For each spec key, the units a caller may name and how many display units one of them is.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Aliases =
        new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal)
        {
            ["length"] = Units(("mm", 1), ("cm", 10), ("m", 1000)),
            ["electricalPotential"] = Units(("V", 1), ("kV", 1000), ("mV", 0.001)),
            ["apparentPower"] = Units(("VA", 1), ("kVA", 1000)),
            ["electricalPower"] = Units(("W", 1), ("kW", 1000)),
            ["current"] = Units(("A", 1), ("mA", 0.001), ("kA", 1000)),
            ["airFlow"] = Units(("L/s", 1), ("l/s", 1), ("m3/h", 1 / 3.6), ("m³/h", 1 / 3.6), ("m3/s", 1000), ("m³/s", 1000)),
            ["flow"] = Units(("L/s", 1), ("l/s", 1), ("L/min", 1 / 60.0), ("l/min", 1 / 60.0), ("m3/h", 1 / 3.6), ("m³/h", 1 / 3.6)),
            ["angle"] = Units(("deg", 1), ("°", 1), ("rad", 180 / Math.PI)),
            ["hvacPressure"] = Units(("Pa", 1), ("kPa", 1000)),
            ["pipingPressure"] = Units(("Pa", 1), ("kPa", 1000), ("bar", 100000)),
            ["number"] = Units(("", 1))
        };

    private static IReadOnlyDictionary<string, double> Units(params (string Name, double Factor)[] units)
    {
        // Case matters for prefixes (mA and MA), so exact matches win; a case-insensitive match is the fallback in ToDefault.
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, factor) in units) map[name] = factor;
        return map;
    }

    /// <summary>The display unit of a spec key, or null for a spec this tool has no unit for.</summary>
    public static string? DisplayUnit(string? specKey) =>
        specKey != null && DefaultUnits.TryGetValue(specKey, out var unit) ? unit : null;

    /// <summary>
    /// A value in <paramref name="unit"/> (null: already the display unit) as display units of <paramref name="specKey"/>.
    /// Returns an error naming the units it accepts instead of throwing.
    /// </summary>
    public static (double Value, string? Error) ToDefault(string? specKey, double value, string? unit)
    {
        if (specKey == null || !Aliases.TryGetValue(specKey, out var aliases))
            return (value, unit == null
                ? "this tool has no display unit for the parameter's type; check the parameter in revit_get_model_family"
                : $"this tool cannot convert '{unit}' for the parameter's type");
        if (unit == null) return (value, null);
        var trimmed = unit.Trim();
        if (aliases.TryGetValue(trimmed, out var factor)) return (value * factor, null);
        var loose = aliases.Where(a => string.Equals(a.Key, trimmed, StringComparison.OrdinalIgnoreCase)).Select(a => a.Value).Distinct().ToList();
        if (loose.Count == 1) return (value * loose[0], null);
        var known = aliases.Keys.Where(k => k.Length > 0).ToList();
        return (value, known.Count == 0
            ? $"'{unit}': the parameter is a plain number; pass the value without a unit"
            : $"'{unit}' is not a unit of this parameter; use {string.Join(", ", known)}");
    }
}
