namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Room name handling shared by the IFC space tools. <c>Room.Name</c> (Element.Name) returns the
/// display name "&lt;Name&gt; &lt;Number&gt;", not the Name parameter, so comparing it with an IFC name
/// never matches (#58). No Revit API dependency — unit tested in RevitMCP.Tests.
/// </summary>
public static class RoomNames
{
    /// <summary>
    /// Recovers the room name from Revit's display name by removing a trailing " &lt;number&gt;".
    /// Returns the display name unchanged when it does not end with the number.
    /// </summary>
    public static string FromDisplayName(string? displayName, string? number)
    {
        var display = (displayName ?? string.Empty).Trim();
        var num = (number ?? string.Empty).Trim();
        if (num.Length == 0 || display.Length <= num.Length) return display;

        var suffix = " " + num;
        return display.EndsWith(suffix, StringComparison.Ordinal)
            ? display.Substring(0, display.Length - suffix.Length).TrimEnd()
            : display;
    }
}
