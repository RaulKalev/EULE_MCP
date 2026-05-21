using RevitMCP.Addin.Query;

namespace RevitMCP.Addin.Presets;

public class QueryPresetFile
{
    public int Version { get; set; } = 1;
    public List<QueryPreset> Presets { get; set; } = new();
}

public class QueryPreset
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<ParameterFilterDto> Filters { get; set; } = new();
    public List<string> Parameters { get; set; } = new();
    public List<GroupKeyOptions> GroupBy { get; set; } = new();
    public string DefaultOutputMode { get; set; } = "Both";
}
