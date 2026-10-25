using RevitMCP.Addin.Schedules;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#103: the schedule planner resolves fields against the catalogue and emits the definition steps.</summary>
public class SchedulePlannerTests
{
    /// <summary>What GetSchedulableFields offers for Electrical Fixtures, shortened.</summary>
    private static readonly List<SchedulableFieldInfo> Catalog = new()
    {
        new() { Index = 0, Name = "Mark", FieldType = "Instance", ParameterId = -1001203 },
        new() { Index = 1, Name = "Type", FieldType = "Instance", ParameterId = -1002052 },
        new() { Index = 2, Name = "Level", FieldType = "Instance", ParameterId = -1002063 },
        new() { Index = 3, Name = "Comments", FieldType = "Instance", ParameterId = -1010106 },
        new() { Index = 4, Name = "Count", FieldType = "Count", ParameterId = -1 },
        new() { Index = 5, Name = "Name", FieldType = "Room", ParameterId = -1006918 },
        new() { Index = 6, Name = "Number", FieldType = "Room", ParameterId = -1006917 },
        new() { Index = 7, Name = "Type Mark", FieldType = "ElementType", ParameterId = -1002150 },
        new() { Index = 8, Name = "Comments", FieldType = "ElementType", ParameterId = -1010105 },
        new() { Index = 9, Name = "Apparent Load", FieldType = "Instance", ParameterId = -1140221 },
        new() { Index = 10, Name = "ELENEA_Nimetus", FieldType = "Instance", ParameterId = 412345, IsShared = true }
    };

    private static ScheduleSpec SocketList() => new()
    {
        Name = "Socket list",
        Category = "Electrical Fixtures",
        Fields =
        {
            new ScheduleFieldSpec { Parameter = "Mark" },
            new ScheduleFieldSpec { Parameter = "Type" },
            new ScheduleFieldSpec { Parameter = "Level" },
            new ScheduleFieldSpec { Parameter = "Room: Name", Heading = "Room" },
            new ScheduleFieldSpec { Parameter = "Comments" },
            new ScheduleFieldSpec { Kind = ScheduleFieldKind.Count }
        },
        Filters = { new ScheduleFilterSpec { Field = "Level", Type = "equals", Value = "Level 2" } },
        Sort = { new ScheduleSortSpec { Field = "Room", Header = true } },
        GrandTotal = "titleCountTotals"
    };

    [Fact]
    public void SocketList_ResolvesEveryFieldInOrder()
    {
        var plan = SchedulePlanner.PlanCreate(SocketList(), Catalog, new List<string>());

        Assert.True(plan.CanApply, string.Join("; ", plan.Errors));
        Assert.Empty(plan.MissingFields);
        Assert.Equal(new[] { "Mark", "Type", "Level", "Room: Name", "Comments", "Count" }, plan.Fields.Select(f => f.QualifiedName));
        Assert.Equal("Room", plan.Fields[3].Heading);
        // Comments exists as an instance and a type parameter: the instance one wins and the choice is reported.
        Assert.Equal(-1010106, plan.Fields[4].ParameterId);
        Assert.Contains(plan.Warnings, w => w.Contains("'Comments' matches several fields"));

        var kinds = plan.Actions.Select(a => a.Kind).ToList();
        Assert.Equal(6, kinds.Count(k => k == ScheduleActionKind.AddField));
        Assert.True(kinds.IndexOf(ScheduleActionKind.AddFilter) > kinds.LastIndexOf(ScheduleActionKind.AddField));
        Assert.Contains(ScheduleActionKind.SetItemized, kinds);

        var sort = plan.Actions.Single(a => a.Kind == ScheduleActionKind.AddSort);
        Assert.Equal(plan.Fields[3].Key, sort.FieldKey);   // "Room" found by its heading
        Assert.True(sort.Sort!.Header);

        var total = plan.Actions.Single(a => a.Kind == ScheduleActionKind.SetGrandTotal);
        Assert.True(total.Flag);
        Assert.True(total.GrandTotalTitle);
        Assert.True(total.GrandTotalCount);
    }

