using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Query;

public class CategoryResolveResult
{
    public Category? Category { get; set; }
    public List<string> Suggestions { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}
