namespace RevitMCP.Addin.Skills.Tasks.DrawingNaming.Models;

/// <summary>
/// A single token in a naming-mapping definition.
/// Type = "Parameter" → read the named Revit parameter value.
/// Type = "Separator" → literal separator string (e.g. "_", "-").
/// </summary>
internal sealed class NamingMappingToken
{
    public string Type  { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
