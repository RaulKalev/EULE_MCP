namespace RevitMCP.Bridge;

// #136: the view argument of revit_count_elements, revit_get_elements_info and revit_find_elements_by_parameter.
internal sealed partial class RevitMcpTools
{
    private const string ViewArgDescription =
        "Read what this view shows instead of the whole model: 'active', a view id or name, or a sheet id, number or name (then per viewport, plus the sheet's own elements). Hidden elements and linked model elements are not returned.";

    private const string ViewScopeArgDescription =
        "With view: model, annotation (tags, text, detail lines and other view-specific elements) or all (default).";

    private const string ViewIncludeRoomArgDescription =
        "With view: add the room each model element is in. Default false.";

    /// <summary>Only sent with view, so a call without it carries the same arguments as before.</summary>
    private static void AddViewArgs(Dictionary<string, object?> args, string? view, string? scope, bool includeRoom = false)
    {
        if (string.IsNullOrWhiteSpace(view)) return;
        args["view"] = view;
        args["scope"] = scope ?? string.Empty;
        args["includeRoom"] = includeRoom;
    }

    /// <summary>Without view, instance parameters stay on by default; with view they come on request.</summary>
    private static bool ViewReadDefaultsToInstanceParameters(string? view, string[]? parameterNames) =>
        string.IsNullOrWhiteSpace(view) || parameterNames is { Length: > 0 };
}
