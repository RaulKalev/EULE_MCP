namespace RevitMCP.Bridge;

/// <summary>
/// Model-family authoring (#168, docs/model-families.md): what the tools of every feature share. Each feature has its
/// own partial file (RevitMcpTools.ModelFamily*.cs).
/// </summary>
internal sealed partial class RevitMcpTools
{
    /// <summary>The target argument of every tool that edits a model family.</summary>
    private const string ModelFamilyTargetDescription =
        "Which family, and what happens to the result. One of {family} (loaded in the project: edited as a copy and loaded back as one undo step), " +
        "{path} (an .rfa not open in Revit: needs overwrite=true to save back, or savePath) or {familyDocument} (title of an open family document, or \"active\": " +
        "edited in place, saved only with save=true or savePath). Optional: savePath (folder or .rfa), overwrite, load (load the result into the project), " +
        "overwriteParameterValues. E.g. {\"path\":\"C:\\fam\\Device.rfa\",\"overwrite\":true}.";

    /// <summary>The target argument of a tool that only reads a model family.</summary>
    private const string ModelFamilyReadTargetDescription =
        "Which family: {family} (loaded in the project), {path} (an .rfa file) or {familyDocument} (title of an open family document, or \"active\").";

    private const string ModelFamilyUnitsNote = "Lengths in millimetres, angles in degrees, points [x, y, z] from the family origin.";

    /// <summary>The arguments every model-family tool sends: its target and the project document.</summary>
    private static Dictionary<string, object?> ModelFamilyArgs(object? target, string? document) => new()
    {
        ["target"] = ToJToken(target),
        ["document"] = document ?? string.Empty
    };
}
