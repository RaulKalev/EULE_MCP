#nullable disable

using System;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tagging
{
    /// <summary>
    /// Builds an orthonormal, in-view frame for a host. Family orientation vectors
    /// are consumed as Revit reports them; flip or mirror flags are not applied a
    /// second time because that would invert already-transformed instances.
    /// </summary>
    public static class HostLocalFrameService
    {
        private const double VectorTolerance = 1e-8;

        public static bool TryCreate(
            Element element,
            View view,
            HostAnchorMode anchorMode,
            out HostLocalFrame frame,
            out string error)
        {
            frame = null;
            error = null;
            if (element == null || view == null)
            {
                error = "A host element and source view are required.";
                return false;
            }

            XYZ normal;
            XYZ viewRight;
            XYZ viewUp;
            if (!TryNormalize(view.ViewDirection, out normal) ||
                !TryProjectAndNormalize(view.RightDirection, normal, out viewRight) ||
                !TryProjectAndNormalize(view.UpDirection, normal, out viewUp))
            {
                error = "The view does not expose a usable right/up coordinate system.";
                return false;
            }

            XYZ anchor;
            string anchorSource;
            if (!TryGetAnchor(
                    element,
                    view,
                    anchorMode,
                    out anchor,
                    out anchorSource,
                    out error))
                return false;

            XYZ right;
            XYZ front;
            string orientationSource;
            var usedFallback = false;
            var family = element as FamilyInstance;

            if (family != null &&
                TryCreateAxes(
                    family.HandOrientation,
                    family.FacingOrientation,
                    normal,
                    out right,
                    out front))
            {
                orientationSource = "FamilyInstance.HandOrientation/FacingOrientation";
            }
            else
            {
                usedFallback = true;
                var point = element.Location as LocationPoint;
                if (point != null &&
                    TryRotateViewAxes(
                        viewRight,
                        viewUp,
                        normal,
                        point.Rotation,
                        out right,
                        out front))
                {
                    orientationSource = "LocationPoint.Rotation";
                }
                else if (family != null &&
                         TryCreateAxes(
                             family.GetTransform().BasisX,
                             family.GetTransform().BasisY,
                             normal,
                             out right,
                             out front))
                {
                    orientationSource = "FamilyInstance.GetTransform";
                }
                else
                {
                    var curve = element.Location as LocationCurve;
                    if (curve != null &&
                        curve.Curve != null &&
                        TryProjectAndNormalize(
                            curve.Curve.GetEndPoint(1) -
                            curve.Curve.GetEndPoint(0),
                            normal,
                            out right))
                    {
                        front = normal.CrossProduct(right);
                        if (!TryNormalize(front, out front))
                        {
                            error = "The host curve direction cannot form an in-view frame.";
                            return false;
                        }
                        orientationSource = "LocationCurve.Direction";
                    }
                    else
                    {
                        right = viewRight;
                        front = viewUp;
                        orientationSource = "View.RightDirection/UpDirection";
                    }
                }
            }

            double rotation;
            if (!TrySignedAngle(viewRight, right, normal, out rotation))
            {
                error = "The host orientation cannot be measured in the source view.";
                return false;
            }

            frame = new HostLocalFrame
            {
                Anchor = anchor,
                Right = right,
                Front = front,
                ViewNormal = normal,
                RotationRadians = rotation,
                OrientationSource = orientationSource,
                AnchorSource = anchorSource,
                UsedFallback = usedFallback,
                FacingFlipped = family != null && family.FacingFlipped,
                HandFlipped = family != null && family.HandFlipped,
                Mirrored = family != null && family.Mirrored
            };
            return true;
        }

        public static XYZ ReconstructPoint(
            HostLocalFrame frame,
            double localRightOffsetFeet,
            double localFrontOffsetFeet)
        {
            return frame.Anchor +
                   frame.Right.Multiply(localRightOffsetFeet) +
                   frame.Front.Multiply(localFrontOffsetFeet);
        }

        public static void ProjectOffset(
            HostLocalFrame frame,
            XYZ point,
            out double localRightOffsetFeet,
            out double localFrontOffsetFeet)
        {
            var offset = point - frame.Anchor;
            localRightOffsetFeet = offset.DotProduct(frame.Right);
            localFrontOffsetFeet = offset.DotProduct(frame.Front);
        }

        private static bool TryGetAnchor(
            Element element,
            View view,
            HostAnchorMode mode,
            out XYZ anchor,
            out string source,
            out string error)
        {
            anchor = null;
            source = string.Empty;
            error = null;

            if (mode == HostAnchorMode.LocationPoint)
            {
                var point = element.Location as LocationPoint;
                if (point == null || point.Point == null)
                {
                    error = "The selected anchorMode requires a LocationPoint, but the host does not have one.";
                    return false;
                }
                anchor = point.Point;
                source = "LocationPoint";
                return true;
            }

            if (mode == HostAnchorMode.ViewBoundingBoxCenter)
            {
                var bounds = element.get_BoundingBox(view);
                if (bounds == null)
                {
                    error = "The host has no bounding box in the source view.";
                    return false;
                }
                anchor = (bounds.Min + bounds.Max) * 0.5;
                source = "ViewBoundingBoxCenter";
                return true;
            }

            if (!TagGeometryService.TryGetAnchorPoint(
                    element,
                    view,
                    TagAnchorPoint.Center,
                    out anchor))
            {
                error = "The Smart Tags center anchor could not be determined for the host.";
                return false;
            }

            source = element.get_BoundingBox(view) != null
                ? "SmartTags.ViewBoundingBoxCenter"
                : element.Location is LocationPoint
                    ? "SmartTags.LocationPointFallback"
                    : "SmartTags.LocationCurveFallback";
            return true;
        }

        private static bool TryCreateAxes(
            XYZ rawRight,
            XYZ rawFront,
            XYZ normal,
            out XYZ right,
            out XYZ front)
        {
            right = null;
            front = null;
            if (!TryProjectAndNormalize(rawRight, normal, out right))
                return false;

            XYZ projectedFront;
            if (!TryProjectAndNormalize(rawFront, normal, out projectedFront))
                return false;

            var orthogonalFront = projectedFront -
                                  right.Multiply(projectedFront.DotProduct(right));
            if (!TryNormalize(orthogonalFront, out front))
                return false;

            // Preserve the sign Revit reported for the facing axis.
            if (front.DotProduct(projectedFront) < 0.0)
                front = front.Negate();
            return true;
        }

        private static bool TryRotateViewAxes(
            XYZ viewRight,
            XYZ viewUp,
            XYZ normal,
            double angle,
            out XYZ right,
            out XYZ front)
        {
            right = null;
            front = null;
            try
            {
                var transform = Transform.CreateRotationAtPoint(
                    normal,
                    angle,
                    XYZ.Zero);
                return TryCreateAxes(
                    transform.OfVector(viewRight),
                    transform.OfVector(viewUp),
                    normal,
                    out right,
                    out front);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryProjectAndNormalize(
            XYZ vector,
            XYZ normal,
            out XYZ normalized)
        {
            normalized = null;
            if (vector == null || normal == null)
                return false;
            return TryNormalize(
                vector - normal.Multiply(vector.DotProduct(normal)),
                out normalized);
        }

        private static bool TryNormalize(XYZ vector, out XYZ normalized)
        {
            normalized = null;
            if (vector == null || vector.GetLength() < VectorTolerance)
                return false;
            normalized = vector.Normalize();
            return true;
        }

        private static bool TrySignedAngle(
            XYZ from,
            XYZ to,
            XYZ normal,
            out double angle)
        {
            angle = 0.0;
            XYZ normalizedFrom;
            XYZ normalizedTo;
            if (!TryNormalize(from, out normalizedFrom) ||
                !TryNormalize(to, out normalizedTo) ||
                normal == null)
                return false;

            var unsigned = normalizedFrom.AngleTo(normalizedTo);
            var sign = normalizedFrom.CrossProduct(normalizedTo)
                .DotProduct(normal) < 0.0
                ? -1.0
                : 1.0;
            angle = unsigned * sign;
            return true;
        }
    }
}
