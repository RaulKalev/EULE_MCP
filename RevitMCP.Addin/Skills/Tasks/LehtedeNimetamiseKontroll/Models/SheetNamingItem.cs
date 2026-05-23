namespace RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll.Models;

/// <summary>Data collected from a single Revit ViewSheet.</summary>
internal class SheetNamingItem
{
    public long SheetId { get; set; }
    public string SheetNumber { get; set; } = string.Empty;
    public string SheetName { get; set; } = string.Empty;
    public bool IsPlaceholder { get; set; }

    // Custom title-block parameters (populated by CollectRevitSheetsTask if present)
    public string Nimetus { get; set; } = string.Empty;   // "Nimetus" param — no spaces allowed
    public string Markus  { get; set; } = string.Empty;   // "Märkus" param  — no spaces allowed

    // Arbitrary extra parameters read from Revit (key = param name, value = trimmed string value)
    public Dictionary<string, string> ExtParams { get; set; } = new();

    // Parsed components (populated by ValidateSheetNumbersTask)
    public bool ParsedSuccessfully { get; set; }
    public string ProjectNumber { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public string Discipline { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Sequence { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
}
