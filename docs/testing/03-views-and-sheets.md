# Smoke tests — Views and sheets

Part of the [smoke test checklist](../../TESTING.md).

## 26. View/Sheet Discovery Tools

### 26.1 View Sheet Summary

**Prompts:**
```
Give me a view and sheet summary for this model.
How many views are unplaced?
```

**Verify:**
- `revit_get_view_sheet_summary` returns `totalSheets`, `totalViews`, `placedViews`, `unplacedViews`, `viewsWithTemplate`, `sheetsWithTitleBlock`.
- No Revit transaction is created.

---

### 26.2 List Title Blocks

**Prompts:**
```
List all title blocks loaded in the model.
Which title block should I use when creating new sheets?
```

**Verify:**
- `revit_list_titleblocks` returns `familySymbolId`, `familyName`, `typeName`, `isInUse` per entry.
- `familySymbolId` can be used directly in `revit_create_sheets_from_table`.
- No Revit transaction is created.

---

### 26.3 List View Templates

**Prompts:**
```
List all view templates.
List all floor plan view templates.
```

**Verify:**
- `revit_list_view_templates` returns `elementId`, `name`, `viewType`, `assignedViewCount`.
- Optional `viewType` filter (e.g. `"FloorPlan"`) narrows results.
- `elementId` can be used directly in `revit_apply_view_template`.
- No Revit transaction is created.

---

### 26.4 List Revisions

**Prompts:**
```
List all revisions in the model.
```

**Verify:**
- `revit_list_revisions` returns at least `elementId`, `sequenceNumber`, `revisionDate`, `description`, `issuedBy`, `visibility`.
- No Revit transaction is created.

---

### 26.5 Get Sheet Viewports

**Prompts:**
```
What views are placed on sheet "E-01"?
Show viewport details for sheets E-01 and E-02.
```

**Verify:**
- `revit_get_sheet_viewports` accepts `sheetIds` (long[]) or `sheetNumbers` (string[]).
- Returns `viewportId`, `viewId`, `viewName`, `viewType`, `sheetPosition`, `detailNumber` per viewport.
- Invalid or unknown sheet IDs/numbers return a clear warning rather than an error.
- No Revit transaction is created.

---

### 26.6 Find Unplaced Views

**Prompts:**
```
Find all views not placed on any sheet.
Find all unplaced floor plan views.
Find the first 10 unplaced views.
```

**Verify:**
- `revit_find_unplaced_views` returns views not currently on any sheet.
- `viewTypes` filter (e.g. `["FloorPlan"]`) narrows results.
- `nameFilter` matches on partial name.
- `includeTemplates=false` (default) excludes view templates.
- `limit` caps the result count.
- No Revit transaction is created.

---

### 26.7 Enhanced revit_list_views

**Prompts:**
```
List all floor plan and section views with name containing "KORRUS".
List views and return their Discipline and View Scale parameters.
```

**Verify:**
- `viewTypes` parameter filters by Revit view type name.
- `nameFilter` does a substring match on the view name.
- `returnParameters` reads extra parameters per view using partial name matching.
- `includeTemplates=false` (default) hides view templates.
- `limit` caps results.
- New return fields: `uniqueId`, `sheetId`, `sheetNumber`, `sheetName` (when placed), `viewTemplateId`, `viewTemplateName`.

---

### 26.8 Enhanced revit_list_sheets

**Prompts:**
```
List all sheets with number starting with "E-".
List sheets and return the Märkus and Sheet Issue Date parameters.
List sheets with full viewport detail.
List sheets using default Estonian parameters.
```

**Verify:**
- `numberFilter` does a substring match on sheet number.
- `nameFilter` does a substring match on sheet name.
- `returnParameters=["default"]` expands to 10 standard sheet params: Sheet Number, Sheet Name, Project Number, Project Status, Projekti osa, Grupi tähis, Järjekorra tähis, Current Revision, Märkus, Sheet Issue Date.
- `includeViewports=true` returns viewport detail (viewId, viewName, position, detailNumber) per sheet.
- `limit` caps results.
- New return fields: `uniqueId`, `titleBlockId`, `titleBlockName`.

---

### 26.9 Revision Numbering Sequences

