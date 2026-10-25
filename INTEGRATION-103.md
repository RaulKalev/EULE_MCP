# Integration notes — #103 Schedules

## Registration (App.cs)

Add to `App.OnStartup`, next to the other registrations:

```csharp
RevitMCP.Addin.Tools.Schedules.SchedulesToolRegistration.Register(handler);
```

(`handler` is the `RevitMCP.Addin.Services.ExternalEventHandler` created there.)

## Approval summaries

`RevitMCP.Addin.Approval.SchedulesApprovalSummaries.TryBuild(McpToolRequest)` — chain it into
`ApprovalSummaryBuilder.Build`'s default case. It returns null for any tool that is not one of the four write tools below.
Pure (arguments only); linked into the tests and covered by `ScheduleSpecParsingTests.Summary_*`.

## Bridge

New file `RevitMCP.Bridge/RevitMcpTools.Schedules.cs` (partial `RevitMcpTools`, `RevitMcpTools.cs` untouched):

| Tool | Add-in permission | Bridge |
|---|---|---|
| `revit_get_schedule` | ReadOnly | `ReadOnly = true` |
| `revit_list_schedulable_fields` | ReadOnly | `ReadOnly = true` |
| `revit_preview_create_schedule` | ReadOnly | `ReadOnly = true` |
| `revit_create_schedule` | RequiresApproval | — |
| `revit_preview_edit_schedule` | ReadOnly | `ReadOnly = true` |
| `revit_edit_schedule` | RequiresApproval | — |
| `revit_place_schedule_on_sheet` | RequiresApproval | — |
| `revit_export_schedule` | RequiresApproval (writes a file) | — |

All take `document` and resolve it with `ToolDocument.Resolve`.

## BridgeToolGroups

- Exact entry `("views", new[] { "revit_list_schedulable_fields" })` — "schedulable" does not contain the `schedule`
  keyword, so without it the tool had no group. The other seven names match the `schedule` rule (→ `views`).
- `views` group description extended: "Views, sheets, schedules (read definitions and rows, create, edit, place on
  sheets, export), title blocks, revisions, view templates".

## Other shared files touched

