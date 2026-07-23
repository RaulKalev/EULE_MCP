#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tagging
{
    public sealed class TagAdjustmentService
    {
        public List<TagAdjustmentProposal> Compute(
            Document doc,
            View view,
            IEnumerable<IndependentTag> tags,
            SmartTagOptions options)
        {
            var proposals = new List<TagAdjustmentProposal>();
            if (doc == null || view == null || tags == null)
                return proposals;

            var source = tags.Where(tag => tag != null).ToList();
            var sorted = source
                .OrderByDescending(tag => DistanceFromHost(doc, view, tag, options))
                .ToList();
            var movingTagIds = new HashSet<ElementId>();

            foreach (var tag in sorted)
            {
                try
                {
                    TagCollisionDetector detector = null;
                    if (options.EnableCollisionDetection)
                    {
                        detector = new TagCollisionDetector(
                            view,
                            options.CollisionGapMillimeters);
                        var exclusions = new HashSet<ElementId>(movingTagIds)
                        {
                            tag.Id
                        };
                        detector.CollectObstacles(doc, exclusions);
                    }

                    var proposal = ComputeOne(doc, view, tag, options, detector);
                    if (proposal != null && proposal.IsSignificantChange())
                    {
                        proposals.Add(proposal);
                        movingTagIds.Add(tag.Id);
                    }
                }
                catch
                {
                    // A single stale tag does not invalidate the rest of the batch.
                }
            }
            return proposals;
        }

        private static TagAdjustmentProposal ComputeOne(
            Document doc,
            View view,
            IndependentTag tag,
            SmartTagOptions options,
            TagCollisionDetector detector)
        {
            var referencedId = TagDiscoveryService
                .GetTaggedElementIds(tag)
                .FirstOrDefault();
            if (referencedId == null ||
                referencedId == ElementId.InvalidElementId)
                return null;

            var element = doc.GetElement(referencedId);
            if (element == null)
                return null;

            XYZ anchor;
            if (!TagGeometryService.TryGetAnchorPoint(
                    element,
                    view,
                    options.AnchorPoint,
                    out anchor))
                return null;

            var oldState = new TagStateSnapshot(tag);
            var baseDirection = TagGeometryService.GetDirectionVector(
                view,
                options.Direction);
            var offsetDirection = TagGeometryService.ResolveOffsetDirection(
                baseDirection,
                view,
                element,
                options.RotationRadians,
                options.DetectElementRotation,
                false);
            var scale = Math.Max(1, view.Scale);
            var leaderOffset =
                (TagGeometryService.MillimetersToFeet(
                     options.AttachedLengthMillimeters) +
                 TagGeometryService.MillimetersToFeet(
                     options.FreeLengthMillimeters)) * scale;
            var distance = leaderOffset > 1e-9
                ? leaderOffset
                : TagGeometryService.MillimetersToFeet(
                    options.MinimumOffsetMillimeters);
            var head = anchor + offsetDirection.Multiply(distance);

            if (detector != null)
            {
                if (oldState.TagHeadPosition != null &&
                    !detector.HasCollisionAtPosition(oldState.TagHeadPosition))
                {
                    head = oldState.TagHeadPosition;
                }
                else
                {
                    bool ignored;
                    head = detector.FindValidPosition(anchor, head, out ignored);
                }
            }

            var newState = new TagStateSnapshot
            {
                TagHeadPosition = head,
                HasLeader = options.HasLeaderSpecified
                    ? options.HasLeader
                    : oldState.HasLeader,
                LeaderEndCondition =
                    (options.HasLeaderSpecified
                         ? options.HasLeader
                         : oldState.HasLeader) &&
                    options.LeaderEndConditionSpecified
                    ? options.LeaderEndCondition
                    : oldState.LeaderEndCondition,
                Orientation = options.OrientationSpecified
                    ? options.Orientation
                    : oldState.Orientation
            };
            return new TagAdjustmentProposal
            {
                TagId = tag.Id,
                ReferencedElementId = referencedId,
                OldState = oldState,
                NewState = newState,
                Reason = "Apply current smart-tag placement settings."
            };
        }

        private static double DistanceFromHost(
            Document doc,
            View view,
            IndependentTag tag,
            SmartTagOptions options)
        {
            try
            {
                var referencedId = TagDiscoveryService
                    .GetTaggedElementIds(tag)
                    .FirstOrDefault();
                if (referencedId == null ||
                    referencedId == ElementId.InvalidElementId)
                    return 0.0;
                var element = doc.GetElement(referencedId);
                XYZ anchor;
                if (element == null ||
                    !TagGeometryService.TryGetAnchorPoint(
                        element,
                        view,
                        options.AnchorPoint,
                        out anchor) ||
                    tag.TagHeadPosition == null)
                    return 0.0;
                return (tag.TagHeadPosition - anchor).GetLength();
            }
            catch
            {
                return 0.0;
            }
        }
    }
}
