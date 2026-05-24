namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Match confidence level constants for IFC Space → Revit Room comparison.
/// Used by Phase 4 validation and sync safety checks.
///
/// Scoring is based exclusively on built-in Room fields (Level, Number, Name)
/// plus optional geometry comparison (location, area).
/// No shared parameters, IFC GUIDs, Comments, or Extensible Storage are used.
/// </summary>
public static class RoomMatchConfidence
{
    /// <summary>
    /// Same Level + same Number + same Name.
    /// Geometry (location/area) may additionally confirm this match.
    /// Eligible for safe built-in field updates.
    /// </summary>
    public const string High = "High";

    /// <summary>
    /// Same Level + same Number (Name differs), or
    /// same Level + same Name (Number differs),
    /// with location and/or area within configured tolerance.
    /// Updates require explicit <c>allowMediumConfidenceUpdates = true</c>.
    /// </summary>
    public const string Medium = "Medium";

    /// <summary>
    /// Same Level + location and area within tolerance, but Number and Name differ.
    /// Updates require explicit <c>allowLowConfidenceUpdates = true</c> and are strongly discouraged.
    /// </summary>
    public const string Low = "Low";

    /// <summary>
    /// More than one Room could plausibly match the IFC Space.
    /// Automatic updates are always blocked — requires manual resolution.
    /// </summary>
    public const string Ambiguous = "Ambiguous";

    /// <summary>No Room matched this IFC Space.</summary>
    public const string None = "None";
}
