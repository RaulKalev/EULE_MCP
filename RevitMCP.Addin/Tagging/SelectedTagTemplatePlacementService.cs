#nullable disable

using System;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tagging
{
    public sealed class SelectedTagTemplatePlacementService
    {
        private const double MillimetersToFeet = 1.0 / 304.8;

        public TagTemplatePlacementResult Apply(
            Document doc,
            TagTemplateAnalysisResult analysis,
            TagTemplateRequestOptions options,
            CancellationToken cancellationToken)
        {
            var result = new TagTemplatePlacementResult();
            if (doc == null ||
                analysis == null ||
                analysis.Template == null ||
                analysis.SourceView == null ||
                analysis.TagType == null)
            {
                result.Errors.Add("A valid selected-tag analysis is required.");
                return result;
            }

            options = options ?? new TagTemplateRequestOptions();
            var template = analysis.Template;
            var view = analysis.SourceView;
            var tagType = analysis.TagType;
            if (!tagType.IsActive)
            {
                tagType.Activate();
                doc.Regenerate();
            }

            TagCollisionDetector detector = null;
            if (options.EnableCollisionDetection)
            {
                detector = new TagCollisionDetector(
                    view,
                    options.CollisionGapMillimeters);
                detector.CollectObstacles(doc);
            }

            foreach (var sourceItem in analysis.Targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = Clone(sourceItem);
                result.Items.Add(item);
                if (!sourceItem.Eligible)
                {
                    result.SkippedCount++;
                    continue;
                }

                var host = doc.GetElement(
                    new ElementId(sourceItem.HostElementId));
                if (host == null)
                {
                    item.Eligible = false;
                    item.Status = "Failed";
                    item.Reason = "Target element no longer exists.";
                    result.FailedCount++;
                    result.Errors.Add(
                        "Element " + sourceItem.HostElementId +
                        ": target no longer exists.");
                    continue;
                }

                HostLocalFrame frame;
                if (!HostLocalFrameService.TryCreate(
                        host,
                        view,
                        template.AnchorMode,
                        out frame,
                        out var frameError))
                {
                    item.Eligible = false;
                    item.Status = "Failed";
                    item.Reason =
                        "Target orientation changed or is invalid: " + frameError;
                    result.FailedCount++;
                    result.Errors.Add(
                        "Element " + sourceItem.HostElementId +
                        ": " + frameError);
                    continue;
                }

                var intendedHead = HostLocalFrameService.ReconstructPoint(
                    frame,
                    template.LocalRightOffsetMillimeters *
                    MillimetersToFeet,
                    template.LocalFrontOffsetMillimeters *
                    MillimetersToFeet);
                var head = intendedHead;
                var collisionFree = true;
                if (detector != null)
                {
                    head = detector.FindValidPosition(
                        frame.Anchor,
                        intendedHead,
                        out collisionFree);
                    if ((head - intendedHead).GetLength() > 1e-7)
                        item.CollisionAdjusted = true;
                }

                using (var targetTransaction = new SubTransaction(doc))
                {
                    var targetTransactionStarted = false;
                    try
                    {
                        targetTransaction.Start();
                        targetTransactionStarted = true;
                        var reference = new Reference(host);

                        // Creating with a temporary leader preserves the requested
                        // head for leaderless tags on affected Revit/tag families.
                        var tag = IndependentTag.Create(
                            doc,
                            tagType.Id,
                            view.Id,
                            reference,
                            true,
                            template.Orientation,
                            head);
                        if (tag == null)
                            throw new InvalidOperationException(
                                "Revit did not create an IndependentTag.");

                        tag.TagHeadPosition = head;
                        ApplyRotation(
                            doc,
                            tag,
                            view,
                            frame,
                            template);
                        ApplyLeader(
                            tag,
                            reference,
                            frame,
                            template);

                        if (detector != null)
                        {
                            doc.Regenerate();
                            TagCollisionDetector.ObstacleBounds actualBounds;
                            if (detector.HasCollisionWithActualBounds(
                                    tag,
                                    out actualBounds))
                            {
                                var learnedDistanceFeet =
                                    template.DistanceFromAnchorMillimeters *
                                    MillimetersToFeet;
                                var requestedMinimumFeet =
                                    Math.Max(
                                        0.0,
                                        options.MinimumOffsetMillimeters) *
                                    MillimetersToFeet;
                                var minimumOffset = requestedMinimumFeet > 0.0
                                    ? requestedMinimumFeet
                                    : Math.Max(
                                        learnedDistanceFeet * 0.5,
                                        TagGeometryService
                                            .MillimetersToFeet(10.0));
                                bool actualCollisionFree;
                                var adjusted = detector
                                    .FindValidPositionWithActualSize(
                                        frame.Anchor,
                                        head,
                                        actualBounds,
                                        minimumOffset,
                                        out actualCollisionFree);
                                if ((adjusted - head).GetLength() > 1e-7)
                                {
                                    tag.TagHeadPosition = adjusted;
                                    head = adjusted;
                                    if (!item.CollisionAdjusted)
                                        item.CollisionAdjusted = true;
                                    doc.Regenerate();
                                }
                                collisionFree = actualCollisionFree;
                            }
                        }

                        // The leaderless workaround is intentionally last.
                        if (!template.HasLeader)
                            tag.HasLeader = false;

                        SmartTagMarkerStorage.SetManagedTag(tag, host.Id);
                        targetTransaction.Commit();
                        targetTransactionStarted = false;

                        item.CreatedTagId = tag.Id.Value;
                        item.Status = "Created";
                        item.Reason = item.CollisionAdjusted
                            ? "Created; collision avoidance adjusted the learned position."
                            : "Created from the selected tag template.";
                        item.CollisionFree = collisionFree;
                        item.ProposedHead = head;
                        item.ProposedHeadX = head.X;
                        item.ProposedHeadY = head.Y;
                        item.ProposedHeadZ = head.Z;
                        result.CreatedCount++;
                        if (item.CollisionAdjusted)
                            result.CollisionAdjustedCount++;
                        if (detector != null)
                        {
                            try
                            {
                                detector.AddNewTag(tag);
                            }
                            catch (Exception detectorException)
                            {
                                result.Errors.Add(
                                    "Tag " + tag.Id.Value +
                                    ": collision index update failed after placement: " +
                                    detectorException.Message);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (targetTransactionStarted)
                        {
                            try
                            {
                                targetTransaction.RollBack();
                            }
                            catch (Exception rollbackException)
                            {
                                result.Errors.Add(
                                    "Cancellation rollback failed for element " +
                                    sourceItem.HostElementId + ": " +
                                    rollbackException.Message);
                            }
                        }
                        throw;
                    }
                    catch (Exception exception)
                    {
                        if (targetTransactionStarted)
                        {
                            try
                            {
                                targetTransaction.RollBack();
                            }
                            catch (Exception rollbackException)
                            {
                                result.Errors.Add(
                                    "Rollback failed for element " +
                                    sourceItem.HostElementId + ": " +
                                    rollbackException.Message);
                            }
                        }
                        item.Status = "Failed";
                        item.Reason = exception.Message;
                        item.CreatedTagId = 0;
                        result.FailedCount++;
                        result.Errors.Add(
                            "Element " + sourceItem.HostElementId +
                            ": " + exception.Message);
                    }
                }
            }

            if (detector != null)
                result.CollisionDiagnostics =
                    detector.GetPerformanceDiagnostics();
            return result;
        }

        private static void ApplyRotation(
            Document doc,
            IndependentTag tag,
            View view,
            HostLocalFrame targetFrame,
            TagPlacementTemplate template)
        {
            var desired = TagTemplateMath.ResolveTargetRotation(
                template.RotationMode,
                template.SourceTagRotationDegrees * Math.PI / 180.0,
                targetFrame.RotationRadians,
                template.RelativeRotationDegrees * Math.PI / 180.0);

            double current;
            try
            {
                current = tag.RotationAngle;
            }
            catch (Exception firstReadException)
            {
                doc.Regenerate();
                try
                {
                    current = tag.RotationAngle;
                }
                catch (Exception secondReadException)
                {
                    throw new InvalidOperationException(
                        "The created tag rotation could not be determined after regeneration. " +
                        firstReadException.Message + " / " +
                        secondReadException.Message,
                        secondReadException);
                }
            }
            var delta = TagTemplateMath.NormalizeRadians(desired - current);
            if (Math.Abs(delta) < 1e-8)
                return;

            var normal = view.ViewDirection;
            if (normal == null || normal.GetLength() < 1e-8)
                throw new InvalidOperationException(
                    "The target view has no usable rotation axis.");
            var head = tag.TagHeadPosition;
            var axis = Line.CreateBound(
                head,
                head + normal.Normalize());
            ElementTransformUtils.RotateElement(
                doc,
                tag.Id,
                axis,
                delta);
        }

        private static void ApplyLeader(
            IndependentTag tag,
            Reference reference,
            HostLocalFrame targetFrame,
            TagPlacementTemplate template)
        {
            if (!template.HasLeader)
                return;

            tag.HasLeader = true;
            tag.LeaderEndCondition = template.LeaderEndCondition;

            if (template.HasFreeLeaderEnd &&
                template.LeaderEndCondition == LeaderEndCondition.Free)
            {
                tag.SetLeaderEnd(
                    reference,
                    HostLocalFrameService.ReconstructPoint(
                        targetFrame,
                        template.LeaderEndLocalRightOffsetMillimeters *
                        MillimetersToFeet,
                        template.LeaderEndLocalFrontOffsetMillimeters *
                        MillimetersToFeet));
            }

            if (template.HasLeaderElbow)
            {
                tag.SetLeaderElbow(
                    reference,
                    HostLocalFrameService.ReconstructPoint(
                        targetFrame,
                        template.LeaderElbowLocalRightOffsetMillimeters *
                        MillimetersToFeet,
                        template.LeaderElbowLocalFrontOffsetMillimeters *
                        MillimetersToFeet));
            }
        }

        private static TagTemplateTargetItem Clone(
            TagTemplateTargetItem source)
        {
            var item = new TagTemplateTargetItem
            {
                HostElementId = source.HostElementId,
                CategoryName = source.CategoryName,
                FamilyName = source.FamilyName,
                TypeName = source.TypeName,
                Eligible = source.Eligible,
                AlreadyTagged = source.AlreadyTagged,
                CollisionAdjusted = source.CollisionAdjusted,
                CollisionFree = source.CollisionFree,
                HostOrientationSource =
                    source.HostOrientationSource,
                OrientationFallbackUsed =
                    source.OrientationFallbackUsed,
                FacingFlipped = source.FacingFlipped,
                HandFlipped = source.HandFlipped,
                Mirrored = source.Mirrored,
                CreatedTagId = source.CreatedTagId,
                Status = source.Status,
                Reason = source.Reason,
                ProposedHeadX = source.ProposedHeadX,
                ProposedHeadY = source.ProposedHeadY,
                ProposedHeadZ = source.ProposedHeadZ,
                HostElement = source.HostElement,
                ProposedHead = source.ProposedHead,
                HostFrame = source.HostFrame
            };
            foreach (var id in source.ExistingTagIds)
                item.ExistingTagIds.Add(id);
            return item;
        }
    }
}
