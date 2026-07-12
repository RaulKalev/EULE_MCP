using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ApplyViewTemplateTool : IRevitMcpTool
{
    public string Name => "revit_apply_view_template";
    public string Description =>
        "Applies a view template to one or more views. Requires approval. " +
        "Required: viewTemplateId (long, use revit_list_view_templates), " +
        "viewIds (long array) OR viewTypes (string array) + nameFilter (string) to select views. " +
        "Optional: limit (int, default 500).";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var templateId = ToolArguments.GetLong(request.Arguments, "viewTemplateId", 0);
        var viewIds    = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var viewTypes  = ToolArguments.GetStringArray(request.Arguments, "viewTypes");
        var nameFilter = ToolArguments.GetString(request.Arguments, "nameFilter");
        var limit      = ToolArguments.GetInt(request.Arguments, "limit", 500);

        if (templateId == 0)
            return Task.FromResult(Fail(request, "viewTemplateId is required."));
        if (viewIds.Length == 0 && viewTypes.Length == 0 && string.IsNullOrWhiteSpace(nameFilter))
            return Task.FromResult(Fail(request, "Provide viewIds, viewTypes, or nameFilter to select views."));

        var templateEid = new ElementId(templateId);
        if (doc.GetElement(templateEid) is not View template || !template.IsTemplate)
            return Task.FromResult(Fail(request, $"View template with ID {templateId} not found."));

        IEnumerable<View> targets;
        if (viewIds.Length > 0)
        {
            targets = viewIds.Select(id => doc.GetElement(new ElementId(id)) as View).Where(v => v != null)!;
        }
        else
        {
            targets = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate);
            if (viewTypes.Length > 0)
                targets = targets.Where(v => viewTypes.Any(vt =>
                    string.Equals(v.ViewType.ToString(), vt, StringComparison.OrdinalIgnoreCase)));
            if (!string.IsNullOrWhiteSpace(nameFilter))
                targets = targets.Where(v => v.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
        }
        targets = targets.Take(limit);

        int applied  = 0;
        var warnings = new List<string>();
        var results  = new List<object>();

        cancellationToken.ThrowIfCancellationRequested();
        using var tx = new Transaction(doc, "Revit MCP - Apply View Template");
        tx.Start();
        foreach (var v in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                v.ViewTemplateId = templateEid;
                applied++;
                results.Add(new { viewId = v.Id.Value, viewName = v.Name });
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed on '{v.Name}': {ex.Message}");
            }
        }
        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(tx);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = applied > 0,
            Message    = $"Applied template '{template.Name}' to {applied} view(s).",
            Data       = new { applied, templateId, templateName = template.Name, results },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
