using System.Globalization;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Schedules;

/// <summary>Describes a schedule's definition for revit_get_schedule and the create/edit results (#103).</summary>
internal static class ScheduleDefinitionReader
{
    public static object Describe(Document doc, ViewSchedule schedule)
    {
        var definition = schedule.Definition;
        var fields = Fields(doc, definition);
        var byId = fields.ToDictionary(f => f.FieldId, f => f.Name);

        var filters = new List<object>();
        for (var i = 0; i < definition.GetFilterCount(); i++)
        {
            var filter = definition.GetFilter(i);
            filters.Add(DescribeFilter(doc, definition, filter, byId));
        }

        var sorting = new List<object>();
        for (var i = 0; i < definition.GetSortGroupFieldCount(); i++)
        {
            var sort = definition.GetSortGroupField(i);
            var index = SafeIndex(definition, sort.FieldId);
            sorting.Add(new
            {
                fieldIndex = index,
                field = byId.TryGetValue(sort.FieldId.IntegerValue, out var name) ? name : null,
                sortOrder = sort.SortOrder == ScheduleSortOrder.Descending ? "descending" : "ascending",
                showHeader = sort.ShowHeader,
                showFooter = sort.ShowFooter,
                showFooterTitle = sort.ShowFooter && sort.ShowFooterTitle,
                showFooterCount = sort.ShowFooter && sort.ShowFooterCount,
                showBlankLine = sort.ShowBlankLine
            });
        }

        var template = schedule.ViewTemplateId != null && schedule.ViewTemplateId != ElementId.InvalidElementId
            ? doc.GetElement(schedule.ViewTemplateId)
            : null;
        var phaseId = schedule.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
        var phase = phaseId != null && phaseId != ElementId.InvalidElementId ? doc.GetElement(phaseId) : null;

        return new
        {
            scheduleId = schedule.Id.Value,
            name = schedule.Name,
            category = ScheduleLookup.CategoryName(doc, definition),
            categoryId = definition.CategoryId?.Value,
            kind = Kind(schedule),
            isItemized = definition.IsItemized,
            isKeySchedule = definition.IsKeySchedule,
            keyScheduleParameterName = definition.IsKeySchedule ? SafeKeyName(schedule) : null,
            grandTotal = ScheduleGrandTotal.Describe(definition.ShowGrandTotal, definition.ShowGrandTotal && definition.ShowGrandTotalTitle, definition.ShowGrandTotal && definition.ShowGrandTotalCount),
            grandTotalTitle = definition.ShowGrandTotal && definition.ShowGrandTotalTitle ? definition.GrandTotalTitle : null,
            showTitle = definition.ShowTitle,
            showHeaders = definition.ShowHeaders,
            isFilteredBySheet = definition.IsFilteredBySheet,
            includeLinkedFiles = definition.IncludeLinkedFiles,
            template = template == null ? null : new { id = template.Id.Value, name = template.Name },
            phase = phase?.Name,
            fieldCount = fields.Count,
            fields = fields.Select(f => f.Shape).ToList(),
            filters,
            sorting,
            placedOn = PlacedOn(doc, schedule)
        };
    }

    /// <summary>The fields in order, with the shape revit_get_schedule returns.</summary>
    public static List<(int FieldId, string Name, object Shape)> Fields(Document doc, ScheduleDefinition definition)
    {
        var result = new List<(int, string, object)>();
        for (var i = 0; i < definition.GetFieldCount(); i++)
        {
            var field = definition.GetField(i);
            var name = ScheduleFieldCatalog.SafeName(field);
            var heading = field.ColumnHeading ?? string.Empty;
            result.Add((field.FieldId.IntegerValue, name, new
            {
                index = i,
                fieldId = field.FieldId.IntegerValue,
                name,
                columnHeading = heading,
                parameterId = field.ParameterId?.Value,
                fieldType = field.FieldType.ToString(),
                isHidden = field.IsHidden,
                isCalculated = field.IsCalculatedField,
                displayType = field.DisplayType.ToString(),
                hasTotals = field.DisplayType == ScheduleFieldDisplayType.Totals,
                widthMm = Math.Round(UnitUtils.ConvertFromInternalUnits(field.GridColumnWidth, UnitTypeId.Millimeters), 1),
                percentageOf = field.FieldType == ScheduleFieldType.Percentage ? NameOf(definition, field.PercentageOf) : null,
                formula = field.FieldType == ScheduleFieldType.Formula ? "(calculated value — the Revit API does not expose the formula text)" : null,
                format = Format(field)
            }));
        }
        return result;
    }

