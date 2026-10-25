using System.Globalization;
using RevitMCP.Addin.Documentation.Placement;
using RevitMCP.Addin.Query;

namespace RevitMCP.Addin.Schedules;

// The schedule planner (#103): validates a create or edit spec against the schedulable-field catalogue and the fields
// a schedule already has, and emits the ordered list of definition changes the add-in executes. Pure — the catalogue
// and the current fields come in as DTOs — so field resolution, suggestions, filter and sort validation are tested
// without Revit.

/// <summary>One entry of ScheduleDefinition.GetSchedulableFields(), as read by the add-in.</summary>
public sealed class SchedulableFieldInfo
{
    /// <summary>Position in GetSchedulableFields() of the definition the catalogue was read from.</summary>
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>ScheduleFieldType member name: Instance, ElementType, Count, Room, FromRoom, …</summary>
    public string FieldType { get; set; } = "Instance";
    public long ParameterId { get; set; }
    public bool? IsShared { get; set; }
    public string? DataType { get; set; }

    /// <summary>The name with its field-type prefix when it comes from a related element ("Room: Name").</summary>
    public string QualifiedName => ScheduleFieldNames.Qualify(Name, FieldType);
}

/// <summary>A field the schedule already has.</summary>
public sealed class ExistingFieldInfo
{
    public int Index { get; set; }
    public int FieldId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Heading { get; set; } = string.Empty;
    public string FieldType { get; set; } = string.Empty;
    public long ParameterId { get; set; }
    public bool Hidden { get; set; }
    public bool Calculated { get; set; }
}

public enum ScheduleActionKind
{
    AddField,
    AddCount,
    AddPercentage,
    RemoveField,
    SetHeading,
    SetWidth,
    SetHidden,
    SetTotals,
    SetFormat,
    SetFieldOrder,
    ClearFilters,
    AddFilter,
    ClearSort,
    AddSort,
    SetItemized,
    SetGrandTotal,
    SetTemplate,
    SetPhase,
    SetKeyName
}

/// <summary>One change to the schedule definition, in execution order.</summary>
public sealed class ScheduleAction
{
    public ScheduleActionKind Kind { get; set; }

    /// <summary>The planned field the action is about (<see cref="PlannedField.Key"/>).</summary>
    public string FieldKey { get; set; } = string.Empty;

    /// <summary>AddField: the catalogue entry.</summary>
    public string? FieldType { get; set; }
    public long ParameterId { get; set; }

    /// <summary>RemoveField: the existing field id.</summary>
    public int? ExistingFieldId { get; set; }

    public string? Text { get; set; }
    public double? Number { get; set; }
    public bool? Flag { get; set; }

    /// <summary>AddFilter: canonical ScheduleFilterType name and the filter as given.</summary>
    public string? FilterType { get; set; }
    public ScheduleFilterSpec? Filter { get; set; }

    public ScheduleSortSpec? Sort { get; set; }

    /// <summary>AddPercentage: the field the percentage is of.</summary>
    public string? OfFieldKey { get; set; }

    /// <summary>SetFieldOrder: every field key in the new order.</summary>
    public List<string>? Order { get; set; }

    public ScheduleFieldFormatSpec? Format { get; set; }

    /// <summary>SetGrandTotal: the three flags.</summary>
    public bool GrandTotalTitle { get; set; }
    public bool GrandTotalCount { get; set; }

    public string Description { get; set; } = string.Empty;
}

/// <summary>A column of the schedule as it will be after the plan ran.</summary>
public sealed class PlannedField
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Heading { get; set; }

    /// <summary>existing | new</summary>
    public string Source { get; set; } = "new";
    public int? ExistingFieldId { get; set; }

    /// <summary>Existing fields: position in the schedule before the edit.</summary>
    public int OriginalIndex { get; set; } = int.MaxValue;
    public string FieldType { get; set; } = string.Empty;
    public long ParameterId { get; set; }
    public bool Hidden { get; set; }

    /// <summary>True when the planner added the field (hidden) because a filter or sort needs it.</summary>
    public bool AddedForFilterOrSort { get; set; }

    public string QualifiedName => ScheduleFieldNames.Qualify(Name, FieldType);
    public string Display => Hidden ? $"{Heading ?? Name} (hidden)" : Heading ?? Name;
}

