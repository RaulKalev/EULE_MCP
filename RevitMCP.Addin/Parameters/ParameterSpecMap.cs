using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Parameters;

/// <summary>A resolved data type: a known spec (by its SpecTypeId member path) or a raw ForgeTypeId string.</summary>
public sealed class SpecResolution
{
    /// <summary>The SpecTypeId member path, e.g. "String.Text", "Length", "Boolean.YesNo"; null for a raw forge id.</summary>
    public string? Key { get; set; }
    /// <summary>A ForgeTypeId string the caller passed (e.g. "autodesk.spec.aec:length-2.0.0"); null for a known key.</summary>
    public string? ForgeId { get; set; }
    /// <summary>The friendly name of a known key (text, length, …).</summary>
    public string? Friendly { get; set; }
}

/// <summary>
/// Friendly data type and parameter group names of the parameter tools (#102) and their Revit identities. Both Revit
/// targets (2024 and 2026) use ForgeTypeId for specs and groups; this map names the SpecTypeId / GroupTypeId members
/// ("String.Text", "IdentityData"), and the Revit side turns a member path into the ForgeTypeId of the running Revit,
/// so no versioned forge id string is hard-coded. Raw forge ids ("autodesk.spec.aec:length-2.0.0") pass through.
/// No Revit API: unit tested in RevitMCP.Tests.
/// </summary>
public static class ParameterSpecMap
{
    /// <summary>Friendly data type names (and aliases) → SpecTypeId member path.</summary>
    private static readonly (string Friendly, string Key, string[] Aliases)[] Specs =
    {
        ("text", "String.Text", new[] { "string", "txt" }),
        ("multilineText", "String.MultilineText", new[] { "multiline" }),
        ("url", "String.Url", new[] { "link", "hyperlink" }),
        ("integer", "Int.Integer", new[] { "int", "whole" }),
        ("number", "Number", new[] { "double", "real", "decimal" }),
        ("length", "Length", new[] { "distance" }),
        ("area", "Area", Array.Empty<string>()),
        ("volume", "Volume", Array.Empty<string>()),
        ("angle", "Angle", Array.Empty<string>()),
        ("slope", "Slope", Array.Empty<string>()),
        ("currency", "Currency", Array.Empty<string>()),
        ("yesNo", "Boolean.YesNo", new[] { "yes/no", "bool", "boolean", "yes no" }),
        ("material", "Reference.Material", Array.Empty<string>()),
        ("image", "Reference.Image", Array.Empty<string>()),
        ("electricalCurrent", "Current", new[] { "current" }),
        ("electricalPotential", "ElectricalPotential", new[] { "voltage", "electrical potential" }),
        ("electricalPower", "ElectricalPower", new[] { "power", "electrical power" }),
        ("apparentPower", "ApparentPower", new[] { "apparent power" }),
        ("wattage", "Wattage", Array.Empty<string>()),
        ("numberOfPoles", "Int.NumberOfPoles", new[] { "poles" })
    };

    /// <summary>Parameter group labels (as Revit shows them) and aliases → GroupTypeId member name. "" = Other (no group).</summary>
    private static readonly (string Label, string Key, string[] Aliases)[] Groups =
    {
        ("Data", "Data", Array.Empty<string>()),
        ("Identity Data", "IdentityData", new[] { "identity" }),
        ("Text", "Text", Array.Empty<string>()),
        ("General", "General", Array.Empty<string>()),
        ("Constraints", "Constraints", Array.Empty<string>()),
        ("Dimensions", "Geometry", new[] { "geometry" }),
        ("Construction", "Construction", Array.Empty<string>()),
        ("Graphics", "Graphics", Array.Empty<string>()),
        ("Materials and Finishes", "Materials", new[] { "materials", "finishes" }),
        ("Phasing", "Phasing", Array.Empty<string>()),
        ("Electrical", "Electrical", Array.Empty<string>()),
        ("Electrical - Circuiting", "ElectricalCircuiting", new[] { "circuiting" }),
        ("Electrical - Lighting", "ElectricalLighting", new[] { "lighting" }),
        ("Electrical - Loads", "ElectricalLoads", new[] { "loads" }),
        ("Electrical Engineering", "ElectricalEngineering", Array.Empty<string>()),
        ("Mechanical", "Mechanical", Array.Empty<string>()),
        ("Plumbing", "Plumbing", Array.Empty<string>()),
        ("Fire Protection", "FireProtection", Array.Empty<string>()),
        ("Structural", "Structural", Array.Empty<string>()),
        ("IFC Parameters", "Ifc", new[] { "ifc" }),
        ("Green Building Properties", "GreenBuilding", Array.Empty<string>()),
        ("Visibility", "Visibility", Array.Empty<string>()),
        ("Other", "", new[] { "invalid", "none" })
    };