**Prompts:**
```
List all revision numbering sequences in the model.
```

**Verify:**
- `revit_list_revision_numbering_sequences` returns a list (may be empty on projects with no custom sequences).
- Each entry has `sequenceId`, `name`, `numberingType`, `prefix`, `suffix`, `minimumDigits`.
- Tool returns `success: true` with an explanatory message when the list is empty.
- No Revit transaction is created.

---

### 26.10 Sheet Revisions

**Prompts:**
```
What revisions are shown on sheet E-01?
Get revision details for sheets E-01, E-02, and E-03.
```

**Verify:**
- `revit_get_sheet_revisions` accepts `sheetIds` (long[]) or `sheetNumbers` (string[]).
- Requires at least one of `sheetIds` or `sheetNumbers`.
- Each entry has `sheetId`, `sheetNumber`, `sheetName`, `revisionCount`, `revisions`.
- Each revision entry has `revisionId`, `sequenceNumber`, `revisionNumber`, `revisionDate`, `description`, `issuedBy`, `issuedTo`.
- `includeRevisionDetails=false` returns counts only (no per-revision list).
- Sheets not found by ID or number produce a warning but the tool still succeeds for valid sheets.
- No Revit transaction is created.
- **Note:** Cloud-specific revision visibility (workshared model cloud tracking) may not be surfaced by the Revit 2026 API and may not appear in results.

---

### 26.11 List View/Sheet Presets

**Prompts:**
```
List available PlaceViews presets.
```

**Verify:**
- `revit_list_view_sheet_presets` checks `C:\ProgramData\RK Tools\PlaceViews\SheetManagerSettings`.
- If the folder does not exist, returns `success: false` with a clear path message.
- Each preset entry has `FileName`, `DetectedType`, `SizeBytes`, `modifiedUtc`.
- No Revit transaction is created.

---

### 26.12 Get View/Sheet Preset

**Prompts:**
```
Read the preset file "MyPreset.json".
Read preset "MyPreset" (without extension).
```

**Verify:**
- `revit_get_view_sheet_preset` accepts `presetName` with or without `.json` extension.
- Returns `workflowType`, `parsedContent`.
- Path traversal attempts (e.g. `presetName = "../../../etc/passwd"`) return `success: false` without reading any file.
- Non-existent preset returns `success: false` with a clear error.
- No Revit transaction is created.

---

### 26.13 Validate View/Sheet Preset

**Prompts:**
```
Validate the preset "MyPreset.json".
```

**Verify:**
- `revit_validate_view_sheet_preset` returns `isValid`, `workflowType`, `errors[]`, `suggestions[]`.
- An empty JSON `{}` returns `isValid: false` with an appropriate error.
- A valid preset returns `isValid: true`.
- No Revit transaction is created.

---

### 26.14 Run View/Sheet Workflow Preset (planning only)

**Prompts:**
```
Plan the workflow from preset "MyPreset.json".
What steps would running preset "SheetManagerSettings.json" perform?
```

**Verify:**
- `revit_run_view_sheet_workflow_preset` returns `workflowType`, `stepCount`, `steps[]`, `notes[]`.
- Notes confirm that no model changes were made.
- Steps list human-readable descriptions with suggested follow-up tool calls.
- No Revit transaction is created.

> **Deferred:** `revit_create_sheets_from_preset` (direct single-call execution of a PlaceViews preset) is not yet implemented. Use `revit_run_view_sheet_workflow_preset` to plan the workflow, then execute steps with `revit_create_sheets_from_table`, `revit_duplicate_sheets`, etc.

---

## 27. View/Sheet Preview Tools

All preview tools are read-only — they return proposals without modifying the model.

### 27.1 Preview Place Views on Sheets

**Prompts:**
```
Preview placing unplaced floor plan views on sheets.
Preview matching views to sheets using fuzzy matching with threshold 0.7.
```

**Verify:**
- `revit_preview_place_views_on_sheets` requires `viewIds`.
- Match modes work: `ExactName`, `Contains`, `Fuzzy`, `SheetNumberPrefix`, `SheetNumberSuffix`, `CustomParameter`.
- `fuzzyThreshold` (0–1) affects which views get a match in Fuzzy mode.
- `skipAlreadyPlaced=true` (default) excludes views already on sheets.
- Returns `proposals` list with `viewId`, `viewName`, `matchedSheetId`, `matchedSheetNumber`, `score`, `reason`.
- No Revit transaction is created.