    private static object DescribeFilter(Document doc, ScheduleDefinition definition, ScheduleFilter filter, Dictionary<int, string> names)
    {
        var type = filter.FilterType.ToString();
        object? value = null;
        string? valueType = null;
        string? unit = null;
        if (filter.IsStringValue) { value = filter.GetStringValue(); valueType = "string"; }
        else if (filter.IsIntegerValue) { value = filter.GetIntegerValue(); valueType = "integer"; }
        else if (filter.IsElementIdValue)
        {
            var id = filter.GetElementIdValue();
            var element = id != null && id != ElementId.InvalidElementId ? doc.GetElement(id) : null;
            value = new { id = id?.Value, name = element?.Name };
            valueType = "elementId";
        }
        else if (filter.IsDoubleValue)
        {
            ForgeTypeId? spec = null;
            try { spec = definition.GetField(filter.FieldId).GetSpecTypeId(); } catch { }
            var (converted, u) = ScheduleFieldValues.FromInternal(doc, filter.GetDoubleValue(), spec);
            value = converted;
            unit = u;
            valueType = "double";
        }
        return new
        {
            fieldIndex = SafeIndex(definition, filter.FieldId),
            field = names.TryGetValue(filter.FieldId.IntegerValue, out var name) ? name : null,
            filterType = ScheduleFilterTypeMap.ToAlias(type),
            value,
            valueType,
            unit
        };
    }

    private static object? Format(ScheduleField field)
    {
        try
        {
            var options = field.GetFormatOptions();
            if (options.UseDefault) return null;
            return new
            {
                unit = options.GetUnitTypeId()?.TypeId,
                accuracy = options.Accuracy,
                suppressTrailingZeros = options.CanSuppressTrailingZeros() && options.SuppressTrailingZeros
            };
        }
        catch
        {
            return null;
        }
    }

    public static List<object> PlacedOn(Document doc, ViewSchedule schedule)
    {
        var result = new List<object>();
        try
        {
            foreach (var instance in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                         .Where(i => i.ScheduleId == schedule.Id))
            {
                var sheet = doc.GetElement(instance.OwnerViewId) as ViewSheet;
                result.Add(new
                {
                    instanceId = instance.Id.Value,
                    sheetId = instance.OwnerViewId.Value,
                    sheetNumber = sheet?.SheetNumber,
                    sheetName = sheet?.Name,
                    segment = instance.SegmentIndex
                });
            }
        }
        catch
        {
            // Descriptive only.
        }
        return result;
    }

    public static string Kind(ViewSchedule schedule)
    {
        var definition = schedule.Definition;
        if (schedule.IsTitleblockRevisionSchedule) return "titleblockRevision";
        if (schedule.IsInternalKeynoteSchedule) return "internalKeynote";
        if (definition.IsKeySchedule) return "key";
        if (definition.IsMaterialTakeoff) return "materialTakeoff";
        if (definition.IsEmbedded) return "embedded";
        return definition.CategoryId == null || definition.CategoryId == ElementId.InvalidElementId ? "multiCategory" : "regular";
    }

    private static int? SafeIndex(ScheduleDefinition definition, ScheduleFieldId id)
    {
        try { return definition.GetFieldIndex(id); }
        catch { return null; }
    }

    private static string? NameOf(ScheduleDefinition definition, ScheduleFieldId id)
    {
        try
        {
            if (id == null || id == ScheduleFieldId.InvalidScheduleFieldId) return null;
            return ScheduleFieldCatalog.SafeName(definition.GetField(id));
        }
        catch { return null; }
    }

    private static string? SafeKeyName(ViewSchedule schedule)
    {
        try { return schedule.KeyScheduleParameterName; }
        catch { return null; }
    }

    public static string Mm(double value) => Math.Round(value, 1).ToString(CultureInfo.InvariantCulture);
}
