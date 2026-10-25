using System.Globalization;
using System.Reflection;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Schedules;

/// <summary>What happened to one planned action.</summary>
internal sealed class ScheduleActionResult
{
    public string Action { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Note { get; set; }
}

internal sealed class ScheduleExecution
{
    public List<ScheduleActionResult> Results { get; } = new();
    public Dictionary<string, ScheduleFieldId> FieldIds { get; } = new(StringComparer.Ordinal);
    public IEnumerable<ScheduleActionResult> Failed => Results.Where(r => !r.Ok);
}

/// <summary>
/// Executes a <see cref="SchedulePlan"/> on a schedule's definition, inside the caller's transaction (#103). Every
/// action runs on its own, so one Revit refusal is reported with its reason instead of hiding the others; the caller
/// decides whether failures roll the whole transaction back.
/// </summary>
internal static class ScheduleBuilder
{
    public static ScheduleExecution Apply(Document doc, ViewSchedule schedule, SchedulePlan plan, CancellationToken cancellationToken)
    {
        var execution = new ScheduleExecution();
        var definition = schedule.Definition;
        var catalog = ScheduleFieldCatalog.Index(definition);

        foreach (var field in plan.Fields.Where(f => f.ExistingFieldId.HasValue))
            execution.FieldIds[field.Key] = new ScheduleFieldId(field.ExistingFieldId!.Value);

        foreach (var action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new ScheduleActionResult { Action = action.Kind.ToString(), Description = action.Description };
            try
            {
                result.Note = Run(doc, schedule, definition, catalog, action, execution);
                result.Ok = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = ex.Message;
            }
            execution.Results.Add(result);
        }
        return execution;
    }

    private static string? Run(
        Document doc, ViewSchedule schedule, ScheduleDefinition definition,
        Dictionary<(string FieldType, long ParameterId), SchedulableField> catalog, ScheduleAction action, ScheduleExecution execution)
    {
        switch (action.Kind)
        {
            case ScheduleActionKind.AddField:
            {
                if (!catalog.TryGetValue((action.FieldType ?? string.Empty, action.ParameterId), out var schedulable))
                    throw new InvalidOperationException("Revit does not offer this field for the schedule's category.");
                execution.FieldIds[action.FieldKey] = definition.AddField(schedulable).FieldId;
                return null;
            }
            case ScheduleActionKind.AddCount:
                execution.FieldIds[action.FieldKey] = definition.AddField(ScheduleFieldType.Count).FieldId;
                return null;
            case ScheduleActionKind.AddPercentage:
            {
                var of = FieldId(execution, action.OfFieldKey ?? string.Empty);
                var field = definition.AddField(ScheduleFieldType.Percentage);
                execution.FieldIds[action.FieldKey] = field.FieldId;
                field.PercentageOf = of;
                return null;
            }
            case ScheduleActionKind.RemoveField:
                definition.RemoveField(new ScheduleFieldId(action.ExistingFieldId ?? -1));
                return null;
            case ScheduleActionKind.SetHeading:
                Field(definition, execution, action).ColumnHeading = action.Text ?? string.Empty;
                return null;
            case ScheduleActionKind.SetWidth:
                Field(definition, execution, action).GridColumnWidth = UnitUtils.ConvertToInternalUnits(action.Number ?? 0, UnitTypeId.Millimeters);
                return null;
            case ScheduleActionKind.SetHidden:
                Field(definition, execution, action).IsHidden = action.Flag ?? false;
                return null;
            case ScheduleActionKind.SetTotals:
            {
                var field = Field(definition, execution, action);
                if (action.Flag == true && !field.CanTotal())
                    throw new InvalidOperationException($"'{ScheduleFieldCatalog.SafeName(field)}' cannot be totaled.");
                field.DisplayType = action.Flag == true ? ScheduleFieldDisplayType.Totals : ScheduleFieldDisplayType.Standard;
                return null;
            }
            case ScheduleActionKind.SetFormat:
                return SetFormat(doc, Field(definition, execution, action), action.Format!);
            case ScheduleActionKind.SetFieldOrder:
                definition.SetFieldOrder((action.Order ?? new List<string>()).Select(key => FieldId(execution, key)).ToList());
                return null;
            case ScheduleActionKind.ClearFilters:
                definition.ClearFilters();
                return null;
            case ScheduleActionKind.AddFilter:
                return AddFilter(doc, definition, execution, action);
            case ScheduleActionKind.ClearSort:
                definition.ClearSortGroupFields();
                return null;
            case ScheduleActionKind.AddSort:
            {
                var id = FieldId(execution, action.FieldKey);
                if (!definition.CanSortByField(id))
                    throw new InvalidOperationException("Revit cannot sort by this field.");
                var sort = action.Sort!;
                definition.AddSortGroupField(new ScheduleSortGroupField(id, sort.Descending ? ScheduleSortOrder.Descending : ScheduleSortOrder.Ascending)
                {
                    ShowHeader = sort.Header,
                    ShowFooter = sort.Footer,
                    ShowBlankLine = sort.BlankLine
                });
                return null;
            }
            case ScheduleActionKind.SetItemized:
                definition.IsItemized = action.Flag ?? true;
                return null;
            case ScheduleActionKind.SetGrandTotal:
                definition.ShowGrandTotal = action.Flag ?? false;
                if (definition.ShowGrandTotal)
                {
                    definition.ShowGrandTotalTitle = action.GrandTotalTitle;
                    definition.ShowGrandTotalCount = action.GrandTotalCount;
                }
                return null;
            case ScheduleActionKind.SetTemplate:
                return SetTemplate(doc, schedule, action.Text ?? string.Empty);
            case ScheduleActionKind.SetPhase:
                return SetPhase(doc, schedule, action.Text ?? string.Empty);
            case ScheduleActionKind.SetKeyName:
                schedule.KeyScheduleParameterName = action.Text ?? string.Empty;
                return null;
            default:
                throw new InvalidOperationException($"Unknown action {action.Kind}.");
        }
    }

