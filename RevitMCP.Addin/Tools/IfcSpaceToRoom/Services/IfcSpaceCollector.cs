using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Collects Generic Model / DirectShape elements from a linked document and filters
/// down to those that are likely IfcSpace representations.
/// Phase 1: read-only, no document modifications.
/// </summary>
public class IfcSpaceCollector
{
    // Parameter names that confirm an element represents an IfcSpace when they contain "IfcSpace"
    private static readonly string[] IfcTypeParamNames =
    [
        "IfcExportAs", "IfcEntity", "IFC Entity", "IfcType", "ObjectType", "Name"
    ];

    // Parameter names whose mere presence (with any value) suggests IFC origin
    private static readonly string[] IfcPresenceParamNames =
    [
        "IfcGUID", "IFC GUID", "GlobalId", "IfcName"
    ];

    /// <summary>
    /// Returns all elements in <paramref name="linkedDoc"/> that are likely IfcSpace representations.
    /// </summary>
    /// <param name="linkedDoc">The linked Revit document to search.</param>
    /// <param name="warnings">Populated with advisory messages for ambiguous detections.</param>
    public List<Element> Collect(Document linkedDoc, List<string> warnings)
    {
        // Primary sweep: Generic Models (most IFC exporters land IfcSpace here)
        var candidates = new FilteredElementCollector(linkedDoc)
            .OfCategory(BuiltInCategory.OST_GenericModel)
            .WhereElementIsNotElementType()
            .ToElements();

        var result = new List<Element>();

        foreach (var element in candidates)
        {
            var detection = ClassifyElement(element);

            switch (detection)
            {
                case DetectionResult.ConfirmedIfcSpace:
                    result.Add(element);
                    break;

                case DetectionResult.ProbableIfcSpace:
                    // Has IFC-origin markers but no explicit IfcSpace type tag.
                    // Include with a warning so the caller can decide.
                    result.Add(element);
                    warnings.Add($"Element {element.Id.Value} ({element.Name}) included as probable IFC Space (has IFC markers but no IfcSpace type tag). Verify manually.");
                    break;

                case DetectionResult.NotIfcSpace:
                default:
                    break;
            }
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private enum DetectionResult
    {
        ConfirmedIfcSpace,
        ProbableIfcSpace,
        NotIfcSpace
    }

    private static DetectionResult ClassifyElement(Element element)
    {
        // Check explicit IFC type parameters for "IfcSpace"
        foreach (var pName in IfcTypeParamNames)
        {
            var value = TryGetString(element, pName);
            if (ContainsIfcSpace(value))
                return DetectionResult.ConfirmedIfcSpace;
        }

        // Fallback: check for IFC presence markers — element is IFC-derived but type unclear
        foreach (var pName in IfcPresenceParamNames)
        {
            var value = TryGetString(element, pName);
            if (!string.IsNullOrWhiteSpace(value))
                return DetectionResult.ProbableIfcSpace;
        }

        // Also check element's built-in class name for DirectShape (another indicator)
        if (element is DirectShape)
        {
            // DirectShape without any IFC parameter is ambiguous; skip unless confirmed above.
        }

        return DetectionResult.NotIfcSpace;
    }

    private static bool ContainsIfcSpace(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.IndexOf("IfcSpace", StringComparison.OrdinalIgnoreCase) >= 0;

    private static string? TryGetString(Element element, string parameterName)
    {
        try
        {
            var param = element.LookupParameter(parameterName);
            return param?.StorageType == StorageType.String ? param.AsString() : null;
        }
        catch
        {
            return null;
        }
    }
}
