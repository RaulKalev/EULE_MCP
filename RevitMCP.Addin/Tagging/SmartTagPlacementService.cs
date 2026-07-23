#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tagging
{
    public sealed class SmartTagPlacementService
    {
        public SmartTagPlacementResult Preview(
            Document doc,
            View view,
            IEnumerable<Element> elements,
            FamilySymbol baseTagType,
            DirectionTagTypes directionTypes,
            SmartTagOptions options,
            CancellationToken cancellationToken)
        {
            return Process(
                doc,
                view,
                elements,
                baseTagType,
                directionTypes,
                options,
                true,
                cancellationToken);
        }

        public SmartTagPlacementResult Place(
            Document doc,
            View view,
            IEnumerable<Element> elements,
            FamilySymbol baseTagType,
            DirectionTagTypes directionTypes,
            SmartTagOptions options,
            CancellationToken cancellationToken)
        {
            return Process(
                doc,
                view,
                elements,
                baseTagType,
                directionTypes,
                options,
                false,
                cancellationToken);
        }

        private static SmartTagPlacementResult Process(
            Document doc,
            View view,
            IEnumerable<Element> elements,
            FamilySymbol baseTagType,
            DirectionTagTypes directionTypes,
            SmartTagOptions options,
            bool previewOnly,
            CancellationToken cancellationToken)
        {
            var result = new SmartTagPlacementResult();
            if (doc == null || view == null || baseTagType == null)
            {
                result.Errors.Add("Document, view, and base tag type are required.");
                return result;
            }

            var sourceElements = elements == null
                ? new List<Element>()
                : elements.Where(element => element != null)
                    .GroupBy(element => element.Id)
                    .Select(group => group.First())
                    .ToList();
            result.CandidateCount = sourceElements.Count;

            var resolvedTypeId = directionTypes == null
                ? baseTagType.Id
                : directionTypes.Resolve(options.Direction, baseTagType.Id);
            var resolvedType = doc.GetElement(resolvedTypeId) as FamilySymbol;
            if (resolvedType == null)
                resolvedType = baseTagType;
            var directionOverride = resolvedType.Id != baseTagType.Id;

            if (!previewOnly && !resolvedType.IsActive)
            {
                resolvedType.Activate();
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

            var scale = Math.Max(1, view.Scale);
            var leaderOffset =
                (TagGeometryService.MillimetersToFeet(
                     options.AttachedLengthMillimeters) +
                 TagGeometryService.MillimetersToFeet(
                     options.FreeLengthMillimeters)) * scale;
            var baseDirection = TagGeometryService.GetDirectionVector(
                view,
                options.Direction);

            foreach (var element in sourceElements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = new SmartTagPlacementItem
                {
                    ElementId = element.Id.Value,
                    TagTypeId = resolvedType.Id.Value,
                    TagTypeName = resolvedType.Family.Name + " : " + resolvedType.Name,
                    CategoryName = element.Category == null
                        ? string.Empty
                        : element.Category.Name
                };
                result.Items.Add(item);

                try
                {
                    if (options.SkipAlreadyTagged &&
                        TagDiscoveryService.IsElementTaggedInView(
                            doc,
                            view,
                            element.Id,
                            resolvedType.Category == null
                                ? ElementId.InvalidElementId
                                : resolvedType.Category.Id))
                    {
                        result.SkippedAlreadyTaggedCount++;
                        item.Reason = "Already tagged in the target view.";
                        continue;
                    }

                    XYZ anchor;
                    if (!TagGeometryService.TryGetAnchorPoint(
                            element,
                            view,
                            options.AnchorPoint,
                            out anchor))
                    {
                        item.Reason = "No usable location or view bounding box.";
                        continue;
                    }

                    var offsetDirection = TagGeometryService.ResolveOffsetDirection(
                        baseDirection,
                        view,
                        element,
                        options.RotationRadians,
                        options.DetectElementRotation,
                        directionOverride);
                    var safeMinimumOffset = TagGeometryService.GetSafeMinimumOffsetFeet(
                        element,
                        view,
                        options.MinimumOffsetMillimeters);
                    var head = anchor + offsetDirection.Multiply(
                        leaderOffset > 1e-9
                            ? leaderOffset
                            : safeMinimumOffset);

                    var collisionFree = true;
                    if (detector != null)
                        head = detector.FindValidPosition(
                            anchor,
                            head,
                            out collisionFree);

                    item.WouldPlace = true;
                    item.CollisionFree = collisionFree;
                    SetHead(item, head);
                    if (previewOnly)
                    {
                        item.Reason = collisionFree
                            ? "Eligible; estimated position is collision-free."
                            : "Eligible; deterministic least-overlap fallback would be used.";
                        if (!collisionFree)
                            result.CollisionFallbackCount++;
                        if (detector != null)
                            detector.AddEstimatedTag(head);
                        continue;
                    }

                    var createWithLeader = true;
                    var tag = IndependentTag.Create(
                        doc,
                        resolvedType.Id,
                        view.Id,
                        new Reference(element),
                        createWithLeader,
                        options.Orientation,
                        head);
                    if (tag == null)
                    {
                        item.WouldPlace = false;
                        item.Reason = "Revit did not create a tag.";
                        continue;
                    }

                    tag.TagHeadPosition = head;
                    if (options.HasLeader)
                    {
                        try { tag.LeaderEndCondition = options.LeaderEndCondition; }
                        catch { }
                    }
                    SmartTagMarkerStorage.SetManagedTag(tag, element.Id);

                    if (detector != null)
                    {
                        doc.Regenerate();
                        TagCollisionDetector.ObstacleBounds actualBounds;
                        if (detector.HasCollisionWithActualBounds(
                                tag,
                                out actualBounds))
                        {
                            bool actualCollisionFree;
                            var adjustedHead = detector.FindValidPositionWithActualSize(
                                anchor,
                                head,
                                actualBounds,
                                safeMinimumOffset,
                                out actualCollisionFree);
                            if ((adjustedHead - head).GetLength() > 1e-6)
                            {
                                tag.TagHeadPosition = adjustedHead;
                                head = adjustedHead;
                                doc.Regenerate();
                            }
                            collisionFree = actualCollisionFree;
                        }
                        detector.AddNewTag(tag);
                    }

                    var rotation = TagGeometryService.ResolveTagRotation(
                        view,
                        element,
                        offsetDirection,
                        options.RotationRadians,
                        options.DetectElementRotation,
                        directionOverride);
                    if (Math.Abs(rotation) > 1e-9)
                    {
                        try
                        {
                            var viewDirection = view.ViewDirection;
                            var axis = Line.CreateBound(
                                head,
                                head + viewDirection);
                            ElementTransformUtils.RotateElement(
                                doc,
                                tag.Id,
                                axis,
                                rotation);
                        }
                        catch
                        {
                            // Unsupported tag families remain unrotated.
                        }
                    }

                    if (!options.HasLeader)
                    {
                        try { tag.HasLeader = false; }
                        catch { }
                    }

                    item.TagId = tag.Id.Value;
                    item.CollisionFree = collisionFree;
                    item.Reason = collisionFree
                        ? "Placed."
                        : "Placed using deterministic least-overlap fallback.";
                    SetHead(item, head);
                    result.PlacedCount++;
                    if (!collisionFree)
                        result.CollisionFallbackCount++;
                }
                catch (Exception exception)
                {
                    item.WouldPlace = false;
                    item.Reason = exception.Message;
                    result.Errors.Add(
                        "Element " + element.Id.Value + ": " + exception.Message);
                }
            }

            if (detector != null)
                result.CollisionDiagnostics = detector.GetPerformanceDiagnostics();
            return result;
        }

        private static void SetHead(SmartTagPlacementItem item, XYZ head)
        {
            if (head == null)
                return;
            item.HeadX = head.X;
            item.HeadY = head.Y;
            item.HeadZ = head.Z;
        }
    }
}
