namespace RevitMCP.Addin.Families;

/// <summary>
/// Configuration for one DWG-to-detail-item generation preset,
/// loaded from %ProgramData%\RKTools\MCP\Config\DwgDetailItemPresets.json.
/// </summary>
public class DwgDetailItemPreset
{
    /// <summary>Full path to the base .rfa Detail Item family used as the generation template.</summary>
    public string PresetFamilyPath { get; set; } = string.Empty;

    /// <summary>Default folder where generated .rfa files are written.</summary>
    public string OutputFolder { get; set; } = string.Empty;

    /// <summary>Prefix prepended to the sanitized user-defined name. Default: "Kilp_".</summary>
    public string FamilyPrefix { get; set; } = "Kilp_";

    /// <summary>DWG import unit. Supported value: "Millimeter".</summary>
    public string ImportUnit { get; set; } = "Millimeter";

    /// <summary>
    /// If set, the tool will use the family view whose Name matches this value.
    /// If null or empty, the first non-template FloorPlan / DraftingView is used.
    /// </summary>
    public string? DefaultViewName { get; set; }

    /// <summary>Reserved for future use. Not implemented in MVP.</summary>
    public bool DeletePlaceholderGeometry { get; set; } = false;
}
