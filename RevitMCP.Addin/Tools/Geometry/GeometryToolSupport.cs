using System.Diagnostics;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Geometry;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Tools.Geometry;

/// <summary>A model a geometry query runs in: the host, or one loaded link with its transform to the host.</summary>
internal sealed class GeometryModel
{
    public Document Doc { get; set; } = null!;
    public RevitLinkInstance? Link { get; set; }

    /// <summary>Link coordinates to host coordinates; null for the host.</summary>
    public Transform? ToHost { get; set; }

    public long? LinkInstanceId => Link?.Id.Value;
    public string LinkName => Link != null ? GeometryConvert.SafeName(Link) : string.Empty;
    public string Label => Link != null ? $"link {Link.Id.Value} ({LinkName})" : "host model";

    public object? Describe() => Link == null ? null : new { id = Link.Id.Value, name = LinkName, documentTitle = Doc.Title };
}

/// <summary>One element a box or radius collector found, with its box in host mm.</summary>
internal sealed class CollectedElement
{
    public Element Element { get; set; } = null!;
    public GeometryModel Model { get; set; } = null!;
    public BoxMm HostBox { get; set; }
}

/// <summary>Shared plumbing of the six geometry tools (#107): results, links, element boxes, origins, collectors.</summary>
internal static class GeometryToolSupport
{
    public static McpToolResult Fail(McpToolRequest request, string message, string status = "validation_failed", List<string>? warnings = null) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Status = status,
        Message = message,
        Warnings = warnings ?? new List<string>()
    };

    public static McpToolResult Ok(McpToolRequest request, Stopwatch sw, string message, object data, List<string> warnings)
    {
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = message,
            Data = data,
            Warnings = warnings.Distinct().ToList(),
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>A loaded link by instance id; answers <see cref="GeometryStatus.LinkNotFound"/> otherwise.</summary>
    public static GeometryModel? ResolveLink(Document hostDoc, long linkInstanceId, McpToolRequest request, out McpToolResult? failure)
    {
        failure = null;
        if (hostDoc.GetElement(new ElementId(linkInstanceId)) is RevitLinkInstance link)
        {
            Document? linkDoc = null;
            try { linkDoc = link.GetLinkDocument(); } catch { }
            var transform = GeometryConvert.LinkTransform(link);
            if (linkDoc != null && transform != null)
                return new GeometryModel { Doc = linkDoc, Link = link, ToHost = transform };
            failure = Fail(request, $"Link {linkInstanceId} ('{GeometryConvert.SafeName(link)}') is not loaded. {DescribeLinks(hostDoc)}", GeometryStatus.LinkNotFound);
            return null;
        }
        failure = Fail(request, $"Element {linkInstanceId} is not a Revit/IFC link instance. {DescribeLinks(hostDoc)}", GeometryStatus.LinkNotFound);
        return null;
    }

    /// <summary>The host and, with <paramref name="includeLinks"/>, every loaded link whose name contains one of <paramref name="linkNameFilters"/> (all when empty).</summary>
    public static List<GeometryModel> Models(Document hostDoc, bool includeLinks, IReadOnlyCollection<string> linkNameFilters, List<string> warnings)
    {
        var models = new List<GeometryModel> { new() { Doc = hostDoc } };
        if (!includeLinks)
        {
            if (linkNameFilters.Count > 0) warnings.Add("linkNameFilters was given but includeLinks is false — the filter has no effect.");
            return models;
        }

        var matched = 0;
        foreach (var link in new FilteredElementCollector(hostDoc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            var name = GeometryConvert.SafeName(link);
            if (linkNameFilters.Count > 0 && !linkNameFilters.Any(f => name.IndexOf(f.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
                continue;
            Document? linkDoc = null;
            try { linkDoc = link.GetLinkDocument(); } catch { }
            var transform = GeometryConvert.LinkTransform(link);
            if (linkDoc == null || transform == null)
            {
                warnings.Add($"Link {link.Id.Value} ('{name}') is not loaded and was skipped.");
                continue;
            }
            matched++;
            models.Add(new GeometryModel { Doc = linkDoc, Link = link, ToHost = transform });
        }
        if (linkNameFilters.Count > 0 && matched == 0)
            warnings.Add($"No loaded link matches linkNameFilters [{string.Join(", ", linkNameFilters)}]. {DescribeLinks(hostDoc)}");
        return models;
    }

    public static string DescribeLinks(Document hostDoc)
    {
        var links = new FilteredElementCollector(hostDoc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Take(15).ToList();
        return links.Count == 0 ? "The model has no links." : "Links: " + string.Join("; ", links.Select(l => $"{l.Id.Value} = {GeometryConvert.SafeName(l)}"));
    }

    /// <summary>An element's bounding box in host mm (view box for view-specific elements), or null.</summary>
    public static BoxMm? HostBox(Element element, Transform? toHost)
    {
        var box = GeometryConvert.ElementBox(element, out _);
        return box == null ? null : GeometryConvert.HostBox(box, toHost);
    }

    /// <summary>
    /// The origin of a point or element query, in host mm: <paramref name="pointKey"/> when given, else the host
    /// element <paramref name="idKey"/> (its bounding box centre, or its location point when it has no box).
    /// </summary>
    public static bool TryResolveOrigin(
        Document doc, Dictionary<string, object?> args, string pointKey, string idKey,
        out Vec3 originMm, out Element? element, out BoxMm? elementBox, out string? error)
    {
        originMm = default;
        element = null;
        elementBox = null;
        error = null;

        if (GeometryArgs.IsGiven(args, pointKey))
        {
            args.TryGetValue(pointKey, out var raw);
            return GeometryArgs.TryReadPoint(raw, pointKey, out originMm, out error);
        }

        var id = ToolArguments.GetLong(args, idKey);
        if (id <= 0)
        {
            error = $"Give {pointKey} {{x, y, z}} in mm or {idKey}.";
            return false;
        }
        element = doc.GetElement(new ElementId(id));
        if (element == null)
        {
            error = $"Element {id} was not found in '{doc.Title}'.";
            return false;
        }
        elementBox = HostBox(element, null);
        if (elementBox.HasValue)
        {
            originMm = elementBox.Value.Center;
            return true;
        }
        var point = GeometryConvert.RepresentativePoint(element);
        if (point == null)
        {
            error = $"Element {id} has no location and no bounding box to measure from.";
            return false;
        }
        originMm = GeometryConvert.ToMm(point);
        return true;
    }

    /// <summary>The category ids of <paramref name="names"/> in one document; unknown names are warned about once per model.</summary>
    public static List<ElementId> ResolveCategories(GeometryModel model, IEnumerable<string> names, List<string> warnings)
    {
        var ids = new List<ElementId>();
        var resolver = new CategoryResolver();
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var resolved = resolver.Resolve(model.Doc, name.Trim());
            if (resolved.Category == null)
            {
                warnings.Add($"Category '{name}' was not found in the {model.Label}" +
                             (resolved.Suggestions.Count > 0 ? $" (did you mean {string.Join(", ", resolved.Suggestions.Take(5))}?)" : ".") );
                continue;
            }
            if (!ids.Contains(resolved.Category.Id)) ids.Add(resolved.Category.Id);
        }
        return ids;
    }

    /// <summary>
    /// Elements whose bounding box intersects (or lies inside) <paramref name="hostBox"/> (host mm), in the host and
    /// the given links. Each link runs the filter on the box transformed into its own coordinates, then every element
    /// is re-checked in host coordinates. Types, view-specific elements, link instances and uncategorised elements are
    /// skipped. Stops after <see cref="QueryLimits.MaxScanElements"/> candidates with a warning.
    /// </summary>
    public static List<CollectedElement> Collect(
        IEnumerable<GeometryModel> models, BoxMm hostBox, string mode, IReadOnlyCollection<string> categories,
        IReadOnlyCollection<string> levels, ICollection<long> excludeHostIds, List<string> warnings, CancellationToken cancellationToken)
    {
        var result = new List<CollectedElement>();
        var scanned = 0;
        var scanCap = QueryLimits.Default.MaxScanElements;
        var levelSet = new HashSet<string>(levels.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()), StringComparer.OrdinalIgnoreCase);
        var inside = string.Equals(mode, BoxMath.ModeInside, StringComparison.OrdinalIgnoreCase);

        foreach (var model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<ElementId>? categoryIds = null;
            if (categories.Count > 0)
            {
                categoryIds = ResolveCategories(model, categories, warnings);
                if (categoryIds.Count == 0) continue;
            }

            // A zero-size box (a point) is padded by a hair so Revit accepts it as an outline.
            var local = model.ToHost == null ? hostBox : GeometryConvert.ToLinkBoxMm(hostBox, model.ToHost);
            local = BoxMath.Pad(local, 0.01);
            var outline = GeometryConvert.Outline(local);
            ElementFilter boxFilter = inside && model.ToHost == null
                ? new BoundingBoxIsInsideFilter(outline)
                : new BoundingBoxIntersectsFilter(outline);

            var collector = new FilteredElementCollector(model.Doc).WhereElementIsNotElementType().WherePasses(boxFilter);
            if (categoryIds != null) collector = collector.WherePasses(new ElementMulticategoryFilter(categoryIds));

            foreach (var element in collector)
            {
                if (++scanned > scanCap)
                {
                    warnings.Add($"Stopped after scanning {scanCap} elements; narrow the box or add categories.");
                    return result;
                }
                if (element.Category == null || element is RevitLinkInstance || element is View) continue;
                bool viewSpecific;
                try { viewSpecific = element.ViewSpecific; } catch { viewSpecific = false; }
                if (viewSpecific) continue;
                if (model.Link == null && excludeHostIds.Contains(element.Id.Value)) continue;

                var box = HostBox(element, model.ToHost);
                if (box == null || !BoxMath.Matches(mode, box.Value, hostBox)) continue;
                if (levelSet.Count > 0 && !levelSet.Contains(RevitLevelReader.Resolve(element).Name)) continue;

                result.Add(new CollectedElement { Element = element, Model = model, HostBox = box.Value });
            }
        }
        return result;
    }

    /// <summary>The element ids to ignore along a ray: the given ones plus, for <paramref name="element"/>, itself and what depends on it.</summary>
    public static HashSet<long> IgnoreSet(IEnumerable<long> ignoreElementIds, Element? element, bool includeDependents)
    {
        var set = new HashSet<long>(ignoreElementIds);
        if (element == null) return set;
        set.Add(element.Id.Value);
        if (!includeDependents) return set;
        try
        {
            foreach (var id in element.GetDependentElements(null)) set.Add(id.Value);
        }
        catch { }
        try
        {
            if (element is FamilyInstance instance)
                foreach (var id in instance.GetSubComponentIds()) set.Add(id.Value);
        }
        catch { }
        return set;
    }

    /// <summary>Accepts a ray target unless it is an ignored host element or outside the wanted categories.</summary>
    public static Func<RayTarget, bool> Accept(ICollection<long> ignoreHostIds, ICollection<string> categories) =>
        target =>
            !(target.LinkInstanceId == null && ignoreHostIds.Contains(target.ElementId)) &&
            GeometryCategories.Matches(categories, target.BuiltInCategory, target.Category);

    /// <summary>Distance in mm along the ray from a hit in feet.</summary>
    public static double Mm(double feet) => GeometryUnits.RoundMm(GeometryUnits.FtToMm(feet));

    /// <summary>The maximum distance argument clamped to 1 … 50 000 mm, with a warning when changed.</summary>
    public static double ClampDistanceMm(double requested, double defaultMm, string name, List<string> warnings)
    {
        if (double.IsNaN(requested) || requested <= 0) return defaultMm;
        if (requested > AlignmentRequest.MaxSearchRadiusMm)
        {
            warnings.Add($"{name} {requested:0} exceeds the cap; using {AlignmentRequest.MaxSearchRadiusMm:0}.");
            return AlignmentRequest.MaxSearchRadiusMm;
        }
        return requested;
    }

    /// <summary>The non-empty strings of an array argument, trimmed.</summary>
    public static string[] Strings(Dictionary<string, object?> args, string key) =>
        ToolArguments.GetStringArray(args, key).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray();

    /// <summary>Element summary fields shared by the collector rows.</summary>
    public static string TypeName(Element element)
    {
        try { return element.Document.GetElement(element.GetTypeId())?.Name ?? string.Empty; }
        catch { return string.Empty; }
    }
}