    // ── Fields ──────────────────────────────────────────────────────────────

    private static ScheduleFieldId FieldId(ScheduleExecution execution, string key)
    {
        if (execution.FieldIds.TryGetValue(key, out var id)) return id;
        throw new InvalidOperationException("The field this step refers to was not added (see the earlier step).");
    }

    private static ScheduleField Field(ScheduleDefinition definition, ScheduleExecution execution, ScheduleAction action)
        => definition.GetField(FieldId(execution, action.FieldKey));

    private static string? SetFormat(Document doc, ScheduleField field, ScheduleFieldFormatSpec format)
    {
        var spec = field.GetSpecTypeId();
        if (spec == null || spec.Empty() || !UnitUtils.IsMeasurableSpec(spec))
            throw new InvalidOperationException($"'{ScheduleFieldCatalog.SafeName(field)}' is not a number with units, so it has no number format.");

        var current = field.GetFormatOptions();
        var options = current.UseDefault ? new FormatOptions(doc.GetUnits().GetFormatOptions(spec)) : current;
        options.UseDefault = false;
        var notes = new List<string>();

        if (!string.IsNullOrWhiteSpace(format.Units))
        {
            if (!ScheduleFormatUnits.TryResolve(format.Units, out var property))
                throw new InvalidOperationException($"Unknown unit '{format.Units}'. Use mm, cm, m, m2, m3, W, kW, VA, kVA, A, V, or a UnitTypeId name.");
            var unit = typeof(UnitTypeId).GetProperty(property, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as ForgeTypeId
                       ?? throw new InvalidOperationException($"Revit has no unit '{property}'.");
            if (!options.IsValidForSpec(spec) || !FormatOptionsAccepts(spec, unit))
                throw new InvalidOperationException($"'{format.Units}' is not a unit of this field's kind ({SafeSpecLabel(spec)}).");
            options.SetUnitTypeId(unit);
            notes.Add($"unit {property}");
        }
        if (format.Accuracy.HasValue)
        {
            if (!options.IsValidAccuracy(format.Accuracy.Value))
                throw new InvalidOperationException($"Accuracy {format.Accuracy.Value.ToString(CultureInfo.InvariantCulture)} is not valid for this unit (use 1, 0.1, 0.01, …).");
            options.Accuracy = format.Accuracy.Value;
        }
        if (format.SuppressTrailingZeros.HasValue && options.CanSuppressTrailingZeros())
            options.SuppressTrailingZeros = format.SuppressTrailingZeros.Value;
        field.SetFormatOptions(options);
        return notes.Count > 0 ? string.Join(", ", notes) : null;
    }

    private static bool FormatOptionsAccepts(ForgeTypeId spec, ForgeTypeId unit)
    {
        try { return UnitUtils.IsValidUnit(spec, unit); }
        catch { return true; }
    }

    private static string SafeSpecLabel(ForgeTypeId spec)
    {
        try { return LabelUtils.GetLabelForSpec(spec); }
        catch { return spec.TypeId; }
    }

    // ── Filters ─────────────────────────────────────────────────────────────

    private static string? AddFilter(Document doc, ScheduleDefinition definition, ScheduleExecution execution, ScheduleAction action)
    {
        var id = FieldId(execution, action.FieldKey);
        var field = definition.GetField(id);
        var type = (ScheduleFilterType)Enum.Parse(typeof(ScheduleFilterType), action.FilterType!);
        var spec = action.Filter!;

        if (!ScheduleFilterTypeMap.NeedsValue(action.FilterType!))
        {
            definition.AddFilter(new ScheduleFilter(id, type));
            return null;
        }
        if (ScheduleFilterTypeMap.IsSubstring(action.FilterType!) && !definition.CanFilterBySubstring(id))
            throw new InvalidOperationException($"Revit cannot apply '{ScheduleFilterTypeMap.ToAlias(action.FilterType!)}' to '{ScheduleFieldCatalog.SafeName(field)}'; substring filters need a text field.");

        var (kind, numberSpec) = ScheduleFieldValues.KindOf(doc, field);
        var value = ScheduleFilterTypeMap.Coerce(action.FilterType!, spec.Value, spec.Number, kind);
        if (value.IsError) throw new InvalidOperationException(value.Error);

        ScheduleFilter filter;
        string note;
        switch (value.Kind)
        {
            case ScheduleFilterTypeMap.KindString:
                filter = new ScheduleFilter(id, type, value.Text ?? string.Empty);
                note = $"text '{value.Text}'";
                break;
            case ScheduleFilterTypeMap.KindInteger:
                filter = new ScheduleFilter(id, type, value.Integer ?? 0);
                note = $"integer {value.Integer}";
                break;
            case ScheduleFilterTypeMap.KindDouble:
            {
                var (internalValue, unit) = ScheduleFieldValues.ToInternal(doc, value.Double ?? 0, numberSpec);
                filter = new ScheduleFilter(id, type, internalValue);
                note = $"number {(value.Double ?? 0).ToString(CultureInfo.InvariantCulture)} ({unit})";
                break;
            }
            case ScheduleFilterTypeMap.KindElementId:
            {
                var target = doc.GetElement(new ElementId(value.ElementId ?? -1)) ?? FindByName(doc, value.Text ?? string.Empty);
                if (target == null) throw new InvalidOperationException($"No element with id or name '{value.Text}'.");
                filter = new ScheduleFilter(id, type, target.Id);
                note = $"element {target.Id.Value} ('{target.Name}')";
                break;
            }
            default: // elementName
            {
                var target = FindByName(doc, value.Text ?? string.Empty);
                if (target == null)
                    throw new InvalidOperationException($"'{ScheduleFieldCatalog.SafeName(field)}' refers to an element; no level, phase or type is named '{value.Text}'.");
                filter = new ScheduleFilter(id, type, target.Id);
                note = $"{target.GetType().Name} '{target.Name}' (id {target.Id.Value})";
                break;
            }
        }

        if (!definition.CanFilterByValue(id) && ScheduleFilterTypeMap.NeedsValue(action.FilterType!))
            throw new InvalidOperationException($"Revit cannot filter '{ScheduleFieldCatalog.SafeName(field)}' by value.");
        definition.AddFilter(filter);
        return value.Note != null ? $"{note}; {value.Note}" : note;
    }

    /// <summary>A level, phase or element type by name (or "Family: Type"), for element-id filter values.</summary>
    internal static Element? FindByName(Document doc, string name)
    {
        var wanted = name.Trim();
        if (wanted.Length == 0) return null;
        bool Same(string? n) => string.Equals(n?.Trim(), wanted, StringComparison.OrdinalIgnoreCase);

        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstOrDefault(e => Same(e.Name));
        if (level != null) return level;
        foreach (Phase phase in doc.Phases)
            if (Same(phase.Name)) return phase;
        return new FilteredElementCollector(doc).WhereElementIsElementType()
            .FirstOrDefault(e => Same(e.Name) || (e is ElementType t && Same($"{t.FamilyName}: {t.Name}")));
    }

    // ── View settings ───────────────────────────────────────────────────────

    private static string SetTemplate(Document doc, ViewSchedule schedule, string name)
    {
        var templates = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Where(v => v.IsTemplate).ToList();
        var template = templates.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (template == null)
        {
            var near = SchedulePlanner.Suggest(name, templates.Select(t => t.Name));
            throw new InvalidOperationException($"No schedule view template named '{name}'." + (near.Count > 0 ? $" Nearest: {string.Join(", ", near)}." : templates.Count == 0 ? " The model has no schedule templates." : string.Empty));
        }
        schedule.ViewTemplateId = template.Id;
        return $"template {template.Id.Value}";
    }

    private static string SetPhase(Document doc, ViewSchedule schedule, string name)
    {
        Phase? phase = null;
        foreach (Phase p in doc.Phases)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) phase = p;
        if (phase == null)
        {
            var names = new List<string>();
            foreach (Phase p in doc.Phases) names.Add(p.Name);
            throw new InvalidOperationException($"No phase named '{name}'. Phases: {string.Join(", ", names)}.");
        }
        var parameter = schedule.get_Parameter(BuiltInParameter.VIEW_PHASE);
        if (parameter == null || parameter.IsReadOnly)
            throw new InvalidOperationException("The schedule's phase cannot be set (it may be controlled by its view template).");
        parameter.Set(phase.Id);
        return $"phase {phase.Id.Value}";
    }
}