/// <summary>A name the planner could not resolve, with the nearest names.</summary>
public sealed class MissingField
{
    public string Name { get; set; } = string.Empty;

    /// <summary>field | filter | sort | percentage | remove | order | rename | hide | show | format | width</summary>
    public string Role { get; set; } = "field";
    public string? Reason { get; set; }
    public List<string> Suggestions { get; set; } = new();
}

/// <summary>The planner's answer: the resulting fields, the actions, and what is wrong.</summary>
public sealed class SchedulePlan
{
    public string? Name { get; set; }
    public List<PlannedField> Fields { get; } = new();
    public List<ScheduleAction> Actions { get; } = new();
    public List<MissingField> MissingFields { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> OrderBefore { get; } = new();
    public bool AllowMissingFields { get; set; }

    /// <summary>A status code for the errors, when one is more specific than validation_failed.</summary>
    public string? ErrorStatus { get; set; }

    public List<string> OrderAfter => Fields.Select(f => f.Display).ToList();

    public bool CanApply => Errors.Count == 0 && (MissingFields.Count == 0 || AllowMissingFields);

    /// <summary>ok | validation_failed (or <see cref="ErrorStatus"/>) | field_not_found.</summary>
    public string Status => Errors.Count > 0 ? ErrorStatus ?? "validation_failed"
        : MissingFields.Count > 0 && !AllowMissingFields ? "field_not_found" : "ok";
}

/// <summary>Field-name helpers shared by the planner and the readers.</summary>
public static class ScheduleFieldNames
{
    private static readonly Dictionary<string, string> Prefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Room"] = "Room", ["FromRoom"] = "From Room", ["ToRoom"] = "To Room", ["Space"] = "Space",
        ["ProjectInfo"] = "Project Information", ["Material"] = "Material", ["MaterialQuantity"] = "Material",
        ["StructuralMaterial"] = "Structural Material", ["RevitLinkInstance"] = "RVT Link", ["RevitLinkType"] = "RVT Link Type",
        ["Analytical"] = "Analytical", ["Revision"] = "Revision", ["Sheets"] = "Sheet", ["Views"] = "View"
    };

    /// <summary>"Room: Name" for a Room field named "Name"; names that already carry a prefix are kept.</summary>
    public static string Qualify(string name, string fieldType)
    {
        if (string.IsNullOrEmpty(name) || name.Contains(':')) return name;
        return Prefixes.TryGetValue(fieldType ?? string.Empty, out var prefix) ? $"{prefix}: {name}" : name;
    }

    /// <summary>The ScheduleFieldType name a short field-type word means (type → ElementType).</summary>
    public static string? NormalizeFieldType(string? fieldType)
    {
        if (string.IsNullOrWhiteSpace(fieldType)) return null;
        var key = fieldType!.Trim().Replace(" ", string.Empty).Replace("_", string.Empty);
        return key.ToLowerInvariant() switch
        {
            "type" or "elementtype" => "ElementType",
            "instance" => "Instance",
            "room" => "Room",
            "fromroom" => "FromRoom",
            "toroom" => "ToRoom",
            "space" => "Space",
            "projectinfo" or "projectinformation" => "ProjectInfo",
            "material" => "Material",
            "count" => "Count",
            _ => key
        };
    }

    /// <summary>Instance first, then type, then the rest: which of several same-named fields an unqualified name means.</summary>
    public static int Preference(string fieldType) => fieldType switch
    {
        "Instance" => 0,
        "ElementType" => 1,
        "Count" => 2,
        "ViewBased" => 3,
        _ => 10
    };
}

public static class SchedulePlanner
{
    /// <summary>Revit's limit on filters per schedule.</summary>
    public const int MaxFilters = 8;

    // ── Create ──────────────────────────────────────────────────────────────

