using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewDuplicateViewsTool : IRevitMcpTool
{
    public string Name => "revit_preview_duplicate_views";
    public string Description =>
        "Previews what views would be created when duplicating views WITHOUT making changes. " +
        "Required: viewIds (long array). " +
        "Optional: duplicateOption (Duplicate|DuplicateWithDetailing|AsDependent, default DuplicateWithDetailing), " +
        "nameSuffix (string, default \" - Copy\"), namePrefix (string, default \"\"). " +
        "Returns proposals: viewId, viewName, newName, viewType, canDuplicate, reason.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var viewIds         = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var duplicateOption = ToolArguments.GetString(request.Arguments, "duplicateOption", "DuplicateWithDetailing");
        var nameSuffix      = ToolArguments.GetString(request.Arguments, "nameSuffix", " - Copy");
        var namePrefix      = ToolArguments.GetString(request.Arguments, "namePrefix", "");

        if (viewIds.Length == 0)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "viewIds is required." });

        // "DuplicateWithDetailing" is the user-facing alias; the Revit enum value is "WithDetailing"
        if (string.Equals(duplicateOption, "DuplicateWithDetailing", StringComparison.OrdinalIgnoreCase))
            duplicateOption = "WithDetailing";

        if (!Enum.TryParse<ViewDuplicateOption>(duplicateOption, true, out var dupOpt))
        {
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false,
                Message = $"Unknown duplicateOption '{duplicateOption}'. Valid: Duplicate, DuplicateWithDetailing, AsDependent." });
        }

        var existingNames = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Select(v => v.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var proposals = new List<object>();
        foreach (var vid in viewIds)
        {
            if (doc.GetElement(new ElementId(vid)) is not View v)
            {
                proposals.Add(new { viewId = vid, viewName = (string?)null, newName = (string?)null, viewType = (string?)null, canDuplicate = false, reason = "View not found" });
                continue;
            }

            var newName = namePrefix + v.Name + nameSuffix;
            bool canDup = v.CanViewBeDuplicated(dupOpt);
            string reason;
            if (!canDup)
            {
                reason = $"View cannot be duplicated with option '{duplicateOption}'";
            }
            else if (existingNames.Contains(newName))
            {
                reason = $"A view named '{newName}' already exists — adjust suffix/prefix";
                canDup = false;
            }
            else
            {
                reason = $"Would create '{newName}' ({duplicateOption})";
            }

            proposals.Add(new
            {
                viewId      = vid,
                viewName    = v.Name,
                newName,
                viewType    = v.ViewType.ToString(),
                canDuplicate = canDup,
                reason
            });
        }

        int canDupCount = proposals.Count(p => ((dynamic)p).canDuplicate);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Preview: {canDupCount}/{viewIds.Length} view(s) can be duplicated.",
            Data       = new { total = viewIds.Length, canDuplicate = canDupCount, proposals },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