---

### 27.2 Preview Duplicate Sheets

**Prompts:**
```
Preview duplicating sheets E-01 and E-02 with suffix "_V2".
```

**Verify:**
- Returns proposals with `sourceSheetNumber`, `newSheetNumber`, `newSheetName`, `duplicateMode`, and source/copied content counts.
- Test `duplicateMode=EmptySheet`, `WithSheetDetailing`, and `WithViews`.
- `keepLegends`, `keepSchedules`, and `copyRevisions` report their respective proposed counts.
- `numberOfCopies` accepts 1–50; `{index}` in name/number suffixes is expanded per copy.
- Conflicting generated sheet numbers are resolved to unique values and reported as warnings.
- No Revit transaction is created.

---

### 27.3 Preview Create Sheets From Table

**Prompts:**
```
Preview creating 3 new sheets: E-10 "Elektripaigaldis", E-11 "Valgustus", E-12 "Maandus".
```

**Verify:**
- Returns per-row validation: `valid`, `conflict`, `issues`.
- Rows with duplicate sheet numbers in the input set or that conflict with existing sheets are flagged.
- `titleBlockId` must be a valid family symbol ID (from `revit_list_titleblocks`).
- No Revit transaction is created.

---

### 27.4 Preview Rename Views

**Prompts:**
```
Preview renaming all floor plan views — replace "KORRUS" with "Level".
Preview adding prefix "EL-" to all electrical section views.
```

**Verify:**
- `mode` must be one of: `FindReplace`, `PrefixSuffix`, `Template`, `RegexFindReplace`.
- `FindReplace` requires `find`; `replace` defaults to empty string.
- `PrefixSuffix` requires at least one of `prefix`, `suffix`.
- `Template` requires `template` with `{Name}` placeholder.
- Returns `oldName`, `newName`, `willChange` per view.
- Views where the new name equals the old name have `willChange=false`.
- No Revit transaction is created.
- `target=Parameter` with `parameterName` previews the same transform against a writable view parameter.
- An explicit selector is required: `viewIds`, `viewTypes`, `nameFilter`, or `allViews=true`.

---

### 27.5 Preview Rename Sheets

**Prompts:**
```
Preview renaming sheet names — replace "Elektripaigaldis" with "Electrical".
Preview adding suffix " (Rev A)" to sheet names for sheets matching number "E-".
Preview renaming sheet numbers — replace "E-" with "EL-".
```

**Verify:**
- `target` parameter: `Name`, `Number`, `Both`, or `Parameter` (`parameterName` required).
- Same rename modes as `revit_preview_rename_views`.
- `numberFilter` / `nameFilter` narrow which sheets are included.
- Returns `oldName`, `newName`, `oldNumber`, `newNumber`, `willChange` per sheet.
- No Revit transaction is created.

---

## 28. View/Sheet Write Tools (Requires Approval)

All write tools follow the standard approval flow: response shows `approval_required`, Pending tab shows the request, Approve/Reject controls are available.

> **Note – Direct Edit mode**: When Direct Edit is enabled in the Revit panel, `RequiresApproval` tools execute immediately without queuing an approval request (see `ExternalEventHandler.cs`). In that mode the tests in this section will NOT show the approval flow — the action executes directly. To verify the approval flow, ensure **Direct Edit is disabled**. Exception: `DestructiveRequiresManualApproval` tools (Delete Views, Delete Sheets) always require manual approval regardless of Direct Edit state.

### 28.1 Place Views on Sheets

**Prompts:**
```
Run revit_preview_place_views_on_sheets first, then place the views.
Preview PlaceViews matching for selected views [view IDs] and sheets [sheet IDs], then apply it.
```

