using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using RevitMCP.Addin.Documents;
using RevitMCP.Village;

namespace RevitMCP.Addin.Checkpoints;

/// <summary>
/// Feeds every open checkpoint of a document from Revit's DocumentChanged event (App.OnDocumentChanged, #98):
/// one <see cref="ChangeBatch"/> per event, attributed to the running connector tool or the user, plus the
/// identity of elements not seen before so a later deletion can still be described. Follows
/// GraphChangeTracker: closed documents are pruned by IsValidObject before any Equals (#80), and nothing
/// ever throws into Revit's event pipeline.
/// </summary>
internal static class CheckpointChangeTracker
{
    /// <summary>Identities read in one event at most, so a huge batch cannot stall Revit.</summary>
    public const int MaxIdentityReadsPerEvent = 20_000;

    private static readonly object SyncRoot = new();
    private static readonly List<(Document Doc, string ModelId)> ModelIds = new();

    /// <summary>The checkpoint store; null until App wires it, and the tracker does nothing until then.</summary>
    public static CheckpointStore? Store { get; set; }

    /// <summary>The model id a checkpoint of this document is stored under (VillageModelId of its paths).</summary>
    public static string ModelIdOf(Document doc)
    {
        lock (SyncRoot)
        {
            PruneClosed();
            foreach (var entry in ModelIds)
                if (entry.Doc.Equals(doc)) return entry.ModelId;

            var modelId = VillageModelId.Compute(RevitDocumentResolver.GetCentralPath(doc), doc.PathName, doc.Title);
            ModelIds.Add((doc, modelId));
            return modelId;
        }
    }

    /// <summary>Forgets a cached model id, e.g. after Save As changed the document's path.</summary>
    public static void Forget(Document doc)
    {
        lock (SyncRoot)
        {
            PruneClosed();
            ModelIds.RemoveAll(e => e.Doc.Equals(doc));
        }
    }

    public static void Record(DocumentChangedEventArgs e)
    {
        try
        {
            var store = Store;
            if (store == null) return;
            var doc = e.GetDocument();
            if (doc == null || doc.IsFamilyDocument) return;

            var modelId = ModelIdOf(doc);
            if (!store.HasCheckpoints(modelId)) return;

            var batch = new ChangeBatch
            {
                At = DateTimeOffset.Now,
                Operation = e.Operation.ToString(),
                Attribution = ActiveToolProbe.CurrentAttribution(),
                Added = WithoutInternal(doc, e.GetAddedElementIds()),
                Modified = WithoutInternal(doc, e.GetModifiedElementIds()),
                Deleted = e.GetDeletedElementIds().Select(id => id.Value).ToArray()
            };
            try { batch.TransactionNames = e.GetTransactionNames().ToList(); } catch { }
            if (batch.IdCount == 0) return;

            Dictionary<long, ElementIdentity>? identities = null;
            var missing = store.MissingIdentities(modelId, batch.Added.Concat(batch.Modified));
            if (missing.Count > 0)
            {
                var reader = new CheckpointSnapshotReader(doc);
                identities = new Dictionary<long, ElementIdentity>();
                foreach (var id in missing.Take(MaxIdentityReadsPerEvent))
                {
                    var identity = reader.ReadIdentity(id);
                    if (identity != null) identities[id] = identity;
                }
            }

            store.Record(modelId, batch, identities);
        }
        catch
        {
            // Never let checkpoint bookkeeping disturb Revit's event pipeline.
        }
    }

    /// <summary>
    /// The ids without Revit's bookkeeping elements (<see cref="InternalElementRule"/>), which nearly every
    /// transaction touches. A batch larger than <see cref="MaxIdentityReadsPerEvent"/> is kept as it is.
    /// </summary>
    public static long[] WithoutInternal(Document doc, ICollection<ElementId> ids)
    {
        if (ids.Count > MaxIdentityReadsPerEvent) return ids.Select(id => id.Value).ToArray();
        return ids.Select(id => id.Value).Where(id => !CheckpointSnapshotReader.IsInternal(doc, id)).ToArray();
    }

    private static void PruneClosed()
    {
        for (var i = ModelIds.Count - 1; i >= 0; i--)
        {
            bool valid;
            try { valid = ModelIds[i].Doc.IsValidObject; } catch { valid = false; }
            if (!valid) ModelIds.RemoveAt(i);
        }
    }
}
