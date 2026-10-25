using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Parameters;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Parameters;

/// <summary>revit_list_project_parameters (#102): the project's parameter bindings.</summary>
public sealed class ListProjectParametersTool : IRevitMcpTool
{
    public string Name => "revit_list_project_parameters";

    public string Description =>
        "Lists the project's parameter bindings (Manage > Project Parameters): name, shared or project parameter, GUID, " +
        "data type (SpecTypeId and its label), parameter group, instance or type binding, the categories it is bound to, " +
        "and whether values vary by group instance. Filters: nameContains, category, sharedOnly. Read-only.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Parameters;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;

        var nameContains = ToolArguments.GetString(request.Arguments, "nameContains").Trim();
        var categoryName = ToolArguments.GetString(request.Arguments, "category").Trim();
        var sharedOnly = ToolArguments.GetBool(request.Arguments, "sharedOnly");

        string? category = null;
        if (categoryName.Length > 0)
        {
            var resolved = new RevitMCP.Addin.Query.CategoryResolver().Resolve(doc, categoryName);
            if (resolved.Category == null)
                return Task.FromResult(new McpToolResult
                {
                    RequestId = request.RequestId, Success = false, Status = "validation_failed",
                    Message = resolved.Message + (resolved.Suggestions.Count > 0 ? $" Did you mean: {string.Join(", ", resolved.Suggestions)}?" : string.Empty)
                });
            category = resolved.Category.Name;
        }

        var all = ParameterBindingReader.Read(doc);
        var rows = all
            .Where(r => nameContains.Length == 0 || r.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
            .Where(r => category == null || r.Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
            .Where(r => !sharedOnly || r.IsShared)
            .ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{rows.Count} of {all.Count} parameter binding(s) match ({rows.Count(r => r.IsShared)} shared, {rows.Count(r => !r.IsShared)} project).",
            Data = new
            {
                documentTitle = doc.Title,
                total = all.Count,
                returned = rows.Count,
                parameters = rows.Select(r => r.ToPayload()).ToList()
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
