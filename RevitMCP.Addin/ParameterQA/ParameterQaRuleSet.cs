namespace RevitMCP.Addin.ParameterQA;

/// <summary>
/// Root JSON model for the parameter-QA rule sets file.
/// Stored at %AppData%\RKTools\RevitMCP\parameter-qa-rules.json
/// </summary>
public sealed class ParameterQaFile
{
    public int Version { get; set; } = 1;
    public List<ParameterQaRuleSet> RuleSets { get; set; } = [];
}

/// <summary>
/// A named collection of per-category parameter completeness rules.
/// </summary>
public sealed class ParameterQaRuleSet
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<ParameterQaRule> Rules { get; set; } = [];
}

/// <summary>
/// Defines which parameters must be filled for elements in a given Revit category.
/// </summary>
public sealed class ParameterQaRule
{
    /// <summary>The Revit category name (e.g. "Fire Alarm Devices", "Lighting Fixtures").</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Parameter names that must exist and be non-empty.</summary>
    public List<string> RequiredParameters { get; set; } = [];

    /// <summary>Whether to check instance parameters. Defaults to true.</summary>
    public bool IncludeInstanceParameters { get; set; } = true;

    /// <summary>Whether to check type parameters. Defaults to true.</summary>
    public bool IncludeTypeParameters { get; set; } = true;
}
