using Autodesk.Revit.UI;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Documents;

/// <summary>
/// The one way a tool finds the document it works on (#101): the request's <c>document</c> argument (title or path)
/// when given, otherwise the active document. Reads, collectors and transactions work on a non-active document;
/// UI-bound work (selection, active view) does not, so such tools ask <see cref="UiDocumentOf"/> and answer
/// <see cref="ActiveRequired"/> when it is null instead of acting on the wrong document.
/// </summary>
public static class ToolDocument
{
    public const string ActiveRequiredStatus = "active_document_required";

    public static RevitDocumentResolver.DocumentTarget? Resolve(UIApplication uiapp, McpToolRequest request, out McpToolResult? failure)
    {
        var target = RevitDocumentResolver.Resolve(uiapp, request, out var error);
        failure = target == null
            ? new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "validation_failed",
                Message = error ?? "No open project document."
            }
            : null;
        return target;
    }

    /// <summary>The UI document for selection and views: only when the target is the active document.</summary>
    public static UIDocument? UiDocumentOf(UIApplication uiapp, RevitDocumentResolver.DocumentTarget target)
        => target.IsActive ? uiapp.ActiveUIDocument : null;

    public static McpToolResult ActiveRequired(McpToolRequest request, string what, string documentTitle) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Status = ActiveRequiredStatus,
        Message = $"{what} needs the document to be active, and '{documentTitle}' is not. Call revit_activate_document first, or pass elementIds instead."
    };

    /// <summary>
    /// True when the request names a document that is open but not active. The model graph and its gate describe the
    /// active document only, so they step aside for such a call.
    /// </summary>
    public static bool TargetsOtherDocument(UIApplication uiapp, McpToolRequest request)
    {
        if (request.Arguments == null || string.IsNullOrWhiteSpace(ToolArguments.GetString(request.Arguments, RevitDocumentResolver.ArgumentName)))
            return false;
        try
        {
            var target = RevitDocumentResolver.Resolve(uiapp, request, out _);
            return target != null && !target.IsActive;
        }
        catch
        {
            return false;
        }
    }
}
