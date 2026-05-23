namespace RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll.Models;

/// <summary>
/// A single validation issue with full Estonian-language fields.
/// Stored in SkillContext.SharedData["sheetIssues"] and converted to SkillIssue for SkillTaskResult.
/// </summary>
internal class SheetNamingIssue
{
    /// <summary>Severity in Estonian: "Info", "Hoiatus", "Viga", "Kriitiline".</summary>
    public string Severity { get; set; } = "Viga";

    public string SheetNumber { get; set; } = string.Empty;
    public string SheetName { get; set; } = string.Empty;

    /// <summary>Machine-readable rule identifier, e.g. "sheet-number-format".</summary>
    public string RuleId { get; set; } = string.Empty;

    public string MessageEt { get; set; } = string.Empty;
    public string RecommendationEt { get; set; } = string.Empty;

    /// <summary>"Revit", "Excel" or "Revit + Excel".</summary>
    public string Source { get; set; } = "Revit";
}