**Verify:**
1. Run preview first — confirm proposals look correct.
2. Call `revit_place_views_on_sheets` with same parameters.
3. Approval appears in Pending tab.
4. Reject → no change, Revit Undo unchanged.
5. Approve → views appear on matched sheets in Revit.
6. Revit Undo shows `"Revit MCP - Place Views on Sheets"`.
7. Already-placed views are skipped when `skipAlreadyPlaced=true`.
8. With `matchMode=PlaceViews`, `sheetIds` or `allSheets=true` is required and the default for `skipAlreadyPlaced` is `false`, matching the source plugin.
9. PlaceViews mode evaluates each selected sheet, chooses its best selected printable view by exact sheet/view name or the branch's number-aware word-prefix score, and reports unmatched sheets.
10. Conflicting floor numbers do not match (`1` versus `10`, or `-1` versus `1`).
11. Preview reports `canPlace=false` when Revit says the matched view is already placed/incompatible, and when the same non-legend view is proposed for multiple sheets.
12. Approved PlaceViews placements use the center of `ViewSheet.Outline`, matching the source plugin.
13. Estonian ordinal floor names from 1–99 match their digit equivalents, including inflected and compound forms such as `esimese` → `1`, `kolmanda` → `3`, and `kahekümne esimese` → `21`; conflicting ordinals remain unmatched.

---

### 28.2 Duplicate Sheets

**Prompts:**
```
Duplicate sheets E-01 and E-02 with number suffix "_COPY".
```

**Verify:**
- Approval required; new sheets created after approval.
- New sheet numbers and names match the preview.
- `keepTitleBlock=true` gives new sheets the same title block family.
- `copyParameters=true` copies sheet instance parameter values.
- `copyTitleBlockParameters=true` copies writable title-block instance parameter values.
- `WithSheetDetailing` copies sheet detail lines, text, generic annotations, dimensions, and tags.
- `WithViews` also duplicates non-legend viewport views and preserves viewport positions.
- `keepLegends=true` places the same legend at its original position.
- `keepSchedules=true` places schedules at their original positions.
- `copyRevisions=true` copies additional sheet revision assignments.
- Transaction name: `"Revit MCP - Duplicate Sheets"`.

---

### 28.3 Create Sheets From Table

**Prompts:**
```
Create sheets E-10 "Elektripaigaldis", E-11 "Valgustus" with title block ID [from revit_list_titleblocks].
```

**Verify:**
- Run preview first to confirm no conflicts.
- After approval, new sheets appear in Revit.
- Sheets with conflicting numbers are skipped with a warning; others still created.
- Parameter values from the row are applied after sheet creation.
- Transaction name: `"Revit MCP - Create Sheets From Table"`.

---

### 28.4 Duplicate Views

**Prompts:**
```
Duplicate view 12345 with option DuplicateWithDetailing and suffix " - Copy".
```

**Verify:**
- `duplicateOption`: `Duplicate`, `DuplicateWithDetailing`, `AsDependent`.
- `numberOfCopies` accepts 1–50; `{index}` in `nameSuffix` is expanded per copy.
- When the requested option is unsupported, `fallbackToDuplicate=true` uses plain `Duplicate` when available.
- New view names follow `{namePrefix}{originalName}{nameSuffix}` and conflicts are resolved to unique names.
- `copyParameters=true` applies explicit `parameterOverrides`; native Revit duplication retains its normal view values.
- Transaction name: `"Revit MCP - Duplicate Views"`.

**Test: EmptyDetailOnly returns a clear error (deferred)**

```
Duplicate view 12345 with option EmptyDetailOnly.
```

Verify:
- `revit_duplicate_views` returns `success: false` with message: `"EmptyDetailOnly is not implemented. Valid: Duplicate, DuplicateWithDetailing, AsDependent."`
- `revit_preview_duplicate_views` returns the same error for `EmptyDetailOnly`.
- Model is not modified.

---

### 28.5 Set View Crop Regions

**Prompts:**
```
Preview copying the crop region from view 12345 to views 23456 and 34567.
Copy the crop region from view 12345 to views 23456 and 34567.
```

**Verify:**
- `revit_preview_set_view_crop_regions` makes no model changes and reports supported/skipped targets.
- `revit_set_view_crop_regions` requires approval and uses the same `referenceViewId` and `targetViewIds` arguments.
- Target crop boxes are activated and their crop visibility matches the reference view.
- A single non-split custom crop loop is copied when the target supports shaped crops.
- Split or multi-loop reference crops fall back to the rectangular reference crop box.
- Floor, ceiling, engineering and area plans, sections, elevations, details, and 3D views are supported; other view types and templates are skipped.
- The reference view is skipped if it also appears in `targetViewIds`.
- Transaction name: `"Revit MCP - Set View Crop Regions"`.

