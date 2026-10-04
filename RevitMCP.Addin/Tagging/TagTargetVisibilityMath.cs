#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitMCP.Addin.Tagging
{
    /// <summary>A view range plane resolved to an absolute elevation (millimetres).</summary>
    public readonly struct ViewRangePlaneElevation
    {
        public ViewRangePlaneElevation(string name, double elevationMillimeters)
        {
            Name = name;
            ElevationMillimeters = elevationMillimeters;
        }

        public string Name { get; }
        public double ElevationMillimeters { get; }
    }

    /// <summary>
    /// Revit-independent checks and wording for the selected-tag template workflow:
    /// which views can host the generated tags, and why a target element is not
    /// visible/taggable in the view (view range, crop region, ...).
    /// </summary>
    public static class TagTargetVisibilityMath
    {
        /// <summary>Revit <c>ViewType</c> names whose views can host tags of model elements.</summary>
        private static readonly HashSet<string> TaggableViewTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "FloorPlan",
                "CeilingPlan",
                "EngineeringPlan",
                "AreaPlan",
                "Section",
                "Elevation",
                "Detail",
                "ThreeD"
            };

        public static bool IsTaggableViewType(string viewType) =>
            !string.IsNullOrWhiteSpace(viewType) &&
            TaggableViewTypes.Contains(viewType.Trim());

        /// <summary>
        /// Validates a requested target view. Returns null when tags can be placed in it,
        /// otherwise an error message for the caller.
        /// </summary>
        public static string ValidateTargetView(
            long viewId,
            bool exists,
            string viewName,
            string viewType,
            bool isTemplate,
            bool isUnlocked3D)
        {
            if (!exists)
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "targetViewId {0} does not identify a view in the active document.",
                    viewId);

            var label = DescribeView(viewId, viewName);
            if (isTemplate)
                return "Target view " + label +
                       " is a view template; tags can only be placed in a real view.";
            if (!IsTaggableViewType(viewType))
                return "Target view " + label + " is a " +
                       (string.IsNullOrWhiteSpace(viewType) ? "non-graphical" : viewType) +
                       " view, which cannot host tags of model elements. Use a plan, ceiling plan, section, elevation, detail or locked 3D view.";
            if (isUnlocked3D)
                return "Target view " + label +
                       " is an unlocked 3D view. Lock the view before tagging.";
            return null;
        }

        /// <summary>
        /// Compares an element's vertical extent against a view's vertical range.
        /// <paramref name="upper"/> / <paramref name="lower"/> are null when that side of the
        /// range is unlimited or could not be resolved. Returns null when the element overlaps
        /// the range, otherwise e.g. "outside view range (Z=7900 mm > Top 7500 mm)".
        /// </summary>
        public static string DescribeOutsideVerticalRange(
            double elementMinZMillimeters,
            double elementMaxZMillimeters,
            ViewRangePlaneElevation? upper,
            ViewRangePlaneElevation? lower,
            double toleranceMillimeters = 1.0)
        {
            var min = Math.Min(elementMinZMillimeters, elementMaxZMillimeters);
            var max = Math.Max(elementMinZMillimeters, elementMaxZMillimeters);
            var tolerance = Math.Max(0.0, toleranceMillimeters);

            if (upper.HasValue && min > upper.Value.ElevationMillimeters + tolerance)
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "outside view range (Z={0} mm > {1} {2} mm)",
                    FormatMillimeters(min),
                    upper.Value.Name,
                    FormatMillimeters(upper.Value.ElevationMillimeters));

            if (lower.HasValue && max < lower.Value.ElevationMillimeters - tolerance)
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "outside view range (Z={0} mm < {1} {2} mm)",
                    FormatMillimeters(max),
                    lower.Value.Name,
                    FormatMillimeters(lower.Value.ElevationMillimeters));

            return null;
        }

        /// <summary>
        /// For views whose range planes are not a plain top/bottom pair (e.g. ceiling plans),
        /// uses the highest and lowest resolved plane as the vertical bounds.
        /// </summary>
        public static string DescribeOutsideVerticalRange(
            double elementMinZMillimeters,
            double elementMaxZMillimeters,
            IReadOnlyList<ViewRangePlaneElevation> boundedPlanes,
            double toleranceMillimeters = 1.0)
        {
            if (boundedPlanes == null || boundedPlanes.Count < 2)
                return null;
            var upper = boundedPlanes.OrderByDescending(p => p.ElevationMillimeters).First();
            var lower = boundedPlanes.OrderBy(p => p.ElevationMillimeters).First();
            return DescribeOutsideVerticalRange(
                elementMinZMillimeters,
                elementMaxZMillimeters,
                upper,
                lower,
                toleranceMillimeters);
        }

        /// <summary>True when two closed intervals overlap (within tolerance).</summary>
        public static bool IntervalsOverlap(
            double aMin,
            double aMax,
            double bMin,
            double bMax,
            double tolerance = 0.0)
        {
            var a0 = Math.Min(aMin, aMax);
            var a1 = Math.Max(aMin, aMax);
            var b0 = Math.Min(bMin, bMax);
            var b1 = Math.Max(bMin, bMax);
            return a0 <= b1 + tolerance && b0 <= a1 + tolerance;
        }

        /// <summary>
        /// True when an element's footprint, expressed in the crop box's own X/Y frame,
        /// lies entirely outside the crop rectangle.
        /// </summary>
        public static bool IsOutsideCrop(
            double elementMinX,
            double elementMinY,
            double elementMaxX,
            double elementMaxY,
            double cropMinX,
            double cropMinY,
            double cropMaxX,
            double cropMaxY,
            double tolerance = 0.0)
        {
            return !IntervalsOverlap(elementMinX, elementMaxX, cropMinX, cropMaxX, tolerance) ||
                   !IntervalsOverlap(elementMinY, elementMaxY, cropMinY, cropMaxY, tolerance);
        }

        /// <summary>
        /// Builds the per-element skip reason. Specific causes are listed when known; the
        /// generic message is kept as the fallback.
        /// </summary>
        public static string ComposeNotVisibleReason(
            IEnumerable<string> specificReasons,
            bool isSeparateTargetView,
            long viewId,
            string viewName)
        {
            var reasons = (specificReasons ?? Enumerable.Empty<string>())
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var viewText = isSeparateTargetView
                ? "the target view " + DescribeView(viewId, viewName)
                : "the source view";
            var generic = "Element is not visible or taggable in " + viewText;
            return reasons.Count == 0
                ? generic + "."
                : generic + ": " + string.Join("; ", reasons) + ".";
        }

        public static string DescribeView(long viewId, string viewName)
        {
            return string.IsNullOrWhiteSpace(viewName)
                ? string.Format(CultureInfo.InvariantCulture, "ID:{0}", viewId)
                : string.Format(CultureInfo.InvariantCulture, "'{0}' (ID:{1})", viewName, viewId);
        }

        public static string FormatMillimeters(double millimeters) =>
            Math.Round(millimeters).ToString("0", CultureInfo.InvariantCulture);
    }
}
