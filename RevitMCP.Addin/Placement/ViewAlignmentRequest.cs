using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Tools;

namespace RevitMCP.Addin.Placement;

/// <summary>What to line up, how, and against what.</summary>
internal sealed class ViewAlignmentOptions
{
    /// <summary>Canonical mode from <see cref="ViewAlignmentMath"/>.</summary>
    public string Mode { get; set; } = ViewAlignmentMath.ModeLeft;

    /// <summary>Canonical reference, or <c>element</c> when one element was nominated.</summary>
    public string Reference { get; set; } = ViewAlignmentMath.ReferenceExtreme;

    /// <summary>Index into the resolved element list; only meaningful for the <c>element</c> reference.</summary>
    public int ReferenceIndex { get; set; }

    public string Spread { get; set; } = ViewAlignmentMath.SpreadCenters;

    /// <summary>Fixed distribution spacing in feet, or null to fill the span the elements occupy.</summary>
    public double? SpacingFt { get; set; }

    /// <summary><c>auto</c>, <c>boundingBox</c>, or <c>origin</c>.</summary>
    public string Anchor { get; set; } = ViewAlignmentRequest.AnchorAuto;

    public bool IsDistribute => ViewAlignmentMath.IsDistribute(Mode);
}

/// <summary>
/// Argument parsing shared by the in-view align preview and write tools, so the two always resolve
/// the same elements, the same view, and the same options.
/// </summary>
internal static class ViewAlignmentRequest
{
    public const int MaxElements = 500;

    public const string AnchorAuto = "auto";
    public const string AnchorBoundingBox = "boundingBox";
    public const string AnchorOrigin = "origin";

    /// <summary>Slides under this are numerical noise — the element is already in line.</summary>
    public const double NegligibleMoveMm = 0.1;

    public static double MmToFt(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

    public static double FtToMm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);

    /// <summary>
    /// Reads the options. Returns null with <paramref name="error"/> set when the request cannot be
    /// honoured at all; softer problems are appended to <paramref name="warnings"/>.
    /// <paramref name="elementCount"/> and <paramref name="referenceElementIndex"/> come from the
    /// already-resolved element list, so "first"/"last" and an explicit reference element can be
    /// turned into an index here.
    /// </summary>
    public static ViewAlignmentOptions? ParseOptions(
        Dictionary<string, object?> arguments,
        int elementCount,
        int referenceElementIndex,
        List<string> warnings,
        out string? error)
    {
        error = null;

        var rawMode = ToolArguments.GetString(arguments, "mode");
        var mode = ViewAlignmentMath.NormalizeMode(rawMode);
        if (mode == null)
        {
            error = string.IsNullOrWhiteSpace(rawMode)
                ? $"mode is required. Valid: {string.Join(", ", ViewAlignmentMath.AllModes)}."
                : $"Unknown mode '{rawMode}'. Valid: {string.Join(", ", ViewAlignmentMath.AllModes)}.";
            return null;
        }

        var options = new ViewAlignmentOptions { Mode = mode };

        var rawAnchor = ToolArguments.GetString(arguments, "anchor", AnchorAuto);
        var anchor = NormalizeAnchor(rawAnchor);
        if (anchor == null)
        {
            error = $"Unknown anchor '{rawAnchor}'. Valid: auto, boundingBox, origin.";
            return null;
        }
        options.Anchor = anchor;

        var rawSpread = ToolArguments.GetString(arguments, "spread", ViewAlignmentMath.SpreadCenters);
        var spread = ViewAlignmentMath.NormalizeSpread(rawSpread);
        if (spread == null)
        {
            error = $"Unknown spread '{rawSpread}'. Valid: centers, gaps.";
            return null;
        }
        options.Spread = spread;

        var spacingMm = ToolArguments.GetDouble(arguments, "spacingMm");
        if (spacingMm < 0)
        {
            error = $"spacingMm must be positive; {spacingMm:F0} was given. The layout direction " +
                    "comes from the mode, not the sign of the spacing.";
            return null;
        }
        if (spacingMm > 1e-9)
            options.SpacingFt = MmToFt(spacingMm);

        var rawReference = ToolArguments.GetString(arguments, "alignTo", ViewAlignmentMath.ReferenceExtreme);
        var reference = ViewAlignmentMath.NormalizeReference(rawReference);
        if (reference == null)
        {
            error = $"Unknown alignTo '{rawReference}'. Valid: extreme, first, last, min, max, average.";
            return null;
        }

        if (referenceElementIndex >= 0)
        {
            options.Reference = ViewAlignmentMath.ReferenceElement;
            options.ReferenceIndex = referenceElementIndex;
            if (reference != ViewAlignmentMath.ReferenceExtreme)
                warnings.Add($"Both referenceElementId and alignTo='{rawReference}' were given; the reference element wins.");
        }
        else if (reference == "first" || reference == "last")
        {
            options.Reference = ViewAlignmentMath.ReferenceElement;
            options.ReferenceIndex = reference == "first" ? 0 : Math.Max(0, elementCount - 1);
        }
        else
        {
            options.Reference = reference;
        }

        if (options.IsDistribute)
        {
            if (referenceElementIndex >= 0 || reference != ViewAlignmentMath.ReferenceExtreme)
            {
                warnings.Add(
                    $"alignTo/referenceElementId does not apply to '{mode}' — the two outermost " +
                    "elements define the span, and everything between them is respaced.");
            }

            if (elementCount < 3 && !options.SpacingFt.HasValue)
            {
                error = $"'{mode}' needs at least 3 elements to spread between, or a spacingMm to lay them out at.";
                return null;
            }
        }
        else if (options.SpacingFt.HasValue)
        {
            warnings.Add($"spacingMm only applies to the distribute modes — ignored for '{mode}'.");
            options.SpacingFt = null;
        }

        return options;
    }

