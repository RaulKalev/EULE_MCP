#nullable disable

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tagging
{
    public static class TagDiscoveryService
    {
        public static List<IndependentTag> FindManagedTags(
            Document doc,
            View view,
            ICollection<ElementId> tagIds,
            ICollection<ElementId> referencedElementIds)
        {
            var result = new List<IndependentTag>();
            if (doc == null || view == null)
                return result;

            var tagIdSet = tagIds == null
                ? new HashSet<ElementId>()
                : new HashSet<ElementId>(tagIds);
            var elementIdSet = referencedElementIds == null
                ? new HashSet<ElementId>()
                : new HashSet<ElementId>(referencedElementIds);

            foreach (var tag in new FilteredElementCollector(doc, view.Id)
                         .OfClass(typeof(IndependentTag))
                         .Cast<IndependentTag>())
            {
                if (!SmartTagMarkerStorage.IsManagedTag(tag))
                    continue;
                if (tagIdSet.Count > 0 && !tagIdSet.Contains(tag.Id))
                    continue;
                if (elementIdSet.Count > 0 &&
                    !GetTaggedElementIds(tag).Any(elementIdSet.Contains))
                    continue;
                result.Add(tag);
            }
            return result;
        }

        public static bool IsElementTaggedInView(
            Document doc,
            View view,
            ElementId elementId,
            ElementId tagCategoryId)
        {
            if (doc == null || view == null || elementId == null ||
                elementId == ElementId.InvalidElementId)
                return false;

            foreach (var tag in new FilteredElementCollector(doc, view.Id)
                         .OfClass(typeof(IndependentTag))
                         .Cast<IndependentTag>())
            {
                if (tagCategoryId != null &&
                    tagCategoryId != ElementId.InvalidElementId &&
                    (tag.Category == null || tag.Category.Id != tagCategoryId))
                    continue;

                if (GetTaggedElementIds(tag).Contains(elementId))
                    return true;
            }
            return false;
        }

        /// <summary>Whether a tag in the view already points at this element through this link (#151).</summary>
        public static bool IsLinkedElementTaggedInView(
            Document doc,
            View view,
            ElementId linkInstanceId,
            ElementId linkedElementId,
            ElementId tagCategoryId)
        {
            if (doc == null || view == null || linkInstanceId == null || linkedElementId == null)
                return false;

            foreach (var tag in new FilteredElementCollector(doc, view.Id)
                         .OfClass(typeof(IndependentTag))
                         .Cast<IndependentTag>())
            {
                if (tagCategoryId != null &&
                    tagCategoryId != ElementId.InvalidElementId &&
                    (tag.Category == null || tag.Category.Id != tagCategoryId))
                    continue;

                foreach (var (link, element) in GetTaggedLinkedIds(tag))
                    if (link == linkInstanceId && element == linkedElementId)
                        return true;
            }
            return false;
        }

        /// <summary>The (link instance, element in the link) pairs a tag points at; empty for tags on host elements.</summary>
        public static List<(ElementId Link, ElementId Element)> GetTaggedLinkedIds(IndependentTag tag)
        {
            var result = new List<(ElementId, ElementId)>();
            if (tag == null)
                return result;
            try
            {
#if REVIT2026
                foreach (var linkedId in tag.GetTaggedElementIds())
                {
                    if (linkedId != null &&
                        linkedId.LinkInstanceId != ElementId.InvalidElementId &&
                        linkedId.LinkedElementId != ElementId.InvalidElementId)
                        result.Add((linkedId.LinkInstanceId, linkedId.LinkedElementId));
                }
#else
                foreach (var reference in tag.GetTaggedReferences())
                {
                    if (reference != null &&
                        reference.LinkedElementId != ElementId.InvalidElementId)
                        result.Add((reference.ElementId, reference.LinkedElementId));
                }
#endif
            }
            catch
            {
                // A tag Revit cannot read is treated as not tagging a linked element.
            }
            return result;
        }

        public static List<ElementId> GetTaggedElementIds(IndependentTag tag)
        {
            var result = new List<ElementId>();
            if (tag == null)
                return result;

            try
            {
#if REVIT2026
                foreach (var linkedId in tag.GetTaggedElementIds())
                {
                    if (linkedId != null &&
                        linkedId.HostElementId != ElementId.InvalidElementId)
                        result.Add(linkedId.HostElementId);
                }
#else
                foreach (var reference in tag.GetTaggedReferences())
                {
                    if (reference != null &&
                        reference.ElementId != ElementId.InvalidElementId)
                        result.Add(reference.ElementId);
                }
#endif
            }
            catch
            {
                SmartTagMetadata metadata;
                if (SmartTagMarkerStorage.TryGetMetadata(tag, out metadata) &&
                    metadata.ReferencedElementId != null &&
                    metadata.ReferencedElementId != ElementId.InvalidElementId)
                    result.Add(metadata.ReferencedElementId);
            }

            return result;
        }
    }
}
