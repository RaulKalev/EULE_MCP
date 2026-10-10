using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Documents;

/// <summary>revit_list_open_documents (#90): the open project documents and, optionally, their links.</summary>
public sealed class ListOpenDocumentsTool : IRevitMcpTool
{
    public string Name => "revit_list_open_documents";
    public string Description =>
        "Lists the open Revit project documents (linked and family documents are skipped): title, path, whether it is " +
        "active, worksharing state and, with includeLinks=true (default), its DWG/IFC/RVT links. Pass a title or path as " +
        "'document' to the document-aware tools to work on a non-active project.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Connection;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var includeLinks = ToolArguments.GetBool(request.Arguments, "includeLinks", true);
        var warnings = new List<string>();

        var documents = RevitDocumentResolver.GetProjectDocuments(uiapp).Select(doc =>
        {
            List<object>? links = null;
            if (includeLinks)
            {
                try
                {
                    links = LinkInventory.Collect(doc).Select(l => l.ToResult()).ToList();
                }
                catch (Exception ex)
                {
                    warnings.Add($"Links of '{doc.Title}' could not be read: {ex.Message}");
                }
            }

            return new
            {
                title = doc.Title,
                path = doc.PathName ?? string.Empty,
                isActive = RevitDocumentResolver.IsActive(uiapp, doc),
                isWorkshared = doc.IsWorkshared,
                centralPath = RevitDocumentResolver.GetCentralPath(doc),
                isDetached = Safe(() => doc.IsDetached),
                isModified = Safe(() => doc.IsModified),
                isReadOnly = Safe(() => doc.IsReadOnly),
                links
            };
        }).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{documents.Count} open project document(s).",
            Data = new { documents },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static bool Safe(Func<bool> read)
    {
        try { return read(); }
        catch { return false; }
    }
}

/// <summary>revit_activate_document (#90): makes an already-open project document the active one.</summary>
public sealed class ActivateDocumentTool : IRevitMcpTool
{
    public string Name => "revit_activate_document";
    public string Description =>
        "Makes an open project document the active one in the Revit UI (UIApplication.OpenAndActivateDocument). " +
        "Only needed for UI-bound tools (selection, active view); document-aware tools work on non-active documents " +
        "via 'document'. Fails while a transaction is open or for documents that were never saved.";
    public ToolPermission Permission => ToolPermission.DirectEdit;
    public ToolCategory Category => ToolCategory.Connection;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(ToolArguments.GetString(request.Arguments, RevitDocumentResolver.ArgumentName)))
            return Task.FromResult(DocumentToolSupport.Fail(request, "document is required (title or path, see revit_list_open_documents)."));

        var target = DocumentToolSupport.ResolveTarget(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);

        var doc = target.Document;
        if (target.IsActive)
            return Task.FromResult(Done(request, doc, sw, $"'{doc.Title}' is already the active document."));

        if (string.IsNullOrWhiteSpace(doc.PathName))
            return Task.FromResult(DocumentToolSupport.Fail(request, $"'{doc.Title}' has never been saved, so it cannot be activated by path."));

        var active = uiapp.ActiveUIDocument?.Document;
        if (active != null && active.IsModifiable)
            return Task.FromResult(DocumentToolSupport.Fail(request, $"The active document '{active.Title}' has an open transaction; activation is not possible now."));

        uiapp.OpenAndActivateDocument(doc.PathName);
        return Task.FromResult(Done(request, doc, sw, $"Activated '{doc.Title}'."));
    }

    private static McpToolResult Done(McpToolRequest request, Document doc, Stopwatch sw, string message)
    {
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = message,
            Data = new { title = doc.Title, path = doc.PathName ?? string.Empty },
            DurationMs = sw.ElapsedMilliseconds
        };
    }
}
