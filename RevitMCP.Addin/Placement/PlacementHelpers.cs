using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Placement;

/// <summary>
/// Shared helpers for the element placement tools. All public coordinates are in
/// millimetres (matching the read tools); conversion to internal feet happens here.
/// </summary>
internal static class PlacementHelpers
{
    public static double MmToFt(double mm) =>
        UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

    public static XYZ PointFromMm(double xMm, double yMm, double zMm) =>
        new(MmToFt(xMm), MmToFt(yMm), MmToFt(zMm));

    /// <summary>
    /// Resolves the target view: explicit viewId when &gt; 0, otherwise the active view.
    /// Rejects view templates and non-graphical views.
    /// </summary>
    public static (View? View, string? Error) ResolveGraphicalView(UIDocument uidoc, Document doc, long viewId)
    {
        View? view;
        if (viewId > 0)
        {
            view = doc.GetElement(new ElementId(viewId)) as View;
            if (view == null)
                return (null, $"Element {viewId} is not a view.");
        }
        else
        {
            view = uidoc.ActiveView;
            if (view == null)
                return (null, "No active view. Pass viewId explicitly.");
        }

        if (view.IsTemplate)
            return (null, $"View '{view.Name}' is a view template — annotations and elements cannot be placed in it.");
        if (view is ViewSchedule)
            return (null, $"View '{view.Name}' is a schedule — nothing can be placed in it.");

        return (view, null);
    }

    /// <summary>Finds the level nearest to the given elevation (internal units), or null when the model has none.</summary>
    public static Level? NearestLevel(Document doc, double elevationFt)
    {
        Level? best = null;
        double bestDist = double.MaxValue;
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(Level)))
        {
            if (element is not Level level) continue;
            var dist = Math.Abs(level.Elevation - elevationFt);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = level;
            }
        }
        return best;
    }

    /// <summary>Reads a double property from a JSON token, accepting both camelCase key and 0 default.</summary>
    public static double TokenDouble(JToken token, string key, double defaultValue = 0.0)
    {
        var val = token[key];
        if (val == null || val.Type == JTokenType.Null) return defaultValue;
        try { return val.Value<double>(); }
        catch { return defaultValue; }
    }

    public static string TokenString(JToken token, string key, string defaultValue = "")
    {
        var val = token[key];
        if (val == null || val.Type == JTokenType.Null) return defaultValue;
        try { return val.Value<string>() ?? defaultValue; }
        catch { return defaultValue; }
    }
}