---

### 28.6 Apply View Template

**Prompts:**
```
Apply view template 56789 to all floor plan views containing "KORRUS".
```

**Verify:**
- `viewTemplateId` must be a valid view template element ID.
- Can target by explicit `viewIds`, or by `viewTypes` + `nameFilter`.
- `limit` caps how many views are updated.
- Invalid template ID returns `"success": false` with a clear error.
- Transaction name: `"Revit MCP - Apply View Template"`.

---

### 28.7 Rename Views

**Prompts:**
```
Rename all section views — replace "Section" with "Lõige".
Add prefix "EL-" to all floor plan views with name containing "KORRUS".
```

**Verify:**
1. Run `revit_preview_rename_views` first to confirm proposals.
2. Call `revit_rename_views` with same parameters.
3. Approval in Pending tab.
4. After approval, view names updated in Revit.
5. Transaction name: `"Revit MCP - Rename Views"`.
6. Rejection leaves all names unchanged.
7. `target=Parameter` transforms the writable parameter named by `parameterName`.
8. View-name swaps and rename chains use temporary unique names inside one transaction.

---

### 28.8 Rename Sheets

**Prompts:**
```
Rename sheet names — replace "Elektripaigaldis" with "Electrical".
Rename sheet numbers — replace prefix "E-" with "EL-".
```

**Verify:**
- Same modes as rename views.
- `target=Name` only renames the sheet name; `target=Number` only the sheet number; `target=Both` renames both.
- `target=Parameter` applies the same find/replace, prefix/suffix, template, or regex transform to a writable sheet parameter.
- Transaction name: `"Revit MCP - Rename Sheets"`.
- Duplicate resulting sheet numbers are rejected with a per-sheet error; others still renamed.

---

### 28.9 Set Sheet Parameters Bulk

**Prompts:**
```
Set "Märkus" to "Rev A" on sheets E-01 and E-02.
Set "Project Status" to "Issued for Construction" on all sheets with number starting with "E-".
```

**Verify:**
- Accepts `sheetIds`, `sheetNumbers`, and/or `nameFilter` to identify target sheets.
- `parameters` is a key→value map of parameter name to value.
- Partial parameter name matching works (Contains mode).
- Read-only parameters are reported as per-sheet failures without aborting the batch.
- Transaction name: `"Revit MCP - Set Sheet Parameters Bulk"`.

---

### 28.10 Set View Parameters Bulk

**Prompts:**
```
Set "Comments" to "Reviewed" on all floor plan views.
```

**Verify:**
- Same verification as 28.8 but targets views.
- `includeTemplates=false` (default) skips view templates.
- Transaction name: `"Revit MCP - Set View Parameters Bulk"`.

---

### 28.11 Apply Sheet Naming

**Prompts:**
```
Preview setting Sheet Number on sheets E-01 and E-02 from tokens: Discipline, "-", Grupi tähis, "-", Järjekorra tähis.
Apply the previewed sheet naming.
```

**Verify:**
- `tokens` accepts ordered `{type:"Parameter",value:"..."}` and `{type:"Separator",value:"..."}` entries.
- Parameters absent from a sheet fall back to Project Information.
- Empty parameter values do not leave doubled or trailing separators.
- `targetParameter` supports `Sheet Number`, `Sheet Name`, or a writable sheet parameter.
- Duplicate proposed sheet numbers are rejected in preview.
- Sheet-number swaps use temporary numbers and commit as one Undo item named `"Revit MCP - Apply Sheet Naming"`.

---

### 28.12 Create Revision

**Prompts:**
```
Create a revision dated 2026-07-16, description "Issued for coordination", issued by MB, and assign it to sheets E-01 and E-02.
```

