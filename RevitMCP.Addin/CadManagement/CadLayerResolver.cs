using Autodesk.Revit.DB;

namespace RevitMCP.Addin.CadManagement;

/// <summary>
/// Maps CAD geometry back to the layer it was drawn on, caching the graphics-style lookup because a
/// drawing walk hits the same handful of styles thousands of times.
/// </summary>
internal sealed class CadLayerResolver
{
    public const string NoLayer = "(no layer)";

    private readonly Document _doc;
    private readonly Dictionary<long, string> _layerNames = new();

    public CadLayerResolver(Document doc)
    {
        _doc = doc;
    }

    /// <summary>The CAD layer behind a geometry object, read from its graphics style.</summary>
    public string? LayerOf(GeometryObject obj)
    {
        ElementId styleId;
        try { styleId = obj.GraphicsStyleId; }
        catch { return null; }

        if (styleId == null || styleId == ElementId.InvalidElementId)
            return null;

        if (_layerNames.TryGetValue(styleId.Value, out var cached))
            return cached;

        string? name = null;
        try
        {
            if (_doc.GetElement(styleId) is GraphicsStyle style)
                name = style.GraphicsStyleCategory?.Name;
        }
        catch { }

        if (string.IsNullOrEmpty(name))
            return null;

        _layerNames[styleId.Value] = name!;
        return name;
    }

    /// <summary>
    /// A block reference often carries no graphics style of its own. Its contents do, and a symbol
    /// block is drawn on one layer, so the first primitive inside names the block's layer.
    /// </summary>
    public string? FirstLayerInside(GeometryInstance instance)
    {
        GeometryElement? symbolGeometry;
        try { symbolGeometry = instance.GetSymbolGeometry(); }
        catch { return null; }

        if (symbolGeometry == null)
            return null;

        foreach (var obj in symbolGeometry)
        {
            if (obj is GeometryInstance nested)
            {
                var deeper = LayerOf(nested) ?? FirstLayerInside(nested);
                if (deeper != null)
                    return deeper;
                continue;
            }

            var layer = LayerOf(obj);
            if (layer != null)
                return layer;
        }

        return null;
    }
}
