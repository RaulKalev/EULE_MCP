#nullable disable

using System;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tagging
{
    public static class TagGeometryService
    {
        public static bool TryGetAnchorPoint(
            Element element,
            View view,
            TagAnchorPoint anchorType,
            out XYZ anchor)
        {
            anchor = null;
            if (element == null || view == null)
                return false;

            var bounds = element.get_BoundingBox(view);
            if (bounds != null)
            {
                anchor = CalculateAnchor(bounds, view, anchorType);
                return true;
            }

            var point = element.Location as LocationPoint;
            if (point != null)
            {
                anchor = point.Point;
                return true;
            }

            var locationCurve = element.Location as LocationCurve;
            if (locationCurve == null || locationCurve.Curve == null)
                return false;

            var start = locationCurve.Curve.GetEndPoint(0);
            var end = locationCurve.Curve.GetEndPoint(1);
            switch (anchorType)
            {
                case TagAnchorPoint.TopLeft:
                case TagAnchorPoint.LeftCenter:
                case TagAnchorPoint.BottomLeft:
                    anchor = start;
                    break;
                case TagAnchorPoint.TopRight:
                case TagAnchorPoint.RightCenter:
                case TagAnchorPoint.BottomRight:
                    anchor = end;
                    break;
                default:
                    anchor = (start + end) * 0.5;
                    break;
            }
            return true;
        }

        public static XYZ GetDirectionVector(View view, TagPlacementDirection direction)
        {
            if (view == null)
                return XYZ.BasisX;

            switch (direction)
            {
                case TagPlacementDirection.Left:
                    return view.RightDirection.Negate();
                case TagPlacementDirection.Up:
                    return view.UpDirection;
                case TagPlacementDirection.Down:
                    return view.UpDirection.Negate();
                default:
                    return view.RightDirection;
            }
        }

        public static XYZ ResolveOffsetDirection(
            XYZ baseDirection,
            View view,
            Element element,
            double baseAngle,
            bool detectElementRotation,
            bool directionTypeOverride)
        {
            if (baseDirection == null || view == null)
                return baseDirection;

            double elementAngle = 0.0;
            var hasElementAngle = detectElementRotation &&
                                  TryGetElementRotationAngle(element, view, out elementAngle);
            var angle = directionTypeOverride
                ? (hasElementAngle ? elementAngle : 0.0)
                : baseAngle + (hasElementAngle ? elementAngle : 0.0);

            return RotateInView(baseDirection, view, angle);
        }

        public static double ResolveTagRotation(
            View view,
            Element element,
            XYZ offsetDirection,
            double baseAngle,
            bool detectElementRotation,
            bool directionTypeOverride)
        {
            if (directionTypeOverride)
            {
                double ignored;
                if (detectElementRotation &&
                    TryGetElementRotationAngle(element, view, out ignored) &&
                    TryGetSignedAngleInView(view, offsetDirection, out var directionAngle))
                    return directionAngle;

                return 0.0;
            }

            double elementAngle;
            return baseAngle +
                   (detectElementRotation &&
                    TryGetElementRotationAngle(element, view, out elementAngle)
                       ? elementAngle
                       : 0.0);
        }

        public static double GetSafeMinimumOffsetFeet(
            Element element,
            View view,
            double minimumOffsetMillimeters)
        {
            var elementRadius = 0.5;
            var bounds = element == null ? null : element.get_BoundingBox(view);
            if (bounds != null)
                elementRadius = (bounds.Max - bounds.Min).GetLength() / 2.0;

            return Math.Max(elementRadius + 0.5, MillimetersToFeet(minimumOffsetMillimeters));
        }

        public static double MillimetersToFeet(double millimeters)
        {
            return millimeters / 304.8;
        }

        private static XYZ CalculateAnchor(
            BoundingBoxXYZ bounds,
            View view,
            TagAnchorPoint anchorType)
        {
            var min = bounds.Min;
            var max = bounds.Max;
            var right = view.RightDirection;
            var up = view.UpDirection;
            if (right == null || up == null ||
                right.GetLength() < 1e-9 || up.GetLength() < 1e-9)
                return (min + max) * 0.5;

            var corners = new[]
            {
                min,
                max,
                new XYZ(min.X, min.Y, max.Z),
                new XYZ(min.X, max.Y, min.Z),
                new XYZ(max.X, min.Y, min.Z),
                new XYZ(min.X, max.Y, max.Z),
                new XYZ(max.X, min.Y, max.Z),
                new XYZ(max.X, max.Y, min.Z)
            };

            var minX = double.MaxValue;
            var maxX = double.MinValue;
            var minY = double.MaxValue;
            var maxY = double.MinValue;
            foreach (var corner in corners)
            {
                var x = corner.DotProduct(right);
                var y = corner.DotProduct(up);
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }

            var xValue = (minX + maxX) * 0.5;
            var yValue = (minY + maxY) * 0.5;
            switch (anchorType)
            {
                case TagAnchorPoint.TopLeft:
                    xValue = minX;
                    yValue = maxY;
                    break;
                case TagAnchorPoint.TopCenter:
                    yValue = maxY;
                    break;
                case TagAnchorPoint.TopRight:
                    xValue = maxX;
                    yValue = maxY;
                    break;
                case TagAnchorPoint.LeftCenter:
                    xValue = minX;
                    break;
                case TagAnchorPoint.RightCenter:
                    xValue = maxX;
                    break;
                case TagAnchorPoint.BottomLeft:
                    xValue = minX;
                    yValue = minY;
                    break;
                case TagAnchorPoint.BottomCenter:
                    yValue = minY;
                    break;
                case TagAnchorPoint.BottomRight:
                    xValue = maxX;
                    yValue = minY;
                    break;
            }

            var center = (min + max) * 0.5;
            var centerX = (minX + maxX) * 0.5;
            var centerY = (minY + maxY) * 0.5;
            return center + right.Multiply(xValue - centerX) + up.Multiply(yValue - centerY);
        }

        private static XYZ RotateInView(XYZ vector, View view, double angle)
        {
            if (Math.Abs(angle) < 1e-9)
                return vector;

            var axis = view.ViewDirection;
            if (axis == null || axis.GetLength() < 1e-9)
                axis = XYZ.BasisZ;

            return Transform.CreateRotationAtPoint(axis.Normalize(), angle, XYZ.Zero)
                .OfVector(vector);
        }

        private static bool TryGetElementRotationAngle(
            Element element,
            View view,
            out double angle)
        {
            angle = 0.0;
            if (element == null || view == null)
                return false;

            XYZ direction = null;
            var family = element as FamilyInstance;
            if (family != null)
            {
                direction = family.HandOrientation;
                if (direction == null || direction.GetLength() < 1e-6)
                    direction = family.FacingOrientation;
            }

            var curve = element.Location as LocationCurve;
            if (direction == null && curve != null && curve.Curve != null)
                direction = curve.Curve.GetEndPoint(1) - curve.Curve.GetEndPoint(0);

            var point = element.Location as LocationPoint;
            if (direction == null && point != null)
            {
                var axis = view.ViewDirection;
                if (axis != null && axis.GetLength() > 1e-9)
                    direction = Transform.CreateRotationAtPoint(
                            axis.Normalize(),
                            point.Rotation,
                            XYZ.Zero)
                        .OfVector(view.RightDirection);
            }

            return direction != null &&
                   direction.GetLength() >= 1e-6 &&
                   TryGetSignedAngleInView(view, direction, out angle);
        }

        private static bool TryGetSignedAngleInView(
            View view,
            XYZ direction,
            out double angle)
        {
            angle = 0.0;
            if (view == null || direction == null)
                return false;

            var normal = view.ViewDirection;
            var right = view.RightDirection;
            if (normal == null || right == null ||
                normal.GetLength() < 1e-9 || right.GetLength() < 1e-9)
                return false;

            var projected = direction - normal.Multiply(direction.DotProduct(normal));
            var baseProjected = right - normal.Multiply(right.DotProduct(normal));
            if (projected.GetLength() < 1e-6 || baseProjected.GetLength() < 1e-6)
                return false;

            projected = projected.Normalize();
            baseProjected = baseProjected.Normalize();
            var sign = baseProjected.CrossProduct(projected).DotProduct(normal) < 0
                ? -1.0
                : 1.0;
            angle = baseProjected.AngleTo(projected) * sign;
            return true;
        }
    }
}
