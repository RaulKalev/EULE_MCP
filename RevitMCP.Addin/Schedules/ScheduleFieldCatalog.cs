using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Schedules;

/// <summary>
/// Reads what a schedule definition offers (GetSchedulableFields) and what it holds (its fields) into the planner's
/// DTOs, and finds the SchedulableField for a planned catalogue entry again (#103).
/// </summary>
internal static class ScheduleFieldCatalog
{
    /// <summary>The schedulable fields of a definition; <paramref name="details"/> adds shared/data-type information.</summary>
    public static List<SchedulableFieldInfo> Read(Document doc, ScheduleDefinition definition, bool details)
    {
        var result = new List<SchedulableFieldInfo>();
        var fields = definition.GetSchedulableFields();
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            string name;
            try { name = field.GetName(doc); }
            catch { continue; }   // a parameter that no longer exists in the document
            var info = new SchedulableFieldInfo
            {
                Index = i,
                Name = name,
                FieldType = field.FieldType.ToString(),
                ParameterId = field.ParameterId.Value
            };
            if (details) Describe(doc, field.ParameterId, info);
            result.Add(info);
        }
        return result;
    }

    /// <summary>The fields a schedule has now, in order.</summary>
    public static List<ExistingFieldInfo> Current(ScheduleDefinition definition)
    {
        var result = new List<ExistingFieldInfo>();
        for (var i = 0; i < definition.GetFieldCount(); i++)
        {
            var field = definition.GetField(i);
            result.Add(new ExistingFieldInfo
            {
                Index = i,
                FieldId = field.FieldId.IntegerValue,
                Name = SafeName(field),
                Heading = field.ColumnHeading ?? string.Empty,
                FieldType = field.FieldType.ToString(),
                ParameterId = field.ParameterId.Value,
                Hidden = field.IsHidden,
                Calculated = field.IsCalculatedField
            });
        }
        return result;
    }

    /// <summary>The definition's SchedulableField for a field type and parameter id, or null.</summary>
    public static Dictionary<(string FieldType, long ParameterId), SchedulableField> Index(ScheduleDefinition definition)
    {
        var map = new Dictionary<(string, long), SchedulableField>();
        foreach (var field in definition.GetSchedulableFields())
        {
            var key = (field.FieldType.ToString(), field.ParameterId.Value);
            if (!map.ContainsKey(key)) map[key] = field;
        }
        return map;
    }

    public static string SafeName(ScheduleField field)
    {
        try { return field.GetName(); }
        catch { return field.ColumnHeading ?? string.Empty; }
    }

    private static void Describe(Document doc, ElementId parameterId, SchedulableFieldInfo info)
    {
        try
        {
            if (parameterId.Value < 0)
            {
                info.IsShared = false;
                info.DataType = StorageName(doc.get_TypeOfStorage((BuiltInParameter)(int)parameterId.Value));
                return;
            }
            if (doc.GetElement(parameterId) is ParameterElement parameter)
            {
                info.IsShared = parameter is SharedParameterElement;
                var dataType = parameter.GetDefinition()?.GetDataType();
                info.DataType = dataType == null || dataType.Empty() ? null : dataType.TypeId;
            }
        }
        catch
        {
            // Descriptive only.
        }
    }

    private static string? StorageName(StorageType storage) => storage switch
    {
        StorageType.String => "text",
        StorageType.Integer => "integer",
        StorageType.Double => "number",
        StorageType.ElementId => "elementId",
        _ => null
    };
}

/// <summary>What a schedule field holds, for filter values: the planner's value kinds plus the spec of numbers.</summary>
internal static class ScheduleFieldValues
{
    public static (string Kind, ForgeTypeId? Spec) KindOf(Document doc, ScheduleField field)
    {
        try
        {
            var spec = field.GetSpecTypeId();
            if (spec != null && !spec.Empty() && UnitUtils.IsMeasurableSpec(spec))
                return (ScheduleFilterTypeMap.KindDouble, spec);
        }
        catch
        {
            // Not a number with units.
        }

        try
        {
            var parameterId = field.ParameterId;
            if (parameterId == null || parameterId == ElementId.InvalidElementId)
                return (ScheduleFilterTypeMap.KindUnknown, null);
            if (parameterId.Value < 0)
                return (FromStorage(doc.get_TypeOfStorage((BuiltInParameter)(int)parameterId.Value)), null);
            if (doc.GetElement(parameterId) is ParameterElement parameter)
            {
                var dataType = parameter.GetDefinition()?.GetDataType();
                if (dataType == null || dataType.Empty()) return (ScheduleFilterTypeMap.KindUnknown, null);
                if (Category.IsBuiltInCategory(dataType)) return (ScheduleFilterTypeMap.KindElementId, null);
                return (FromStorage(doc.GetTypeOfStorage(dataType)), UnitUtils.IsMeasurableSpec(dataType) ? dataType : null);
            }
        }
        catch
        {
            // Fall through: the value type is unknown and the filter is tried as given.
        }
        return (ScheduleFilterTypeMap.KindUnknown, null);
    }

    private static string FromStorage(StorageType storage) => storage switch
    {
        StorageType.String => ScheduleFilterTypeMap.KindString,
        StorageType.Integer => ScheduleFilterTypeMap.KindInteger,
        StorageType.Double => ScheduleFilterTypeMap.KindDouble,
        StorageType.ElementId => ScheduleFilterTypeMap.KindElementId,
        _ => ScheduleFilterTypeMap.KindUnknown
    };

    /// <summary>A filter number in internal units: mm for lengths (the connector's unit), the project's display unit otherwise.</summary>
    public static (double Internal, string Unit) ToInternal(Document doc, double value, ForgeTypeId? spec)
    {
        if (spec == null || spec.Empty()) return (value, "as given");
        if (spec == SpecTypeId.Length)
            return (UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters), "mm");
        var unit = doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId();
        return (UnitUtils.ConvertToInternalUnits(value, unit), SafeLabel(unit));
    }

    /// <summary>The inverse of <see cref="ToInternal"/>, for reading filters back.</summary>
    public static (double Value, string Unit) FromInternal(Document doc, double value, ForgeTypeId? spec)
    {
        if (spec == null || spec.Empty()) return (value, "internal");
        if (spec == SpecTypeId.Length)
            return (Math.Round(UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters), 1), "mm");
        var unit = doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId();
        return (UnitUtils.ConvertFromInternalUnits(value, unit), SafeLabel(unit));
    }

    private static string SafeLabel(ForgeTypeId unit)
    {
        try { return LabelUtils.GetLabelForUnit(unit); }
        catch { return unit.TypeId; }
    }
}
