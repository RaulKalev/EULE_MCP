using Autodesk.Revit.DB;
using RevitMCP.Addin.Coordination.Clash.Geometry;

namespace RevitMCP.Addin.Coordination.Clash.Services;

public class ClashCandidateInfo
{
    public long ElementId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Model { get; set; } = "Host";
    public long? LinkInstanceId { get; set; }
    public string? LinkName { get; set; }
    public BoundingBoxXYZ? BoundingBox { get; set; }
    public Document OwnerDocument { get; set; } = null!;
    public Transform LinkTransform { get; set; } = Transform.Identity;
}

public class ClashCandidateCollector
{
    public static readonly Dictionary<string, BuiltInCategory> CategoryMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Cable Trays"]           = BuiltInCategory.OST_CableTray,
            ["Conduits"]              = BuiltInCategory.OST_Conduit,
            ["Ducts"]                 = BuiltInCategory.OST_DuctCurves,
            ["Pipes"]                 = BuiltInCategory.OST_PipeCurves,
            ["Walls"]                 = BuiltInCategory.OST_Walls,
            ["Floors"]                = BuiltInCategory.OST_Floors,
            ["Ceilings"]              = BuiltInCategory.OST_Ceilings,
            ["Structural Framing"]    = BuiltInCategory.OST_StructuralFraming,
            ["Structural Columns"]    = BuiltInCategory.OST_StructuralColumns,
            ["Structural Foundations"]= BuiltInCategory.OST_StructuralFoundation,
            ["Generic Models"]        = BuiltInCategory.OST_GenericModel,
            ["Mechanical Equipment"]  = BuiltInCategory.OST_MechanicalEquipment,
            ["Electrical Fixtures"]   = BuiltInCategory.OST_ElectricalFixtures,
            ["Fire Alarm Devices"]    = BuiltInCategory.OST_FireAlarmDevices,
        };

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

        // Host document
        foreach (var catName in cats)
        {
            if (!CategoryMap.TryGetValue(catName, out var bic)) { warnings.Add($"Unknown category '{catName}' — skipped."); continue; }
            if (!includeGenericModels && bic == BuiltInCategory.OST_GenericModel) continue;
            CollectFromDoc(hostDoc, bic, catName, "Host", null, null, Transform.Identity, result, limit);
        }

        if (includeImportedGeometry)
        {
            foreach (var imp in new FilteredElementCollector(hostDoc)
                         .OfClass(typeof(ImportInstance))
                         .WhereElementIsNotElementType()
                         .Cast<ImportInstance>())
            {
                var bbox = imp.get_BoundingBox(null);
                if (bbox == null) continue;
                result.Add(new ClashCandidateInfo
                {
                    ElementId = imp.Id.Value, Category = "ImportedGeometry",
                    Model = "Host", BoundingBox = bbox,
                    OwnerDocument = hostDoc, LinkTransform = Transform.Identity
                });
            }
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
                if (!CategoryMap.TryGetValue(catName, out var bic)) continue;
                if (!includeGenericModels && bic == BuiltInCategory.OST_GenericModel) continue;
                CollectFromDoc(linkDoc, bic, catName, link.Name, link.Id.Value, link.Name, transform, result, limit);
            }
        }

        return (result, warnings);
    }

    private static void CollectFromDoc(
        Document doc, BuiltInCategory bic, string catName,
        string modelLabel, long? linkInstanceId, string? linkName,
        Transform transform, List<ClashCandidateInfo> result, int limit)
    {
        IEnumerable<Element> elements = new FilteredElementCollector(doc)
            .OfCategory(bic)
            .WhereElementIsNotElementType();

        if (limit > 0 && result.Count < limit)
            elements = elements.Take(limit - result.Count);

        foreach (var el in elements)
        {
            var bbox = el.get_BoundingBox(null);
            if (bbox == null) continue;
            result.Add(new ClashCandidateInfo
            {
                ElementId = el.Id.Value,
                Category = catName,
                Model = modelLabel,
                LinkInstanceId = linkInstanceId,
                LinkName = linkName,
                BoundingBox = transform.IsIdentity ? bbox : ClashTransformHelper.TransformBoundingBox(bbox, transform),
                OwnerDocument = doc,
                LinkTransform = transform
            });
        }
    }
}
