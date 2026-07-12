using System.Runtime.CompilerServices;

namespace RevitMCP.Addin.Approval;

/// <summary>
/// Maintains an in-process change stamp for each open Revit document.
/// </summary>
public static class DocumentChangeTracker
{
    private static readonly ConditionalWeakTable<object, Counter> Versions = new();

    public static long Capture(object? document)
    {
        if (document == null) return 0;
        return Volatile.Read(ref Versions.GetValue(document, _ => new Counter()).Value);
    }

    public static void MarkChanged(object? document)
    {
        if (document == null) return;
        Interlocked.Increment(ref Versions.GetValue(document, _ => new Counter()).Value);
    }

    private sealed class Counter
    {
        public long Value;
    }
}
