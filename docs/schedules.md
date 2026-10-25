# Schedules

Read a schedule's definition and the rows it shows, create schedules from a specification, edit
them, put them on sheets and export them. A Revit schedule updates with the model and sits on a
sheet, so it is the deliverable for device lists, socket lists and cable lists — not an Excel
file assembled from a query.

Panel schedules (`PanelScheduleView`) are a different API and are not handled here; the
[PanelSchematics tools](panelschematics.md) cover the company's panel tables. Material takeoffs,
embedded schedules, conditional formatting and appearance (fonts, lines) beyond column widths
are out of scope.

## Tools

| Tool | Permission | Purpose |
|---|---|---|
| `revit_get_schedule` | Read-only | The definition (fields, filters, sorting/grouping, itemize, grand total, template, phase, sheets) and, with `include=rows`, the table as Revit displays it, paged |
| `revit_list_schedulable_fields` | Read-only | The fields a schedule of a category can show — the names the other tools accept |
| `revit_preview_create_schedule` | Read-only | Resolve a spec and build it in a rolled-back trial run |
| `revit_create_schedule` | Requires approval | Create the schedule |
| `revit_preview_edit_schedule` | Read-only | Field order before and after, the steps, a rolled-back trial run |
| `revit_edit_schedule` | Requires approval | Add, remove, reorder, rename, hide, format fields; replace filters and sorting |
| `revit_place_schedule_on_sheet` | Requires approval | `ScheduleSheetInstance.Create` at a point or next to a viewport |
| `revit_export_schedule` | Requires approval (writes a file) | Revit's own CSV/TXT export, or an Excel workbook of the table |

`revit_list_schedules` still lists the schedules of the model. `revit_place_views_on_sheets`
now also takes schedule ids: they go on as schedule instances, centred on the title block like a
viewport (`Viewport.Create` refuses schedules, which used to make it fail).

All tools take `document` (title or path, empty = active) like the other model tools. Every
`schedule` argument is a schedule's name or element id.

## Worked example: a socket list for level 2

### 1. Find the field names

```json
{ "tool": "revit_list_schedulable_fields", "category": "Electrical Fixtures", "nameFilter": "room" }
```

Fields of related elements carry their prefix in `qualifiedName` — `Room: Name`,
`Room: Number`, `From Room: Number` — and that is the name to use. When no schedule of the
category exists yet, the tool makes one in a transaction that is rolled back; the model is
unchanged.

### 2. Preview, then create

```json
{
  "tool": "revit_preview_create_schedule",
  "name": "EL - Socket list L2",
  "category": "Electrical Fixtures",
  "fields": [
    "Mark",
    { "parameter": "Type", "width": 40 },
    "Level",
    { "parameter": "Room: Name", "heading": "Room" },
    { "parameter": "Comments", "heading": "Notes", "width": 60 },
    { "count": true, "heading": "Qty" }
  ],
  "filters": [ { "field": "Level", "type": "equals", "value": "Level 2" } ],
  "sort": [ { "field": "Room", "header": true, "footer": true }, "Mark" ],
  "itemize": true,
  "grandTotal": "titleCountTotals"
}
```

The preview resolves every field against the category's catalogue and builds the schedule in a
transaction it rolls back. It returns:

- `plan.fields` — the columns in order with the field type and parameter id each name resolved to;
- `plan.steps` — every definition change, in order (`Add field 'Mark'`, `Heading 'Room'`,
  `Filter: Level equals 'Level 2'`, `Sort by Room ascending, with header, footer`, …);
- `plan.missingFields` — names that do not resolve, each with the nearest names;
- `trialRun` — the fields as Revit created them, `rowCount`, `columns` and the first rows, and
  how each filter value was read (`Level 'Level 2' (id 311)`);
- warnings — for example when a name exists both as an instance and a type parameter, which one
  was used.

When everything resolves, run `revit_create_schedule` with the same arguments. The result has
`scheduleId`, the fields as created and `rowCount`. Compare `rowCount` with
`revit_count_elements` for the category and level to check the filter.

### 3. Read the rows back

```json
{ "tool": "revit_get_schedule", "schedule": "EL - Socket list L2", "include": "rows", "page": 1, "pageSize": 100 }
```