    private static string? NormalizeAnchor(string raw)
    {
        var key = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return key switch
        {
            "" or "auto" => AnchorAuto,
            "boundingbox" or "bbox" or "box" or "extents" => AnchorBoundingBox,
            "origin" or "point" or "head" or "insertion" => AnchorOrigin,
            _ => null
        };
    }

    /// <summary>
    /// Picks the view the alignment happens in. Everything is measured and moved in this view's
    /// plane, so it decides what "left" and "up" mean.
    /// </summary>
    public static View? ResolveView(
        UIDocument uidoc,
        Dictionary<string, object?> arguments,
        List<string> warnings,
        out string? error)
    {
        error = null;
        var doc = uidoc.Document;
        var viewId = ToolArguments.GetLong(arguments, "viewId");

        View? view;
        if (viewId > 0)
        {
            view = doc.GetElement(new ElementId(viewId)) as View;
            if (view == null)
            {
                error = $"Element {viewId} is not a view.";
                return null;
            }
        }
        else
        {
            view = uidoc.ActiveView;
            if (view == null)
            {
                error = "No active view. Pass viewId.";
                return null;
            }
        }

        if (view.IsTemplate)
        {
            error = $"View '{view.Name}' is a view template — it has no drawing area to align in.";
            return null;
        }

        if (view is View3D { IsPerspective: true })
        {
            error = $"View '{view.Name}' is a perspective view. Screen positions there are not a " +
                    "plane the model can be moved in — use an orthographic view.";
            return null;
        }

        if (view is View3D)
        {
            warnings.Add(
                $"'{view.Name}' is a 3D view: elements are moved in the plane of the screen, which " +
                "is rarely along a model axis. A plan, section, elevation, drafting view or sheet " +
                "gives predictable results.");
        }

        var right = view.RightDirection;
        var up = view.UpDirection;
        if (right == null || up == null || right.IsZeroLength() || up.IsZeroLength())
        {
            error = $"View '{view.Name}' does not report a usable orientation, so there is no " +
                    "left/right or up/down to align along.";
            return null;
        }

        return view;
    }

