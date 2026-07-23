#nullable disable

using System;

namespace RevitMCP.Addin.Tagging
{
    public enum HostAnchorMode
    {
        SmartTagCenter,
        LocationPoint,
        ViewBoundingBoxCenter
    }

    public enum PlacementSide
    {
        Front,
        Back,
        Left,
        Right,
        FrontRight,
        FrontLeft,
        BackRight,
        BackLeft,
        Center,
        Custom
    }

    public enum TagRotationMode
    {
        KeepViewAligned,
        FollowHost,
        RelativeToHost
    }

    public enum TagTemplateScopeMode
    {
        SameFamily,
        SameFamilyAndType,
        SameCategory,
        Selection,
        ExplicitElementIds
    }

    public readonly struct TagTemplateVector2
    {
        public TagTemplateVector2(double right, double front)
        {
            Right = right;
            Front = front;
        }

        public double Right { get; }
        public double Front { get; }
        public double Length => Math.Sqrt(Right * Right + Front * Front);
    }

    /// <summary>
    /// Revit-independent calculations used by the selected-tag template workflow.
    /// Keeping these calculations separate makes rotation and mirrored-axis behavior
    /// testable without starting Revit.
    /// </summary>
    public static class TagTemplateMath
    {
        public static TagTemplateVector2 Project(
            double vectorX,
            double vectorY,
            double rightX,
            double rightY,
            double frontX,
            double frontY)
        {
            return new TagTemplateVector2(
                vectorX * rightX + vectorY * rightY,
                vectorX * frontX + vectorY * frontY);
        }

        public static TagTemplateVector2 Reconstruct(
            double rightOffset,
            double frontOffset,
            double rightX,
            double rightY,
            double frontX,
            double frontY)
        {
            return new TagTemplateVector2(
                rightX * rightOffset + frontX * frontOffset,
                rightY * rightOffset + frontY * frontOffset);
        }

        public static PlacementSide ClassifyPlacement(
            double rightOffset,
            double frontOffset,
            double centerTolerance,
            double angularToleranceDegrees)
        {
            var distance = Math.Sqrt(
                rightOffset * rightOffset +
                frontOffset * frontOffset);
            if (distance <= Math.Max(0.0, centerTolerance))
                return PlacementSide.Center;

            var angle = NormalizeDegrees(
                Math.Atan2(frontOffset, rightOffset) * 180.0 / Math.PI);
            if (IsAngleNear(angle, 0.0, angularToleranceDegrees))
                return PlacementSide.Right;
            if (IsAngleNear(angle, 45.0, angularToleranceDegrees))
                return PlacementSide.FrontRight;
            if (IsAngleNear(angle, 90.0, angularToleranceDegrees))
                return PlacementSide.Front;
            if (IsAngleNear(angle, 135.0, angularToleranceDegrees))
                return PlacementSide.FrontLeft;
            if (IsAngleNear(angle, 180.0, angularToleranceDegrees))
                return PlacementSide.Left;
            if (IsAngleNear(angle, -135.0, angularToleranceDegrees))
                return PlacementSide.BackLeft;
            if (IsAngleNear(angle, -90.0, angularToleranceDegrees))
                return PlacementSide.Back;
            if (IsAngleNear(angle, -45.0, angularToleranceDegrees))
                return PlacementSide.BackRight;
            return PlacementSide.Custom;
        }

        public static TagRotationMode InferRotationMode(
            double tagRotationRadians,
            double hostRotationRadians,
            double toleranceRadians)
        {
            var viewError = Math.Abs(NormalizeRadians(tagRotationRadians));
            var hostError = Math.Abs(
                NormalizeRadians(tagRotationRadians - hostRotationRadians));
            var tolerance = Math.Max(0.0, toleranceRadians);

            // With a zero-degree source host, view-aligned and host-following are
            // indistinguishable. Prefer the safer view-aligned interpretation and
            // let callers use the explicit rotationMode override when needed.
            if (viewError <= tolerance)
                return TagRotationMode.KeepViewAligned;
            if (hostError <= tolerance)
                return TagRotationMode.FollowHost;
            return TagRotationMode.RelativeToHost;
        }

        public static double ResolveTargetRotation(
            TagRotationMode mode,
            double sourceTagRotationRadians,
            double targetHostRotationRadians,
            double relativeRotationRadians)
        {
            switch (mode)
            {
                case TagRotationMode.FollowHost:
                    return NormalizeRadians(targetHostRotationRadians);
                case TagRotationMode.RelativeToHost:
                    return NormalizeRadians(
                        targetHostRotationRadians + relativeRotationRadians);
                default:
                    return NormalizeRadians(sourceTagRotationRadians);
            }
        }

        public static double NormalizeRadians(double radians)
        {
            var twoPi = Math.PI * 2.0;
            while (radians > Math.PI)
                radians -= twoPi;
            while (radians <= -Math.PI)
                radians += twoPi;
            return radians;
        }

        public static double NormalizeDegrees(double degrees)
        {
            while (degrees > 180.0)
                degrees -= 360.0;
            while (degrees <= -180.0)
                degrees += 360.0;
            return degrees;
        }

        private static bool IsAngleNear(
            double angle,
            double expected,
            double tolerance)
        {
            return Math.Abs(NormalizeDegrees(angle - expected)) <=
                   Math.Max(0.0, tolerance);
        }
    }
}
