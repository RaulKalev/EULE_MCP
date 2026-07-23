#nullable disable

using System;

namespace RevitMCP.Addin.Tagging
{
    /// <summary>Pure view-plane math shared by collision search and unit tests.</summary>
    public static class TagCollisionMath
    {
        public static bool RectanglesOverlap(
            double firstMinX,
            double firstMaxX,
            double firstMinY,
            double firstMaxY,
            double secondMinX,
            double secondMaxX,
            double secondMinY,
            double secondMaxY,
            double gap)
        {
            return !(secondMaxX + gap < firstMinX - gap ||
                     secondMinX - gap > firstMaxX + gap ||
                     secondMaxY + gap < firstMinY - gap ||
                     secondMinY - gap > firstMaxY + gap);
        }

        public static double OverlapArea(
            double firstMinX,
            double firstMaxX,
            double firstMinY,
            double firstMaxY,
            double secondMinX,
            double secondMaxX,
            double secondMinY,
            double secondMaxY)
        {
            var width = Math.Max(
                0.0,
                Math.Min(firstMaxX, secondMaxX) -
                Math.Max(firstMinX, secondMinX));
            var height = Math.Max(
                0.0,
                Math.Min(firstMaxY, secondMaxY) -
                Math.Max(firstMinY, secondMinY));
            return width * height;
        }

        public static TagOffset2D RadialOffset(
            double radius,
            int sampleIndex,
            int sampleCount)
        {
            if (sampleCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleCount));
            if (sampleIndex < 0 || sampleIndex >= sampleCount)
                throw new ArgumentOutOfRangeException(nameof(sampleIndex));

            var angle = 2.0 * Math.PI * sampleIndex / sampleCount;
            return new TagOffset2D(
                radius * Math.Cos(angle),
                radius * Math.Sin(angle));
        }
    }

    public struct TagOffset2D
    {
        public TagOffset2D(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }
}
