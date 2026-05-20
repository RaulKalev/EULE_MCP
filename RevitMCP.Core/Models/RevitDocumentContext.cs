namespace RevitMCP.Core.Models;

public class RevitDocumentContext
{
    public string RevitVersion { get; set; } = string.Empty;
    public string ModelTitle { get; set; } = string.Empty;
    public string CentralPath { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string ActiveViewName { get; set; } = string.Empty;
    public bool IsWorkshared { get; set; }
    public string RevitUsername { get; set; } = string.Empty;
}
