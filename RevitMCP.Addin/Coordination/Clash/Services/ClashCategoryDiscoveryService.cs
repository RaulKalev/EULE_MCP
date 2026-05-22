using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Coordination.Clash.Services;

public class ClashCategoryDiscoveryService
{
    private static readonly (BuiltInCategory Bic, string DisplayName)[] _knownCategories =
    [
        (BuiltInCategory.OST_CableTray,           "Cable Trays"),
        (BuiltInCategory.OST_Conduit,             "Conduits"),
        (BuiltInCategory.OST_DuctCurves,          "Ducts"),
        (BuiltInCategory.OST_PipeCurves,          "Pipes"),
        (BuiltInCategory.OST_Walls,               "Walls"),
        (BuiltInCategory.OST_Floors,              "Floors"),
        (BuiltInCategory.OST_Ceilings,            "Ceilings"),
        (BuiltInCategory.OST_StructuralFraming,   "Structural Framing"),
        (BuiltInCategory.OST_StructuralColumns,   "Structural Columns"),
        (BuiltInCategory.OST_StructuralFoundation,"Structural Foundations"),
        (BuiltInCategory.OST_GenericModel,        "Generic Models"),
        (BuiltInCategory.OST_MechanicalEquipment, "Mechanical Equipment"),
        (BuiltInCategory.OST_ElectricalFixtures,  "Electrical Fixtures"),
        (BuiltInCategory.OST_FireAlarmDevices,    "Fire Alarm Devices"),
    ];

    public List<object> GetClashableCategories(
        Document doc,
        bool includeLinks,
        bool includeGenericModels,
        bool includeImportedGeometry)
    {
        var result = new List<object>();

        foreach (var (bic, displayName) in _knownCategories)
        {
            if (!includeGenericModels && bic == BuiltInCategory.OST_GenericModel) continue;

            try
            {
                var count = new FilteredElementCollector(doc)
                    .OfCategory(bic)
                    .WhereElementIsNotElementType()
                    .GetElementCount();
                if (count > 0)
                    result.Add(new { category = displayName, count, source = "Host" });
            }
            catch { /* category not valid in this document — skip */ }
        }

        if (includeImportedGeometry)
        {
            var importCount = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance))
                .GetElementCount();
            if (importCount > 0)
                result.Add(new { category = "ImportedGeometry", count = importCount, source = "Host" });
        }

        if (!includeLinks) return result;

        foreach (var link in new FilteredElementCollector(doc)
                     .OfClass(typeof(RevitLinkInstance))
                     .Cast<RevitLinkInstance>())
        {
            var linkDoc = link.GetLinkDocument();
            if (linkDoc == null) continue;

            foreach (var (bic, displayName) in _knownCategories)
            {
                if (!includeGenericModels && bic == BuiltInCategory.OST_GenericModel) continue;
                try
                {
                    var count = new FilteredElementCollector(linkDoc)
                        .OfCategory(bic)
                        .WhereElementIsNotElementType()
                        .GetElementCount();
                    if (count > 0)
                        result.Add(new { category = displayName, count, source = link.Name });
                }
                catch { }
            }
        }

        return result;
    }
}
