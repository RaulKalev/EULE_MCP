using Autodesk.Revit.DB;
using RevitMCP.Addin.Coordination.Clash.DTOs;
using RevitMCP.Addin.Coordination.Clash.Geometry;

namespace RevitMCP.Addin.Coordination.Clash.Services;

public class ClashCandidateInfo
{
    public long ElementId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Model { get; set; } = "Host";
    public long? LinkInstanceId { get; set; }
    public string? LinkName { get; set; }
    public string UniqueId { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Level { get; set; }
    public BoundingBoxXYZ? BoundingBox { get; set; }
    public Document OwnerDocument { get; set; } = null!;
    public Transform LinkTransform { get; set; } = Transform.Identity;

    public ClashElementRefDto ToRef() => new()
    {
        ElementId = ElementId,
        UniqueId = UniqueId,
        Category = Category,
        Family = Family,
        Type = Type,
        Model = Model,
        LinkInstanceId = LinkInstanceId,
        LinkName = LinkName
    };

    /// <summary>The clash's level: the host element's, else the other one's (the summary groups by it).</summary>
    public static string? LevelOf(ClashCandidateInfo source, ClashCandidateInfo target) =>
        source.LinkInstanceId == null && source.Level != null ? source.Level
        : target.LinkInstanceId == null && target.Level != null ? target.Level
        : source.Level ?? target.Level;
}

public class ClashCandidateCollector
{
    public (List<ClashCandidateInfo> candidates, List<string> warnings) Collect(
        Document hostDoc,
        IEnumerable<string> categoryNames,
        bool includeLinks,
        bool includeGenericModels,
        bool includeImportedGeometry,
        IEnumerable<string>? linkNameFilters,
        int limit)
    {
        var result = new List<ClashCandidateInfo>();
        var warnings = new List<string>();
        var linkFilters = linkNameFilters?.ToList() ?? new List<string>();
        var cats = categoryNames.ToList();
        var resolvedCats = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase);

        // Host document.
        // "ImportedGeometry" is a special pseudo-category: only collected when explicitly listed
        // in the category names AND includeImportedGeometry is true.
        // It is NEVER injected automatically — this prevents DWG imports from polluting
        // category-specific sets like "Cable Trays" or "Conduits".
        foreach (var catName in cats)
        {
            if (catName.Equals("ImportedGeometry", StringComparison.OrdinalIgnoreCase))
            {
                if (includeImportedGeometry)
                    CollectImportedGeometryFromDoc(hostDoc, result, warnings, limit);
                continue;
            }
            // Any physical model category resolves (ClashCategoryRules), by alias, "OST_" name or document name.
            var resolved = ClashCategoryDiscoveryService.Resolve(hostDoc, catName, out var resolveError);
            if (resolved == null) { warnings.Add(resolveError!); continue; }
            var bic = resolved.Value;
            resolvedCats[catName] = bic;
            if (!includeGenericModels && bic == BuiltInCategory.OST_GenericModel) continue;
            CollectFromDoc(hostDoc, bic, catName, "Host", null, null, Transform.Identity, result, limit);
        }

        if (!includeLinks) return (result, warnings);

