using Autodesk.Revit.DB.Architecture;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Writes built-in Room parameters (Number and Name) to a newly created Room.
/// IMPORTANT: only built-in Room fields are written.
/// No shared parameters, no IFC GUIDs, no Comments, no Extensible Storage.
/// Caller must supply an open Transaction; this class does not open or close transactions.
/// Phase 3.
/// </summary>
public static class RoomParameterWriter
{
    /// <summary>
    /// Sets <c>room.Number</c> and <c>room.Name</c> from IFC metadata.
    /// </summary>
    /// <param name="room">The newly created Room element.</param>
    /// <param name="number">
    /// Value to assign to <c>room.Number</c>. Skipped when null or whitespace.
    /// </param>
    /// <param name="name">
    /// Value to assign to <c>room.Name</c>. Skipped when null or whitespace.
    /// </param>
    /// <param name="warnings">Receives advisory messages when a parameter could not be set.</param>
    /// <returns>True when all supplied non-null values were written without exception.</returns>
    public static bool Write(Room room, string? number, string? name, List<string> warnings)
    {
        bool allOk = true;

        if (!string.IsNullOrWhiteSpace(number))
        {
            try
            {
                room.Number = number!.Trim();
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not set Room Number to '{number}': {ex.Message}");
                allOk = false;
            }
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            try
            {
                room.Name = name!.Trim();
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not set Room Name to '{name}': {ex.Message}");
                allOk = false;
            }
        }

        return allOk;
    }
}