    [Fact]
    public void MissingField_FailsWithSuggestions()
    {
        var spec = SocketList();
        spec.Fields.Add(new ScheduleFieldSpec { Parameter = "Comment" });
        spec.Fields.Add(new ScheduleFieldSpec { Parameter = "Room" });

        var plan = SchedulePlanner.PlanCreate(spec, Catalog, new List<string>());

        Assert.False(plan.CanApply);
        Assert.Equal("field_not_found", plan.Status);
        var comment = plan.MissingFields.Single(m => m.Name == "Comment");
        Assert.Contains("Comments", comment.Suggestions);
        var room = plan.MissingFields.Single(m => m.Name == "Room");
        Assert.Contains("Room: Name", room.Suggestions);
        Assert.Contains("Room: Number", room.Suggestions);
    }

    [Fact]
    public void MissingField_IsSkippedWhenAllowed()
    {
        var spec = SocketList();
        spec.Fields.Add(new ScheduleFieldSpec { Parameter = "Nonexistent Parameter" });
        spec.AllowMissingFields = true;

        var plan = SchedulePlanner.PlanCreate(spec, Catalog, new List<string>());

        Assert.True(plan.CanApply);
        Assert.Equal("ok", plan.Status);
        Assert.Single(plan.MissingFields);
        Assert.Equal(6, plan.Fields.Count);
    }

    [Fact]
    public void NormalizedNamesAndFieldTypeResolve()
    {
        var spec = new ScheduleSpec
        {
            Category = "Electrical Fixtures",
            Fields =
            {
                new ScheduleFieldSpec { Parameter = "elenea nimetus" },
                new ScheduleFieldSpec { Parameter = "Comments", FieldType = "type" },
                new ScheduleFieldSpec { Parameter = "type-mark" }
            }
        };
        var plan = SchedulePlanner.PlanCreate(spec, Catalog, new List<string>());

        Assert.True(plan.CanApply);
        Assert.Equal(412345, plan.Fields[0].ParameterId);
        Assert.Equal("ElementType", plan.Fields[1].FieldType);
        Assert.Equal(-1010105, plan.Fields[1].ParameterId);
        Assert.Equal("Type Mark", plan.Fields[2].Name);
    }

    [Fact]
    public void FilterOnAFieldThatIsNoColumn_AddsItHidden()
    {
        var spec = new ScheduleSpec
        {
            Category = "Electrical Fixtures",
            Fields = { new ScheduleFieldSpec { Parameter = "Mark" } },
            Filters = { new ScheduleFilterSpec { Field = "Level", Type = "=", Value = "Level 2" } },
            Sort = { new ScheduleSortSpec { Field = "Room: Number" } }
        };
        var plan = SchedulePlanner.PlanCreate(spec, Catalog, new List<string>());

        Assert.True(plan.CanApply);
        Assert.Equal(3, plan.Fields.Count);
        Assert.True(plan.Fields[1].Hidden && plan.Fields[1].AddedForFilterOrSort);
        Assert.Equal("Level", plan.Fields[1].Name);
        Assert.True(plan.Fields[2].Hidden);
        Assert.Contains(plan.Warnings, w => w.Contains("added as a hidden field"));
        Assert.Equal("Equal", plan.Actions.Single(a => a.Kind == ScheduleActionKind.AddFilter).FilterType);
    }

    [Fact]
    public void FilterValidation_UnknownTypeMissingValueAndTooMany()
    {
        var spec = new ScheduleSpec
        {
            Category = "Electrical Fixtures",
            Fields = { new ScheduleFieldSpec { Parameter = "Mark" }, new ScheduleFieldSpec { Kind = ScheduleFieldKind.Count } },
            Filters =
            {
                new ScheduleFilterSpec { Field = "Mark", Type = "roughly", Value = "A" },
                new ScheduleFilterSpec { Field = "Mark", Type = "contains" },
                new ScheduleFilterSpec { Field = "Mark", Type = "hasValue" },
                new ScheduleFilterSpec { Field = "Count", Type = "greaterThan", Number = 1, Value = "1" }
            }
        };
        var plan = SchedulePlanner.PlanCreate(spec, Catalog, new List<string>());

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Errors, e => e.Contains("unknown type 'roughly'"));
        Assert.Contains(plan.Errors, e => e.Contains("needs a value"));
        Assert.Contains(plan.Errors, e => e.Contains("cannot filter on 'Count'"));
        Assert.Single(plan.Actions, a => a.Kind == ScheduleActionKind.AddFilter && a.FilterType == "HasValue");

