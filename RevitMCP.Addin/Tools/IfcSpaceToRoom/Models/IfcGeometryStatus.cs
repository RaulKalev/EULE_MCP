namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Geometry extraction status constants used by Phase 2 services and MCP responses.
/// Only <see cref="GeometryReady"/> items should be eligible for Phase 3 conversion.
/// </summary>
public static class IfcGeometryStatus
{
    /// <summary>A valid, closed footprint loop was extracted and is ready for Phase 3 room creation.</summary>
    public const string GeometryReady = "GeometryReady";

    /// <summary>The element has no geometry at all (get_Geometry returned null).</summary>
    public const string NoGeometry = "NoGeometry";

    /// <summary>The element has geometry but no usable solids (only meshes, curves, etc.).</summary>
    public const string NoSolidGeometry = "NoSolidGeometry";

    /// <summary>No horizontal planar face was found within the face angle tolerance.</summary>
    public const string NoUsableBottomFace = "NoUsableBottomFace";

    /// <summary>A bottom face was found but its edge loops could not form a closed loop.</summary>
    public const string NoClosedLoop = "NoClosedLoop";

    /// <summary>A loop was extracted but failed geometric validation (too few segments, not planar, too small, etc.).</summary>
    public const string InvalidLoop = "InvalidLoop";

    /// <summary>Loop cleanup (tiny segment removal / endpoint snapping) produced an unusable result.</summary>
    public const string LoopCleanupFailed = "LoopCleanupFailed";

    /// <summary>A valid loop was found but no point inside the loop could be determined for Room placement.</summary>
    public const string PlacementPointFailed = "PlacementPointFailed";

    /// <summary>No host Level could be matched to this element's elevation.</summary>
    public const string LevelMatchFailed = "LevelMatchFailed";

    /// <summary>An unexpected exception occurred during processing of this element.</summary>
    public const string Error = "Error";
}
