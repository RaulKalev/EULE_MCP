using Autodesk.Revit.DB;
using RevitMCP.Addin.Documentation.Placement;

namespace RevitMCP.Addin.Schedules;

/// <summary>
/// Puts a schedule on a sheet with ScheduleSheetInstance.Create (Viewport.Create does not take schedules) and moves it
/// so its table's top-left corner lands where <see cref="SchedulePlacement"/> planned (#103). Inside the caller's
/// transaction. Used by revit_place_schedule_on_sheet and by revit_place_views_on_sheets for schedule ids.
/// </summary>
internal static class ScheduleSheetPlacer
{
    public static double MmToFt(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    public static double FtToMm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);

    /// <summary>The schedule's instances already on the sheet.</summary>
    public static List<ScheduleSheetInstance> InstancesOn(Document doc, ViewSheet sheet, ElementId scheduleId) =>
        new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
            .Where(i => i.ScheduleId == scheduleId && !i.IsTitleblockRevisionSchedule)
            .ToList();

    public static SheetRect? BoxOf(Element element, View sheet)
    {
        var box = element.get_BoundingBox(sheet);
        return box == null ? null : new SheetRect(FtToMm(box.Min.X), FtToMm(box.Min.Y), FtToMm(box.Max.X), FtToMm(box.Max.Y));
    }

    public static SheetRect ViewportBox(Viewport viewport)
    {
        var outline = viewport.GetBoxOutline();
        return new SheetRect(FtToMm(outline.MinimumPoint.X), FtToMm(outline.MinimumPoint.Y), FtToMm(outline.MaximumPoint.X), FtToMm(outline.MaximumPoint.Y));
    }

    /// <summary>The title block's box on the sheet, or null.</summary>
    public static SheetRect? TitleblockBox(Document doc, ViewSheet sheet)
    {
        var titleblock = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstOrDefault();
        return titleblock == null ? null : BoxOf(titleblock, sheet);
    }

    /// <summary>Viewports and schedule instances on the sheet with their boxes, except <paramref name="except"/>.</summary>
    public static List<(string Label, SheetRect Box)> Occupied(Document doc, ViewSheet sheet, ElementId? except)
    {
        var result = new List<(string, SheetRect)>();
        foreach (var id in sheet.GetAllViewports())
        {
            if (except != null && id == except) continue;
            if (doc.GetElement(id) is not Viewport viewport) continue;
            var view = doc.GetElement(viewport.ViewId) as View;
            try { result.Add(($"viewport {id.Value} ('{view?.Name}')", ViewportBox(viewport))); }
            catch { /* a viewport without a box */ }
        }
        foreach (var instance in new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
        {
            if (instance.IsTitleblockRevisionSchedule || (except != null && instance.Id == except)) continue;
            if (BoxOf(instance, sheet) is { } box) result.Add(($"schedule {instance.Id.Value} ('{instance.Name}')", box));
        }
        return result;
    }

    /// <summary>
    /// Creates the instance and moves it so its top-left corner is at <paramref name="topLeftFor"/>(width, height)
    /// (paper mm). Warns when it overlaps another viewport or schedule or leaves the title block.
    /// </summary>
    public static ScheduleSheetInstance Place(
        Document doc, ViewSheet sheet, ViewSchedule schedule, Func<double, double, (double X, double Y)> topLeftFor,
        List<string> warnings, out SheetRect? placedBox)
    {
        var first = topLeftFor(0, 0);
        var instance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, new XYZ(MmToFt(first.X), MmToFt(first.Y), 0));
        doc.Regenerate();

        placedBox = BoxOf(instance, sheet);
        if (placedBox is { } box)
        {
            var desired = topLeftFor(box.Width, box.Height);
            var (dx, dy) = SchedulePlacement.Correction(desired, box);
            if (Math.Abs(dx) > 0.05 || Math.Abs(dy) > 0.05)
            {
                instance.Point = instance.Point + new XYZ(MmToFt(dx), MmToFt(dy), 0);
                doc.Regenerate();
                placedBox = BoxOf(instance, sheet) ?? box.Offset(dx, dy);
            }
        }
        else
        {
            warnings.Add("Revit gave no box for the placed schedule, so its position was not checked.");
        }

        if (placedBox is { } final)
        {
            foreach (var (label, other) in Occupied(doc, sheet, instance.Id))
                if (final.Overlaps(other))
                    warnings.Add($"The schedule {final} overlaps {label} {other}.");
            if (TitleblockBox(doc, sheet) is { } titleblock && !final.Inside(titleblock))
                warnings.Add($"The schedule {final} extends beyond the title block {titleblock}.");
        }
        return instance;
    }

    /// <summary>Placement at the sheet's title-block centre (revit_place_views_on_sheets' rule for viewports).</summary>
    public static ScheduleSheetInstance PlaceCentered(Document doc, ViewSheet sheet, ViewSchedule schedule, List<string> warnings, out SheetRect? placedBox)
    {
        var center = PlacementPointResolver.GetSheetCenter(doc, sheet);
        var centerMm = (FtToMm(center.X), FtToMm(center.Y));
        return Place(doc, sheet, schedule, (w, h) => SchedulePlacement.CenteredTopLeft(centerMm, w, h), warnings, out placedBox);
    }
}
