using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Result from <see cref="IfcSpaceCollector.Collect"/> for one element.
/// Carries the Revit element reference alongside its detection confidence and any
/// per-element warnings that explain why the detection is less than Confirmed.
/// This is an internal service-layer model — Revit types are acceptable here.
/// </summary>
public class IfcSpaceDetectionResult
{
    /// <summary>The Generic Model / DirectShape element.</summary>
    public Element Element { get; init; } = null!;

    /// <summary>
    /// Detection confidence: "Confirmed" when an explicit IfcSpace type parameter is found;
    /// "Probable" when only generic IFC-origin markers are present.
    /// </summary>
    public string Confidence { get; init; } = IfcDetectionConfidence.Confirmed;

    /// <summary>Per-element advisory messages explaining the detection.</summary>
    public List<string> Warnings { get; init; } = new();
}

/// <summary>Detection confidence constants for IFC Space classification.</summary>
public static class IfcDetectionConfidence
{
    /// <summary>An explicit IfcSpace type/entity parameter was found on this element.</summary>
    public const string Confirmed = "Confirmed";

    /// <summary>
    /// Only generic IFC-origin markers (GUID, IfcName, etc.) were found.
    /// The element may be any IFC entity, not necessarily IfcSpace.
    /// </summary>
    public const string Probable = "Probable";
}