    /// <summary>
    /// Resolves the elements to line up. Order is preserved — "first" and "last" refer to it —
    /// and anything owned by a different view is dropped, because a move measured in this view's
    /// plane means nothing to an annotation that lives somewhere else.
    /// </summary>
    public static List<Element>? ResolveElements(
        UIDocument uidoc,
        View view,
        Dictionary<string, object?> arguments,
        List<string> warnings,
        out string? error)
    {
        error = null;
        var doc = uidoc.Document;
        var elementIds = ToolArguments.GetLongArray(arguments, "elementIds");
        var useSelection = ToolArguments.GetBool(arguments, "useSelection");

        List<ElementId> ids;
        if (elementIds.Length > 0)
        {
            ids = elementIds.Distinct().Select(id => new ElementId(id)).ToList();
        }
        else if (useSelection)
        {
            ids = uidoc.Selection.GetElementIds().ToList();
            if (ids.Count == 0)
            {
                error = "useSelection=true but nothing is selected in Revit.";
                return null;
            }

            // Revit hands back a set, not a picking order, so "the first one I clicked" is not
            // knowable here — say so rather than align to an arbitrary element.
            var alignTo = ViewAlignmentMath.NormalizeReference(
                ToolArguments.GetString(arguments, "alignTo", ViewAlignmentMath.ReferenceExtreme));
            if (alignTo is "first" or "last" && ToolArguments.GetLong(arguments, "referenceElementId") <= 0)
            {
                warnings.Add(
                    "A Revit selection has no picking order, so alignTo='" + alignTo + "' uses an " +
                    "arbitrary element. Pass referenceElementId, or elementIds in the order you want.");
            }
        }
        else
        {
            error = "Provide elementIds, or set useSelection=true to align the current selection.";
            return null;
        }

        var elements = new List<Element>();
        foreach (var id in ids)
        {
            var element = doc.GetElement(id);
            if (element == null)
            {
                warnings.Add($"Element {id.Value} was not found in this model.");
                continue;
            }
            if (element is ElementType)
            {
                warnings.Add($"Element {id.Value} is a type, not a placed instance — skipped.");
                continue;
            }
            if (element is View)
            {
                warnings.Add($"Element {id.Value} is a view. To line up a view on a sheet, pass its " +
                             "viewport ID and open the sheet.");
                continue;
            }

            if (element.ViewSpecific &&
                element.OwnerViewId != ElementId.InvalidElementId &&
                element.OwnerViewId != view.Id)
            {
                var owner = doc.GetElement(element.OwnerViewId) as View;
                warnings.Add(
                    $"{Describe(element)} belongs to view '{owner?.Name ?? element.OwnerViewId.ToString()}', " +
                    $"not '{view.Name}' — skipped.");
                continue;
            }

            elements.Add(element);
        }

        if (elements.Count == 0)
        {
            error = $"No alignable elements were resolved in view '{view.Name}'.";
            return null;
        }

        if (elements.Count < 2)
        {
            error = "Aligning needs at least 2 elements — one element is already in line with itself.";
            return null;
        }

        if (elements.Count > MaxElements)
        {
            warnings.Add($"Capped at {MaxElements} element(s); {elements.Count - MaxElements} were dropped.");
            elements = elements.Take(MaxElements).ToList();
        }

        return elements;
    }

    /// <summary>
    /// The index of an explicitly nominated reference element within the resolved list, or -1.
    /// Reported as a warning rather than an error when the element is not among those being aligned:
    /// aligning to something outside the set is a legitimate ask, but this tool cannot measure it
    /// without moving it too.
    /// </summary>
    public static int FindReferenceIndex(
        Dictionary<string, object?> arguments,
        List<Element> elements,
        List<string> warnings)
    {
        var referenceId = ToolArguments.GetLong(arguments, "referenceElementId");
        if (referenceId <= 0)
            return -1;

        var index = elements.FindIndex(e => e.Id.Value == referenceId);
        if (index < 0)
        {
            warnings.Add(
                $"referenceElementId {referenceId} is not among the elements being aligned — " +
                "add it to elementIds (it will not move) or drop the argument.");
        }
        return index;
    }

    public static string Describe(Element element)
    {
        var category = element.Category?.Name;
        var name = element.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = category ?? "Element";
        return $"'{name}' ({element.Id.Value})";
    }
}