- `RevitMCP.Tests/RevitMCP.Tests.csproj`: one new `<ItemGroup>` at the end (#103 comment). It also links
  `Documentation/Placement/FuzzyNameMatcher.cs` (the planner's suggestions use it). If another branch links the same file,
  keep one entry (a duplicate `Compile` gives CS2002).
- `README.md`: one Capabilities row (Schedules, 8 tools) and one line in the repository references list.
- `RevitMCP.Addin/Tools/PlaceViewsOnSheetsTool.cs` (owned by #103): schedule ids are routed to
  `ScheduleSheetPlacer.PlaceCentered` (ScheduleSheetInstance, centred on the title block) in all three paths (targetSheetId,
  matching, PlaceViews mode with its sub-transaction) instead of failing in `Viewport.Create`. A schedule already on the
  sheet is skipped with a warning. Other views behave exactly as before.

Not touched, possibly worth a line by the integrator: `McpToolCatalog.QueryProfileNames` (the `query` profile) could list
`revit_get_schedule` and `revit_list_schedulable_fields`; `McpToolResult.Status`'s doc comment could list the three new
status codes (`schedule_not_found`, `field_not_found`, `schedule_kind_unsupported`). `revit_preview_place_views_on_sheets`
does not know about schedules (unchanged; it only proposes matches).

## Files

Pure (linked into tests): `RevitMCP.Addin/Schedules/ScheduleSpec.cs` (DTOs + `ScheduleSpecParser`), `SchedulePlanner.cs`
(catalogue/field DTOs, plan, actions, suggestions, unique names), `ScheduleFilterTypeMap.cs` (+ `ScheduleGrandTotal`,
`ScheduleFormatUnits`), `SchedulePlacement.cs` (`SheetRect`, next-to geometry, correction), `ScheduleTableMath.cs`
(paging, heading-row detection, `ScheduleExportNaming`), `RevitMCP.Addin/Approval/SchedulesApprovalSummaries.cs`.

Revit side: `Schedules/ScheduleFieldCatalog.cs` (+ `ScheduleFieldValues`), `ScheduleLookup.cs`, `ScheduleBuilder.cs`
(executes a plan), `ScheduleDefinitionReader.cs`, `ScheduleTableReader.cs`, `ScheduleSheetPlacer.cs`.
Tools: `Tools/Schedules/GetScheduleTool.cs`, `ListSchedulableFieldsTool.cs`, `CreateScheduleTools.cs`,
`EditScheduleTools.cs`, `PlaceScheduleOnSheetTool.cs`, `ExportScheduleTool.cs`, `ScheduleToolSupport.cs`,
`SchedulesToolRegistration.cs`.

Tests: `SchedulePlannerTests`, `ScheduleFilterTypeMapTests`, `SchedulePlacementTests`, `ScheduleSpecParsingTests` (spec
parsing and defaults, paging, heading detection, export names, approval summaries). Suite: 2601 → 2702, all green.
Both targets build without warnings in the new files.

## Deviations from the issue

- **Formula fields are not created.** The Revit API has no setter for a calculated value's formula (no `Formula`
  property; `AddField(ScheduleFieldType.Formula)` cannot set the expression). A `formula` field is reported under
  `missingFields` (role `formula`) with that reason, so the preview fails unless `allowMissingFields=true`.
  `revit_get_schedule` marks existing formula fields `isCalculated` but cannot return the formula text.
- **Percentage fields** are attempted with `AddField(ScheduleFieldType.Percentage)` + `PercentageOf`. Whether Revit
  accepts adding a percentage field through `AddField` must be confirmed in the manual test; if it refuses, the step
  fails with Revit's reason (the transaction rolls back unless `allowMissingFields=true`).
- **`HasTotals`** no longer exists in either API; totals are `ScheduleField.DisplayType = Totals` (`hasTotals` in the
  read result is derived from it).
- **Filter value types.** `ScheduleField.GetSpecTypeId()` is empty for anything but numbers with units, so the value kind
  comes from the parameter's storage type (`Document.TypeOfStorage` for built-ins, `ParameterElement` data type
  otherwise). Element-id fields (Level, Phase, types) take a name, resolved to a Level, Phase or element type.
  Number values: mm for lengths, the project's display unit for other specs.
- **Element ids per row** are never paired with rows: when the schedule is itemized and its collector count equals the
  data-row count, `elementIds` is returned as the set, with a note that it is not in row order; otherwise omitted with
  the reason.
- **Column headings** are in the body section; the reader finds the heading row among the first rows by comparing with
  the visible fields' headings and drops one blank separator row after it (`ScheduleTableMath.DataStartRow`). To be
  confirmed on a real schedule in both versions (see manual test 3).
- **Place on sheet** also accepts `schedule` (name or id) next to `scheduleId`, `sheet` as number, name or id, and
  `nextTo.viewportId` as a viewport, a view placed on the sheet or a placed schedule; `side` also takes `left`/`above`.
  The table is measured after `Create` and moved so its top-left corner lands on the planned point, so the result does
  not depend on where Revit anchors the instance point.
- **Edit extras**: `format {field: {...}}` and `widths {field: mm}` were added for "format fields" (the issue listed
  formatting without an argument for it).
- **All or nothing**: besides missing names, any step Revit refuses rolls the create/edit back unless
  `allowMissingFields=true` (stricter than the issue, which only named missing fields).
- Refused kinds also include the internal keynote schedule and embedded schedules; read-only tools only refuse
  title-block revision schedules and panel schedules.
- Benchmark not run (needs Revit; the integrator runs it).

## API notes per target

`ScheduleDefinition`, `ScheduleField`, `ScheduleFilter` (all value accessors and constructors), `ScheduleSortGroupField`,
`ScheduleSheetInstance.Create(doc, sheetId, scheduleId, XYZ)`, `ViewScheduleExportOptions`, `ViewSchedule.Export`,
`TableView.GetCellText`, `FormatOptions`, `UnitUtils.IsValidUnit` and the enums (`ScheduleFieldType`, `ScheduleFilterType`,
`ScheduleSortOrder`, `ExportTextQualifier`, `ExportColumnHeaders`, `ScheduleFieldDisplayType`) were compared member by
member in the 2024.1 and 2026 reference assemblies (RevitAPI.xml): identical. No `#if` was needed.

## Manual Revit test (2026 and 2024)

1. `revit_list_schedulable_fields category="Electrical Fixtures" nameFilter=room` — `Room: Name`/`Room: Number` listed;
   the model is unchanged (no undo entry) when no Electrical Fixtures schedule existed.
2. `revit_preview_create_schedule` with the socket list of docs/schedules.md — no missing fields, the level filter note
   names the level and its id, `trialRun.rowCount` equals `revit_count_elements` for Electrical Fixtures on Level 2; the
   model is unchanged. Misspell a field (`Coments`): `field_not_found` with `Comments` suggested, nothing created.
3. `revit_create_schedule` with the same arguments; open it in Revit: field order Mark, Type, Level, Room, Notes, Qty;
   headings as given; the Level filter; sorting by Room with header and footer, then Mark; itemized; grand total with
   title, count and totals — identical to building it by hand.
4. `revit_get_schedule include=all pageSize=10` — rows equal what the schedule view shows (units/formatting), headings
   are not among the data rows, page 2 continues; check `headingRows` and that no blank row leads the data.
5. `revit_place_schedule_on_sheet` next to the plan viewport (`side=right`, then `below` on another sheet): no overlap,
   tops (or left edges) aligned, `boxMm` matches; a second call is refused without `allowDuplicate`.
6. `revit_place_views_on_sheets viewIds=[<schedule id>] targetSheetId=<sheet>` — placed as a schedule instance, centred.
7. `revit_export_schedule format=csv delimiter=";"` and `format=xlsx` into the default folder, then `folder=C:\Windows`
   (refused by the file access policy). Open both files.
8. `revit_preview_edit_schedule` adding `Circuit Number` at 1 and a second filter — `fieldsAfter` shows the new order;
   `revit_edit_schedule` applies it; a percentage field (`{percentage: {of: "Apparent Load"}}`) on an Electrical
   Equipment schedule — record whether Revit accepts it.
9. A key schedule: an edit with `setSort` is refused (`schedule_kind_unsupported`); `hide` works. A panel schedule name:
   `schedule_kind_unsupported`.
