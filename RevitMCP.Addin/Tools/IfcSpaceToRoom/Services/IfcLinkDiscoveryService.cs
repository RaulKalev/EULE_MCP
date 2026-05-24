using Autodesk.Revit.DB;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Discovers all RevitLinkInstance elements in the host document and identifies which
/// are likely derived from an IFC model.
/// Phase 1: read-only, no document modifications.
/// </summary>
public class IfcLinkDiscoveryService
{
    /// <summary>
    /// Returns a list of <see cref="IfcLinkInfo"/> DTOs for all RevitLinkInstance elements
    /// in <paramref name="doc"/>.
    /// </summary>
    /// <param name="doc">Active host document.</param>
    /// <param name="includeAllRevitLinks">
    /// When true, every link is marked as likely IFC regardless of heuristics.
    /// All links are always returned so the caller can decide what to surface.
    /// </param>
    /// <param name="includeUnloaded">When false (default), unloaded links are still returned but flagged.</param>
    public List<IfcLinkInfo> GetLinks(Document doc, bool includeAllRevitLinks = false, bool includeUnloaded = false)
    {
        var result = new List<IfcLinkInfo>();

        foreach (var linkInstance in new FilteredElementCollector(doc)
                     .OfClass(typeof(RevitLinkInstance))
                     .Cast<RevitLinkInstance>())
        {
            var linkedDoc = linkInstance.GetLinkDocument();
            var isLoaded  = linkedDoc != null;

            var name          = linkInstance.Name ?? string.Empty;
            var documentTitle = linkedDoc?.Title ?? string.Empty;

            // Try to get the file path from the link type (best-effort)
            string? filePath = TryGetLinkFilePath(doc, linkInstance);

            bool isLikelyIfc = includeAllRevitLinks
                || IsLikelyIfcLink(name, documentTitle, filePath);

            bool transformAvailable = false;
            if (isLoaded)
            {
                try
                {
                    var t = linkInstance.GetTotalTransform();
                    transformAvailable = t != null;
                }
                catch
                {
                    transformAvailable = false;
                }
            }

            result.Add(new IfcLinkInfo
            {
                LinkInstanceId     = linkInstance.Id.Value,
                Name               = name,
                DocumentTitle      = documentTitle,
                IsLoaded           = isLoaded,
                IsLikelyIfc        = isLikelyIfc,
                TransformAvailable = transformAvailable
            });
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to read the external file path from the RevitLinkType associated with
    /// <paramref name="linkInstance"/>. Returns null if unavailable.
    /// </summary>
    private static string? TryGetLinkFilePath(Document doc, RevitLinkInstance linkInstance)
    {
        try
        {
            var typeId   = linkInstance.GetTypeId();
            if (typeId == null || typeId == ElementId.InvalidElementId) return null;

            var linkType = doc.GetElement(typeId) as RevitLinkType;
            if (linkType == null) return null;

            var extRef = linkType.GetExternalFileReference();
            if (extRef == null) return null;

            return ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetPath());
        }
        catch
        {
            // Path retrieval is best-effort and non-fatal
            return null;
        }
    }

    private static bool IsLikelyIfcLink(string name, string documentTitle, string? filePath)
    {
        if (ContainsIfc(name))          return true;
        if (ContainsIfc(documentTitle)) return true;
        if (documentTitle.EndsWith(".ifc.RVT", StringComparison.OrdinalIgnoreCase)) return true;
        if (filePath != null && ContainsIfc(filePath)) return true;
        return false;
    }

    private static bool ContainsIfc(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.IndexOf(".ifc", StringComparison.OrdinalIgnoreCase) >= 0;
}
