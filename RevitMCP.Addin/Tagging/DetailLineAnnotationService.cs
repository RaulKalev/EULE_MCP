#nullable disable

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tagging
{
    public static class DetailLineAnnotationService
    {
        public static List<ElementId> Place(
            Document doc,
            View view,
            IEnumerable<DetailCurve> curves,
            FamilySymbol symbol,
            double offsetMillimeters,
            TagPlacementDirection direction,
            bool alignToLine)
        {
            var created = new List<ElementId>();
            if (doc == null || view == null || curves == null || symbol == null)
                return created;

            if (!symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }

            var offset = TagGeometryService.MillimetersToFeet(offsetMillimeters) *
                         Math.Max(1, view.Scale);
            var offsetDirection = TagGeometryService.GetDirectionVector(
                view,
                direction);

            foreach (var curve in curves)
            {
                var midpoint = GetMidpoint(curve);
                if (midpoint == null)
                    continue;

                var point = midpoint + offsetDirection.Multiply(offset);
                FamilyInstance instance;
                try { instance = doc.Create.NewFamilyInstance(point, symbol, view); }
                catch { continue; }
                if (instance == null)
                    continue;

                if (alignToLine)
                {
                    try
                    {
                        var angle = GetRotationAngle(curve, view);
                        if (Math.Abs(angle) > 1e-9)
                        {
                            var axis = Line.CreateBound(
                                point,
                                point + view.ViewDirection);
                            ElementTransformUtils.RotateElement(
                                doc,
                                instance.Id,
                                axis,
                                angle);
                        }
                    }
                    catch
                    {
                        // Placement remains useful when a family cannot rotate.
                    }
                }
                created.Add(instance.Id);
            }
            return created;
        }

        private static XYZ GetMidpoint(DetailCurve detailCurve)
        {
            if (detailCurve == null || detailCurve.GeometryCurve == null)
                return null;
            try { return detailCurve.GeometryCurve.Evaluate(0.5, true); }
            catch
            {
                var start = detailCurve.GeometryCurve.GetEndPoint(0);
                var end = detailCurve.GeometryCurve.GetEndPoint(1);
                return (start + end) * 0.5;
            }
        }

        private static double GetRotationAngle(DetailCurve detailCurve, View view)
        {
            XYZ tangent;
            try
            {
                tangent = detailCurve.GeometryCurve
                    .ComputeDerivatives(0.5, true)
                    .BasisX;
            }
            catch
            {
                tangent = detailCurve.GeometryCurve.GetEndPoint(1) -
                          detailCurve.GeometryCurve.GetEndPoint(0);
            }

            var normal = view.ViewDirection;
            tangent -= normal.Multiply(tangent.DotProduct(normal));
            if (tangent.GetLength() < 1e-9)
                return 0.0;
            tangent = tangent.Normalize();
            var right = view.RightDirection;
            var sign = right.CrossProduct(tangent).DotProduct(normal) < 0
                ? -1.0
                : 1.0;
            return right.AngleTo(tangent) * sign;
        }
    }
}
