using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Documents;

/// <summary>
/// Resolves the optional <c>document</c> argument (#90) to an open project document. Reads, collectors
/// and transactions work on non-active documents; UI-bound work (selection, active view) does not, so
/// tools check <see cref="DocumentTarget.IsActive"/> before using them.
/// </summary>
public static class RevitDocumentResolver
{
    public const string ArgumentName = "document";

    public sealed class DocumentTarget
    {
        public Document Document { get; init; } = null!;
        public bool IsActive { get; init; }
    }

    /// <summary>Open project documents: linked and family documents are skipped.</summary>
    public static List<Document> GetProjectDocuments(UIApplication uiapp)
    {
        var result = new List<Document>();
        foreach (Document doc in uiapp.Application.Documents)
        {
            try
            {
                if (doc.IsLinked || doc.IsFamilyDocument) continue;
            }
            catch
            {
                continue;
            }
            result.Add(doc);
        }
        return result;
    }

    public static bool IsActive(UIApplication uiapp, Document doc)
    {
        var active = uiapp.ActiveUIDocument?.Document;
        if (active == null) return false;
        try { return active.Equals(doc); }
        catch { return false; }
    }

    public static string GetCentralPath(Document doc)
    {
        try
        {
            if (!doc.IsWorkshared) return string.Empty;
            var central = doc.GetWorksharingCentralModelPath();
            return central == null ? string.Empty : ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
        }
        catch
        {
            return string.Empty;
        }
    }

    public static OpenDocumentInfo Describe(UIApplication uiapp, Document doc) => new()
    {
        Title = doc.Title,
        PathName = doc.PathName ?? string.Empty,
        CentralPath = GetCentralPath(doc),
        IsActive = IsActive(uiapp, doc)
    };

    /// <summary>Resolves <paramref name="selector"/> (title or path; empty = active document).</summary>
    public static DocumentTarget? Resolve(UIApplication uiapp, string? selector, out string? error)
    {
        var documents = GetProjectDocuments(uiapp);
        var infos = documents.Select(d => Describe(uiapp, d)).ToList();
        var match = DocumentTargetMatcher.Match(infos, selector);
        if (!match.Success)
        {
            error = match.Error;
            return null;
        }

        error = null;
        return new DocumentTarget { Document = documents[match.Index], IsActive = infos[match.Index].IsActive };
    }

    /// <summary>Resolves the request's <c>document</c> argument.</summary>
    public static DocumentTarget? Resolve(UIApplication uiapp, McpToolRequest request, out string? error) =>
        Resolve(uiapp, ToolArguments.GetString(request.Arguments, ArgumentName), out error);

    /// <summary>
    /// The document an approval binds to: the request's <c>document</c> target when it resolves, otherwise the
    /// active document. Approvals for non-active targets are then validated against that same document.
    /// </summary>
    public static Document? ResolveApprovalDocument(UIApplication uiapp, McpToolRequest request)
    {
        var selector = request.Arguments == null ? string.Empty : ToolArguments.GetString(request.Arguments, ArgumentName);
        if (string.IsNullOrWhiteSpace(selector))
            return uiapp.ActiveUIDocument?.Document;
        try
        {
            return Resolve(uiapp, selector, out _)?.Document;
        }
        catch
        {
            return null;
        }
    }
}