`table.rows` are the data rows as Revit displays them (`GetCellText`: units, formatting, group
headers and footers, the grand total). `table.columns` are the visible fields' headings and
`table.headingRows` the heading rows of the table. Rows are paged (`pageSize` up to 500;
`totalRows`, `totalPages`, `hasMore`).

Revit does not say which element a row is. `elementIds` is returned only when the schedule is
itemized and its rows are exactly its elements (no group headers, footers, blank lines or grand
total in between) — and then as the set of elements, not in row order. Otherwise
`elementIdsNote` says why it is missing.

### 4. Put it on the sheet next to the plan

```json
{
  "tool": "revit_place_schedule_on_sheet",
  "schedule": "EL - Socket list L2",
  "sheet": "EL-201",
  "nextTo": { "viewportId": 2231457, "side": "right", "gapMm": 10 }
}
```

`viewportId` may be the viewport (`revit_get_sheet_viewports`), the plan view placed on the sheet,
or another placed schedule. `side` is `right` (tops aligned), `below` (left edges aligned),
`left` or `above`. Instead of `nextTo`, `xMm`/`yMm` put the table's top-left corner at a sheet
point; with neither, it is centred on the title block. After Revit places the table the tool
measures it and moves it so its corner is exactly where it was planned, then reports overlaps
with other viewports or schedules and a table that leaves the title block. A schedule already on
the sheet is refused unless `allowDuplicate=true`.

### 5. Export

```json
{ "tool": "revit_export_schedule", "schedule": "EL - Socket list L2", "format": "csv", "delimiter": ";" }
{ "tool": "revit_export_schedule", "schedule": "EL - Socket list L2", "format": "xlsx" }
```

CSV and TXT (tab-separated) use Revit's own `ViewSchedule.Export` with `title` and
`headersFootersBlanks` (both default true). XLSX writes the table as displayed: title, bold
column headings, headings frozen. Files go to `folder` (default
`Documents\RKTools\RevitMCP\Exports`), which must be allowed by the file access policy
(`FileAccessPolicy.json`); `fileName` defaults to the schedule name, and an existing file is
never overwritten — the name gets a timestamp.

### 6. Edit: add a field and a filter

```json
{
  "tool": "revit_preview_edit_schedule",
  "schedule": "EL - Socket list L2",
  "addFields": [ { "parameter": "Circuit Number", "heading": "Circuit", "insertAt": 1 } ],
  "setFilters": [
    { "field": "Level", "type": "equals", "value": "Level 2" },
    { "field": "Comments", "type": "notContains", "value": "spare" }
  ]
}
```

The preview returns `fieldsBefore` (`Mark, Type, Level, Room, Notes, Qty`) and `fieldsAfter`
(`Mark, Circuit, Type, Level, Room, Notes, Qty`), the steps and a trial run with the row count
after the edit. `setFilters` replaces every filter, so the level filter is repeated; `[]` removes
them all. Then run `revit_edit_schedule` with the same arguments.

## Create spec

| Argument | Meaning |
|---|---|
| `name` | Schedule name; made unique (`name 1`, `name 2`, …). Default: Revit's default name for the category |
| `category` | Category name as Revit shows it. Not with `multiCategory` |
| `multiCategory` | A multi-category schedule |
| `keySchedule`, `keyName` | A key schedule of the category and the name of its key parameter |
| `fields` | Columns in order (below) |
| `filters` | Up to 8 `{field, type, value}` (below) |
| `sort` | Sorting/grouping levels in order: field names, or `{field, descending, header, footer, blankLine}` |
| `itemize` | One row per instance (default `true`); `false` groups rows by the sort fields |
| `grandTotal` | `none`, `title` (title and totals), `count` (count and totals), `totals` (totals only), `titleCountTotals`. Default: none |
| `template` | Name of a schedule view template to apply |
| `phase` | Phase name |
| `allowMissingFields` | Build without what does not resolve instead of refusing |

### Fields

A field is a name or an object:

