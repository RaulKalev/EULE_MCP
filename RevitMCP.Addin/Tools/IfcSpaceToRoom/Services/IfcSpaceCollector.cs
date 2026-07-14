using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Collects Generic Model / DirectShape elements from a linked document and classifies
/// them as Confirmed or Probable IFC Space representations.
///
/// Detection logic:
///   Confirmed — one of the explicit IFC type parameters contains "IfcSpace".
///   Probable  — generic IFC-origin markers exist (GUID, IfcName, etc.)
///               but no explicit IfcSpace type was found.
///               Probable elements are NOT returned by default.
///
/// Phase 1: read-only, no document modifications.
/// </summary>
public class IfcSpaceCollector
{
    // Parameters that CONFIRM IfcSpace when their value contains "IfcSpace"
    private static readonly string[] IfcTypeParamNames =
    [
        "IfcExportAs", "IfcEntity", "IFC Entity", "IfcType", "ObjectType"
    ];

    // Parameters whose mere non-empty presence suggests IFC origin (but not IfcSpace specifically)
    private static readonly string[] IfcPresenceParamNames =
    [
        "IfcGUID", "IFC GUID", "GlobalId", "IfcName"
    ];

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns detected IFC Space elements from the linked document.
    /// </summary>
    /// <param name="linkedDoc">The linked Revit document to search.</param>
    /// <param name="includeProbable">
    /// When false (default), only elements with a confirmed IfcSpace type are returned.
    /// When true, elements with only generic IFC markers are also included, marked
    /// <c>Confidence = "Probable"</c> with explanatory warnings.
    /// </param>
    public List<IfcSpaceDetectionResult> Collect(
        Document linkedDoc,
        bool includeProbable = false)
    {
        var candidates = new FilteredElementCollector(linkedDoc)
            .OfCategory(BuiltInCategory.OST_GenericModel)
            .WhereElementIsNotElementType()
            .ToElements();

        var result = new List<IfcSpaceDetectionResult>(candidates.Count);

        foreach (var element in candidates)
        {
            var detection = Detect(element);

            if (detection.Confidence == IfcDetectionConfidence.Confirmed)
            {
                result.Add(new IfcSpaceDetectionResult
                {
                    Element    = element,
                    Confidence = IfcDetectionConfidence.Confirmed,
                    Reason     = detection.Reason
                });
            }
            else if (detection.Confidence == IfcDetectionConfidence.Probable && includeProbable)
            {
                result.Add(new IfcSpaceDetectionResult
                {
                    Element    = element,
                    Confidence = IfcDetectionConfidence.Probable,
                    Reason     = detection.Reason,
                    Warnings   =
                    [
                        $"Element {element.Id.Value} ({element.Name}) is a probable IFC Space: " +
                        "IFC-origin markers found but no explicit IfcSpace type parameter. " +
                        "Verify manually before converting."
                    ]
                });
            }
            // Rejected elements are silently dropped
        }

        return result;
    }

    /// <summary>Classifies one element using both legacy and project IFC conventions.</summary>
    public IfcConventionDetection Detect(Element element) =>
        IfcSpaceConventionResolver.Detect(
            IfcParameterReader.ReadParameters(element), element.Name);

    // ── Legacy compatibility ───────────────────────────────────────────────────

    /// <summary>
    /// Legacy overload kept for backward compatibility with callers that pass a warnings list.
    /// Returns only element references; probable-element warnings are appended to
    /// <paramref name="warnings"/>. New code should call
    /// <see cref="Collect(Document, bool)"/> directly.
    /// </summary>
    [Obsolete("Use Collect(Document, bool) instead.")]
    public List<Element> Collect(Document linkedDoc, List<string> warnings)
    {
        var results = Collect(linkedDoc, includeProbable: true);
        foreach (var r in results.Where(r => r.Confidence == IfcDetectionConfidence.Probable))
            warnings.AddRange(r.Warnings);
        return results.Select(r => r.Element).ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────

    private enum Classification { Confirmed, Probable, Rejected }

    private static Classification Classify(Element element)
    {
        // 1. Check explicit IFC type parameters for "IfcSpace"
        foreach (var pName in IfcTypeParamNames)
        {
            var value = TryGetString(element, pName);
            if (ContainsIfcSpace(value))
                return Classification.Confirmed;
        }

        // 2. Also check element Name (built-in) — some exporters set the element/family
        //    Name to "IfcSpace" rather than using a custom parameter
        if (ContainsIfcSpace(element.Name))
            return Classification.Confirmed;

        // 3. Fallback: check for generic IFC presence markers
        foreach (var pName in IfcPresenceParamNames)
        {
            var value = TryGetString(element, pName);
            if (!string.IsNullOrWhiteSpace(value))
                return Classification.Probable;
        }

        return Classification.Rejected;
    }

    private static bool ContainsIfcSpace(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.IndexOf("IfcSpace", StringComparison.OrdinalIgnoreCase) >= 0;

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
