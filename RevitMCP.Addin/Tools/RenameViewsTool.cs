using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Naming;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class RenameViewsTool : IRevitMcpTool
{
    public string Name => "revit_rename_views";
    public string Description =>
        "Renames views. Requires approval. " +
        "Required: viewIds (long array) OR (viewTypes + nameFilter) to select views, " +
        "mode (FindReplace|PrefixSuffix|Template|RegexFindReplace). " +
        "Mode params: find, replace (FindReplace/Regex), prefix, suffix (PrefixSuffix), template with {Name} (Template). " +
        "Use revit_preview_rename_views first to verify.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var viewIds    = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var mode       = ToolArguments.GetString(request.Arguments, "mode");
        var find       = ToolArguments.GetString(request.Arguments, "find");
        var replace    = ToolArguments.GetString(request.Arguments, "replace");
        var prefix     = ToolArguments.GetString(request.Arguments, "prefix");
        var suffix     = ToolArguments.GetString(request.Arguments, "suffix");
        var template   = ToolArguments.GetString(request.Arguments, "template");
        var nameFilter = ToolArguments.GetString(request.Arguments, "nameFilter");
        var viewTypes  = ToolArguments.GetStringArray(request.Arguments, "viewTypes");

        var modeError = RenameEngine.ValidateMode(mode);
        if (modeError != null)
            return Task.FromResult(Fail(request, modeError));

        IEnumerable<View> views;
        if (viewIds.Length > 0)
        {
            views = viewIds.Select(id => doc.GetElement(new ElementId(id)) as View).Where(v => v != null)!;
        }
        else
        {
            views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate);
            if (viewTypes.Length > 0)
                views = views.Where(v => viewTypes.Any(vt =>
                    string.Equals(v.ViewType.ToString(), vt, StringComparison.OrdinalIgnoreCase)));
            if (!string.IsNullOrWhiteSpace(nameFilter))
                views = views.Where(v => v.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
        }

        int renamed  = 0;
        var warnings = new List<string>();
        var results  = new List<object>();

        using var t = new Transaction(doc, "Revit MCP - Rename Views");
        t.Start();
        foreach (var v in views)
        {
            var proposed = RenameEngine.Apply(v.Name, mode, find, replace, prefix, suffix, template);
            if (proposed == null) continue;
            try
            {
                var oldName = v.Name;
                v.Name = proposed;
                renamed++;
                results.Add(new { viewId = v.Id.Value, oldName, newName = proposed });
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to rename '{v.Name}' → '{proposed}': {ex.Message}");
            }
        }
        t.Commit();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = renamed > 0,
            Message    = $"Renamed {renamed} view(s).",
            Data       = new { renamed, results },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