    public static SchedulePlan PlanCreate(ScheduleSpec spec, IReadOnlyList<SchedulableFieldInfo> catalog, ICollection<string> takenViewNames)
    {
        var plan = new SchedulePlan { AllowMissingFields = spec.AllowMissingFields };

        var requested = string.IsNullOrWhiteSpace(spec.Name) ? string.Empty : spec.Name.Trim();
        if (requested.Length > 0)
        {
            plan.Name = UniqueName(requested, takenViewNames);
            if (!string.Equals(plan.Name, requested, StringComparison.Ordinal))
                plan.Warnings.Add($"A view named '{requested}' exists; the schedule is named '{plan.Name}'.");
        }

        var counter = 0;
        foreach (var field in spec.Fields)
            AddRequestedField(plan, field, catalog, ref counter, insertAt: null);
        ResolvePercentages(plan, spec.Fields);

        if (spec.KeySchedule && !string.IsNullOrWhiteSpace(spec.KeyName))
            plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.SetKeyName, Text = spec.KeyName, Description = $"Key name '{spec.KeyName}'" });

        if (spec.KeySchedule && (spec.Filters.Count > 0 || spec.Sort.Count > 0))
            plan.Warnings.Add("Key schedules list key rows, not elements: filters and sorting apply to the key rows.");

        AddFilters(plan, spec.Filters, catalog, ref counter, replace: false);
        AddSorts(plan, spec.Sort, catalog, ref counter, replace: false);

        plan.Actions.Add(new ScheduleAction
        {
            Kind = ScheduleActionKind.SetItemized, Flag = spec.Itemize,
            Description = spec.Itemize ? "Itemize every instance" : "Do not itemize (one row per group)"
        });
        if (!spec.Itemize && spec.Sort.Count == 0)
            plan.Warnings.Add("itemize=false without sorting puts every element on one row.");

        if (spec.GrandTotal != null) AddGrandTotal(plan, spec.GrandTotal);

        if (!string.IsNullOrWhiteSpace(spec.Template))
            plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.SetTemplate, Text = spec.Template, Description = $"View template '{spec.Template}'" });
        if (!string.IsNullOrWhiteSpace(spec.Phase))
            plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.SetPhase, Text = spec.Phase, Description = $"Phase '{spec.Phase}'" });

        if (plan.Fields.Count == 0 && !spec.KeySchedule && plan.Errors.Count == 0)
            plan.Errors.Add("None of the requested fields could be resolved, so the schedule would be empty.");
        return plan;
    }

    // ── Edit ────────────────────────────────────────────────────────────────

    public static SchedulePlan PlanEdit(
        ScheduleEditSpec edit, IReadOnlyList<ExistingFieldInfo> current, IReadOnlyList<SchedulableFieldInfo> catalog, bool isKeySchedule)
    {
        var plan = new SchedulePlan { AllowMissingFields = edit.AllowMissingFields };
        foreach (var field in current.OrderBy(f => f.Index))
        {
            plan.Fields.Add(new PlannedField
            {
                Key = "#" + field.FieldId.ToString(CultureInfo.InvariantCulture),
                Name = field.Name,
                Heading = field.Heading,
                Source = "existing",
                ExistingFieldId = field.FieldId,
                OriginalIndex = field.Index,
                FieldType = field.FieldType,
                ParameterId = field.ParameterId,
                Hidden = field.Hidden
            });
        }
        plan.OrderBefore.AddRange(plan.Fields.Select(f => f.Display));

        if (edit.IsEmpty)
        {
            plan.Errors.Add("Nothing to change: pass addFields, removeFields, order, rename, hide, show, format, widths, setFilters, setSort, itemize or grandTotal.");
            return plan;
        }
        if (isKeySchedule && edit.ChangesStructure)
        {
            plan.Errors.Add("This is a key schedule: only its fields can be edited here (add, remove, order, rename, hide, show, format, widths), not filters, sorting, itemize or grand totals.");
            plan.ErrorStatus = "schedule_kind_unsupported";
            return plan;
        }

        // Remove first, so a name added back in the same edit is the new field.
        foreach (var name in edit.RemoveFields)
        {
            var target = FindByNameOrIndex(plan.Fields, name);
            if (target == null)
            {
                plan.MissingFields.Add(Missing(name, "remove", plan.Fields.Select(f => f.Heading ?? f.Name)));
                continue;
            }
            plan.Fields.Remove(target);
            plan.Actions.Add(new ScheduleAction
            {
                Kind = ScheduleActionKind.RemoveField, FieldKey = target.Key, ExistingFieldId = target.ExistingFieldId,
                Description = $"Remove '{target.Heading ?? target.Name}'"
            });
        }

        var counter = 0;
        foreach (var field in edit.AddFields)
            AddRequestedField(plan, field, catalog, ref counter, field.InsertAt);
        ResolvePercentages(plan, edit.AddFields);

        foreach (var (name, heading) in edit.Rename)
            WithField(plan, name, "rename", field =>
            {
                field.Heading = heading;
                plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.SetHeading, FieldKey = field.Key, Text = heading, Description = $"Heading of '{field.Name}' → '{heading}'" });
            });
        foreach (var name in edit.Hide)
            WithField(plan, name, "hide", field =>
            {
                field.Hidden = true;
                plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.SetHidden, FieldKey = field.Key, Flag = true, Description = $"Hide '{field.Heading ?? field.Name}'" });
            });
        foreach (var name in edit.Show)
            WithField(plan, name, "show", field =>
            {
                field.Hidden = false;
                plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.SetHidden, FieldKey = field.Key, Flag = false, Description = $"Show '{field.Heading ?? field.Name}'" });
            });
        foreach (var (name, format) in edit.Format)
            WithField(plan, name, "format", field =>
                plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.SetFormat, FieldKey = field.Key, Format = format, Description = $"Format of '{field.Heading ?? field.Name}': {format}" }));
        foreach (var (name, width) in edit.Widths)
            WithField(plan, name, "width", field =>
                plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.SetWidth, FieldKey = field.Key, Number = width, Description = $"Width of '{field.Heading ?? field.Name}' → {Mm(width)} mm" }));

        if (edit.Order.Count > 0)
            Reorder(plan, edit.Order);

        if (edit.SetFilters != null) AddFilters(plan, edit.SetFilters, catalog, ref counter, replace: true);
        if (edit.SetSort != null) AddSorts(plan, edit.SetSort, catalog, ref counter, replace: true);

        if (edit.Itemize.HasValue)
            plan.Actions.Add(new ScheduleAction
            {
                Kind = ScheduleActionKind.SetItemized, Flag = edit.Itemize.Value,
                Description = edit.Itemize.Value ? "Itemize every instance" : "Do not itemize (one row per group)"
            });
        if (edit.GrandTotal != null) AddGrandTotal(plan, edit.GrandTotal);

        AddOrderAction(plan);
        if (plan.Fields.Count == 0 && plan.Errors.Count == 0)
            plan.Errors.Add("The edit would remove every field; a schedule needs at least one.");
        return plan;
    }

    // ── Fields ──────────────────────────────────────────────────────────────

    private static void AddRequestedField(SchedulePlan plan, ScheduleFieldSpec spec, IReadOnlyList<SchedulableFieldInfo> catalog, ref int counter, int? insertAt)
    {
        PlannedField? planned = null;
        switch (spec.Kind)
        {
            case ScheduleFieldKind.Formula:
                plan.MissingFields.Add(new MissingField
                {
                    Name = spec.Label,
                    Role = "formula",
                    Reason = "The Revit API cannot create calculated (formula) fields; add it in Revit's Fields dialog after the schedule is created."
                });
                return;

            case ScheduleFieldKind.Count:
            {
                if (plan.Fields.Any(f => f.FieldType == "Count"))
                {
                    plan.Warnings.Add("Count is listed twice; it is added once.");
                    return;
                }
                var entry = catalog.FirstOrDefault(c => c.FieldType == "Count");
                planned = new PlannedField { Key = NewKey(ref counter), Name = entry?.Name ?? "Count", FieldType = "Count", ParameterId = entry?.ParameterId ?? -1 };
                plan.Actions.Add(entry != null
                    ? new ScheduleAction { Kind = ScheduleActionKind.AddField, FieldKey = planned.Key, FieldType = entry.FieldType, ParameterId = entry.ParameterId, Description = $"Add field '{planned.Name}'" }
                    : new ScheduleAction { Kind = ScheduleActionKind.AddCount, FieldKey = planned.Key, Description = "Add field 'Count'" });
                break;
            }

            case ScheduleFieldKind.Percentage:
                planned = new PlannedField { Key = NewKey(ref counter), Name = spec.Heading ?? $"% {spec.PercentageOf}", FieldType = "Percentage", ParameterId = -1 };
                plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.AddPercentage, FieldKey = planned.Key, Text = spec.PercentageOf, Description = $"Add a percentage of '{spec.PercentageOf}'" });
                break;

            default:
            {
                var entry = ResolveCatalog(plan, spec.Parameter, spec.FieldType, catalog, "field");
                if (entry == null) return;
                var duplicate = plan.Fields.FirstOrDefault(f => f.FieldType == entry.FieldType && f.ParameterId == entry.ParameterId && f.FieldType != "Percentage");
                if (duplicate != null)
                {
                    plan.Warnings.Add($"'{spec.Parameter}' is already a field of the schedule ('{duplicate.Heading ?? duplicate.Name}'); it is not added twice.");
                    return;
                }
                planned = new PlannedField { Key = NewKey(ref counter), Name = entry.Name, FieldType = entry.FieldType, ParameterId = entry.ParameterId };
                plan.Actions.Add(new ScheduleAction
                {
                    Kind = ScheduleActionKind.AddField, FieldKey = planned.Key, FieldType = entry.FieldType, ParameterId = entry.ParameterId,
                    Description = $"Add field '{entry.QualifiedName}'"
                });
                break;
            }
        }

        if (insertAt.HasValue)
            plan.Fields.Insert(Math.Max(0, Math.Min(insertAt.Value, plan.Fields.Count)), planned);
        else
            plan.Fields.Add(planned);
        ApplyFieldSettings(plan, planned, spec);
    }

    private static void ApplyFieldSettings(SchedulePlan plan, PlannedField field, ScheduleFieldSpec spec)
    {
        if (!string.IsNullOrEmpty(spec.Heading))
        {
            field.Heading = spec.Heading;
            plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.SetHeading, FieldKey = field.Key, Text = spec.Heading, Description = $"Heading '{spec.Heading}'" });
        }
        if (spec.WidthMm.HasValue)
            plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.SetWidth, FieldKey = field.Key, Number = spec.WidthMm, Description = $"Width {Mm(spec.WidthMm.Value)} mm" });
        if (spec.Hidden == true)
        {
            field.Hidden = true;
            plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.SetHidden, FieldKey = field.Key, Flag = true, Description = "Hidden" });
        }
        if (spec.Totals.HasValue)
            plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.SetTotals, FieldKey = field.Key, Flag = spec.Totals, Description = spec.Totals.Value ? "Calculate totals" : "No totals" });
        if (spec.Format != null)
            plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.SetFormat, FieldKey = field.Key, Format = spec.Format, Description = $"Format: {spec.Format}" });
    }

    /// <summary>Percentage fields name the field they are of; resolve it once every field is planned.</summary>
    private static void ResolvePercentages(SchedulePlan plan, IEnumerable<ScheduleFieldSpec> specs)
    {
        foreach (var action in plan.Actions.Where(a => a.Kind == ScheduleActionKind.AddPercentage && a.OfFieldKey == null).ToList())
        {
            var of = FindPlanned(plan, action.Text ?? string.Empty);
            if (of == null || of.FieldType is "Count" or "Percentage")
            {
                plan.MissingFields.Add(new MissingField
                {
                    Name = action.Text ?? string.Empty, Role = "percentage",
                    Reason = of == null ? "A percentage needs a numeric field of this schedule to be of." : "Percentages cannot be of Count or of another percentage.",
                    Suggestions = Suggest(action.Text ?? string.Empty, plan.Fields.Where(f => f.FieldType is not ("Count" or "Percentage")).Select(f => f.Heading ?? f.Name))
                });
                plan.Actions.Remove(action);
                plan.Fields.RemoveAll(f => f.Key == action.FieldKey);
                plan.Actions.RemoveAll(a => a.FieldKey == action.FieldKey);
                continue;
            }
            action.OfFieldKey = of.Key;
        }
    }

    private static SchedulableFieldInfo? ResolveCatalog(SchedulePlan plan, string name, string? fieldType, IReadOnlyList<SchedulableFieldInfo> catalog, string role)
    {
        var wanted = ScheduleFieldNames.NormalizeFieldType(fieldType);
        var pool = wanted == null ? catalog : catalog.Where(c => string.Equals(c.FieldType, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        var trimmed = name.Trim();

        var hits = pool.Where(c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(c.QualifiedName, trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0)
        {
            var normalized = ParameterMatcher.Normalize(trimmed);
            hits = pool.Where(c => ParameterMatcher.Normalize(c.Name) == normalized || ParameterMatcher.Normalize(c.QualifiedName) == normalized).ToList();
        }

        if (hits.Count == 0)
        {
            plan.MissingFields.Add(Missing(trimmed, role, catalog.Select(c => c.QualifiedName),
                wanted != null && catalog.Any(c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                    ? $"'{trimmed}' exists, but not as a {wanted} field."
                    : null));
            return null;
        }

        // An exact qualified-name hit beats a plain-name hit; then instance before type before related elements.
        var best = hits
            .OrderBy(c => string.Equals(c.QualifiedName, trimmed, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => ScheduleFieldNames.Preference(c.FieldType))
            .ThenBy(c => c.Index)
            .First();
        var others = hits.Where(c => !ReferenceEquals(c, best)).Select(c => $"{c.QualifiedName} ({c.FieldType})").Distinct().ToList();
        if (others.Count > 0)
            plan.Warnings.Add($"'{trimmed}' matches several fields; used '{best.QualifiedName}' ({best.FieldType}). Others: {string.Join(", ", others)} — name one of them (or pass fieldType) to pick it.");
        return best;
    }

    // ── Filters and sorting ─────────────────────────────────────────────────

    private static void AddFilters(SchedulePlan plan, List<ScheduleFilterSpec> filters, IReadOnlyList<SchedulableFieldInfo> catalog, ref int counter, bool replace)
    {
        if (replace)
            plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.ClearFilters, Description = filters.Count == 0 ? "Remove every filter" : "Replace the filters" });
        if (filters.Count > MaxFilters)
            plan.Errors.Add($"{filters.Count} filters given; Revit allows at most {MaxFilters} per schedule.");

        foreach (var filter in filters)
        {
            if (!ScheduleFilterTypeMap.TryMap(filter.Type, out var type))
            {
                plan.Errors.Add($"Filter on '{filter.Field}': unknown type '{filter.Type}'. Use one of: {ScheduleFilterTypeMap.AcceptedText}.");
                continue;
            }
            if (ScheduleFilterTypeMap.NeedsValue(type) && !filter.HasValue)
            {
                plan.Errors.Add($"Filter on '{filter.Field}' ({ScheduleFilterTypeMap.ToAlias(type)}) needs a value.");
                continue;
            }
            var field = FieldForFilterOrSort(plan, filter.Field, catalog, ref counter, "filter");
            if (field == null) continue;
            if (field.FieldType is "Count" or "Percentage")
            {
                plan.Errors.Add($"Revit cannot filter on '{field.Heading ?? field.Name}' ({field.FieldType}).");
                continue;
            }
            plan.Actions.Add(new ScheduleAction
            {
                Kind = ScheduleActionKind.AddFilter, FieldKey = field.Key, FilterType = type, Filter = filter,
                Description = $"Filter: {field.Heading ?? field.Name} {ScheduleFilterTypeMap.ToAlias(type)}" + (filter.HasValue ? $" '{filter.Value}'" : string.Empty)
            });
        }
    }

    private static void AddSorts(SchedulePlan plan, List<ScheduleSortSpec> sorts, IReadOnlyList<SchedulableFieldInfo> catalog, ref int counter, bool replace)
    {
        if (replace)
            plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.ClearSort, Description = sorts.Count == 0 ? "Remove every sorting/grouping level" : "Replace the sorting/grouping" });

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sort in sorts)
        {
            var field = FieldForFilterOrSort(plan, sort.Field, catalog, ref counter, "sort");
            if (field == null) continue;
            if (field.FieldType is "Count" or "Percentage")
            {
                plan.Errors.Add($"Revit cannot sort by '{field.Heading ?? field.Name}' ({field.FieldType}).");
                continue;
            }
            if (!seen.Add(field.Key))
            {
                plan.Warnings.Add($"Sorting by '{field.Heading ?? field.Name}' is listed twice; the second level is dropped.");
                continue;
            }
            var extras = new List<string>();
            if (sort.Header) extras.Add("header");
            if (sort.Footer) extras.Add("footer");
            if (sort.BlankLine) extras.Add("blank line");
            plan.Actions.Add(new ScheduleAction
            {
                Kind = ScheduleActionKind.AddSort, FieldKey = field.Key, Sort = sort,
                Description = $"Sort by {field.Heading ?? field.Name} {(sort.Descending ? "descending" : "ascending")}" +
                              (extras.Count > 0 ? $", with {string.Join(", ", extras)}" : string.Empty)
            });
        }
    }

    /// <summary>A field of the plan, or a catalogue field added hidden: Revit filters and sorts only by schedule fields.</summary>
    private static PlannedField? FieldForFilterOrSort(SchedulePlan plan, string name, IReadOnlyList<SchedulableFieldInfo> catalog, ref int counter, string role)
    {
        var field = FindPlanned(plan, name);
        if (field != null) return field;

        var missingBefore = plan.MissingFields.Count;
        var entry = ResolveCatalog(plan, name, null, catalog, role);
        if (entry == null)
        {
            // Suggest the schedule's own fields first: they are what the name most likely meant.
            if (plan.MissingFields.Count > missingBefore)
            {
                var missing = plan.MissingFields[plan.MissingFields.Count - 1];
                missing.Suggestions = Suggest(name, plan.Fields.Select(f => f.Heading ?? f.Name).Concat(missing.Suggestions)).Take(5).ToList();
            }
            return null;
        }

        var added = new PlannedField
        {
            Key = NewKey(ref counter), Name = entry.Name, FieldType = entry.FieldType, ParameterId = entry.ParameterId,
            Hidden = true, AddedForFilterOrSort = true
        };
        plan.Fields.Add(added);
        plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.AddField, FieldKey = added.Key, FieldType = entry.FieldType, ParameterId = entry.ParameterId, Description = $"Add field '{entry.QualifiedName}' (hidden, for the {role})" });
        plan.Actions.Add(new ScheduleAction { Kind = ScheduleActionKind.SetHidden, FieldKey = added.Key, Flag = true, Description = "Hidden" });
        plan.Warnings.Add($"'{entry.QualifiedName}' is not a column of the schedule; it is added as a hidden field so the {role} can use it.");
        return added;
    }

    private static void AddGrandTotal(SchedulePlan plan, string choice)
    {
        if (!ScheduleGrandTotal.TryMap(choice, out var show, out var title, out var count))
        {
            plan.Errors.Add($"grandTotal '{choice}' is not one of: {ScheduleGrandTotal.AcceptedText}.");
            return;
        }
        plan.Actions.Add(new ScheduleAction
        {
            Kind = ScheduleActionKind.SetGrandTotal, Flag = show, GrandTotalTitle = title, GrandTotalCount = count,
            Description = $"Grand total: {ScheduleGrandTotal.Describe(show, title, count)}"
        });
    }

    // ── Order ───────────────────────────────────────────────────────────────

    private static void Reorder(SchedulePlan plan, List<string> order)
    {
        var listed = new List<PlannedField>();
        foreach (var name in order)
        {
            var field = FindPlanned(plan, name);
            if (field == null)
            {
                plan.MissingFields.Add(Missing(name, "order", plan.Fields.Select(f => f.Heading ?? f.Name)));
                continue;
            }
            if (!listed.Contains(field)) listed.Add(field);
        }
        var rest = plan.Fields.Where(f => !listed.Contains(f)).ToList();
        if (rest.Count > 0 && listed.Count > 0)
            plan.Warnings.Add($"order does not list {string.Join(", ", rest.Select(f => $"'{f.Heading ?? f.Name}'"))}; they follow the listed fields in their current order.");
        plan.Fields.Clear();
        plan.Fields.AddRange(listed);
        plan.Fields.AddRange(rest);
    }

    /// <summary>
    /// Fields added by AddField land at the end; existing ones keep their place. When the planned order differs from
    /// that natural order, one SetFieldOrder after the field changes puts every field where the plan says.
    /// </summary>
    private static void AddOrderAction(SchedulePlan plan)
    {
        // Existing fields in their current order, then the new fields in the order they are added.
        var natural = plan.Fields.Where(f => f.Source == "existing").OrderBy(f => f.OriginalIndex).Select(f => f.Key).ToList();
        natural.AddRange(plan.Actions.Where(a => a.Kind is ScheduleActionKind.AddField or ScheduleActionKind.AddCount or ScheduleActionKind.AddPercentage)
            .Select(a => a.FieldKey).Where(k => plan.Fields.Any(f => f.Key == k)));
        var wanted = plan.Fields.Select(f => f.Key).ToList();
        if (natural.SequenceEqual(wanted, StringComparer.Ordinal)) return;

        // Field-setting actions refer to keys, so the order can come after all of them, before filters and sorting.
        var index = plan.Actions.FindIndex(a => a.Kind is ScheduleActionKind.ClearFilters or ScheduleActionKind.AddFilter or ScheduleActionKind.ClearSort or ScheduleActionKind.AddSort or ScheduleActionKind.SetItemized or ScheduleActionKind.SetGrandTotal);
        var action = new ScheduleAction { Kind = ScheduleActionKind.SetFieldOrder, Order = wanted, Description = "Field order: " + string.Join(", ", plan.Fields.Select(f => f.Heading ?? f.Name)) };
        if (index < 0) plan.Actions.Add(action);
        else plan.Actions.Insert(index, action);
    }

    // ── Lookup helpers ──────────────────────────────────────────────────────

    /// <summary>A planned field by heading, name or qualified name; exact (any case) first, then normalized.</summary>
    public static PlannedField? FindPlanned(SchedulePlan plan, string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return null;
        var exact = plan.Fields.FirstOrDefault(f => string.Equals(f.Heading, trimmed, StringComparison.OrdinalIgnoreCase))
                    ?? plan.Fields.FirstOrDefault(f => string.Equals(f.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                    ?? plan.Fields.FirstOrDefault(f => string.Equals(f.QualifiedName, trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var normalized = ParameterMatcher.Normalize(trimmed);
        return plan.Fields.FirstOrDefault(f => ParameterMatcher.Normalize(f.Heading ?? string.Empty) == normalized)
               ?? plan.Fields.FirstOrDefault(f => ParameterMatcher.Normalize(f.Name) == normalized)
               ?? plan.Fields.FirstOrDefault(f => ParameterMatcher.Normalize(f.QualifiedName) == normalized);
    }

    private static PlannedField? FindByNameOrIndex(List<PlannedField> fields, string nameOrIndex)
    {
        if (int.TryParse(nameOrIndex.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            return index >= 0 && index < fields.Count ? fields[index] : null;
        var probe = new SchedulePlan();
        probe.Fields.AddRange(fields);
        return FindPlanned(probe, nameOrIndex);
    }

    private static void WithField(SchedulePlan plan, string name, string role, Action<PlannedField> apply)
    {
        var field = FindPlanned(plan, name);
        if (field == null)
        {
            plan.MissingFields.Add(Missing(name, role, plan.Fields.Select(f => f.Heading ?? f.Name)));
            return;
        }
        apply(field);
    }

    private static MissingField Missing(string name, string role, IEnumerable<string> candidates, string? reason = null) => new()
    {
        Name = name,
        Role = role,
        Reason = reason,
        Suggestions = Suggest(name, candidates)
    };

    /// <summary>The nearest names: containment of the normalized name, then edit-distance similarity (FuzzyNameMatcher).</summary>
    public static List<string> Suggest(string name, IEnumerable<string> candidates, int max = 5)
    {
        var normalized = ParameterMatcher.Normalize(name);
        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c =>
            {
                var cn = ParameterMatcher.Normalize(c);
                var score = FuzzyNameMatcher.Similarity(normalized, cn);
                if (normalized.Length > 0 && cn.Length > 0 && (cn.Contains(normalized) || normalized.Contains(cn)))
                    score = Math.Max(score, 0.8 + 0.1 * Math.Min(cn.Length, normalized.Length) / Math.Max(cn.Length, normalized.Length));
                return (Name: c, Score: score);
            })
            .Where(x => x.Score >= 0.5)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.Name)
            .ToList();
    }

    /// <summary>The candidate, or "candidate 1", "candidate 2", … — the rule revit_duplicate uses for view names.</summary>
    public static string UniqueName(string candidate, ICollection<string> taken)
    {
        bool Taken(string name) => taken.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
        if (!Taken(candidate)) return candidate;
        for (var index = 1; ; index++)
        {
            var resolved = $"{candidate} {index}";
            if (!Taken(resolved)) return resolved;
        }
    }

    private static string NewKey(ref int counter) => "new:" + (++counter).ToString(CultureInfo.InvariantCulture);

    private static string Mm(double value) => Math.Round(value, 1).ToString(CultureInfo.InvariantCulture);
}
