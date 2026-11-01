using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Annotation;

/// <summary>One line of revit_set_line_geometry: new endpoints in mm. A missing z keeps the line in its own plane.</summary>
public sealed class LineGeometryEdit
{
    public long ElementId { get; set; }
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public double? Z1 { get; set; }
    public double? Z2 { get; set; }
}

/// <summary>A rectangle in the view's plane: its corners in order around it, in mm.</summary>
public sealed class RectanglePlan
{
    public IReadOnlyList<(double X, double Y)> Corners { get; set; } = Array.Empty<(double, double)>();
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double RotationDegrees { get; set; }
}

/// <summary>Argument validation and geometry of the line edit and rectangle tools (#140). Pure, unit tested.</summary>
public static class LineShapePlanner
{
    /// <summary>Reads <c>[{elementId, x1, y1, x2, y2, z1?, z2?}]</c>; null with the reason when any line is unusable.</summary>
    public static List<LineGeometryEdit>? ParseEdits(JArray? lines, out string? error)
    {
        error = null;
        if (lines == null || lines.Count == 0)
        {
            error = "Provide 'lines': a JSON array of {elementId, x1, y1, x2, y2} in millimetres (z1, z2 optional).";
            return null;
        }

        var edits = new List<LineGeometryEdit>();
        var seen = new HashSet<long>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i] as JObject;
            var id = Number(line, "elementId");
            var x1 = Number(line, "x1");
            var y1 = Number(line, "y1");
            var x2 = Number(line, "x2");
            var y2 = Number(line, "y2");
            if (id == null || x1 == null || y1 == null || x2 == null || y2 == null)
            {
                error = $"Line {i + 1}: elementId, x1, y1, x2 and y2 are all required.";
                return null;
            }
            if (!seen.Add((long)id.Value))
            {
                error = $"Line {i + 1}: element {(long)id.Value} is listed twice.";
                return null;
            }
            edits.Add(new LineGeometryEdit
            {
                ElementId = (long)id.Value,
                X1 = x1.Value,
                Y1 = y1.Value,
                X2 = x2.Value,
                Y2 = y2.Value,
                Z1 = Number(line, "z1"),
                Z2 = Number(line, "z2")
            });
        }
        return edits;
    }

    /// <summary>
    /// A rectangle from {minX, minY, maxX, maxY} or from a centre, width, height and rotation; null with the reason
    /// when neither form is complete, both are given, or the rectangle has no size.
    /// </summary>
    public static RectanglePlan? PlanRectangle(
        double? minX, double? minY, double? maxX, double? maxY,
        double? centerX, double? centerY, double? width, double? height, double rotationDegrees,
        out string? error)
    {
        error = null;
        var anyBox = minX != null || minY != null || maxX != null || maxY != null;
        var anyCenter = centerX != null || centerY != null || width != null || height != null;
        if (anyBox && anyCenter)
        {
            error = "Give either minX, minY, maxX, maxY or center, width, height, not both.";
            return null;
        }

        double cx, cy, w, h;
        if (anyBox)
        {
            if (minX == null || minY == null || maxX == null || maxY == null)
            {
                error = "minX, minY, maxX and maxY are all required.";
                return null;
            }
            if (rotationDegrees != 0)
            {
                error = "rotationDeg needs center, width and height: a min/max box is not rotated.";
                return null;
            }
            w = maxX.Value - minX.Value;
            h = maxY.Value - minY.Value;
            cx = (minX.Value + maxX.Value) / 2;
            cy = (minY.Value + maxY.Value) / 2;
        }
        else if (anyCenter)
        {
            if (centerX == null || centerY == null || width == null || height == null)
            {
                error = "center {x, y}, width and height are all required.";
                return null;
            }
            cx = centerX.Value;
            cy = centerY.Value;
            w = width.Value;
            h = height.Value;
        }
        else
        {
            error = "Give minX, minY, maxX, maxY, or center, width, height (optional rotationDeg).";
            return null;
        }

        if (w <= 0 || h <= 0)
        {
            error = $"The rectangle has no size ({w:0.###} x {h:0.###} mm): width and height must be above zero, and max above min.";
            return null;
        }

        var radians = rotationDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        (double, double) Corner(double u, double v) => (cx + u * cos - v * sin, cy + u * sin + v * cos);
        return new RectanglePlan
        {
            Corners = new[] { Corner(-w / 2, -h / 2), Corner(w / 2, -h / 2), Corner(w / 2, h / 2), Corner(-w / 2, h / 2) },
            CenterX = cx,
            CenterY = cy,
            Width = w,
            Height = h,
            RotationDegrees = rotationDegrees
        };
    }

    private static double? Number(JObject? token, string key)
    {
        var value = token?[key];
        if (value == null || value.Type == JTokenType.Null) return null;
        try { return value.Value<double>(); }
        catch (Exception) { return null; }
    }
}
