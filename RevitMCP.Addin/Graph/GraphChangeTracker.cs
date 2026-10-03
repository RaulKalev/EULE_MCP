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

    // Revit hands out a new managed Document wrapper per access; Equals/GetHashCode identify the
    // native document, so this must be a normal dictionary (ConditionalWeakTable compares references).
    private static readonly Dictionary<Document, GraphChangeSet> Sets = new();

    public static GraphChangeSet For(Document doc)
    {
        lock (SyncRoot)
        {
            if (!Sets.TryGetValue(doc, out var set))
            {
                PruneClosed();
                Sets[doc] = set = new GraphChangeSet();
            }
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
        foreach (var doc in Sets.Keys.ToList())
        {
            bool valid;
            try { valid = doc.IsValidObject; } catch { valid = false; }
            if (!valid) Sets.Remove(doc);
        }
    }
}
