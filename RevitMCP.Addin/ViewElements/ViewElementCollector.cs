using Autodesk.Revit.DB;
using RevitMCP.Addin.Geometry;

namespace RevitMCP.Addin.ViewElements;

/// <summary>One list of elements inside a view read: the view, a viewport's view, or the sheet itself.</summary>
internal sealed class ViewSource
{
    public View? View { get; set; }
    public Viewport? Viewport { get; set; }
    public bool IsSheetItself { get; set; }
    public string? Problem { get; set; }
    public List<ElementId> Ids { get; set; } = new();
    public Dictionary<string, int> CategoryCounts { get; } = new(StringComparer.Ordinal);
}

internal sealed class ViewCollectOptions
{
    public ElementId? CategoryId { get; set; }
    public ViewElementScope Scope { get; set; }
    /// <summary>Only these ids (the caller's elementIds or selection); null means every element in the view.</summary>
    public HashSet<long>? OnlyIds { get; set; }
}

internal sealed class ViewCollection
{
    public List<ViewSource> Sources { get; } = new();
    public Dictionary<string, int> Excluded { get; } = new(StringComparer.Ordinal);
    public List<string> Warnings { get; } = new();
    public bool Truncated { get; set; }
    public bool TemporaryHideActive { get; set; }
}

/// <summary>
/// Lists the elements a view or sheet shows (#136): Revit's view-scoped collector, then the exclusion rules and the
/// visibility checks the collector does not promise. A sheet is its own elements plus each viewport's view.
/// Never reads parameters, so counting stays cheap.
/// </summary>
internal static class ViewElementCollector
{
    /// <summary>Backstop per read. Elements are only counted here; reading their values has its own limits.</summary>
    public const int MaxElements = 100_000;

    public static ViewCollection Collect(Document doc, ViewTarget target, ViewCollectOptions options, CancellationToken cancellationToken)
    {
        var collection = new ViewCollection();
        var collected = 0;

        if (target.Sheet is { } sheet)
        {
            collection.Sources.Add(new ViewSource { View = sheet, IsSheetItself = true });
            foreach (var viewportId in sheet.GetAllViewports())
            {
                if (doc.GetElement(viewportId) is not Viewport viewport) continue;
                collection.Sources.Add(new ViewSource { View = doc.GetElement(viewport.ViewId) as View, Viewport = viewport });
            }
        }
        else
        {
            collection.Sources.Add(new ViewSource { View = target.View });
        }

        foreach (var source in collection.Sources)
        {
            var view = source.View;
            if (view == null)
            {
                source.Problem = "The viewport's view could not be read.";
                collection.Warnings.Add($"Viewport {source.Viewport?.Id.Value}: {source.Problem}");
                continue;
            }
            if (!ViewElementRules.IsCollectableViewType(view.ViewType.ToString()) || !ViewTargetResolver.IsIterable(doc, view))
            {
                source.Problem = $"'{view.Name}' ({view.ViewType}) does not draw elements a collector can list.";
                collection.Warnings.Add($"Viewport {source.Viewport?.Id.Value}: {source.Problem}");
                continue;
            }
            collected = CollectFrom(doc, source, view, options, collection, collected, cancellationToken);
        }

        if (collection.Truncated)
            collection.Warnings.Add($"Stopped listing after {MaxElements} elements (safety limit); counts are partial. Narrow with category or scope.");
        if (collection.TemporaryHideActive)
            collection.Warnings.Add("Temporary hide/isolate is active in this view; elements it hides are left out until it is reset.");
        return collection;
    }

    private static int CollectFrom(
        Document doc, ViewSource source, View view, ViewCollectOptions options, ViewCollection collection, int collected, CancellationToken cancellationToken)
    {
        IList<Element> elements;
        try
        {
            var collector = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();
            if (options.CategoryId != null) collector = collector.OfCategoryId(options.CategoryId);
            elements = collector.ToElements();
        }
        catch (Exception ex)
        {
            source.Problem = $"Revit could not list '{view.Name}': {ex.Message}";
            collection.Warnings.Add(source.Problem);
            return collected;
        }

        var sketchMembers = SketchMembers(doc, view, elements);
        var visibility = new ViewVisibility(doc, view);
        if (visibility.TemporaryHideActive) collection.TemporaryHideActive = true;
        var viewName = view.Name;

        foreach (var element in elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (collected >= MaxElements)
            {
                collection.Truncated = true;
                return collected;
            }

            var reason = ViewElementRules.ExclusionReason(Facts(element, sketchMembers), viewName);
            if (reason != null)
            {
                Count(collection.Excluded, reason);
                continue;
            }

            var category = element.Category!;
            if (!ViewElementRules.InScope(options.Scope, element.ViewSpecific, category.CategoryType.ToString())) continue;
            if (options.OnlyIds != null && !options.OnlyIds.Contains(element.Id.Value)) continue;

            var hidden = visibility.HiddenReason(element);
            if (hidden != null)
            {
                Count(collection.Excluded, hidden);
                continue;
            }

            source.Ids.Add(element.Id);
            Count(source.CategoryCounts, category.Name);
            collected++;
        }
        return collected;
    }

