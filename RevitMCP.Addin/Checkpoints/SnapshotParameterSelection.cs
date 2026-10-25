namespace RevitMCP.Addin.Checkpoints;

/// <summary>A parameter the snapshot reader could keep: its name, whether it is writable and whether it has a value.</summary>
public sealed class SnapshotParameterCandidate
{
    public SnapshotParameterCandidate(string name, bool isReadOnly, bool hasValue)
    {
        Name = name;
        IsReadOnly = isReadOnly;
        HasValue = hasValue;
    }

    public string Name { get; }
    public bool IsReadOnly { get; }
    public bool HasValue { get; }
}

/// <summary>
/// Which parameters a checkpoint snapshot keeps when the scope names none (#98). Writable parameters are kept
/// even when empty: a value written into an empty parameter (Comments, Mark) must show in the diff and be
/// restorable, and the "now" side only reads the names the snapshot has. Order: writable with a value, writable
/// empty, read-only with a value; a read-only empty one tells nothing. Pure.
/// </summary>
public static class SnapshotParameterSelection
{
    public static List<SnapshotParameterCandidate> Select(IEnumerable<SnapshotParameterCandidate> candidates, int max)
    {
        var list = candidates.Where(c => !string.IsNullOrEmpty(c.Name)).ToList();
        var ordered = list.Where(c => !c.IsReadOnly && c.HasValue)
            .Concat(list.Where(c => !c.IsReadOnly && !c.HasValue))
            .Concat(list.Where(c => c.IsReadOnly && c.HasValue));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SnapshotParameterCandidate>();
        foreach (var c in ordered)
        {
            if (result.Count >= Math.Max(0, max)) break;
            if (seen.Add(c.Name)) result.Add(c);
        }
        return result;
    }
}

/// <summary>
/// Revit's own bookkeeping elements that nearly every transaction touches: the hidden System Browser view and
/// other uncategorised views, and plain uncategorised <c>Element</c> objects. Change tracking skips them so
/// diffs, rollbacks, plan checks and job reports list only model changes. Uncategorised elements of a real
/// API class (fill and line patterns, phases) and element types stay tracked. Pure: the Revit side passes the facts.
/// </summary>
public static class InternalElementRule
{
    public static bool IsInternal(bool hasCategory, bool isElementType, bool isView, bool isPlainElement)
        => !hasCategory && !isElementType && (isView || isPlainElement);
}
