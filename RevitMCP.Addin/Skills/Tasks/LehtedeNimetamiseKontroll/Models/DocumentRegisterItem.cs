namespace RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll.Models;

/// <summary>A single row read from the Excel document register.</summary>
internal class DocumentRegisterItem
{
    public string DocumentNumber { get; set; } = string.Empty;
    public string DocumentName { get; set; } = string.Empty;
    public string Discipline { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public int RowNumber { get; set; }
}
