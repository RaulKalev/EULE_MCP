using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Documentation.Sheets;

internal enum SheetDuplicateMode
{
    EmptySheet,
    WithSheetDetailing,
    WithViews
}

internal sealed class SheetDuplicationOptions
{
    public SheetDuplicateMode Mode { get; set; } = SheetDuplicateMode.EmptySheet;
    public bool KeepTitleBlock { get; set; } = true;
    public bool CopySheetParameters { get; set; } = true;
    public bool CopyTitleBlockParameters { get; set; } = true;
    public bool KeepLegends { get; set; }
    public bool KeepSchedules { get; set; }
    public bool CopyRevisions { get; set; }
    public ViewDuplicateOption ViewDuplicateOption { get; set; } = ViewDuplicateOption.WithDetailing;
}

internal sealed class SheetContentSummary
{
    public int DetailingElementCount { get; set; }
    public int ModelViewportCount { get; set; }
    public int LegendViewportCount { get; set; }
    public int ScheduleCount { get; set; }
    public int RevisionCount { get; set; }
}

internal sealed class SheetDuplicationResult
{
    public ViewSheet Sheet { get; set; } = null!;
    public int CopiedDetailingElements { get; set; }
    public int DuplicatedViews { get; set; }
    public int PlacedLegends { get; set; }
    public int PlacedSchedules { get; set; }
    public int CopiedRevisions { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Revit-only implementation of the SheetManager duplication behavior. The service is UI-agnostic
/// and is called from MCP tools that already execute on Revit's ExternalEvent thread.
/// </summary>
internal static class SheetDuplicationService
{
    private static readonly HashSet<long> SheetDetailCategoryIds = new()
    {
        (long)BuiltInCategory.OST_Lines,
        (long)BuiltInCategory.OST_TextNotes,
        (long)BuiltInCategory.OST_GenericAnnotation,
        (long)BuiltInCategory.OST_Dimensions,
        (long)BuiltInCategory.OST_Tags
    };

    public static bool TryParseMode(string value, out SheetDuplicateMode mode)
    {
        if (string.Equals(value, "Empty", StringComparison.OrdinalIgnoreCase))
            value = nameof(SheetDuplicateMode.EmptySheet);
        else if (string.Equals(value, "WithDetailing", StringComparison.OrdinalIgnoreCase))
            value = nameof(SheetDuplicateMode.WithSheetDetailing);

        return Enum.TryParse(value, true, out mode);
    }

    public static bool TryParseViewDuplicateOption(string value, out ViewDuplicateOption option)
    {
        if (string.Equals(value, "DuplicateWithDetailing", StringComparison.OrdinalIgnoreCase))
            value = "WithDetailing";
        return Enum.TryParse(value, true, out option);
    }

    public static SheetContentSummary Inspect(Document doc, ViewSheet source)
    {
        var summary = new SheetContentSummary
        {
            DetailingElementCount = GetSheetDetailingElementIds(doc, source).Count,
            RevisionCount = source.GetAdditionalRevisionIds().Count
        };

        foreach (var viewportId in source.GetAllViewports())
        {
            var viewport = doc.GetElement(viewportId) as Viewport;
            var view = viewport == null ? null : doc.GetElement(viewport.ViewId) as View;
            if (view == null) continue;
            if (view.ViewType == ViewType.Legend) summary.LegendViewportCount++;
            else summary.ModelViewportCount++;
        }

        summary.ScheduleCount = GetScheduleInstances(doc, source).Count;
        return summary;
    }

    public static int CountDuplicableModelViews(
        Document doc,
        ViewSheet source,
        ViewDuplicateOption option,
        out List<string> unsupportedViewNames)
    {
        unsupportedViewNames = new List<string>();
        var count = 0;
        foreach (var viewportId in source.GetAllViewports())
        {
            var viewport = doc.GetElement(viewportId) as Viewport;
            var view = viewport == null ? null : doc.GetElement(viewport.ViewId) as View;
            if (view == null || view.ViewType == ViewType.Legend) continue;
            if (view.CanViewBeDuplicated(option)) count++;
            else unsupportedViewNames.Add(view.Name);
        }
        return count;
    }

    public static SheetDuplicationResult Duplicate(
        Document doc,
        ViewSheet source,
        string newSheetNumber,
        string newSheetName,
        SheetDuplicationOptions options)
    {
        var result = new SheetDuplicationResult();
        var titleBlockTypeId = options.KeepTitleBlock
            ? GetTitleBlock(doc, source)?.GetTypeId() ?? ElementId.InvalidElementId
            : ElementId.InvalidElementId;

        var newSheet = ViewSheet.Create(doc, titleBlockTypeId);
        result.Sheet = newSheet;

        if (options.Mode != SheetDuplicateMode.EmptySheet)
            result.CopiedDetailingElements = CopySheetDetailing(doc, source, newSheet, result.Warnings);

        if (options.CopySheetParameters)
            CopyWritableParameters(source, newSheet, skipSheetIdentity: true, result.Warnings);

        if (options.CopyTitleBlockParameters && options.KeepTitleBlock)
        {
            var sourceTitleBlock = GetTitleBlock(doc, source);
            var targetTitleBlock = GetTitleBlock(doc, newSheet);
            if (sourceTitleBlock != null && targetTitleBlock != null)
                CopyWritableParameters(sourceTitleBlock, targetTitleBlock, skipSheetIdentity: false, result.Warnings);
        }

        if (options.CopyRevisions)
        {
            var revisionIds = source.GetAdditionalRevisionIds();
            if (revisionIds.Count > 0)
            {
                newSheet.SetAdditionalRevisionIds(revisionIds);
                result.CopiedRevisions = revisionIds.Count;
            }
        }

        if (options.KeepSchedules)
            CopySchedules(doc, source, newSheet, result);

        CopyViewports(doc, source, newSheet, options, result);

        // Set identity last. Some project/shared parameter definitions can alias built-in sheet
        // identity parameters, and Revit validates sheet-number uniqueness at transaction commit.
        newSheet.SheetNumber = newSheetNumber;
        newSheet.Name = newSheetName;

        return result;
    }

    private static FamilyInstance? GetTitleBlock(Document doc, ViewSheet sheet)
    {
        return new FilteredElementCollector(doc)
            .OwnedByView(sheet.Id)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType()
            .Cast<FamilyInstance>()
            .FirstOrDefault();
    }

    private static List<ElementId> GetSheetDetailingElementIds(Document doc, ViewSheet source)
    {
        return new FilteredElementCollector(doc, source.Id)
            .WhereElementIsNotElementType()
            .Where(e => e.Category != null && SheetDetailCategoryIds.Contains(e.Category.Id.Value))
            .Select(e => e.Id)
            .ToList();
    }

    private static int CopySheetDetailing(
        Document doc,
        ViewSheet source,
        ViewSheet target,
        List<string> warnings)
    {
        var ids = GetSheetDetailingElementIds(doc, source);
        if (ids.Count == 0) return 0;

        try
        {
            var copied = ElementTransformUtils.CopyElements(
                source,
                ids,
                target,
                Transform.Identity,
                new CopyPasteOptions());
            return copied.Count;
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not copy all sheet detailing as one group: {ex.Message}");
        }

        // A bad tag or dimension should not prevent independent text/detail items from copying.
        var copiedCount = 0;
        foreach (var id in ids)
        {
            try
            {
                var copied = ElementTransformUtils.CopyElements(
                    source,
                    new[] { id },
                    target,
                    Transform.Identity,
                    new CopyPasteOptions());
                copiedCount += copied.Count;
            }
            catch (Exception ex)
            {
                warnings.Add($"Sheet detailing element {id.Value} was skipped: {ex.Message}");
            }
        }
        return copiedCount;
    }

    private static void CopyViewports(
        Document doc,
        ViewSheet source,
        ViewSheet target,
        SheetDuplicationOptions options,
        SheetDuplicationResult result)
    {
        foreach (var viewportId in source.GetAllViewports())
        {
            var sourceViewport = doc.GetElement(viewportId) as Viewport;
            var sourceView = sourceViewport == null ? null : doc.GetElement(sourceViewport.ViewId) as View;
            if (sourceViewport == null || sourceView == null) continue;

            var duplicatedViewId = ElementId.InvalidElementId;

            try
            {
                ElementId viewToPlace;
                if (sourceView.ViewType == ViewType.Legend)
                {
                    if (!options.KeepLegends) continue;
                    viewToPlace = sourceView.Id;
                }
                else
                {
                    if (options.Mode != SheetDuplicateMode.WithViews) continue;
                    if (!sourceView.CanViewBeDuplicated(options.ViewDuplicateOption))
                    {
                        result.Warnings.Add(
                            $"View '{sourceView.Name}' cannot be duplicated with {options.ViewDuplicateOption} and was skipped.");
                        continue;
                    }
                    viewToPlace = sourceView.Duplicate(options.ViewDuplicateOption);
                    duplicatedViewId = viewToPlace;
                }

                if (!Viewport.CanAddViewToSheet(doc, target.Id, viewToPlace))
                {
                    if (duplicatedViewId != ElementId.InvalidElementId)
                        doc.Delete(duplicatedViewId);
                    result.Warnings.Add($"View '{sourceView.Name}' cannot be placed on sheet '{target.SheetNumber}'.");
                    continue;
                }

                var newViewport = Viewport.Create(doc, target.Id, viewToPlace, sourceViewport.GetBoxCenter());
                TryCopyViewportProperties(sourceViewport, newViewport, result.Warnings);

                if (sourceView.ViewType == ViewType.Legend) result.PlacedLegends++;
                else result.DuplicatedViews++;
            }
            catch (Exception ex)
            {
                if (duplicatedViewId != ElementId.InvalidElementId && doc.GetElement(duplicatedViewId) != null)
                {
                    try { doc.Delete(duplicatedViewId); }
                    catch { /* retain the original placement error */ }
                }
                result.Warnings.Add($"Could not copy viewport for '{sourceView.Name}': {ex.Message}");
            }
        }
    }

    private static List<ScheduleSheetInstance> GetScheduleInstances(Document doc, ViewSheet source)
    {
        return new FilteredElementCollector(doc, source.Id)
            .OfClass(typeof(ScheduleSheetInstance))
            .Cast<ScheduleSheetInstance>()
            .Where(s => !s.IsTitleblockRevisionSchedule)
            .ToList();
    }

    private static void CopySchedules(
        Document doc,
        ViewSheet source,
        ViewSheet target,
        SheetDuplicationResult result)
    {
        foreach (var instance in GetScheduleInstances(doc, source))
        {
            try
            {
                if (instance.ScheduleId == ElementId.InvalidElementId) continue;
                ScheduleSheetInstance.Create(doc, target.Id, instance.ScheduleId, instance.Point);
                result.PlacedSchedules++;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Could not place schedule {instance.ScheduleId.Value}: {ex.Message}");
            }
        }
    }

    private static void TryCopyViewportProperties(
        Viewport source,
        Viewport target,
        List<string> warnings)
    {
        try
        {
            var typeId = source.GetTypeId();
            if (typeId != ElementId.InvalidElementId && target.GetTypeId() != typeId)
                target.ChangeTypeId(typeId);
        }
        catch (Exception ex)
        {
            warnings.Add($"Viewport type could not be preserved: {ex.Message}");
        }

        CopyWritableParameters(source, target, skipSheetIdentity: false, warnings);
    }

    private static void CopyWritableParameters(
        Element source,
        Element target,
        bool skipSheetIdentity,
        List<string> warnings)
    {
        foreach (Parameter sourceParameter in source.Parameters)
        {
            if (skipSheetIdentity && IsSheetIdentityParameter(sourceParameter)) continue;

            Parameter? targetParameter;
            try
            {
                targetParameter = target.get_Parameter(sourceParameter.Definition)
                                  ?? target.LookupParameter(sourceParameter.Definition.Name);
            }
            catch
            {
                continue;
            }

            if (targetParameter == null || targetParameter.IsReadOnly ||
                targetParameter.StorageType != sourceParameter.StorageType)
                continue;

            try
            {
                switch (sourceParameter.StorageType)
                {
                    case StorageType.String:
                        targetParameter.Set(sourceParameter.AsString() ?? string.Empty);
                        break;
                    case StorageType.Integer:
                        targetParameter.Set(sourceParameter.AsInteger());
                        break;
                    case StorageType.Double:
                        targetParameter.Set(sourceParameter.AsDouble());
                        break;
                    case StorageType.ElementId:
                        targetParameter.Set(sourceParameter.AsElementId());
                        break;
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Parameter '{sourceParameter.Definition.Name}' could not be copied: {ex.Message}");
            }
        }
    }

    private static bool IsSheetIdentityParameter(Parameter parameter)
    {
        if (parameter.Definition is InternalDefinition definition)
        {
            return definition.BuiltInParameter == BuiltInParameter.SHEET_NUMBER ||
                   definition.BuiltInParameter == BuiltInParameter.VIEW_NAME;
        }

        return string.Equals(parameter.Definition.Name, "Sheet Number", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(parameter.Definition.Name, "Sheet Name", StringComparison.OrdinalIgnoreCase);
    }
}
