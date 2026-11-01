using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Annotation;

/// <summary>A box in a view's plane, in mm.</summary>
public readonly struct ViewBox
{
    public ViewBox(double minX, double minY, double maxX, double maxY)
    {
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
    }

    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }
}

/// <summary>
/// The mm coordinates annotation tools use in a view's plane. In a view looking along Z (plan, drafting, legend, sheet)
/// they are the model's (or sheet's) x and y. In a section or elevation they are measured along the view's right and up
/// directions from the model origin, so a point keeps its coordinates between tools.
/// </summary>
internal sealed class ViewPlaneFrame
{
    private const double FeetToMm = 304.8;

    private readonly XYZ _right;
    private readonly XYZ _up;
    private readonly XYZ _direction;
    private readonly double _depth;

    public ViewPlaneFrame(View view)
    {
        IsHorizontal = Math.Abs(view.ViewDirection.Z) > 0.999;
        _right = IsHorizontal ? XYZ.BasisX : view.RightDirection;
        _up = IsHorizontal ? XYZ.BasisY : view.UpDirection;
        _direction = IsHorizontal ? XYZ.BasisZ : view.ViewDirection;
        _depth = view.Origin.DotProduct(_direction);
    }

    public bool IsHorizontal { get; }

    /// <summary>"modelXY" or "viewRightUp": what the coordinates of this frame mean.</summary>
    public string Name => IsHorizontal ? "modelXY" : "viewRightUp";

    public XYZ ToModel(double xMm, double yMm) =>
        _right.Multiply(xMm / FeetToMm) + _up.Multiply(yMm / FeetToMm) + _direction.Multiply(_depth);

    public (double X, double Y) FromModel(XYZ point) =>
        (point.DotProduct(_right) * FeetToMm, point.DotProduct(_up) * FeetToMm);

    /// <summary>The element's box in this frame; null when Revit gives the element no box in this view.</summary>
    public ViewBox? BoxOf(Element element, View view)
    {
        BoundingBoxXYZ? box;
        try { box = element.get_BoundingBox(view); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
        if (box == null) return null;

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        foreach (var x in new[] { box.Min.X, box.Max.X })
            foreach (var y in new[] { box.Min.Y, box.Max.Y })
                foreach (var z in new[] { box.Min.Z, box.Max.Z })
                {
                    var (px, py) = FromModel(box.Transform.OfPoint(new XYZ(x, y, z)));
                    minX = Math.Min(minX, px);
                    minY = Math.Min(minY, py);
                    maxX = Math.Max(maxX, px);
                    maxY = Math.Max(maxY, py);
                }
        return new ViewBox(minX, minY, maxX, maxY);
    }
}