| Shape | Meaning |
|---|---|
| `"Mark"`, `"Room: Name"` | A field by its name in `revit_list_schedulable_fields` (case and spaces, `-`, `_` ignored when there is no exact match) |
| `{ "parameter": "Comments", "fieldType": "type" }` | `fieldType` (`instance`, `type`, `room`, `fromRoom`, …) picks among same-named fields. Without it the instance field wins, then the type field, and the choice is reported |
| `"Count"` or `{ "count": true }` | The count column |
| `{ "percentage": { "of": "Apparent Load" }, "heading": "Share" }` | A percentage of a numeric field of the schedule |
| `heading` | Column heading |
| `width` | Column width on the sheet, mm |
| `hidden` | Hidden column (still usable by filters and sorting) |
| `totals` | Calculate totals (`DisplayType = Totals`) |
| `format` | `{ "units": "mm", "accuracy": 0.1, "suppressTrailingZeros": true }` for numbers with units. Units: `mm`, `cm`, `m`, `m2`, `m3`, `W`, `kW`, `VA`, `kVA`, `A`, `V`, `Hz`, `lx`, `deg`, `%`, or a `UnitTypeId` name |

A filter or sort on a field that is not one of the columns adds it as a hidden field, since Revit
filters and sorts only by schedule fields.

**Formula fields cannot be created.** The Revit API has no way to set a calculated value's
formula; a `{ "formula": … }` field is reported under `missingFields` with that reason. Add it in
Revit's Fields dialog after the schedule exists.

### Filters

| `type` | Also accepted |
|---|---|
| `equals` | `=`, `eq`, `is` |
| `notEquals` | `!=`, `<>`, `ne` |
| `greaterThan`, `greaterThanOrEqual`, `lessThan`, `lessThanOrEqual` | `>`, `>=`, `<`, `<=`, `gt`, `gte`, `lt`, `lte` |
| `contains`, `notContains`, `beginsWith`, `notBeginsWith`, `endsWith`, `notEndsWith` | `startsWith`, `doesNotContain`, … (text fields only) |
| `hasValue`, `hasNoValue`, `hasParameter` | `isEmpty`, `notEmpty`, `exists` (no value) |

The value is read the way the field holds it: text as text; numbers with units in mm for lengths
and in the project's display unit otherwise (`2,5` and `2.5` both work); yes/no fields take
`yes`/`no`/`true`/`false`/`1`/`0`; fields that refer to an element (Level, Phase, a type) take the
element's name or id. How each value was read comes back in the preview's notes.

## Edit spec

| Argument | Meaning |
|---|---|
| `addFields` | Same shapes as `fields`, each with an optional `insertAt` (0-based position in the result) |
| `removeFields` | Names or 0-based indexes |
| `order` | Names in the new order; fields not listed follow in their current order |
| `rename` | `{ "field": "new heading" }` |
| `hide`, `show` | Field names |
| `format` | `{ "field": { "units", "accuracy", "suppressTrailingZeros" } }` |
| `widths` | `{ "field": mm }` |
| `setFilters` | Replaces every filter; `[]` removes them. Omitted: filters unchanged |
| `setSort` | Replaces the sorting/grouping; `[]` removes it. Omitted: unchanged |
| `itemize`, `grandTotal` | As in the create spec; omitted: unchanged |
| `allowMissingFields` | Apply the rest when some names do not resolve |

Names are matched against the headings, then the field names. Key schedules can have their fields
edited, not their filters, sorting, itemize or grand total. View templates, title-block revision
schedules, the internal keynote schedule, embedded and panel schedules are refused
(`schedule_kind_unsupported`).

## All or nothing

`revit_create_schedule` and `revit_edit_schedule` run in one transaction. A name that does not
resolve, or a step Revit refuses (a filter Revit does not accept for that field, a unit that does
not fit the field, a template that does not exist), rolls the whole transaction back: no
half-finished schedule is left in the model. `allowMissingFields=true` keeps what works and lists
the rest. The previews run the same steps in a transaction that is always rolled back, so they
show what Revit itself would refuse.

## Status codes

| Status | When |
|---|---|
| `schedule_not_found` | No schedule with that name or id (`data.suggestions` has the nearest names) |
| `field_not_found` | A field, filter, sort, rename, hide or order name does not resolve (`data.plan.missingFields` with `suggestions`) |
| `schedule_kind_unsupported` | A panel schedule, a template, a title-block revision schedule, or a structural edit of a key schedule |
| `validation_failed` | Bad arguments, a refused step, or a document that cannot take a transaction now |

## Revit versions

The schedule API used here (`ScheduleDefinition`, `ScheduleField`, `ScheduleFilter`,
`ScheduleSortGroupField`, `ScheduleSheetInstance.Create`, `ViewScheduleExportOptions`,
`TableView.GetCellText`) is identical in the Revit 2024 and 2026 reference assemblies, so the
tools behave the same on both.
