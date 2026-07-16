using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools;

internal static class ViewCropRegionToolSupport
{
    internal static bool IsCroppableView(View? view)
    {
        if (view == null || view.IsTemplate)
            return false;

        switch (view.ViewType)
        {
            case ViewType.FloorPlan:
            case ViewType.CeilingPlan:
            case ViewType.EngineeringPlan:
            case ViewType.AreaPlan:
            case ViewType.Section:
            case ViewType.Elevation:
            case ViewType.Detail:
            case ViewType.ThreeD:
                return true;
            default:
                return false;
        }
    }

    internal static CropRegionSnapshot Capture(View referenceView)
    {
        if (!IsCroppableView(referenceView))
            throw new InvalidOperationException(
                "The reference view must be a non-template plan, section, elevation, detail, or 3D view.");

        var cropBox = referenceView.CropBox;
        if (cropBox == null)
            throw new InvalidOperationException("The reference view does not expose a crop box.");

        CurveLoop? customShape = null;
        var isSplit = false;
        var manager = referenceView.GetCropRegionShapeManager();
        isSplit = manager.Split;
        if (manager.ShapeSet && !isSplit)
        {
            var loops = manager.GetCropShape();
            if (loops != null && loops.Count == 1)
                customShape = loops[0];
        }

        return new CropRegionSnapshot(
            cropBox,
            customShape,
            referenceView.CropBoxActive,
            referenceView.CropBoxVisible,
            isSplit);
    }

    internal static void Apply(View targetView, CropRegionSnapshot snapshot)
    {
        if (!IsCroppableView(targetView))
            throw new InvalidOperationException(
                "The target view must be a non-template plan, section, elevation, detail, or 3D view.");

        targetView.CropBoxActive = true;
        targetView.CropBoxVisible = snapshot.CropBoxVisible;

        var manager = targetView.GetCropRegionShapeManager();
        if (manager.ShapeSet)
            manager.RemoveCropRegionShape();

        targetView.CropBox = snapshot.CropBox;

        if (snapshot.CustomShape != null && manager.CanHaveShape)
            manager.SetCropShape(snapshot.CustomShape);
    }

    internal sealed class CropRegionSnapshot
    {
        internal CropRegionSnapshot(
            BoundingBoxXYZ cropBox,
            CurveLoop? customShape,
            bool cropBoxActive,
            bool cropBoxVisible,
            bool isSplit)
        {
            CropBox = cropBox;
            CustomShape = customShape;
            CropBoxActive = cropBoxActive;
            CropBoxVisible = cropBoxVisible;
            IsSplit = isSplit;
        }

        internal BoundingBoxXYZ CropBox { get; }
        internal CurveLoop? CustomShape { get; }
        internal bool CropBoxActive { get; }
        internal bool CropBoxVisible { get; }
        internal bool IsSplit { get; }
        internal string ShapeMode => CustomShape != null ? "CustomSingleLoop" : "Rectangular";
    }
}
