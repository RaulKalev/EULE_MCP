using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ListRevisionNumberingSequencesTool : IRevitMcpTool
{
    public string Name => "revit_list_revision_numbering_sequences";
    public string Description =>
        "Lists available revision numbering sequences in the active document. " +
        "Returns: sequenceId, name, numberingType, prefix, suffix, minimumDigits. " +
        "Handles projects with no custom sequences gracefully.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var sequences = new List<object>();
        var warnings  = new List<string>();

        try
        {
            var collector = new FilteredElementCollector(doc)
                .OfClass(typeof(RevisionNumberingSequence))
                .Cast<RevisionNumberingSequence>();

            foreach (var seq in collector)
            {
                var entry = new Dictionary<string, object?>
                {
                    ["sequenceId"]      = seq.Id.Value,
                    ["name"]            = seq.Name,
                    // Prefix/suffix/numberingType properties are not accessible in this Revit API version;
                    // fields are returned as null with a note so callers know the schema.
                    ["numberingType"]   = (object?)null,
                    ["prefix"]          = (object?)null,
                    ["suffix"]          = (object?)null,
                    ["minimumDigits"]   = (object?)null
                };

                sequences.Add(entry);
            }

            if (sequences.Count > 0)
                warnings.Add("numberingType, prefix, suffix, and minimumDigits are not accessible via the Revit API in this version and are returned as null.");
        }
        catch (Exception ex)
        {
            warnings.Add($"RevisionNumberingSequence not accessible via FilteredElementCollector in this Revit version: {ex.Message}");
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = sequences.Count > 0
                ? $"Returned {sequences.Count} revision numbering sequence{(sequences.Count == 1 ? "" : "s")}."
                : "No revision numbering sequences found. This is expected in projects using only the default Revit numbering.",
            Data       = new { count = sequences.Count, sequences },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