    private static ViewElementFacts Facts(Element element, HashSet<long> sketchMembers)
    {
        var category = element.Category;
        return new ViewElementFacts
        {
            HasCategory = category != null,
            BuiltInCategory = GeometryConvert.BuiltInName(category),
            CategoryType = category?.CategoryType.ToString() ?? string.Empty,
            ClassName = element.GetType().Name,
            Name = GeometryConvert.SafeName(element),
            IsView = element is View,
            IsSketchMember = sketchMembers.Contains(element.Id.Value)
        };
    }

    /// <summary>
    /// The lines of a sketch (a filled region's boundary, for one) are plain curve elements by class and category, so
    /// they are recognised through their sketch: those the view lists, and those behind its filled regions.
    /// </summary>
    private static HashSet<long> SketchMembers(Document doc, View view, IList<Element> elements)
    {
        var members = new HashSet<long>();
        if (!elements.Any(e => e is CurveElement)) return members;

        var sketches = new List<Sketch>();
        try
        {
            sketches.AddRange(new FilteredElementCollector(doc, view.Id).OfClass(typeof(Sketch)).Cast<Sketch>());
        }
        catch { /* the regions below still cover the common case */ }

        try
        {
            foreach (var region in new FilteredElementCollector(doc, view.Id).OfClass(typeof(FilledRegion)))
                foreach (var id in region.GetDependentElements(new ElementClassFilter(typeof(Sketch))))
                    if (doc.GetElement(id) is Sketch sketch) sketches.Add(sketch);
        }
        catch { /* then only the sketches the view lists are recognised */ }

        foreach (var sketch in sketches)
        {
            try
            {
                foreach (var id in sketch.GetAllElements()) members.Add(id.Value);
            }
            catch { }
        }
        return members;
    }

    private static void Count(Dictionary<string, int> counts, string key)
    {
        counts.TryGetValue(key, out var count);
        counts[key] = count + 1;
    }
}

/// <summary>
/// What the Revit API can tell about an element's visibility beyond the collector: hidden by element, by category or
/// category class, by a view filter, by temporary hide/isolate, or outside the crop. Every check fails open, so an
/// element is only left out on evidence.
/// </summary>
internal sealed class ViewVisibility
{
    private readonly View _view;
    private readonly Dictionary<long, bool> _categoryHidden = new();
    private readonly List<(HashSet<long> Categories, ElementFilter? Filter)> _hidingParameterFilters = new();
    private readonly HashSet<long> _hiddenBySelectionFilter = new();
    private readonly Transform? _toCrop;
    private readonly List<IReadOnlyList<(double X, double Y)>>? _cropLoops;

    public bool TemporaryHideActive { get; }

    public ViewVisibility(Document doc, View view)
    {
        _view = view;
        try { TemporaryHideActive = view.IsTemporaryHideIsolateActive(); } catch { }
        LoadFilters(doc);
        if (TryCrop(out var toCrop, out var loops))
        {
            _toCrop = toCrop;
            _cropLoops = loops;
        }
    }

    public string? HiddenReason(Element element)
    {
        try
        {
            if (element.IsHidden(_view)) return ViewElementRules.ReasonHiddenInView;
        }
        catch { }

        if (IsCategoryHidden(element.Category)) return ViewElementRules.ReasonCategoryHidden;
        if (IsHiddenByFilter(element)) return ViewElementRules.ReasonFilterHidden;

        if (TemporaryHideActive)
        {
            try
            {
                if (!_view.IsElementVisibleInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate, element.Id))
                    return ViewElementRules.ReasonTemporarilyHidden;
            }
            catch { }
        }

