#nullable disable

using System;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tagging
{
    /// <summary>
    /// What a tag is placed on: an element of the host document, or an element of a linked model seen through its
    /// link instance (#151). Geometry is always returned in host coordinates, so the placement pipeline does not
    /// need to know which one it has.
    /// </summary>
    public sealed class TagTarget
    {
        private TagTarget(Element element, RevitLinkInstance link)
        {
            Element = element;
            Link = link;
            Transform = link == null ? Transform.Identity : link.GetTotalTransform();
        }

        /// <summary>The element, in its own document (the linked document for a linked target).</summary>
        public Element Element { get; }

        /// <summary>The link instance in the host, or null for a host element.</summary>
        public RevitLinkInstance Link { get; }

        /// <summary>Linked document → host coordinates (identity for a host element).</summary>
        public Transform Transform { get; }

        public bool IsLinked => Link != null;

        public long ElementId => Element.Id.Value;

        public long? LinkInstanceId => Link?.Id.Value;

        /// <summary>Unique across host and links: "host:&lt;id&gt;" or "link:&lt;instance&gt;:&lt;id&gt;".</summary>
        public string Key => IsLinked ? $"link:{Link.Id.Value}:{Element.Id.Value}" : $"host:{Element.Id.Value}";

        public string LinkName
        {
            get
            {
                if (Link == null) return null;
                try { return Link.Name; }
                catch { return null; }
            }
        }

        public static TagTarget Host(Element element) => element == null ? null : new TagTarget(element, null);

        public static TagTarget Linked(RevitLinkInstance link, Element linkedElement) =>
            link == null || linkedElement == null ? null : new TagTarget(linkedElement, link);

        /// <summary>The reference IndependentTag.Create takes: the element, or the element through its link.</summary>
        public Reference CreateReference() =>
            IsLinked ? new Reference(Element).CreateLinkReference(Link) : new Reference(Element);

        /// <summary>
        /// The element's box in host coordinates. A host element uses its box in the view; a linked element has no box
        /// in a host view, so its model box is transformed into the host (an axis-aligned box around the turned corners).
        /// </summary>
        public BoundingBoxXYZ GetBounds(View view)
        {
            if (!IsLinked) return Element.get_BoundingBox(view);
            var box = Element.get_BoundingBox(null);
            if (box == null) return null;
            var min = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue);
            var max = new XYZ(double.MinValue, double.MinValue, double.MinValue);
            foreach (var corner in Corners(box))
            {
                var p = Transform.OfPoint(box.Transform.OfPoint(corner));
                min = new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                max = new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
            }
            return new BoundingBoxXYZ { Min = min, Max = max };
        }

        public bool TryGetLocationPoint(out XYZ point)
        {
            point = null;
            if (!(Element.Location is LocationPoint location) || location.Point == null) return false;
            point = Transform.OfPoint(location.Point);
            return true;
        }

        public bool TryGetLocationCurveEnds(out XYZ start, out XYZ end)
        {
            start = null;
            end = null;
            if (!(Element.Location is LocationCurve location) || location.Curve == null) return false;
            start = Transform.OfPoint(location.Curve.GetEndPoint(0));
            end = Transform.OfPoint(location.Curve.GetEndPoint(1));
            return true;
        }

        /// <summary>
        /// The element's own direction in host coordinates: a family's hand (or facing) orientation, a curve's
        /// direction, or a point element's rotation.
        /// </summary>
        public XYZ GetDirection(View view)
        {
            if (Element is FamilyInstance family)
            {
                var direction = family.HandOrientation;
                if (direction == null || direction.GetLength() < 1e-6)
                    direction = family.FacingOrientation;
                if (direction != null && direction.GetLength() >= 1e-6)
                    return Transform.OfVector(direction);
            }

            if (Element.Location is LocationCurve curve && curve.Curve != null)
                return Transform.OfVector(curve.Curve.GetEndPoint(1) - curve.Curve.GetEndPoint(0));

            if (Element.Location is LocationPoint point && view != null)
            {
                if (!IsLinked)
                {
                    // Host: the point's rotation about the view direction, from the view's right (unchanged behaviour).
                    var axis = view.ViewDirection;
                    if (axis != null && axis.GetLength() > 1e-9)
                        return Transform.CreateRotationAtPoint(axis.Normalize(), point.Rotation, XYZ.Zero).OfVector(view.RightDirection);
                    return null;
                }
                // Linked: the point's rotation is about the link's Z, from the link's X.
                var local = Transform.CreateRotationAtPoint(XYZ.BasisZ, point.Rotation, XYZ.Zero).OfVector(XYZ.BasisX);
                return Transform.OfVector(local);
            }
            return null;
        }

        private static XYZ[] Corners(BoundingBoxXYZ box)
        {
            var a = box.Min;
            var b = box.Max;
            return new[]
            {
                new XYZ(a.X, a.Y, a.Z), new XYZ(b.X, a.Y, a.Z), new XYZ(a.X, b.Y, a.Z), new XYZ(b.X, b.Y, a.Z),
                new XYZ(a.X, a.Y, b.Z), new XYZ(b.X, a.Y, b.Z), new XYZ(a.X, b.Y, b.Z), new XYZ(b.X, b.Y, b.Z)
            };
        }
    }
}