**Verify:**
- Approval is required.
- Revit assigns the sequence/revision number.
- Optional `numberingSequenceId` accepts an ID from `revit_list_revision_numbering_sequences`.
- The revision is added to each requested sheet's additional revisions.
- `isIssued=true` is applied only after all editable revision fields are set.
- Transaction name: `"Revit MCP - Create Revision"`.

---

## 29. View/Sheet/Element Destructive Delete Tools

> **Safety:** `revit_delete_views`, `revit_delete_sheets`, and `revit_delete_elements` use `DestructiveRequiresManualApproval` — they **always** require manual approval in the Pending tab even when Direct Edit mode is enabled. This cannot be overridden.

### 29.1 Preview Delete Views

**Prompts:**
```
Preview deleting all unplaced section views with name containing "Working".
```

**Verify:**
- `revit_preview_delete_views` returns the views that would be deleted with `elementId`, `name`, `viewType`.
- `skipPlacedOnSheets=true` (default) excludes views currently placed on a sheet.
- `viewTypes` + `nameFilter` narrow scope.
- No Revit transaction is created.

---

### 29.2 Delete Views

**Prompts:**
```
Delete views 12345 and 67890.
```

**Verify:**
1. Run `revit_preview_delete_views` first.
2. Call `revit_delete_views` with confirmed view IDs.
3. **Direct Edit enabled** → approval still required (not bypassed).
4. Rejection → model unchanged.
5. Approval → views are deleted.
6. `skipPlacedOnSheets=true` causes placed views to be skipped with a warning.
7. Transaction name: `"Revit MCP - Delete Views"`.
8. Revit Undo restores deleted views.

---

### 29.3 Preview Delete Sheets

**Prompts:**
```
Preview deleting sheets with number containing "_COPY".
```

**Verify:**
- `revit_preview_delete_sheets` returns sheets with `elementId`, `sheetNumber`, `sheetName`, `viewportCount`.
- `skipSheetsWithViews=true` (default) excludes sheets that have placed viewports.
- No Revit transaction is created.

---

### 29.4 Delete Sheets

**Prompts:**
```
Delete sheets with number suffix "_COPY".
```

**Verify:**
1. Run `revit_preview_delete_sheets` first.
2. Call `revit_delete_sheets` with confirmed IDs or numbers.
3. **Direct Edit enabled** → approval still required (not bypassed).
4. Rejection → model unchanged.
5. Approval → sheets are deleted.
6. `skipSheetsWithViews=true` skips occupied sheets with a per-sheet warning.
7. Transaction name: `"Revit MCP - Delete Sheets"`.
8. Revit Undo restores deleted sheets.

---

### 29.5 Preview Delete Elements

**Prompts:**
```
Preview deleting elements 12345 and 67890.
```

**Verify:**
- `revit_preview_delete_elements` returns proposals with `elementId`, `name`, `category`, `typeName`, `level`, `isPinned`, `groupName`, `wouldDelete`, `reason`.
- `skipPinned=true` (default) marks pinned elements as protected.
- View/sheet ids are rejected with a hint to use the view/sheet delete tools.
- Levels and Revit links produce a `HIGH RISK` cascade warning; grouped elements produce a group warning.
- No Revit transaction is created.

---

### 29.6 Delete Elements

**Prompts:**
```
Delete elements 12345 and 67890.
```

**Verify:**
1. Run `revit_preview_delete_elements` first.
2. Call `revit_delete_elements` with confirmed element IDs.
3. **Direct Edit enabled** → approval still required (not bypassed).
4. Rejection → model unchanged.
5. Approval → elements are deleted; `totalRemoved` includes cascade-deleted dependents (tags, dimensions, hosted elements).
6. `skipPinned=true` skips pinned elements with a per-element warning; missing ids and views/sheets are skipped with warnings.
7. Transaction name: `"Revit MCP - Delete Elements"`.
8. Revit Undo restores deleted elements.

---

### 29.7 Delete Open/Active Views (regression — issue #19)

**Prompts:**
```
Delete the view I currently have open.
```

**Verify:**
1. Open the target view as the active view, then approve the deletion.
2. The tool switches the active view to another view, closes the target's tab, and the deletion succeeds (previously it failed with "Deleted 0 view(s)" because Revit refuses to delete the active view).
3. If the target view is the only view in the project, it is skipped with a clear warning instead of a silent failure.
