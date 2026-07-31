using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Tools;

namespace RevitMCP.Addin.Placement;

/// <summary>
/// Argument parsing shared by the align preview and write tools, so the two always resolve the
/// same elements, the same search view, and the same options.
/// </summary>
internal static class AlignmentRequest
{
    public const double DefaultSearchRadiusMm = 3000.0;
    public const double MaxSearchRadiusMm = 50000.0;
    public const int MaxElements = 500;

    public static double MmToFt(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

    public static double FtToMm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);

    /// <summary>
    /// Reads the options. Returns null with <paramref name="error"/> set when the request cannot
    /// be honoured at all; softer problems are appended to <paramref name="warnings"/>.
    /// </summary>
    public static AlignmentOptions? ParseOptions(
        Dictionary<string, object?> arguments,
        List<string> warnings,
        out string? error)
    {
        error = null;

        var surface = ToolArguments.GetString(arguments, "surface", AlignmentMath.SurfaceNearest)
            .Trim()
            .ToLowerInvariant();
        if (!AlignmentMath.IsKnownSurface(surface))
        {
            error = $"Unknown surface '{surface}'. Valid: wall, ceiling, floor, nearest.";
            return null;
        }

        var requestedRadius = ToolArguments.GetDouble(arguments, "searchRadiusMm", DefaultSearchRadiusMm);
        if (requestedRadius <= 0)
            requestedRadius = DefaultSearchRadiusMm;
        if (requestedRadius > MaxSearchRadiusMm)
        {
            warnings.Add($"searchRadiusMm {requestedRadius:F0} exceeds the cap; using {MaxSearchRadiusMm:F0}.");
            requestedRadius = MaxSearchRadiusMm;
        }

        var scope = ToolArguments.GetString(arguments, "scope", "both").Trim().ToLowerInvariant();
        if (scope is not ("both" or "links" or "host"))
        {
            error = $"Unknown scope '{scope}'. Valid: both, links, host.";
            return null;
        }

        var requestedAngle = ToolArguments.GetDouble(arguments, "angleToleranceDegrees", 30.0);
        var angle = AlignmentMath.ClampAngleTolerance(requestedAngle);
        if (Math.Abs(angle - requestedAngle) > 1e-6)
        {
            warnings.Add(
                $"angleToleranceDegrees {requestedAngle:F1} is outside " +
                $"{AlignmentMath.MinAngleToleranceDegrees:F0}-{AlignmentMath.MaxAngleToleranceDegrees:F0}; using {angle:F1}. " +
                "Above 45 degrees the wall, ceiling, and floor bands would overlap.");
        }

        var samples = ToolArguments.GetInt(arguments, "horizontalSamples", 8);
        if (samples < 4 || samples > 32)
        {
            warnings.Add($"horizontalSamples {samples} is outside 4-32; using 8.");
            samples = 8;
        }

        var options = new AlignmentOptions
        {
            Surface = surface,
            SearchRadiusFt = MmToFt(requestedRadius),
            GapFt = MmToFt(ToolArguments.GetDouble(arguments, "gapMm")),
            HorizontalSamples = samples,
            AngleToleranceDegrees = angle,
            IncludeLinks = scope is "both" or "links",
            IncludeHost = scope is "both" or "host",
            RotateToSurface = ToolArguments.GetBool(arguments, "rotateToSurface")
        };

        foreach (var id in ToolArguments.GetLongArray(arguments, "linkInstanceIds"))
            options.LinkInstanceIds.Add(id);

        if (options.LinkInstanceIds.Count > 0 && !options.IncludeLinks)
            warnings.Add("linkInstanceIds was supplied but scope excludes links — the filter has no effect.");

        foreach (var category in ToolArguments.GetStringArray(arguments, "targetCategories"))
        {
            if (!string.IsNullOrWhiteSpace(category))
                options.TargetCategories.Add(category.Trim());
        }

        return options;
    }

    /// <summary>Resolves the elements to align. They must live in the host model — links are read-only.</summary>
    public static List<Element>? ResolveElements(
        UIDocument uidoc,
        Dictionary<string, object?> arguments,
        List<string> warnings,
        out string? error)
    {
        error = null;
        var doc = uidoc.Document;
        var elementIds = ToolArguments.GetLongArray(arguments, "elementIds");
        var useSelection = ToolArguments.GetBool(arguments, "useSelection");

        ICollection<ElementId> ids;
        if (elementIds.Length > 0)
        {
            ids = elementIds.Distinct().Select(id => new ElementId(id)).ToList();
        }
        else if (useSelection)
        {
            ids = uidoc.Selection.GetElementIds();
            if (ids.Count == 0)
            {
                error = "useSelection=true but nothing is selected in Revit.";
                return null;
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
                warnings.Add($"Element {id.Value} was not found in the host model.");
                continue;
            }
            if (element is ElementType)
            {
                warnings.Add($"Element {id.Value} is a type, not a placed instance — skipped.");
                continue;
            }
            elements.Add(element);
        }

        if (elements.Count == 0)
        {
            error = "No movable host-model elements were resolved. " +
                    "Elements inside a link cannot be moved — open the link's own model for that.";
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
    /// Picks the 3D view the ray casts run in. Ray casting only sees what is visible in that view,
    /// so the choice is reported back and obvious clipping is warned about.
    /// </summary>
    public static View3D? ResolveSearchView(
        UIDocument uidoc,
        Dictionary<string, object?> arguments,
        List<string> warnings,
        out string? error)
    {
        error = null;
        var doc = uidoc.Document;
        var viewId = ToolArguments.GetLong(arguments, "searchViewId");

        View3D? view;
        if (viewId > 0)
        {
            view = doc.GetElement(new ElementId(viewId)) as View3D;
            if (view == null)
            {
                error = $"Element {viewId} is not a 3D view. Ray casting needs a 3D view.";
                return null;
            }
            if (view.IsTemplate)
            {
                error = $"View '{view.Name}' is a view template and cannot be used for ray casting.";
                return null;
            }
        }
        else if (uidoc.ActiveView is View3D active && !active.IsTemplate)
        {
            view = active;
        }
        else
        {
            view = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate);

            if (view == null)
            {
                error = "This model has no 3D view. Ray casting needs one — create a 3D view, " +
                        "or pass searchViewId.";
                return null;
            }
        }

        try
        {
            if (view.IsSectionBoxActive)
            {
                warnings.Add(
                    $"3D view '{view.Name}' has an active section box. Geometry outside it is invisible to " +
                    "the search and will not be found — pass searchViewId for an unclipped view if targets go missing.");
            }
        }
        catch { }

        return view;
    }
}
