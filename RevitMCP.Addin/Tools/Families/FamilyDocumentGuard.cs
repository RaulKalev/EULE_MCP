using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools.Families;

/// <summary>
/// Opens a loaded family for the family tools without ever taking over a document the user has open.
/// <c>Document.EditFamily</c> can hand back the document that is already open in the Family Editor; closing or
/// saving that one would close the user's window or lose their unsaved work. Every tool that opens a family goes
/// through here and closes (or saves) only a document it owns.
/// </summary>
internal static class FamilyDocumentGuard
{
    /// <summary>
    /// <c>host.EditFamily(family)</c>. <paramref name="owned"/> is false when the returned document was open before
    /// the call: read it if you must, but never close it, save it or change it.
    /// </summary>
    public static Document? EditFamily(Document host, Family family, out bool owned)
    {
        var before = host.Application.Documents.Cast<Document>().ToList();
        var opened = host.EditFamily(family);
        owned = opened != null && !before.Any(open => SameDocument(open, opened));
        return opened;
    }

    /// <summary>Closes without saving, only when the document is ours.</summary>
    public static void CloseIfOwned(Document? document, bool owned)
    {
        if (document == null || !owned) return;
        try { document.Close(false); }
        catch { /* already closed */ }
    }

    public static string OpenInEditorMessage(string familyName) =>
        $"'{familyName}' is open in the Family Editor: close it first (save it there if you want its changes), then run this again.";

    /// <summary>
    /// The same document, or a family document that looks the same (title and path). When in doubt this says "same":
    /// leaving a background copy open is harmless, closing the user's document is not.
    /// </summary>
    private static bool SameDocument(Document open, Document opened)
    {
        try
        {
            if (open.Equals(opened)) return true;
            return open.IsFamilyDocument && opened.IsFamilyDocument
                   && string.Equals(open.Title, opened.Title, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(open.PathName ?? string.Empty, opened.PathName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }
}
