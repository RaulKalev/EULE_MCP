using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Documents;

/// <summary>A DWG/CAD, IFC or RVT link type in a project document with its instances.</summary>
public sealed class LinkEntry
{
    public ElementType Type { get; set; } = null!;
    public long TypeId => Type.Id.Value;
    public string Name { get; set; } = string.Empty;
    public LinkKind Kind { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public List<Element> Instances { get; set; } = new();

    public LinkCandidate ToCandidate() => new() { TypeId = TypeId, Name = Name, Kind = Kind, Path = Path };

    public object ToResult() => new
    {
        typeId = TypeId,
        name = Name,
        kind = Kind.ToString().ToUpperInvariant(),
        path = Path,
        status = Status,
        instanceIds = Instances.Select(i => i.Id.Value).ToList()
    };
}

/// <summary>
/// Lists the top-level links of a document (#90): linked CAD types (imports are skipped — they have no
/// file to reload) and Revit link types, which covers IFC links. Nested Revit links are skipped.
/// </summary>
public static class LinkInventory
{
    public static List<LinkEntry> Collect(Document doc)
    {
        var result = new List<LinkEntry>();

        var importsByType = new FilteredElementCollector(doc)
            .OfClass(typeof(ImportInstance))
            .WhereElementIsNotElementType()
            .Cast<Element>()
            .GroupBy(e => e.GetTypeId().Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var cad in new FilteredElementCollector(doc).OfClass(typeof(CADLinkType)).Cast<CADLinkType>())
        {
            if (!IsExternal(cad)) continue;
            var path = GetPath(cad);
            result.Add(new LinkEntry
            {
                Type = cad,
                Name = cad.Name,
                Kind = LinkFileMatcher.ClassifyCadLink(cad.Name, path),
                Path = path,
                Status = GetStatus(cad),
                Instances = importsByType.TryGetValue(cad.Id.Value, out var instances) ? instances : new List<Element>()
            });
        }

        var linksByType = new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<Element>()
            .GroupBy(e => e.GetTypeId().Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var rvt in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
        {
            if (rvt.IsNestedLink) continue;
            var path = GetPath(rvt);
            result.Add(new LinkEntry
            {
                Type = rvt,
                Name = rvt.Name,
                Kind = LinkFileMatcher.ClassifyRevitLink(rvt.Name, path),
                Path = path,
                Status = GetStatus(rvt),
                Instances = linksByType.TryGetValue(rvt.Id.Value, out var instances) ? instances : new List<Element>()
            });
        }

        return result.OrderBy(l => l.Kind).ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool IsExternal(Element type)
    {
        try { return type.IsExternalFileReference(); }
        catch { return false; }
    }

    public static string GetPath(Element type)
    {
        try
        {
            var reference = type.GetExternalFileReference();
            var path = reference?.GetAbsolutePath() ?? reference?.GetPath();
            return path == null ? string.Empty : ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetStatus(Element type)
    {
        try { return type.GetExternalFileReference()?.GetLinkedFileStatus().ToString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Finds links by name or type id. Returns the matches and, for a selector that matches nothing,
    /// an error naming the available links.
    /// </summary>
    public static List<LinkEntry> Find(IReadOnlyList<LinkEntry> links, string selector, out string? error)
    {
        error = null;
        var wanted = selector.Trim();
        if (long.TryParse(wanted, out var id))
        {
            var byId = links.Where(l => l.TypeId == id || l.Instances.Any(i => i.Id.Value == id)).ToList();
            if (byId.Count > 0) return byId;
        }

        var exact = links.Where(l => string.Equals(l.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;

        var stem = LinkFileMatcher.Stem(wanted);
        var byStem = links.Where(l => string.Equals(LinkFileMatcher.Stem(l.Name), stem, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byStem.Count > 0) return byStem;

        var contains = links.Where(l => l.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        if (contains.Count > 0) return contains;

        error = $"No link matches '{wanted}'. Links: {(links.Count == 0 ? "none" : string.Join(", ", links.Select(l => $"'{l.Name}' ({l.Kind.ToString().ToUpperInvariant()}, type {l.TypeId})")))}.";
        return contains;
    }
}
