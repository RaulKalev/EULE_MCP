namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Top-level JSON-serializable result for the <c>validate_ifc_space_room_conversion</c> endpoint.
/// </summary>
public class IfcRoomValidationResult
{
    /// <summary>True when the validation run completed without a fatal error.</summary>
    public bool Success { get; set; }

    /// <summary>Set when a fatal error prevented the run from starting (e.g. link not found).</summary>
    public string? Error { get; set; }

    /// <summary>Link instance id that was validated.</summary>
    public long LinkInstanceId { get; set; }

    /// <summary>Aggregate counts for all processed spaces.</summary>
    public IfcRoomValidationSummary Summary { get; set; } = new();

    /// <summary>Detailed result for each processed IFC Space candidate.</summary>
    public List<IfcRoomValidationItem> Items { get; set; } = new();
}
