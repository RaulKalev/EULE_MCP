namespace RevitMCP.Addin.Approval;

/// <summary>
/// Maintains an in-process change stamp for each open Revit document.
/// </summary>
public static class DocumentChangeTracker
{
    private static readonly object SyncRoot = new();

    // Revit can expose multiple managed Document wrappers for one open native document.
    // Its Equals/GetHashCode contract identifies that native document consistently, so a
    // normal dictionary is required here; ConditionalWeakTable uses reference identity.
    private static readonly Dictionary<object, Counter> Versions = new();

    public static long Capture(object? document)
    {
        if (document == null) return 0;

        lock (SyncRoot)
        {
            return GetOrCreateCounter(document).Value;
        }
    }

    public static void MarkChanged(object? document)
    {
        if (document == null) return;

        lock (SyncRoot)
        {
            GetOrCreateCounter(document).Value++;
        }
    }

    private static Counter GetOrCreateCounter(object document)
    {
        if (Versions.TryGetValue(document, out var existing) && existing != null)
            return existing;

        var counter = new Counter();
        Versions.Add(document, counter);
        return counter;
    }

    private sealed class Counter
    {
        public long Value;
    }
}
