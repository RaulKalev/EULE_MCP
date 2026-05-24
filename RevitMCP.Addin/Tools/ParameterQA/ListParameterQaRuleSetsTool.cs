using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ParameterQA;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ParameterQA;

/// <summary>
/// Lists all available parameter QA rule sets from the configured JSON file.
/// </summary>
public class ListParameterQaRuleSetsTool : IRevitMcpTool
{
    public string Name => "revit_list_parameter_qa_rule_sets";
    public string Description => "Lists all available parameter QA rule sets. Each rule set defines which parameters must be filled for specific Revit categories.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    private readonly ParameterQaRuleSetService _service = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var ruleSets = _service.GetAll();

        var result = ruleSets.Select(rs => new
        {
            name        = rs.Name,
            description = rs.Description,
            ruleCount   = rs.Rules.Count,
            rules = rs.Rules.Select(r => new
            {
                category            = r.Category,
                requiredParameters  = r.RequiredParameters,
                includeInstance     = r.IncludeInstanceParameters,
                includeType         = r.IncludeTypeParameters
            }).ToList()
        }).ToList();

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success   = true,
            Message   = $"Found {ruleSets.Count} rule set(s).",
            Data      = new { count = ruleSets.Count, ruleSets = result }
        });
    }
}
