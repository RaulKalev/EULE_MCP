namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// One IFC Space candidate detected from a linked document.
/// Contains metadata, level match result, and conversion-readiness flags.
/// Phase 1: read-only, no modifications to the host document.
/// </summary>
public class IfcSpaceCandidate
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>Revit element id of the RevitLinkInstance.</summary>
    public long LinkInstanceId { get; set; }

    /// <summary>Revit element id of the Generic Model / DirectShape inside the linked document.</summary>
    public long LinkedElementId { get; set; }

    // ── IFC Metadata ─────────────────────────────────────────────────────────

    /// <summary>IFC GlobalId / GUID of the original IfcSpace, if available.</summary>
    public string? IfcGuid { get; set; }

    /// <summary>Room number / reference, e.g. "101".</summary>
    public string? Number { get; set; }

    /// <summary>Room name / long name, e.g. "Office".</summary>
    public string? Name { get; set; }

    /// <summary>Building storey name from the IFC model, e.g. "Level 1".</summary>
    public string? StoreyName { get; set; }

    // ── Level Match ───────────────────────────────────────────────────────────

    /// <summary>Revit element id of the matched host Level, or null if unmatched.</summary>
    public long? TargetLevelId { get; set; }

    /// <summary>Name of the matched host Level.</summary>
    public string? TargetLevelName { get; set; }

    /// <summary>Approximate bottom elevation in host coordinates, in millimetres.</summary>
    public double? BottomElevationMm { get; set; }

    /// <summary>Vertical offset from the matched Level to the space bottom, in millimetres.</summary>
    public double? LevelOffsetMm { get; set; }

    // ── Geometry (approximate, Phase 1 only) ─────────────────────────────────

    /// <summary>
    /// Approximate floor area derived from the host-transformed bounding box (width × depth).
    /// This is an approximation only — do not use for quantity take-off.
    /// </summary>
    public double? ApproxAreaM2 { get; set; }

    // ── Existing Room Check ───────────────────────────────────────────────────

    /// <summary>
    /// Result of comparing this candidate against existing Rooms in the host document.
    /// Possible values (Phase 1): NoMatch, ExactNumberNameLevelMatch,
    /// NumberLevelMatchNameDifferent, Skipped.
    /// </summary>
    public string ExistingRoomMatch { get; set; } = "Skipped";

    // ── Detection Metadata ────────────────────────────────────────────────────

    /// <summary>
    /// How confident the collector is that this element is an IfcSpace.
    /// "Confirmed" — explicit IfcSpace type parameter found (default).
    /// "Probable"  — only generic IFC-origin markers found; may be any IFC entity.
    /// </summary>
    public string DetectionConfidence { get; set; } = "Confirmed";

    // ── Metadata Sources ──────────────────────────────────────────────────────

    /// <summary>
    /// Parameter name from which <see cref="Number"/> was read.
    /// Null when Number is null. Helps operators understand the data source.
    /// </summary>
    public string? NumberSource { get; set; }

    /// <summary>
    /// Parameter name from which <see cref="Name"/> was read.
    /// Null when Name is null. Helps operators understand the data source.
    /// </summary>
    public string? NameSource { get; set; }

    // ── Conversion Readiness ──────────────────────────────────────────────────

    /// <summary>True when this candidate could be converted in a later Phase without blocking issues.</summary>
    public bool CanConvertLater { get; set; }

    /// <summary>
    /// Overall status for this candidate:
    /// Ready, AlreadyExists, Warning, MissingMetadata, NoLevelMatch, NotIfcSpace, Error.
    /// </summary>
    public string Status { get; set; } = "Error";

    /// <summary>Non-blocking advisory messages about this candidate.</summary>
    public List<string> Warnings { get; set; } = new();
}
