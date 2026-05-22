using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Coordination.Clash.Services;

public class ClashLinkDiscoveryService
{
    public List<object> GetClashableLinks(Document doc)
    {
        var result = new List<object>();

        foreach (var link in new FilteredElementCollector(doc)
                     .OfClass(typeof(RevitLinkInstance))
                     .Cast<RevitLinkInstance>())
        {
            var linkDoc = link.GetLinkDocument();
            result.Add(new
            {
                linkInstanceId = link.Id.Value,
                linkName = link.Name,
                status = linkDoc != null ? "Loaded" : "NotLoaded",
                documentTitle = linkDoc?.Title ?? "(not loaded)"
            });
        }

        foreach (var imp in new FilteredElementCollector(doc)
                     .OfClass(typeof(ImportInstance))
                     .Cast<ImportInstance>())
        {
            result.Add(new
            {
                linkInstanceId = imp.Id.Value,
                linkName = imp.Category?.Name ?? imp.Name,
                status = "Imported",
                documentTitle = "(imported geometry)"
            });
        }

        return result;
    }
}
