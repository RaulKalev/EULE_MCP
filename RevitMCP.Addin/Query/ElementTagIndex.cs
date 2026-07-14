using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;

namespace RevitMCP.Addin.Query;

/// <summary>
/// Reverse lookup from a tagged model element to the annotation tags pointing at it.
/// Built with a single collector pass over all tags in the document, then queried per
/// element. Must be built on the Revit API thread.
/// </summary>
public class ElementTagIndex
{
    private readonly Dictionary<long, List<TagInfoDto>> _tagsByElement;

    private ElementTagIndex(Dictionary<long, List<TagInfoDto>> tagsByElement) =>
        _tagsByElement = tagsByElement;

    /// <summary>Tags attached to the given element, or null when it has none.</summary>
    public List<TagInfoDto>? GetTags(long elementId) =>
        _tagsByElement.TryGetValue(elementId, out var tags) ? tags : null;

    public static ElementTagIndex Build(Document doc)
    {
        var map = new Dictionary<long, List<TagInfoDto>>();
        var viewNames = new Dictionary<ElementId, string>();

        // IndependentTag covers category tags, multi-category tags, material tags and
        // keynote tags. One tag can reference several elements (multi-leader tags) and
        // one element can carry any number of tags — hence a list per element id.
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)))
        {
            if (element is not IndependentTag tag) continue;

            ICollection<ElementId> taggedIds;
            try
            {
                // Orphaned tags and tags pointing only at linked elements yield no local ids.
                taggedIds = tag.GetTaggedLocalElementIds();
            }
            catch
            {
                continue;
            }

            foreach (var taggedId in taggedIds)
            {
                if (taggedId == null || taggedId == ElementId.InvalidElementId) continue;
                Add(map, taggedId.Value, BuildInfo(doc, tag, GetTagText(tag), viewNames));
            }
        }

        // Room/space/area tags are not IndependentTags. Their concrete classes cannot be
        // used in a class filter, so collect via the filterable base SpatialElementTag.
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(SpatialElementTag)))
        {
            if (element is not SpatialElementTag tag) continue;

            ElementId? taggedId = null;
            try
            {
                taggedId = tag switch
                {
                    RoomTag roomTag => roomTag.Room?.Id,
                    SpaceTag spaceTag => spaceTag.Space?.Id,
                    AreaTag areaTag => areaTag.Area?.Id,
                    _ => null
                };
            }
            catch { }

            if (taggedId == null || taggedId == ElementId.InvalidElementId) continue;
            Add(map, taggedId.Value, BuildInfo(doc, tag, GetTagText(tag), viewNames));
        }

        return new ElementTagIndex(map);
    }

    private static void Add(Dictionary<long, List<TagInfoDto>> map, long elementId, TagInfoDto info)
    {
        if (!map.TryGetValue(elementId, out var list))
        {
            list = new List<TagInfoDto>();
            map[elementId] = list;
        }
        list.Add(info);
    }

    private static TagInfoDto BuildInfo(
        Document doc,
        Element tag,
        string tagText,
        Dictionary<ElementId, string> viewNameCache)
    {
        var typeElem = doc.GetElement(tag.GetTypeId()) as ElementType;

        long? viewId = null;
        var viewName = string.Empty;
        var ownerViewId = tag.OwnerViewId;
        if (ownerViewId != null && ownerViewId != ElementId.InvalidElementId)
        {
            viewId = ownerViewId.Value;
            if (!viewNameCache.TryGetValue(ownerViewId, out var cached))
            {
                cached = (doc.GetElement(ownerViewId) as View)?.Name ?? string.Empty;
                viewNameCache[ownerViewId] = cached;
            }
            viewName = cached;
        }

        return new TagInfoDto
        {
            TagId = tag.Id.Value,
            TagText = tagText,
            Category = tag.Category?.Name ?? string.Empty,
            Family = typeElem?.FamilyName ?? string.Empty,
            Type = typeElem?.Name ?? string.Empty,
            ViewId = viewId,
            ViewName = viewName
        };
    }

    private static string GetTagText(Element tag)
    {
        // TagText can throw for tags in transitional states (e.g. empty or orphaned).
        try
        {
            return tag switch
            {
                IndependentTag it => it.TagText,
                SpatialElementTag st => st.TagText,
                _ => string.Empty
            };
        }
        catch
        {
            return string.Empty;
        }
    }
}
