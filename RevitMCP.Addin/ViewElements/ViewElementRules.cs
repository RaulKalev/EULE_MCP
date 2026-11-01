namespace RevitMCP.Addin.ViewElements;

public enum ViewElementScope
{
    All,
    Model,
    Annotation
}

/// <summary>What the rules need to know about an element (no Revit types, unit-testable).</summary>
public sealed class ViewElementFacts
{
    public bool HasCategory { get; set; }
    /// <summary>The <c>BuiltInCategory</c> name (<c>OST_Walls</c>); empty for a user or import category.</summary>
    public string BuiltInCategory { get; set; } = string.Empty;
    /// <summary>The <c>CategoryType</c> name: Model, Annotation, Internal, AnalyticalModel.</summary>
    public string CategoryType { get; set; } = string.Empty;
    /// <summary>The element's runtime class name: Sketch, DetailLine, ViewPlan, ...</summary>
    public string ClassName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsView { get; set; }
    public bool IsSketchMember { get; set; }
}

/// <summary>
/// Which of the elements a view-scoped collector passes count as elements of the view (#136), and whether they are
/// model or annotation elements. Pure. The rule and its reasons are explained in docs/view-element-reads.md.
/// </summary>
public static class ViewElementRules
{
    // Why an element is left out; the keys of the "excluded" block in a view read.
    public const string ReasonNoCategory = "noCategory";
    public const string ReasonInternal = "internalCategory";
    public const string ReasonViewElement = "viewElement";
    public const string ReasonHelper = "helper";
    public const string ReasonSketchMember = "sketchMember";
    public const string ReasonHiddenInView = "hiddenInView";
    public const string ReasonCategoryHidden = "categoryHidden";
    public const string ReasonFilterHidden = "filterHidden";
    public const string ReasonTemporarilyHidden = "temporarilyHidden";
    public const string ReasonOutsideCrop = "outsideCrop";

    private static readonly HashSet<string> NonCollectableViewTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Schedule", "PanelSchedule", "ColumnSchedule", "Report", "CostReport", "LoadsReport", "PresureLossReport",
        "ProjectBrowser", "SystemBrowser", "Rendering", "Internal", "Undefined"
    };

    private static readonly HashSet<string> HelperClasses = new(StringComparer.Ordinal)
    {
        "Sketch", "SketchPlane", "SketchBase"
    };

    // Reference planes and lines, weak dimensions, crop and section boxes, cameras, constraint graphics, and
    // electrical circuits, which a view's collector lists although they draw nothing.
    private static readonly HashSet<string> HelperCategories = new(StringComparer.Ordinal)
    {
        "OST_CLines", "OST_ReferenceLines", "OST_ReferencePoints", "OST_ReferencePoints_HiddenLines",
        "OST_ReferencePoints_Lines", "OST_ReferencePoints_Planes", "OST_ReferencePoints_Points",
        "OST_WeakDims", "OST_Constraints", "OST_DimLockControlLeader",
        "OST_SectionBox", "OST_CropBoundary", "OST_CropBoundarySpecial", "OST_AnnotationCrop", "OST_AnnotationCropSpecial",
        "OST_Cameras", "OST_Camera_Lines", "OST_RenderRegions", "OST_ElectricalCircuit"
    };

    // OST_IOS* are internal object styles, apart from these real elements.
    private static readonly HashSet<string> RealInternalStyleCategories = new(StringComparer.Ordinal)
    {
        "OST_IOSModelGroups", "OST_IOSDetailGroups", "OST_IOSAttachedDetailGroups", "OST_IOSGroups", "OST_IOSArrays"
    };

    public static bool IsCollectableViewType(string viewType) =>
        !string.IsNullOrEmpty(viewType) && !NonCollectableViewTypes.Contains(viewType);

    /// <summary>Parses the <c>scope</c> argument; empty means all. False for anything else.</summary>
    public static bool TryParseScope(string? text, out ViewElementScope scope)
    {
        scope = ViewElementScope.All;
        switch (text?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "all":
                return true;
            case "model":
                scope = ViewElementScope.Model;
                return true;
            case "annotation":
                scope = ViewElementScope.Annotation;
                return true;
            default:
                return false;
        }
    }

    public static string ScopeName(ViewElementScope scope) => scope.ToString().ToLowerInvariant();

    /// <summary>
    /// Annotation means drawn in one view only, or of an annotation category. Detail lines, filled regions and detail
    /// components have a model CategoryType but belong to one view, so they count as annotation.
    /// </summary>
    public static bool IsAnnotation(bool isViewSpecific, string categoryType) =>
        isViewSpecific || string.Equals(categoryType, "Annotation", StringComparison.OrdinalIgnoreCase);

    public static bool InScope(ViewElementScope scope, bool isViewSpecific, string categoryType) => scope switch
    {
        ViewElementScope.Model => !IsAnnotation(isViewSpecific, categoryType),
        ViewElementScope.Annotation => IsAnnotation(isViewSpecific, categoryType),
        _ => true
    };

    /// <summary>Why an element a view's collector passed is not an element of that view, or null when it is.</summary>
    public static string? ExclusionReason(ViewElementFacts element, string viewName)
    {
        if (element.IsView) return ReasonViewElement;
        if (HelperClasses.Contains(element.ClassName)) return ReasonHelper;
        if (!element.HasCategory) return ReasonNoCategory;
        if (element.IsSketchMember) return ReasonSketchMember;
        if (string.Equals(element.CategoryType, "Internal", StringComparison.OrdinalIgnoreCase)) return ReasonInternal;
        if (IsHelperCategory(element.BuiltInCategory)) return ReasonHelper;
        // The view's own crop element is a viewer with the view's name; markers of other views are real elements.
        if (element.BuiltInCategory == "OST_Viewers" && string.Equals(element.Name, viewName, StringComparison.Ordinal))
            return ReasonViewElement;
        return null;
    }

    public static bool IsHelperCategory(string builtInCategory)
    {
        if (string.IsNullOrEmpty(builtInCategory)) return false;
        if (HelperCategories.Contains(builtInCategory)) return true;
        if (builtInCategory.IndexOf("Sketch", StringComparison.Ordinal) >= 0) return true;
        return builtInCategory.StartsWith("OST_IOS", StringComparison.Ordinal) && !RealInternalStyleCategories.Contains(builtInCategory);
    }
}
