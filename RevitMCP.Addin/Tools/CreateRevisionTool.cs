using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class CreateRevisionTool : IRevitMcpTool
{
    public string Name => "revit_create_revision";
    public string Description =>
        "Creates a project revision and optionally assigns it as an additional revision to sheets. Requires approval. " +
        "Optional: revisionDate, description, issuedTo, issuedBy, isIssued (default false), " +
        "numberingSequenceId (use revit_list_revision_numbering_sequences), " +
        "sheetIds or sheetNumbers. Revit assigns the revision sequence number automatically.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var revisionDate = ToolArguments.GetString(request.Arguments, "revisionDate");
        var description = ToolArguments.GetString(request.Arguments, "description");
        var issuedTo = ToolArguments.GetString(request.Arguments, "issuedTo");
        var issuedBy = ToolArguments.GetString(request.Arguments, "issuedBy");
        var isIssued = ToolArguments.GetBool(request.Arguments, "isIssued", false);
        var numberingSequenceId = ToolArguments.GetLong(request.Arguments, "numberingSequenceId", 0L);
        var sheetIds = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var sheetNumbers = ToolArguments.GetStringArray(request.Arguments, "sheetNumbers");

        RevisionNumberingSequence? numberingSequence = null;
        if (numberingSequenceId > 0)
        {
            numberingSequence = doc.GetElement(new ElementId(numberingSequenceId)) as RevisionNumberingSequence;
            if (numberingSequence == null)
                return Task.FromResult(Fail(request,
                    $"Revision numbering sequence {numberingSequenceId} was not found."));
        }

        var warnings = new List<string>();
        var targetSheets = ResolveSheets(doc, sheetIds, sheetNumbers, warnings);
        if ((sheetIds.Length > 0 || sheetNumbers.Length > 0) && targetSheets.Count == 0)
            return Task.FromResult(Fail(request, "No target sheets were found.", warnings));

        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = new Transaction(doc, "Revit MCP - Create Revision");
        transaction.Start();

        var revision = Revision.Create(doc);
        if (numberingSequence != null)
            revision.RevisionNumberingSequenceId = numberingSequence.Id;
        revision.RevisionDate = revisionDate.Trim();
        revision.Description = description.Trim();
        revision.IssuedTo = issuedTo.Trim();
        revision.IssuedBy = issuedBy.Trim();

        var assigned = 0;
        foreach (var sheet in targetSheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var revisionIds = sheet.GetAdditionalRevisionIds().ToList();
                if (!revisionIds.Contains(revision.Id))
                {
                    revisionIds.Add(revision.Id);
                    sheet.SetAdditionalRevisionIds(revisionIds);
                }
                assigned++;
            }
            catch (Exception ex)
            {
                warnings.Add($"Revision was not assigned to sheet '{sheet.SheetNumber}': {ex.Message}");
            }
        }

        // Issued revisions are locked, so this must be the final mutation.
        revision.Issued = isIssued;
        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(transaction);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Created revision {revision.SequenceNumber} and assigned it to {assigned} sheet(s).",
            Data = new
            {
                revisionId = revision.Id.Value,
                sequenceNumber = revision.SequenceNumber,
                revisionNumber = TryGetRevisionNumber(revision),
                revisionDate = revision.RevisionDate,
                revision.Description,
                revision.IssuedTo,
                revision.IssuedBy,
                revision.Issued,
                numberingSequenceId = numberingSequence?.Id.Value,
                assignedSheetCount = assigned
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static List<ViewSheet> ResolveSheets(
        Document doc,
        long[] sheetIds,
        string[] sheetNumbers,
        List<string> warnings)
    {
        var allSheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(s => !s.IsPlaceholder)
            .ToList();
        var result = new List<ViewSheet>();

        foreach (var id in sheetIds.Distinct())
        {
            var sheet = allSheets.FirstOrDefault(s => s.Id.Value == id);
            if (sheet == null) warnings.Add($"Sheet ID {id} was not found.");
            else result.Add(sheet);
        }

        foreach (var number in sheetNumbers.Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var sheet = allSheets.FirstOrDefault(s =>
                string.Equals(s.SheetNumber, number, StringComparison.OrdinalIgnoreCase));
            if (sheet == null) warnings.Add($"Sheet number '{number}' was not found.");
            else if (!result.Any(s => s.Id == sheet.Id)) result.Add(sheet);
        }

        return result;
    }

    private static string TryGetRevisionNumber(Revision revision)
    {
        try { return revision.RevisionNumber; }
        catch { return string.Empty; }
    }

    private static McpToolResult Fail(
        McpToolRequest request,
        string message,
        List<string>? warnings = null) =>
        new()
        {
            RequestId = request.RequestId,
            Success = false,
            Message = message,
            Warnings = warnings ?? new List<string>()
        };
}
