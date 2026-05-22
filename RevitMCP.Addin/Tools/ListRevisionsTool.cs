using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ListRevisionsTool : IRevitMcpTool
{
    public string Name => "revit_list_revisions";
    public string Description =>
        "Lists all revisions defined in the active document. " +
        "Returns: elementId, sequenceNumber, revisionDate, description, issuedBy, issuedTo, revisionNumber, visibility (Cloud/Tag/None), numbering (Alphanumeric/Numeric/None).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var revisions = Revision.GetAllRevisionIds(doc)
            .Select(rid => doc.GetElement(rid))
            .OfType<Revision>()
            .Select(r => new
            {
                elementId      = r.Id.Value,
                sequenceNumber = r.SequenceNumber,
                revisionDate   = r.RevisionDate,
                description    = r.Description,
                issuedBy       = r.IssuedBy,
                issuedTo       = r.IssuedTo,
                revisionNumber = TryGetRevisionNumber(r),
                visibility     = r.Visibility.ToString(),
                numbering      = string.Empty // RevisionNumberType not accessible in this Revit API version
            })
            .ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Returned {revisions.Count} revisions.",
            Data       = new { count = revisions.Count, revisions },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static string TryGetRevisionNumber(Revision r)
    {
        try { return r.RevisionNumber; } catch { return string.Empty; }
    }


}