    /// <summary>A ForgeTypeId string: "namespace:name" with an optional "-major.minor.patch" version.</summary>
    private static readonly Regex ForgeId = new(@"^[a-z0-9]+(\.[a-z0-9]+)*:[A-Za-z0-9._]+(-\d+\.\d+\.\d+)?$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> FriendlySpecNames => Specs.Select(s => s.Friendly).ToList();
    public static IReadOnlyList<string> SpecKeys => Specs.Select(s => s.Key).ToList();
    public static IReadOnlyList<string> GroupLabels => Groups.Select(g => g.Label).ToList();
    public static IReadOnlyList<string> GroupKeys => Groups.Select(g => g.Key).ToList();

    /// <summary>Resolves a data type argument: a friendly name, a SpecTypeId member path, or a raw ForgeTypeId string.</summary>
    public static SpecResolution? ResolveSpec(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        var norm = Normalize(text);
        foreach (var (friendly, key, aliases) in Specs)
            if (Normalize(friendly) == norm || Normalize(key) == norm || aliases.Any(a => Normalize(a) == norm))
                return new SpecResolution { Key = key, Friendly = friendly };
        if (IsForgeId(text))
            return new SpecResolution { ForgeId = text, Friendly = FriendlyOfForgeId(text) };
        return null;
    }

    /// <summary>The friendly name of a SpecTypeId member path, or null.</summary>
    public static string? FriendlyOfKey(string? key) =>
        Specs.Where(s => string.Equals(s.Key, key, StringComparison.Ordinal)).Select(s => s.Friendly).FirstOrDefault();

    /// <summary>
    /// The friendly name of a forge id whose type name matches a known spec ("autodesk.spec.aec:length-2.0.0" → length).
    /// Matches on the name after the colon, version ignored, so it works across Revit versions.
    /// </summary>
    public static string? FriendlyOfForgeId(string? forgeId)
    {
        var name = TypeNameOf(forgeId);
        if (name == null) return null;
        return name switch
        {
            "spec.string" => "text",
            "spec.int64" => "integer",
            "spec.bool" => "yesNo",
            "number" => "number",
            "length" => "length",
            "area" => "area",
            "volume" => "volume",
            "angle" => "angle",
            "slope" => "slope",
            "currency" => "currency",
            _ => null
        };
    }

    /// <summary>"autodesk.spec.aec:length-2.0.0" → "length"; null when it is not a forge id.</summary>
    public static string? TypeNameOf(string? forgeId)
    {
        if (string.IsNullOrWhiteSpace(forgeId) || !IsForgeId(forgeId!.Trim())) return null;
        var text = forgeId.Trim();
        var name = text.Substring(text.IndexOf(':') + 1);
        var dash = name.LastIndexOf('-');
        return dash > 0 && Regex.IsMatch(name.Substring(dash), @"^-\d+\.\d+\.\d+$") ? name.Substring(0, dash) : name;
    }

    /// <summary>Two forge ids that name the same type, versions ignored.</summary>
    public static bool SameType(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(StripVersion(a!), StripVersion(b!), StringComparison.Ordinal);
    }

    public static string StripVersion(string forgeId)
    {
        var text = forgeId.Trim();
        var dash = text.LastIndexOf('-');
        return dash > 0 && Regex.IsMatch(text.Substring(dash), @"^-\d+\.\d+\.\d+$") ? text.Substring(0, dash) : text;
    }

    public static bool IsForgeId(string value) => ForgeId.IsMatch(value.Trim());

    /// <summary>
    /// Resolves a parameter group argument to a GroupTypeId member name ("" = Other). Accepts Revit's label
    /// ("Identity Data"), the member name ("IdentityData") or an alias. Null when unknown — the Revit side then tries
    /// the labels of every built-in group of the running Revit.
    /// </summary>
    public static string? ResolveGroupKey(string? value)
    {
        var norm = Normalize(value ?? string.Empty);
        if (norm.Length == 0) return null;
        foreach (var (label, key, aliases) in Groups)
            if (Normalize(label) == norm || (key.Length > 0 && Normalize(key) == norm) || aliases.Any(a => Normalize(a) == norm))
                return key;
        return null;
    }

    public static string LabelOfGroupKey(string key) =>
        Groups.Where(g => string.Equals(g.Key, key, StringComparison.Ordinal)).Select(g => g.Label).FirstOrDefault() ?? key;

    /// <summary>Lower case, letters and digits only: "Identity Data" = "identity_data" = "IdentityData".</summary>
    public static string Normalize(string value) =>
        new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Up to five known names that look like <paramref name="value"/>, for an error message.</summary>
    public static List<string> SuggestSpecs(string? value)
    {
        var norm = Normalize(value ?? string.Empty);
        return Specs.Select(s => s.Friendly)
            .Where(f => norm.Length > 0 && (Normalize(f).Contains(norm) || norm.Contains(Normalize(f))))
            .Take(5).ToList();
    }
}
