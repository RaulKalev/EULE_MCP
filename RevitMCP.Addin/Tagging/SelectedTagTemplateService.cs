#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tagging
{
    public sealed class SelectedTagTemplateService
    {
        private const double FeetToMillimeters = 304.8;
        private const double RotationToleranceRadians =
            2.0 * Math.PI / 180.0;

        public TagTemplateAnalysisResult Analyze(
            Document doc,
            ICollection<ElementId> selectedIds,
            TagTemplateRequestOptions options,
            CancellationToken cancellationToken)
        {
            var result = new TagTemplateAnalysisResult();
            options = options ?? new TagTemplateRequestOptions();
            if (doc == null)
            {
                result.Errors.Add("No active document.");
                return result;
            }
            if (options.ReplaceExistingTags)
            {
                result.Errors.Add(
                    "replaceExistingTags=true is not supported. Existing tags are never deleted by this workflow.");
                return result;
            }
            if (options.Override != null &&
                options.Override.HasAnchorMode)
                options.AnchorMode = options.Override.AnchorMode;

            IndependentTag sourceTag;
            FamilyInstance sourceHost;
            View sourceView;
            Reference sourceReference;
            FamilySymbol tagType;
            if (!TryResolveSource(
                    doc,
                    selectedIds,
                    options,
                    out sourceTag,
                    out sourceHost,
                    out sourceView,
                    out sourceReference,
                    out tagType,
                    out var sourceError))
            {
                result.Errors.Add(sourceError);
                return result;
            }

            cancellationToken.ThrowIfCancellationRequested();
            HostLocalFrame sourceFrame;
            if (!HostLocalFrameService.TryCreate(
                    sourceHost,
                    sourceView,
                    options.AnchorMode,
                    out sourceFrame,
                    out var frameError))
            {
                result.Errors.Add(
                    "Source host orientation cannot be determined: " + frameError);
                return result;
            }

            var template = BuildTemplate(
                doc,
                sourceTag,
                sourceHost,
                sourceView,
                sourceReference,
                tagType,
                sourceFrame,
                result.Warnings);
            if (template == null)
            {
                result.Errors.Add("The source tag placement rule could not be inferred safely.");
                return result;
            }

            if (!ApplyOverride(template, options.Override, out var overrideError))
            {
                result.Errors.Add(overrideError);
                return result;
            }
            options.AnchorMode = template.AnchorMode;

            result.Template = template;
            result.SourceTag = sourceTag;
            result.SourceHost = sourceHost;
            result.SourceView = sourceView;
            result.SourceReference = sourceReference;
            result.TagType = tagType;

            var candidates = ResolveCandidates(
                doc,
                sourceView,
                sourceHost,
                sourceTag,
                selectedIds,
                options,
                result.Targets,
                result.Warnings);
            result.UnsupportedCount = result.Targets.Count(item =>
                item.Status == "Unsupported");
            var existingTagsByHost = BuildExistingMatchingTagIndex(
                doc,
                sourceView,
                tagType.Id);

            foreach (var element in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = CreateTargetItem(element);
                result.Targets.Add(item);

                if (!options.IncludeSourceHost &&
                    element.Id == sourceHost.Id)
                {
                    item.Status = "Skipped";
                    item.Reason = "Source host excluded; it already owns the example tag.";
                    continue;
                }

                if (!IsCompatibleHost(sourceHost, element))
                {
                    item.Status = "Unsupported";
                    item.Reason =
                        "Target is not a family instance in the source host category.";
                    result.UnsupportedCount++;
                    continue;
                }

                List<ElementId> existingTagIds;
                if (!existingTagsByHost.TryGetValue(
                        element.Id.Value,
                        out existingTagIds))
                    existingTagIds = new List<ElementId>();
                foreach (var id in existingTagIds)
                    item.ExistingTagIds.Add(id.Value);
                item.AlreadyTagged = existingTagIds.Count > 0;
                if (item.AlreadyTagged && options.SkipAlreadyTagged)
                {
                    item.Status = "Skipped";
                    item.Reason = "Already has a matching tag in the source view.";
                    result.AlreadyTaggedCount++;
                    continue;
                }

                HostLocalFrame targetFrame;
                if (!HostLocalFrameService.TryCreate(
                        element,
                        sourceView,
                        template.AnchorMode,
                        out targetFrame,
                        out frameError))
                {
                    item.Status = "Unsupported";
                    item.Reason =
                        "Target host orientation cannot be determined: " + frameError;
                    result.UnsupportedCount++;
                    continue;
                }

                var head = HostLocalFrameService.ReconstructPoint(
                    targetFrame,
                    template.LocalRightOffsetMillimeters /
                    FeetToMillimeters,
                    template.LocalFrontOffsetMillimeters /
                    FeetToMillimeters);
                item.HostFrame = targetFrame;
                item.HostOrientationSource =
                    targetFrame.OrientationSource;
                item.OrientationFallbackUsed =
                    targetFrame.UsedFallback;
                item.FacingFlipped = targetFrame.FacingFlipped;
                item.HandFlipped = targetFrame.HandFlipped;
                item.Mirrored = targetFrame.Mirrored;
                item.ProposedHead = head;
                item.ProposedHeadX = head.X;
                item.ProposedHeadY = head.Y;
                item.ProposedHeadZ = head.Z;
                item.Eligible = true;
                item.Status = "Eligible";
                item.Reason = targetFrame.UsedFallback
                    ? "Eligible; host orientation fallback: " +
                      targetFrame.OrientationSource + "."
                    : "Eligible.";
                result.EligibleCount++;
            }

            result.CandidateCount = result.Targets.Count;
            result.SkippedCount = result.Targets.Count(item =>
                !item.Eligible && item.Status == "Skipped");
            if (sourceFrame.UsedFallback)
            {
                result.Warnings.Add(
                    "Source host orientation used fallback '" +
                    sourceFrame.OrientationSource + "'.");
            }
            return result;
        }

        private static bool TryResolveSource(
            Document doc,
            ICollection<ElementId> selectedIds,
            TagTemplateRequestOptions options,
            out IndependentTag sourceTag,
            out FamilyInstance sourceHost,
            out View sourceView,
            out Reference sourceReference,
            out FamilySymbol tagType,
            out string error)
        {
            sourceTag = null;
            sourceHost = null;
            sourceView = null;
            sourceReference = null;
            tagType = null;
            error = null;

            var selection = selectedIds == null
                ? new List<ElementId>()
                : selectedIds.Where(id =>
                        id != null && id != ElementId.InvalidElementId)
                    .Distinct()
                    .ToList();
            var selectedTags = selection
                .Select(doc.GetElement)
                .OfType<IndependentTag>()
                .ToList();

            if (options.SourceTagId > 0)
            {
                sourceTag = doc.GetElement(
                    new ElementId(options.SourceTagId)) as IndependentTag;
                if (sourceTag == null)
                {
                    error = "sourceTagId does not identify an IndependentTag.";
                    return false;
                }
                if (selectedTags.Count > 0 &&
                    (selectedTags.Count != 1 ||
                     selectedTags[0].Id != sourceTag.Id))
                {
                    error =
                        "The selected IndependentTag does not match sourceTagId.";
                    return false;
                }
            }
            else
            {
                if (selectedTags.Count == 0)
                {
                    error =
                        "Select exactly one example IndependentTag before running this tool.";
                    return false;
                }
                if (selectedTags.Count > 1)
                {
                    error =
                        "More than one IndependentTag is selected. Select one example tag.";
                    return false;
                }
                sourceTag = selectedTags[0];
            }

            IList<LinkElementId> taggedIds;
            IList<Reference> references;
            try
            {
                taggedIds = sourceTag.GetTaggedElementIds().ToList();
                references = sourceTag.GetTaggedReferences().ToList();
            }
            catch (Exception exception)
            {
                error =
                    "The selected tag references could not be read: " +
                    exception.Message;
                return false;
            }

            if (taggedIds.Count == 0 || references.Count == 0)
            {
                error = "The selected tag has no host reference.";
                return false;
            }
            if (taggedIds.Count != 1 || references.Count != 1)
            {
                error =
                    "The selected tag references multiple hosts. A single-host example tag is required.";
                return false;
            }
            if (taggedIds[0].LinkInstanceId != ElementId.InvalidElementId)
            {
                error =
                    "The selected tag references a linked model, which this workflow does not support.";
                return false;
            }

            var hostId = taggedIds[0].HostElementId;
            sourceHost = doc.GetElement(hostId) as FamilyInstance;
            if (sourceHost == null)
            {
                error =
                    "The selected tag host is not a supported FamilyInstance.";
                return false;
            }
            sourceReference = references[0];

            sourceView = doc.GetElement(sourceTag.OwnerViewId) as View;
            if (sourceView == null ||
                sourceView.IsTemplate ||
                sourceView is ViewSchedule)
            {
                error = "The selected tag owner view is not a supported graphical view.";
                return false;
            }
            var view3D = sourceView as View3D;
            if (view3D != null && !view3D.IsLocked)
            {
                error =
                    "The selected tag is in an unlocked 3D view. Lock the view before tagging.";
                return false;
            }

            tagType = doc.GetElement(sourceTag.GetTypeId()) as FamilySymbol;
            if (tagType == null)
            {
                error = "The selected tag family type is missing or unsupported.";
                return false;
            }

            var resolvedSourceTagId = sourceTag.Id;
            var resolvedSourceHostId = sourceHost.Id;
            var selectedNonTags = selection
                .Where(id => id != resolvedSourceTagId)
                .ToList();
            if (options.ScopeMode != TagTemplateScopeMode.Selection &&
                selectedNonTags.Any(id => id != resolvedSourceHostId))
            {
                error =
                    "The selection is ambiguous. For family/category scope, select only the example tag and optionally its referenced host.";
                return false;
            }
            if (selectedNonTags.Count == 1 &&
                selectedNonTags[0] != resolvedSourceHostId &&
                options.ScopeMode != TagTemplateScopeMode.Selection)
            {
                error =
                    "The selected element is not referenced by the example tag.";
                return false;
            }
            return true;
        }

        private static TagPlacementTemplate BuildTemplate(
            Document doc,
            IndependentTag sourceTag,
            FamilyInstance sourceHost,
            View sourceView,
            Reference sourceReference,
            FamilySymbol tagType,
            HostLocalFrame sourceFrame,
            IList<string> warnings)
        {
            XYZ tagHead;
            TagOrientation orientation;
            double tagRotation;
            try
            {
                tagHead = sourceTag.TagHeadPosition;
                orientation = sourceTag.TagOrientation;
                tagRotation = sourceTag.RotationAngle;
            }
            catch (Exception exception)
            {
                warnings.Add(
                    "Tag rotation or orientation could not be read: " +
                    exception.Message);
                return null;
            }
            if (tagHead == null)
                return null;

            HostLocalFrameService.ProjectOffset(
                sourceFrame,
                tagHead,
                out var rightOffsetFeet,
                out var frontOffsetFeet);
            var rightOffsetMm = rightOffsetFeet * FeetToMillimeters;
            var frontOffsetMm = frontOffsetFeet * FeetToMillimeters;
            var rotationMode = TagTemplateMath.InferRotationMode(
                tagRotation,
                sourceFrame.RotationRadians,
                RotationToleranceRadians);
            var relativeRotation = TagTemplateMath.NormalizeRadians(
                tagRotation - sourceFrame.RotationRadians);

            var symbol = sourceHost.Symbol;
            var family = symbol == null ? null : symbol.Family;
            var template = new TagPlacementTemplate
            {
                SourceTagId = sourceTag.Id.Value,
                SourceHostElementId = sourceHost.Id.Value,
                SourceViewId = sourceView.Id.Value,
                TagTypeId = tagType.Id.Value,
                TagFamilyName = tagType.Family == null
                    ? string.Empty
                    : tagType.Family.Name,
                TagTypeName = tagType.Name,
                HostCategoryId = sourceHost.Category == null
                    ? 0L
                    : sourceHost.Category.Id.Value,
                HostFamilyId = family == null ? 0L : family.Id.Value,
                HostTypeId = sourceHost.GetTypeId().Value,
                HostCategoryName = sourceHost.Category == null
                    ? string.Empty
                    : sourceHost.Category.Name,
                HostFamilyName = family == null
                    ? string.Empty
                    : family.Name,
                HostTypeName = symbol == null
                    ? string.Empty
                    : symbol.Name,
                AnchorMode = ParseAnchorMode(sourceFrame.AnchorSource),
                AnchorSource = sourceFrame.AnchorSource,
                OrientationSource = sourceFrame.OrientationSource,
                OrientationFallbackUsed = sourceFrame.UsedFallback,
                SourceFacingFlipped = sourceFrame.FacingFlipped,
                SourceHandFlipped = sourceFrame.HandFlipped,
                SourceMirrored = sourceFrame.Mirrored,
                LocalRightOffsetMillimeters = rightOffsetMm,
                LocalFrontOffsetMillimeters = frontOffsetMm,
                PlacementSide = TagTemplateMath.ClassifyPlacement(
                    rightOffsetMm,
                    frontOffsetMm,
                    1.0,
                    12.0),
                DistanceFromAnchorMillimeters = Math.Sqrt(
                    rightOffsetMm * rightOffsetMm +
                    frontOffsetMm * frontOffsetMm),
                RotationMode = rotationMode,
                SourceHostRotationDegrees =
                    sourceFrame.RotationRadians * 180.0 / Math.PI,
                SourceTagRotationDegrees =
                    tagRotation * 180.0 / Math.PI,
                RelativeRotationDegrees =
                    relativeRotation * 180.0 / Math.PI,
                Orientation = orientation,
                HasLeader = sourceTag.HasLeader
            };

            if (!template.HasLeader)
                return template;

            try
            {
                template.LeaderEndCondition = sourceTag.LeaderEndCondition;
            }
            catch (Exception exception)
            {
                warnings.Add(
                    "Leader end condition could not be read: " +
                    exception.Message);
            }

            try
            {
                if (sourceTag.HasLeaderElbow(sourceReference))
                {
                    var elbow = sourceTag.GetLeaderElbow(sourceReference);
                    HostLocalFrameService.ProjectOffset(
                        sourceFrame,
                        elbow,
                        out var elbowRight,
                        out var elbowFront);
                    template.HasLeaderElbow = true;
                    template.LeaderElbowLocalRightOffsetMillimeters =
                        elbowRight * FeetToMillimeters;
                    template.LeaderElbowLocalFrontOffsetMillimeters =
                        elbowFront * FeetToMillimeters;
                }
            }
            catch (Exception exception)
            {
                warnings.Add(
                    "Leader elbow could not be read: " + exception.Message);
            }

            if (template.LeaderEndCondition != LeaderEndCondition.Free)
                return template;

            try
            {
                var end = sourceTag.GetLeaderEnd(sourceReference);
                HostLocalFrameService.ProjectOffset(
                    sourceFrame,
                    end,
                    out var endRight,
                    out var endFront);
                template.HasFreeLeaderEnd = true;
                template.LeaderEndLocalRightOffsetMillimeters =
                    endRight * FeetToMillimeters;
                template.LeaderEndLocalFrontOffsetMillimeters =
                    endFront * FeetToMillimeters;
            }
            catch (Exception exception)
            {
                warnings.Add(
                    "Free leader endpoint could not be read: " +
                    exception.Message);
            }
            return template;
        }

        private static HostAnchorMode ParseAnchorMode(string anchorSource)
        {
            if (anchorSource == "LocationPoint")
                return HostAnchorMode.LocationPoint;
            if (anchorSource == "ViewBoundingBoxCenter")
                return HostAnchorMode.ViewBoundingBoxCenter;
            return HostAnchorMode.SmartTagCenter;
        }

        private static bool ApplyOverride(
            TagPlacementTemplate template,
            TagTemplateOverride value,
            out string error)
        {
            error = null;
            if (value == null)
                return true;
            if ((value.ExpectedSourceTagId > 0 &&
                 value.ExpectedSourceTagId != template.SourceTagId) ||
                (value.ExpectedSourceHostElementId > 0 &&
                 value.ExpectedSourceHostElementId !=
                 template.SourceHostElementId) ||
                (value.ExpectedSourceViewId > 0 &&
                 value.ExpectedSourceViewId != template.SourceViewId) ||
                (value.ExpectedTagTypeId > 0 &&
                 value.ExpectedTagTypeId != template.TagTypeId))
            {
                error =
                    "The analyzed template no longer matches the selected source tag, host, view, or tag type. Analyze it again.";
                return false;
            }

            if (value.HasAnchorMode)
                template.AnchorMode = value.AnchorMode;
            if (value.HasLocalRightOffset)
                template.LocalRightOffsetMillimeters =
                    value.LocalRightOffsetMillimeters;
            if (value.HasLocalFrontOffset)
                template.LocalFrontOffsetMillimeters =
                    value.LocalFrontOffsetMillimeters;
            if (value.HasRotationMode)
                template.RotationMode = value.RotationMode;
            if (value.HasRelativeRotation)
                template.RelativeRotationDegrees =
                    value.RelativeRotationDegrees;
            if (value.HasOrientation)
                template.Orientation = value.Orientation;
            if (value.HasLeader)
                template.HasLeader = value.LeaderValue;

            template.DistanceFromAnchorMillimeters = Math.Sqrt(
                template.LocalRightOffsetMillimeters *
                template.LocalRightOffsetMillimeters +
                template.LocalFrontOffsetMillimeters *
                template.LocalFrontOffsetMillimeters);
            template.PlacementSide = TagTemplateMath.ClassifyPlacement(
                template.LocalRightOffsetMillimeters,
                template.LocalFrontOffsetMillimeters,
                1.0,
                12.0);
            return true;
        }

        private static List<Element> ResolveCandidates(
            Document doc,
            View view,
            FamilyInstance sourceHost,
            IndependentTag sourceTag,
            ICollection<ElementId> selectedIds,
            TagTemplateRequestOptions options,
            IList<TagTemplateTargetItem> invalidItems,
            IList<string> warnings)
        {
            IEnumerable<Element> candidates;
            var visibleInstances = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(FamilyInstance))
                .WhereElementIsNotElementType()
                .Cast<Element>()
                .ToList();

            switch (options.ScopeMode)
            {
                case TagTemplateScopeMode.SameFamilyAndType:
                    candidates = visibleInstances.Where(element =>
                        element.GetTypeId() == sourceHost.GetTypeId());
                    break;
                case TagTemplateScopeMode.SameCategory:
                    candidates = visibleInstances.Where(element =>
                        element.Category != null &&
                        sourceHost.Category != null &&
                        element.Category.Id == sourceHost.Category.Id);
                    break;
                case TagTemplateScopeMode.Selection:
                    candidates = ResolveIds(
                        doc,
                        selectedIds == null
                            ? new List<ElementId>()
                            : selectedIds,
                        visibleInstances,
                        sourceTag,
                        invalidItems);
                    break;
                case TagTemplateScopeMode.ExplicitElementIds:
                    candidates = ResolveIds(
                        doc,
                        options.ExplicitElementIds
                            .Select(value => new ElementId(value))
                            .ToList(),
                        visibleInstances,
                        sourceTag,
                        invalidItems);
                    break;
                default:
                    if (!options.IncludeAllHostTypes)
                    {
                        candidates = visibleInstances.Where(element =>
                            element.GetTypeId() == sourceHost.GetTypeId());
                    }
                    else
                    {
                        var sourceFamilyId = sourceHost.Symbol?.Family?.Id;
                        candidates = visibleInstances
                            .OfType<FamilyInstance>()
                            .Where(instance =>
                                sourceFamilyId != null &&
                                instance.Symbol?.Family?.Id ==
                                sourceFamilyId)
                            .Cast<Element>();
                    }
                    break;
            }

            var result = candidates
                .Where(element => element != null &&
                                  element.Id != sourceTag.Id)
                .GroupBy(element => element.Id)
                .Select(group => group.First())
                .ToList();
            if (result.Count == 0 && invalidItems.Count == 0)
                warnings.Add("No matching target elements were found in the source view.");
            return result;
        }

        private static IEnumerable<Element> ResolveIds(
            Document doc,
            ICollection<ElementId> ids,
            ICollection<Element> visibleInstances,
            IndependentTag sourceTag,
            IList<TagTemplateTargetItem> invalidItems)
        {
            var visibleIds = new HashSet<ElementId>(
                visibleInstances.Select(element => element.Id));
            var result = new List<Element>();
            foreach (var id in ids.Distinct())
            {
                if (id == null ||
                    id == ElementId.InvalidElementId ||
                    id == sourceTag.Id)
                    continue;
                var element = doc.GetElement(id);
                if (element == null)
                {
                    invalidItems.Add(new TagTemplateTargetItem
                    {
                        HostElementId = id.Value,
                        Status = "Unsupported",
                        Reason = "Element ID does not exist in the active document."
                    });
                    continue;
                }
                if (!visibleIds.Contains(id))
                {
                    var invisible = CreateTargetItem(element);
                    invisible.Status = "Unsupported";
                    invisible.Reason =
                        "Element is not visible or taggable in the source view.";
                    invalidItems.Add(invisible);
                    continue;
                }
                result.Add(element);
            }
            return result;
        }

        private static bool IsCompatibleHost(
            FamilyInstance source,
            Element target)
        {
            return target is FamilyInstance &&
                   source.Category != null &&
                   target.Category != null &&
                   source.Category.Id == target.Category.Id;
        }

        private static Dictionary<long, List<ElementId>>
            BuildExistingMatchingTagIndex(
            Document doc,
            View view,
            ElementId tagTypeId)
        {
            var result = new Dictionary<long, List<ElementId>>();
            var tags = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>()
                .Where(tag => tag.GetTypeId() == tagTypeId);
            foreach (var tag in tags)
            {
                foreach (var hostId in
                         TagDiscoveryService.GetTaggedElementIds(tag))
                {
                    List<ElementId> tagIds;
                    if (!result.TryGetValue(hostId.Value, out tagIds))
                    {
                        tagIds = new List<ElementId>();
                        result[hostId.Value] = tagIds;
                    }
                    tagIds.Add(tag.Id);
                }
            }
            return result;
        }

        private static TagTemplateTargetItem CreateTargetItem(
            Element element)
        {
            var family = element as FamilyInstance;
            return new TagTemplateTargetItem
            {
                HostElementId = element.Id.Value,
                CategoryName = element.Category == null
                    ? string.Empty
                    : element.Category.Name,
                FamilyName = family?.Symbol?.Family?.Name ?? string.Empty,
                TypeName = family?.Symbol?.Name ??
                           (element.Document.GetElement(
                               element.GetTypeId())?.Name ?? string.Empty),
                HostElement = element
            };
        }
    }
}