        return IsOutsideCrop(element) ? ViewElementRules.ReasonOutsideCrop : null;
    }

    private bool IsCategoryHidden(Category? category)
    {
        // A subcategory is hidden with its parent. GetCategoryHidden looks at one category only, so the class
        // switches of Visibility/Graphics ("Show model categories in this view") are checked as well.
        for (var current = category; current != null; current = current.Parent)
        {
            var id = current.Id.Value;
            if (!_categoryHidden.TryGetValue(id, out var hidden))
            {
                try { hidden = _view.GetCategoryHidden(current.Id); }
                catch { hidden = false; }
                _categoryHidden[id] = hidden;
            }
            if (hidden) return true;
        }
        if (category == null) return false;
        try
        {
            switch (category.CategoryType)
            {
                case CategoryType.Model: return _view.AreModelCategoriesHidden;
                case CategoryType.Annotation: return _view.AreAnnotationCategoriesHidden;
                case CategoryType.AnalyticalModel: return _view.AreAnalyticalModelCategoriesHidden;
            }
        }
        catch { }
        return false;
    }

    private void LoadFilters(Document doc)
    {
        try
        {
            if (!_view.AreGraphicsOverridesAllowed()) return;
            foreach (var filterId in _view.GetFilters())
            {
                if (!_view.GetIsFilterEnabled(filterId) || _view.GetFilterVisibility(filterId)) continue;
                switch (doc.GetElement(filterId))
                {
                    case ParameterFilterElement parameterFilter:
                        _hidingParameterFilters.Add((
                            new HashSet<long>(parameterFilter.GetCategories().Select(c => c.Value)),
                            parameterFilter.GetElementFilter()));
                        break;
                    case SelectionFilterElement selectionFilter:
                        foreach (var id in selectionFilter.GetElementIds()) _hiddenBySelectionFilter.Add(id.Value);
                        break;
                }
            }
        }
        catch { /* a view without readable filters shows what the collector passed */ }
    }

    private bool IsHiddenByFilter(Element element)
    {
        if (_hiddenBySelectionFilter.Contains(element.Id.Value)) return true;
        if (_hidingParameterFilters.Count == 0) return false;
        var categoryId = element.Category?.Id.Value;
        if (categoryId == null) return false;
        foreach (var (categories, filter) in _hidingParameterFilters)
        {
            if (!categories.Contains(categoryId.Value)) continue;
            try
            {
                if (filter == null || filter.PassesFilter(element)) return true;
            }
            catch { }
        }
        return false;
    }

    private bool TryCrop(out Transform? toCrop, out List<IReadOnlyList<(double X, double Y)>>? loops)
    {
        toCrop = null;
        loops = null;
        try
        {
            if (_view is ViewSheet || _view is ViewDrafting || !_view.CropBoxActive) return false;
            if (_view is View3D { IsPerspective: true }) return false;
            var box = _view.CropBox;
            if (box == null) return false;
            var transform = box.Transform.Inverse;
            toCrop = transform;
            loops = ShapeLoops(transform) ?? new List<IReadOnlyList<(double X, double Y)>>
            {
                new (double X, double Y)[] { (box.Min.X, box.Min.Y), (box.Max.X, box.Min.Y), (box.Max.X, box.Max.Y), (box.Min.X, box.Max.Y) }
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The non-rectangular or split crop shape, in the crop box's coordinates; null when there is none to read.</summary>
    private List<IReadOnlyList<(double X, double Y)>>? ShapeLoops(Transform toCrop)
    {
        try
        {
            var manager = _view.GetCropRegionShapeManager();
            if (manager == null) return null;
            var result = new List<IReadOnlyList<(double X, double Y)>>();
            foreach (var loop in manager.GetCropShape())
            {
                var points = new List<(double X, double Y)>();
                foreach (var curve in loop)
                {
                    var tessellated = curve.Tessellate();
                    for (var i = 0; i < tessellated.Count - 1; i++)
                    {
                        var p = toCrop.OfPoint(tessellated[i]);
                        points.Add((p.X, p.Y));
                    }
                }
                if (points.Count >= 3) result.Add(points);
            }
            return result.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Model elements only: annotation follows the annotation crop, which the API does not expose as a shape. Compares
    /// the element's box to the crop in the view's plane, so a box that only overlaps the crop with its corner stays.
    /// </summary>
    private bool IsOutsideCrop(Element element)
    {
        if (_toCrop == null || _cropLoops == null || element.ViewSpecific) return false;
        try
        {
            var box = element.get_BoundingBox(null);
            if (box == null) return false;
            var own = box.Transform ?? Transform.Identity;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var x in new[] { box.Min.X, box.Max.X })
            foreach (var y in new[] { box.Min.Y, box.Max.Y })
            foreach (var z in new[] { box.Min.Z, box.Max.Z })
            {
                var p = _toCrop.OfPoint(own.OfPoint(new XYZ(x, y, z)));
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
            }
            return !ViewCropMath.BoxOverlapsCrop(minX, minY, maxX, maxY, _cropLoops);
        }
        catch
        {
            return false;
        }
    }
}