        var many = new ScheduleSpec { Category = "X", Fields = { new ScheduleFieldSpec { Parameter = "Mark" } } };
        for (var i = 0; i < 9; i++) many.Filters.Add(new ScheduleFilterSpec { Field = "Mark", Type = "notEquals", Value = i.ToString() });
        Assert.Contains(SchedulePlanner.PlanCreate(many, Catalog, new List<string>()).Errors, e => e.Contains("at most 8"));
    }

    [Fact]
    public void SortValidation_DuplicateLevelsAndCount()
    {
        var spec = new ScheduleSpec
        {
            Category = "Electrical Fixtures",
            Fields = { new ScheduleFieldSpec { Parameter = "Mark" }, new ScheduleFieldSpec { Parameter = "Level" }, new ScheduleFieldSpec { Kind = ScheduleFieldKind.Count } },
            Sort =
            {
                new ScheduleSortSpec { Field = "Level", Header = true, Footer = true },
                new ScheduleSortSpec { Field = "level", Descending = true },
                new ScheduleSortSpec { Field = "Mark", Descending = true },
                new ScheduleSortSpec { Field = "Count" }
            }
        };
        var plan = SchedulePlanner.PlanCreate(spec, Catalog, new List<string>());

        var sorts = plan.Actions.Where(a => a.Kind == ScheduleActionKind.AddSort).ToList();
        Assert.Equal(2, sorts.Count);
        Assert.Contains("with header, footer", sorts[0].Description);
        Assert.Contains("descending", sorts[1].Description);
        Assert.Contains(plan.Warnings, w => w.Contains("listed twice"));
        Assert.Contains(plan.Errors, e => e.Contains("cannot sort by 'Count'"));
    }

    [Fact]
    public void DuplicateScheduleName_IsMadeUnique()
    {
        var taken = new List<string> { "Socket list", "socket list 1" };
        var plan = SchedulePlanner.PlanCreate(SocketList(), Catalog, taken);

        Assert.Equal("Socket list 2", plan.Name);
        Assert.Contains(plan.Warnings, w => w.Contains("'Socket list 2'"));
        Assert.Equal("Fresh", SchedulePlanner.UniqueName("Fresh", taken));
    }

    [Fact]
    public void FormulaFields_AreReportedAsUnsupported()
    {
        var spec = SocketList();
        spec.Fields.Add(new ScheduleFieldSpec { Kind = ScheduleFieldKind.Formula, FormulaName = "Load kW", FormulaExpression = "Apparent Load / 1000" });
        var plan = SchedulePlanner.PlanCreate(spec, Catalog, new List<string>());

        var formula = plan.MissingFields.Single();
        Assert.Equal("formula", formula.Role);
        Assert.Contains("cannot create calculated", formula.Reason);
        Assert.False(plan.CanApply);
    }

    [Fact]
    public void Percentage_ResolvesItsFieldOrFails()
    {
        var ok = new ScheduleSpec
        {
            Category = "Electrical Fixtures",
            Fields =
            {
                new ScheduleFieldSpec { Parameter = "Apparent Load" },
                new ScheduleFieldSpec { Kind = ScheduleFieldKind.Percentage, PercentageOf = "Apparent Load", Heading = "Share" }
            }
        };
        var plan = SchedulePlanner.PlanCreate(ok, Catalog, new List<string>());
        var percentage = plan.Actions.Single(a => a.Kind == ScheduleActionKind.AddPercentage);
        Assert.Equal(plan.Fields[0].Key, percentage.OfFieldKey);

        ok.Fields[1].PercentageOf = "Count";
        ok.Fields.Add(new ScheduleFieldSpec { Kind = ScheduleFieldKind.Count });
        var bad = SchedulePlanner.PlanCreate(ok, Catalog, new List<string>());
        Assert.Contains(bad.MissingFields, m => m.Role == "percentage");
        Assert.DoesNotContain(bad.Actions, a => a.Kind == ScheduleActionKind.AddPercentage);
        Assert.Equal(2, bad.Fields.Count);
    }

    [Fact]
    public void Count_IsAddedOnceFromTheCatalogue()
    {
        var spec = new ScheduleSpec
        {
            Category = "X",
            Fields = { new ScheduleFieldSpec { Kind = ScheduleFieldKind.Count }, new ScheduleFieldSpec { Kind = ScheduleFieldKind.Count } }
        };
        var plan = SchedulePlanner.PlanCreate(spec, Catalog, new List<string>());
        Assert.Single(plan.Fields);
        Assert.Equal("Count", plan.Actions.First().FieldType);
        Assert.Contains(plan.Warnings, w => w.Contains("Count is listed twice"));

        var noCatalogCount = SchedulePlanner.PlanCreate(spec, Catalog.Where(c => c.FieldType != "Count").ToList(), new List<string>());
        Assert.Equal(ScheduleActionKind.AddCount, noCatalogCount.Actions.First().Kind);
    }

    [Fact]
    public void FieldSettings_BecomeSteps()
    {
        var spec = new ScheduleSpec
        {
            Category = "X",
            Fields =
            {
                new ScheduleFieldSpec
                {
                    Parameter = "Apparent Load", Heading = "Load", WidthMm = 25, Hidden = true, Totals = true,
                    Format = new ScheduleFieldFormatSpec { Units = "kVA", Accuracy = 0.01 }
                }
            },
            Itemize = false,
            GrandTotal = "nonsense"
        };
        var plan = SchedulePlanner.PlanCreate(spec, Catalog, new List<string>());
        var kinds = plan.Actions.Select(a => a.Kind).ToList();
        Assert.Equal(new[]
        {
            ScheduleActionKind.AddField, ScheduleActionKind.SetHeading, ScheduleActionKind.SetWidth, ScheduleActionKind.SetHidden,
            ScheduleActionKind.SetTotals, ScheduleActionKind.SetFormat, ScheduleActionKind.SetItemized
        }, kinds);
        Assert.Equal(25, plan.Actions[2].Number);
        Assert.Contains(plan.Errors, e => e.Contains("grandTotal 'nonsense'"));
        Assert.Contains(plan.Warnings, w => w.Contains("itemize=false without sorting"));
    }

    // ── Edit ────────────────────────────────────────────────────────────────

    private static readonly List<ExistingFieldInfo> Current = new()
    {
        new() { Index = 0, FieldId = 10, Name = "Mark", Heading = "Mark", FieldType = "Instance", ParameterId = -1001203 },
        new() { Index = 1, FieldId = 11, Name = "Type", Heading = "Type", FieldType = "Instance", ParameterId = -1002052 },
        new() { Index = 2, FieldId = 12, Name = "Level", Heading = "Level", FieldType = "Instance", ParameterId = -1002063 },
        new() { Index = 3, FieldId = 13, Name = "Count", Heading = "Count", FieldType = "Count", ParameterId = -1 }
    };

    [Fact]
    public void Edit_AddInsertRemoveAndReorder()
    {
        var edit = new ScheduleEditSpec
        {
            AddFields = { new ScheduleFieldSpec { Parameter = "Comments", InsertAt = 1, Heading = "Notes" } },
            RemoveFields = { "Type" }
        };
        var plan = SchedulePlanner.PlanEdit(edit, Current, Catalog, isKeySchedule: false);

        Assert.True(plan.CanApply, string.Join("; ", plan.Errors));
        Assert.Equal(new[] { "Mark", "Type", "Level", "Count" }, plan.OrderBefore);
        Assert.Equal(new[] { "Mark", "Notes", "Level", "Count" }, plan.OrderAfter);
        Assert.Equal(ScheduleActionKind.RemoveField, plan.Actions[0].Kind);
        Assert.Equal(11, plan.Actions[0].ExistingFieldId);
        var order = plan.Actions.Single(a => a.Kind == ScheduleActionKind.SetFieldOrder);
        Assert.Equal(new[] { "#10", plan.Fields[1].Key, "#12", "#13" }, order.Order);
    }

    [Fact]
    public void Edit_AppendNeedsNoOrderStep()
    {
        var plan = SchedulePlanner.PlanEdit(new ScheduleEditSpec { AddFields = { new ScheduleFieldSpec { Parameter = "Comments" } } }, Current, Catalog, false);
        Assert.DoesNotContain(plan.Actions, a => a.Kind == ScheduleActionKind.SetFieldOrder);
        Assert.Equal("Comments", plan.OrderAfter.Last());
    }

    [Fact]
    public void Edit_PartialOrderKeepsTheRest()
    {
        var plan = SchedulePlanner.PlanEdit(new ScheduleEditSpec { Order = { "Level", "Mark" } }, Current, Catalog, false);
        Assert.Equal(new[] { "Level", "Mark", "Type", "Count" }, plan.OrderAfter);
        Assert.Contains(plan.Warnings, w => w.Contains("order does not list"));
        Assert.Equal(new[] { "#12", "#10", "#11", "#13" }, plan.Actions.Single(a => a.Kind == ScheduleActionKind.SetFieldOrder).Order);
    }

    [Fact]
    public void Edit_RenameHideFiltersAndSort()
    {
        var edit = new ScheduleEditSpec
        {
            Rename = { new KeyValuePair<string, string>("Mark", "Tag") },
            Hide = { "Level" },
            SetFilters = new List<ScheduleFilterSpec> { new() { Field = "Level", Type = "equals", Value = "Level 1" } },
            SetSort = new List<ScheduleSortSpec>(),
            GrandTotal = "count"
        };
        var plan = SchedulePlanner.PlanEdit(edit, Current, Catalog, false);

        Assert.True(plan.CanApply);
        Assert.Equal(new[] { "Tag", "Type", "Level (hidden)", "Count" }, plan.OrderAfter);
        var kinds = plan.Actions.Select(a => a.Kind).ToList();
        Assert.Equal(new[]
        {
            ScheduleActionKind.SetHeading, ScheduleActionKind.SetHidden, ScheduleActionKind.ClearFilters, ScheduleActionKind.AddFilter,
            ScheduleActionKind.ClearSort, ScheduleActionKind.SetGrandTotal
        }, kinds);
        Assert.Equal("#12", plan.Actions[3].FieldKey);
    }

    [Fact]
    public void Edit_UnknownNamesAreMissingWithSuggestions()
    {
        var edit = new ScheduleEditSpec { RemoveFields = { "Lvl" }, Hide = { "Mrk" }, Order = { "Typ" } };
        var plan = SchedulePlanner.PlanEdit(edit, Current, Catalog, false);

        Assert.Equal("field_not_found", plan.Status);
        Assert.Equal(new[] { "remove", "hide", "order" }, plan.MissingFields.Select(m => m.Role));
        Assert.Contains("Mark", plan.MissingFields[1].Suggestions);
    }

    [Fact]
    public void Edit_RemoveByIndexAndEmptyEdit()
    {
        var plan = SchedulePlanner.PlanEdit(new ScheduleEditSpec { RemoveFields = { "0" } }, Current, Catalog, false);
        Assert.Equal(10, plan.Actions.Single().ExistingFieldId);

        var empty = SchedulePlanner.PlanEdit(new ScheduleEditSpec(), Current, Catalog, false);
        Assert.False(empty.CanApply);
        Assert.Contains("Nothing to change", empty.Errors.Single());

        var all = SchedulePlanner.PlanEdit(new ScheduleEditSpec { RemoveFields = { "Mark", "Type", "Level", "Count" } }, Current, Catalog, false);
        Assert.Contains(all.Errors, e => e.Contains("at least one"));
    }

    [Fact]
    public void Edit_KeyScheduleAllowsFieldsOnly()
    {
        var structural = SchedulePlanner.PlanEdit(new ScheduleEditSpec { SetSort = new List<ScheduleSortSpec>() }, Current, Catalog, isKeySchedule: true);
        Assert.Equal("schedule_kind_unsupported", structural.Status);

        var fields = SchedulePlanner.PlanEdit(new ScheduleEditSpec { Hide = { "Type" } }, Current, Catalog, isKeySchedule: true);
        Assert.True(fields.CanApply);
    }

    [Fact]
    public void Edit_AddingAFieldTheScheduleHas_IsSkipped()
    {
        var plan = SchedulePlanner.PlanEdit(new ScheduleEditSpec { AddFields = { new ScheduleFieldSpec { Parameter = "Mark" } } }, Current, Catalog, false);
        Assert.Contains(plan.Warnings, w => w.Contains("already a field"));
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Suggestions_PreferContainmentAndSimilarity()
    {
        var suggestions = SchedulePlanner.Suggest("Nimetus", Catalog.Select(c => c.QualifiedName));
        Assert.Equal("ELENEA_Nimetus", suggestions.First());
        Assert.Empty(SchedulePlanner.Suggest("zzzzzz", Catalog.Select(c => c.QualifiedName)));
    }

    [Fact]
    public void QualifiedNames_FollowTheFieldType()
    {
        Assert.Equal("Room: Name", ScheduleFieldNames.Qualify("Name", "Room"));
        Assert.Equal("From Room: Number", ScheduleFieldNames.Qualify("Number", "FromRoom"));
        Assert.Equal("Mark", ScheduleFieldNames.Qualify("Mark", "Instance"));
        Assert.Equal("Room: Name", ScheduleFieldNames.Qualify("Room: Name", "Room"));
        Assert.Equal("ElementType", ScheduleFieldNames.NormalizeFieldType("Type"));
        Assert.Equal("FromRoom", ScheduleFieldNames.NormalizeFieldType("from room"));
    }
}
