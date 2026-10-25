using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using RevitMCP.Addin.Checkpoints;

namespace RevitMCP.Addin.Plans;

/// <summary>
/// Collects the element ids Revit reports while one plan-authorized call runs (#99), fed by the same
/// DocumentChanged hook as the checkpoint tracker (App.OnDocumentChanged). API thread only: a call runs and
/// is recorded inside one ExternalEvent.Execute, so there is at most one recording at a time.
/// </summary>
internal static class PlanChangeRecorder
{
    private static Document? _document;
    private static CallChanges? _changes;

    public static CallChanges Begin(Document doc)
    {
        _document = doc;
        _changes = new CallChanges();
        return _changes;
    }

    public static void End()
    {
        _changes = null;
        _document = null;
    }

    public static void Record(DocumentChangedEventArgs e)
    {
        var changes = _changes;
        var expected = _document;
        if (changes == null || expected == null) return;
        try
        {
            var doc = e.GetDocument();
            if (doc == null || !expected.IsValidObject || !doc.Equals(expected)) return;
            // Revit's bookkeeping elements are not the call's changes (a strict plan would refuse them).
            changes.Record(
                CheckpointChangeTracker.WithoutInternal(doc, e.GetAddedElementIds()),
                CheckpointChangeTracker.WithoutInternal(doc, e.GetModifiedElementIds()),
                e.GetDeletedElementIds().Select(id => id.Value));
        }
        catch
        {
            // Never let plan bookkeeping disturb Revit's event pipeline.
        }
    }
}
