namespace RevitMCP.Addin.Query;

public class GroupKeyOptions
{
    /// <summary>Category, Family, Type, Level, or Parameter</summary>
    public string Type { get; set; } = "Parameter";
    public string ParameterName { get; set; } = string.Empty;
    // Default to ContainsNormalized so short names like "ELENEA_Nimetus" match
    // full Revit parameter names like "ELENEA_ÜLD 001_Nimetus" in preset group keys.
    // Preset JSON can override with "Exact" or "Contains" when needed.
    public string ParameterMatchMode { get; set; } = "ContainsNormalized";
    public string Scope { get; set; } = "InstanceAndType";
}
