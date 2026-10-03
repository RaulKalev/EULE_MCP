using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace RevitMCP.Addin.Graph;

/// <summary>
/// Records element changes per open document for incremental graph updates (#61). Fed by Revit's
/// DocumentChanged event (App.OnDocumentChanged). Tracking only describes changes made while this
/// add-in instance is loaded, which is why incremental updates require a baseline built in this session.
/// </summary>
internal static class GraphChangeTracker
{
    private static readonly object SyncRoot = new();

    // Revit hands out a new managed Document wrapper per access; Equals identifies the native document,
    // so lookups compare with Equals (ConditionalWeakTable compares references). Not a Dictionary:
    // Equals/GetHashCode throw InvalidObjectException once a document is closed (#80), so closed
    // entries are dropped by position before any comparison. Only a handful of documents are open.
    private static readonly List<(Document Doc, GraphChangeSet Set)> Sets = new();

    public static GraphChangeSet For(Document doc)
    {
        lock (SyncRoot)
        {
            PruneClosed();
            foreach (var entry in Sets)
            {
                if (entry.Doc.Equals(doc)) return entry.Set;
            }

            var set = new GraphChangeSet();
            Sets.Add((doc, set));
            return set;
        }
    }

    public static void Record(DocumentChangedEventArgs e)
    {
        try
        {
            var doc = e.GetDocument();
            if (doc == null || doc.IsFamilyDocument) return;
            var set = For(doc);
            lock (SyncRoot)
            {
                set.Record(
                    e.GetAddedElementIds().Select(id => id.Value),
                    e.GetModifiedElementIds().Select(id => id.Value),
                    e.GetDeletedElementIds().Select(id => id.Value));
            }
        }
        catch
        {
            // Never let graph bookkeeping disturb Revit's event pipeline.
        }
    }

    private static void PruneClosed()
    {
        for (var i = Sets.Count - 1; i >= 0; i--)
        {
            bool valid;
            try { valid = Sets[i].Doc.IsValidObject; } catch { valid = false; }
            if (!valid) Sets.RemoveAt(i);
        }
    }
}