        foreach (var link in new FilteredElementCollector(hostDoc)
                     .OfClass(typeof(RevitLinkInstance))
                     .Cast<RevitLinkInstance>())
        {
            if (linkFilters.Count > 0 &&
                !linkFilters.Any(f => link.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                continue;

            var linkDoc = link.GetLinkDocument();
            if (linkDoc == null) { warnings.Add($"Link '{link.Name}' is not loaded — skipped."); continue; }

            var transform = link.GetTransform();
            foreach (var catName in cats)
            {
                // "ImportedGeometry" from linked documents is not collected (complex transforms,
                // rarely meaningful for clash purposes). Skip silently.
                if (catName.Equals("ImportedGeometry", StringComparison.OrdinalIgnoreCase)) continue;
                // Built-in categories are the same in every document, so the host resolution applies.
                if (!resolvedCats.TryGetValue(catName, out var bic)) continue;
                if (!includeGenericModels && bic == BuiltInCategory.OST_GenericModel) continue;
                CollectFromDoc(linkDoc, bic, catName, link.Name, link.Id.Value, link.Name, transform, result, limit);
            }
        }

        // A category that resolved but has no element anywhere was silent (Communication Devices in the 2026-10-25 check).
        foreach (var catName in resolvedCats.Keys.Where(c => !result.Any(r => string.Equals(r.Category, c, StringComparison.OrdinalIgnoreCase))))
            warnings.Add($"'{catName}' has no elements in the host model{(includeLinks ? " or the loaded links" : string.Empty)}.");

        return (result, warnings);
    }

    private static void CollectImportedGeometryFromDoc(
        Document hostDoc,
        List<ClashCandidateInfo> result,
        List<string> warnings,
        int limit)
    {
        var items = new FilteredElementCollector(hostDoc)
            .OfClass(typeof(ImportInstance))
            .WhereElementIsNotElementType()
            .Cast<ImportInstance>()
            .ToList();

        if (items.Count == 0) return;

        warnings.Add($"Imported geometry (DWG/IFC) detected ({items.Count} instance(s)): bounding-box detection only — solid accuracy not available for these elements.");

        foreach (var imp in items)
        {
            if (limit > 0 && result.Count >= limit) break;
            var bbox = imp.get_BoundingBox(null);
            if (bbox == null) continue;
            result.Add(new ClashCandidateInfo
            {
                ElementId = imp.Id.Value, Category = "ImportedGeometry", UniqueId = imp.UniqueId, Type = SafeName(imp),
                Model = "Host", BoundingBox = bbox,
                OwnerDocument = hostDoc, LinkTransform = Transform.Identity
            });
        }
    }

    private static void CollectFromDoc(
        Document doc, BuiltInCategory bic, string catName,
        string modelLabel, long? linkInstanceId, string? linkName,
        Transform transform, List<ClashCandidateInfo> result, int limit)
    {
        // Same instance set the category discovery counts (view-independent, not types).
        IEnumerable<Element> elements = ClashCategoryDiscoveryService.InstancesOf(doc, new ElementId(bic));

        if (limit > 0 && result.Count < limit)
            elements = elements.Take(limit - result.Count);

        foreach (var el in elements)
        {
            var bbox = el.get_BoundingBox(null);
            if (bbox == null) continue;
            var (family, type, level) = Names(doc, el);
            result.Add(new ClashCandidateInfo
            {
                ElementId = el.Id.Value,
                Category = catName,
                UniqueId = el.UniqueId,
                Family = family,
                Type = type,
                Level = level,
                Model = modelLabel,
                LinkInstanceId = linkInstanceId,
                LinkName = linkName,
                BoundingBox = transform.IsIdentity ? bbox : ClashTransformHelper.TransformBoundingBox(bbox, transform),
                OwnerDocument = doc,
                LinkTransform = transform
            });
        }
    }

    /// <summary>Family, type and level names for the clash report; they were always empty (M23).</summary>
    private static (string Family, string Type, string? Level) Names(Document doc, Element el)
    {
        string family = string.Empty, type = string.Empty;
        string? level = null;
        try
        {
            var elementType = doc.GetElement(el.GetTypeId()) as ElementType;
            type = elementType?.Name ?? string.Empty;
            family = el is FamilyInstance fi ? fi.Symbol?.FamilyName ?? string.Empty : elementType?.FamilyName ?? string.Empty;
        }
        catch { }
        try
        {
            if (el.LevelId != null && el.LevelId != ElementId.InvalidElementId)
                level = doc.GetElement(el.LevelId)?.Name;
        }
        catch { }
        return (family, type, level);
    }

    private static string SafeName(Element e)
    {
        try { return e.Name ?? string.Empty; }
        catch { return string.Empty; }
    }
}
