#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tagging
{
    /// <summary>
    /// SmartTags-compatible two-pass collision detector. All bounds are projected
    /// into the active view plane and indexed in a uniform grid.
    /// </summary>
    public sealed class TagCollisionDetector
    {
        private const double EstimatedTagWidthFeet = 2.0;
        private const double EstimatedTagHeightFeet = 0.66;

        private readonly View _view;
        private readonly double _gapFeet;
        private readonly SpatialIndex2D _obstacleIndex = new SpatialIndex2D(5.0);
        private readonly SpatialIndex2D _newTagIndex = new SpatialIndex2D(5.0);
        private int _collisionChecks;
        private int _nearbyCandidates;

        public TagCollisionDetector(View view, double gapMillimeters)
        {
            if (view == null)
                throw new ArgumentNullException(nameof(view));
            _view = view;
            _gapFeet = TagGeometryService.MillimetersToFeet(
                Math.Max(0.0, gapMillimeters));
        }

        public void CollectObstacles(
            Document doc,
            ISet<ElementId> excludedTagIds = null,
            ElementId excludedElementId = null)
        {
            _obstacleIndex.Clear();
            _newTagIndex.Clear();
            _collisionChecks = 0;
            _nearbyCandidates = 0;
            if (doc == null)
                return;

            var tags = new FilteredElementCollector(doc, _view.Id)
                .OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>()
                .Where(tag => excludedTagIds == null ||
                              !excludedTagIds.Contains(tag.Id))
                .Cast<Element>();

            var modelElements = new FilteredElementCollector(doc, _view.Id)
                .WhereElementIsNotElementType()
                .Where(element =>
                    !(element is IndependentTag) &&
                    IsObstacleCategory(element.Category));

            foreach (var element in tags.Concat(modelElements))
            {
                try
                {
                    if (excludedElementId != null &&
                        element.Id == excludedElementId)
                        continue;
                    if (element.IsHidden(_view))
                        continue;

                    var bounds = GetBounds(element);
                    if (bounds != null)
                        _obstacleIndex.Add(bounds);
                }
                catch
                {
                    // A malformed or non-graphical element must not abort a batch.
                }
            }
        }

        public XYZ FindValidPosition(
            XYZ anchor,
            XYZ intendedHead,
            out bool collisionFree)
        {
            return FindCandidate(
                anchor,
                intendedHead,
                CreateEstimatedBounds(intendedHead),
                0.0,
                out collisionFree);
        }

        public XYZ FindValidPositionWithActualSize(
            XYZ anchor,
            XYZ intendedHead,
            ObstacleBounds actualBounds,
            double minimumDistanceFromAnchor,
            out bool collisionFree)
        {
            return FindCandidate(
                anchor,
                intendedHead,
                actualBounds,
                minimumDistanceFromAnchor,
                out collisionFree);
        }

        public bool HasCollisionAtPosition(XYZ position)
        {
            return HasCollision(CreateEstimatedBounds(position), null);
        }

        public bool HasCollisionWithActualBounds(
            IndependentTag tag,
            out ObstacleBounds bounds)
        {
            bounds = GetBounds(tag);
            return bounds != null && HasCollision(bounds, null);
        }

        public void AddNewTag(IndependentTag tag)
        {
            var bounds = GetBounds(tag);
            if (bounds != null)
                _newTagIndex.Add(bounds);
        }

        public void AddEstimatedTag(XYZ head)
        {
            var bounds = CreateEstimatedBounds(head);
            if (bounds != null)
                _newTagIndex.Add(bounds);
        }

        public string GetPerformanceDiagnostics()
        {
            return string.Format(
                "collisionChecks={0}, nearbyCandidates={1}, obstacles={2}, newTags={3}",
                _collisionChecks,
                _nearbyCandidates,
                _obstacleIndex.Count,
                _newTagIndex.Count);
        }

        private XYZ FindCandidate(
            XYZ anchor,
            XYZ intendedHead,
            ObstacleBounds sourceBounds,
            double minimumDistance,
            out bool collisionFree)
        {
            collisionFree = false;
            if (anchor == null || intendedHead == null || sourceBounds == null)
                return intendedHead;

            var right = _view.RightDirection;
            var up = _view.UpDirection;
            if (right == null || up == null ||
                right.GetLength() < 1e-9 || up.GetLength() < 1e-9)
                return intendedHead;

            var width = sourceBounds.MaxX - sourceBounds.MinX;
            var height = sourceBounds.MaxY - sourceBounds.MinY;
            var intendedBounds = BoundsAt(intendedHead, width, height);
            var intendedDistance = (intendedHead - anchor).GetLength();
            if (intendedDistance >= minimumDistance &&
                !HasCollision(intendedBounds, null))
            {
                collisionFree = true;
                return intendedHead;
            }

            var initialRadius = Math.Max(
                Math.Max(intendedDistance, minimumDistance),
                0.5);
            var maxRadius = Math.Max(5.0, initialRadius * 3.0);
            var step = Math.Max(0.1, _view.Scale / 120.0);
            const int angularSamples = 16;

            var best = intendedHead;
            var bestDistance = double.MaxValue;
            for (var radius = initialRadius; radius <= maxRadius; radius += step)
            {
                for (var index = 0; index < angularSamples; index++)
                {
                    var offset = TagCollisionMath.RadialOffset(
                        radius,
                        index,
                        angularSamples);
                    var candidate = anchor +
                                    right.Multiply(offset.X) +
                                    up.Multiply(offset.Y);
                    if (HasCollision(BoundsAt(candidate, width, height), null))
                        continue;

                    var distance = (candidate - intendedHead).GetLength();
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                        collisionFree = true;
                    }
                }

                if (collisionFree && bestDistance < step * 2.0)
                    break;
            }

            if (!collisionFree)
                best = SelectLeastOverlap(
                    anchor,
                    width,
                    height,
                    initialRadius,
                    maxRadius,
                    step,
                    angularSamples);

            return best;
        }

        private XYZ SelectLeastOverlap(
            XYZ anchor,
            double width,
            double height,
            double minimumRadius,
            double maximumRadius,
            double step,
            int angularSamples)
        {
            var right = _view.RightDirection;
            var up = _view.UpDirection;
            var best = anchor + right.Multiply(minimumRadius);
            var leastOverlap = double.MaxValue;

            for (var radius = minimumRadius; radius <= maximumRadius; radius += step)
            {
                for (var index = 0; index < angularSamples; index++)
                {
                    var offset = TagCollisionMath.RadialOffset(
                        radius,
                        index,
                        angularSamples);
                    var candidate = anchor +
                                    right.Multiply(offset.X) +
                                    up.Multiply(offset.Y);
                    var bounds = BoundsAt(candidate, width, height);
                    var overlap = TotalOverlap(bounds);
                    if (overlap < leastOverlap)
                    {
                        leastOverlap = overlap;
                        best = candidate;
                        if (overlap < 1e-9)
                            return best;
                    }
                }
            }

            Debug.WriteLine(
                "[RevitMCP.Tagging] No collision-free position found; " +
                "using deterministic least-overlap fallback.");
            return best;
        }

        private double TotalOverlap(ObstacleBounds bounds)
        {
            var result = 0.0;
            foreach (var obstacle in _obstacleIndex.GetNearby(bounds))
                result += bounds.OverlapArea(obstacle);
            foreach (var obstacle in _newTagIndex.GetNearby(bounds))
                result += bounds.OverlapArea(obstacle);
            return result;
        }

        private bool HasCollision(
            ObstacleBounds bounds,
            ObstacleBounds ignored)
        {
            if (bounds == null)
                return false;

            var nearby = _obstacleIndex.GetNearby(bounds);
            _nearbyCandidates += nearby.Count;
            foreach (var obstacle in nearby)
            {
                if (ReferenceEquals(obstacle, ignored))
                    continue;
                _collisionChecks++;
                if (bounds.Overlaps(obstacle, _gapFeet))
                    return true;
            }

            nearby = _newTagIndex.GetNearby(bounds);
            _nearbyCandidates += nearby.Count;
            foreach (var obstacle in nearby)
            {
                if (ReferenceEquals(obstacle, ignored))
                    continue;
                _collisionChecks++;
                if (bounds.Overlaps(obstacle, _gapFeet))
                    return true;
            }
            return false;
        }

        private ObstacleBounds CreateEstimatedBounds(XYZ point)
        {
            return point == null
                ? null
                : BoundsAt(point, EstimatedTagWidthFeet, EstimatedTagHeightFeet);
        }

        private ObstacleBounds BoundsAt(XYZ point, double width, double height)
        {
            var x = point.DotProduct(_view.RightDirection);
            var y = point.DotProduct(_view.UpDirection);
            return new ObstacleBounds(
                x - width / 2.0,
                x + width / 2.0,
                y - height / 2.0,
                y + height / 2.0);
        }

        private ObstacleBounds GetBounds(Element element)
        {
            if (element == null)
                return null;

            var bounds = element.get_BoundingBox(_view);
            if (bounds == null)
            {
                var tag = element as IndependentTag;
                if (tag == null || tag.TagHeadPosition == null)
                    return null;
                return BoundsAt(tag.TagHeadPosition, 0.2, 0.2);
            }

            var minX = double.MaxValue;
            var maxX = double.MinValue;
            var minY = double.MaxValue;
            var maxY = double.MinValue;
            var min = bounds.Min;
            var max = bounds.Max;
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
            foreach (var corner in corners)
            {
                var x = corner.DotProduct(_view.RightDirection);
                var y = corner.DotProduct(_view.UpDirection);
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
            return new ObstacleBounds(minX, maxX, minY, maxY);
        }

        private static bool IsObstacleCategory(Category category)
        {
            if (category == null)
                return false;

            switch (category.BuiltInCategory)
            {
                case BuiltInCategory.OST_Walls:
                case BuiltInCategory.OST_Doors:
                case BuiltInCategory.OST_Windows:
                case BuiltInCategory.OST_Furniture:
                case BuiltInCategory.OST_DuctCurves:
                case BuiltInCategory.OST_PipeCurves:
                case BuiltInCategory.OST_Conduit:
                case BuiltInCategory.OST_CableTray:
                case BuiltInCategory.OST_StructuralFraming:
                case BuiltInCategory.OST_StructuralColumns:
                case BuiltInCategory.OST_Columns:
                case BuiltInCategory.OST_Floors:
                case BuiltInCategory.OST_Roofs:
                case BuiltInCategory.OST_Ceilings:
                case BuiltInCategory.OST_DetailComponents:
                case BuiltInCategory.OST_GenericModel:
                case BuiltInCategory.OST_Casework:
                case BuiltInCategory.OST_PlumbingFixtures:
                case BuiltInCategory.OST_LightingFixtures:
                case BuiltInCategory.OST_ElectricalEquipment:
                case BuiltInCategory.OST_MechanicalEquipment:
                case BuiltInCategory.OST_SpecialityEquipment:
                    return true;
                default:
                    return false;
            }
        }

        public sealed class ObstacleBounds
        {
            public ObstacleBounds(double minX, double maxX, double minY, double maxY)
            {
                MinX = minX;
                MaxX = maxX;
                MinY = minY;
                MaxY = maxY;
            }

            public double MinX { get; }
            public double MaxX { get; }
            public double MinY { get; }
            public double MaxY { get; }

            public bool Overlaps(ObstacleBounds other, double gap)
            {
                return other != null &&
                       TagCollisionMath.RectanglesOverlap(
                           MinX,
                           MaxX,
                           MinY,
                           MaxY,
                           other.MinX,
                           other.MaxX,
                           other.MinY,
                           other.MaxY,
                           gap);
            }

            public double OverlapArea(ObstacleBounds other)
            {
                if (other == null)
                    return 0.0;
                return TagCollisionMath.OverlapArea(
                    MinX,
                    MaxX,
                    MinY,
                    MaxY,
                    other.MinX,
                    other.MaxX,
                    other.MinY,
                    other.MaxY);
            }
        }

        private sealed class SpatialIndex2D
        {
            private readonly double _cellSize;
            private readonly Dictionary<Tuple<int, int>, List<ObstacleBounds>> _grid =
                new Dictionary<Tuple<int, int>, List<ObstacleBounds>>();

            public SpatialIndex2D(double cellSize)
            {
                _cellSize = cellSize > 0.0 ? cellSize : 5.0;
            }

            public int Count
            {
                get
                {
                    return _grid.Values
                        .SelectMany(value => value)
                        .Distinct()
                        .Count();
                }
            }

            public void Add(ObstacleBounds bounds)
            {
                if (bounds == null)
                    return;
                for (var x = Cell(bounds.MinX); x <= Cell(bounds.MaxX); x++)
                for (var y = Cell(bounds.MinY); y <= Cell(bounds.MaxY); y++)
                {
                    var key = Tuple.Create(x, y);
                    List<ObstacleBounds> values;
                    if (!_grid.TryGetValue(key, out values))
                    {
                        values = new List<ObstacleBounds>();
                        _grid[key] = values;
                    }
                    values.Add(bounds);
                }
            }

            public List<ObstacleBounds> GetNearby(ObstacleBounds bounds)
            {
                var result = new HashSet<ObstacleBounds>();
                if (bounds == null)
                    return result.ToList();
                for (var x = Cell(bounds.MinX); x <= Cell(bounds.MaxX); x++)
                for (var y = Cell(bounds.MinY); y <= Cell(bounds.MaxY); y++)
                {
                    List<ObstacleBounds> values;
                    if (!_grid.TryGetValue(Tuple.Create(x, y), out values))
                        continue;
                    foreach (var value in values)
                        result.Add(value);
                }
                return result.ToList();
            }

            public void Clear()
            {
                _grid.Clear();
            }

            private int Cell(double coordinate)
            {
                return (int)Math.Floor(coordinate / _cellSize);
            }
        }
    }
}
