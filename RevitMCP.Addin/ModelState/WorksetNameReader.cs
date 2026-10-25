using Autodesk.Revit.DB;

namespace RevitMCP.Addin.ModelState;

/// <summary>
/// Workset names of elements with a per-document cache (#109: moved out of <c>RevitGraphExtractor</c> so the query
/// engine and the audit read worksets the same way). An empty string when the document is not workshared or the
/// workset cannot be read. API thread only; never throws.
/// </summary>
public sealed class WorksetNameReader
{
    private readonly Document _doc;
    private readonly Dictionary<int, string> _names = new();

    public WorksetNameReader(Document doc)
    {
        _doc = doc;
        try { IsWorkshared = doc.IsWorkshared; } catch { IsWorkshared = false; }
    }

    public bool IsWorkshared { get; }

    /// <summary>Seeds the cache with a workset already read (the graph extractor's workset pass).</summary>
    public void Remember(WorksetId id, string? name)
    {
        if (id == null) return;
        _names[id.IntegerValue] = name ?? string.Empty;
    }

    public string NameOf(Element element)
    {
        if (!IsWorkshared) return string.Empty;
        try
        {
            return NameOf(element.WorksetId);
        }
        catch
        {
            return string.Empty;
        }
    }

    public string NameOf(WorksetId? worksetId)
    {
        if (!IsWorkshared || worksetId == null) return string.Empty;
        try
        {
            if (_names.TryGetValue(worksetId.IntegerValue, out var cached)) return cached;
            var workset = _doc.GetWorksetTable().GetWorkset(worksetId);
            var name = workset?.Name ?? string.Empty;
            _names[worksetId.IntegerValue] = name;
            return name;
        }
        catch
        {
            return string.Empty;
        }
    }
}
