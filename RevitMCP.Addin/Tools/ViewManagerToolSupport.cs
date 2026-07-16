using Newtonsoft.Json.Linq;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools;

internal static class ViewManagerToolSupport
{
    public static List<View> ResolveViews(
        Document doc,
        Dictionary<string, object?> arguments,
        out string? error)
    {
        var viewIds = ToolArguments.GetLongArray(arguments, "viewIds");
        var viewTypes = ToolArguments.GetStringArray(arguments, "viewTypes");
        var nameFilter = ToolArguments.GetString(arguments, "nameFilter");
        var allViews = ToolArguments.GetBool(arguments, "allViews", false);

        if (viewIds.Length == 0 && viewTypes.Length == 0 &&
            string.IsNullOrWhiteSpace(nameFilter) && !allViews)
        {
            error = "Provide viewIds, viewTypes, nameFilter, or allViews=true.";
            return new List<View>();
        }

        if (viewIds.Length > 0)
        {
            error = null;
            return viewIds.Distinct()
                .Select(id => doc.GetElement(new ElementId(id)) as View)
                .Where(view => IsManagedView(view))
                .Cast<View>()
                .ToList();
        }

        IEnumerable<View> views = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(IsManagedView);

        if (viewTypes.Length > 0)
            views = views.Where(view => viewTypes.Any(type =>
                string.Equals(view.ViewType.ToString(), type, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(nameFilter))
            views = views.Where(view =>
                view.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));

        error = null;
        return views.ToList();
    }

    internal static bool IsManagedView(View? view)
    {
        return view != null &&
               !view.IsTemplate &&
               view.CanBePrinted &&
               view.ViewType != ViewType.DrawingSheet &&
               view.ViewType != ViewType.Internal &&
               view.ViewType != ViewType.ProjectBrowser;
    }

    public static bool TryParseDuplicateOption(string value, out Autodesk.Revit.DB.ViewDuplicateOption option)
    {
        if (string.Equals(value, "DuplicateWithDetailing", StringComparison.OrdinalIgnoreCase))
            value = "WithDetailing";
        return Enum.TryParse(value, true, out option);
    }

    public static Dictionary<string, string> GetStringDictionary(
        Dictionary<string, object?> arguments,
        string key)
    {
        if (!arguments.TryGetValue(key, out var raw) || raw == null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        JObject? json;
        try
        {
            json = raw switch
            {
                JObject obj => obj,
                JToken token => token as JObject,
                string text => ToolArguments.TryParseJObject(text),
                _ => JObject.FromObject(raw)
            };
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (json == null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return json.Properties().ToDictionary(
            property => property.Name,
            property => property.Value.Type == JTokenType.Null
                ? string.Empty
                : property.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);
    }

    public static string ApplyCopyIndex(string value, int index, int copies)
    {
        if (value.Contains("{index}", StringComparison.OrdinalIgnoreCase))
            return value.Replace("{index}", index.ToString(), StringComparison.OrdinalIgnoreCase);
        return copies > 1 ? $"{value} {index}" : value;
    }

    public static string ResolveUniqueName(string candidate, HashSet<string> takenNames)
    {
        if (!takenNames.Contains(candidate)) return candidate;
        for (var index = 1; ; index++)
        {
            var resolved = $"{candidate} {index}";
            if (!takenNames.Contains(resolved)) return resolved;
        }
    }
}
