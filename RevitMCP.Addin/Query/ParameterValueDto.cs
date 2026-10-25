namespace RevitMCP.Addin.Query;

public class ParameterValueDto
{
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    /// <summary>Unit symbol of a measured value in the project's units ("mm"); null when it has none (X3).</summary>
    public string? Unit { get; set; }
    public object? RawValue { get; set; }
    public string StorageType { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty; // Instance / Type
    public bool IsReadOnly { get; set; }
    public bool IsShared { get; set; }
    public string? Guid { get; set; }
    public long? ParameterId { get; set; }
    public string? BuiltInParameterName { get; set; }
    public long? TypeElementId { get; set; }
    public string? TypeName { get; set; }
}
