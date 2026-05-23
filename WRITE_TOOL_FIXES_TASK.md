# Write Tool Fixes

## Context

Live smoke testing was run against the Revit 2026 test model `2312_Käo_EN_2026_WIP` through the EULE_MCP connector on `2026-05-23`.

Temporary write-test artifacts were created and mostly cleaned up:

- Temporary `SMOKETEST` views were deleted after manual approval.
- Temporary `_SMK1` sheets were deleted after manual approval.
- Temporary circuit `4136953` still exists on panel `ATS keskseade`.

This document lists only confirmed defects that need follow-up.

## Confirmed issues

### 1. `revit_create_sheets_from_table` and `revit_preview_create_sheets_from_table` do not receive row data correctly

#### Observed behavior

- Tool calls with valid row objects reached the add-in as rows with empty `sheetNumber` and `sheetName`.
- Preview returned:
  - `sheetNumber is empty`
  - `sheetName is empty`
- Apply returned:
  - `Created 0 sheet(s), skipped 1.`
  - `Skipped row with empty sheetNumber or sheetName.`

#### Repro

Call:

```json
{
  "titleBlockId": 3666964,
  "rows": [
    {
      "sheetNumber": "SMK-20260523-C1",
      "sheetName": "CREATE Sheet SMOKETEST"
    }
  ]
}
```

Expected:

- preview should show 1 valid row
- apply should create 1 sheet

Actual:

- row fields become empty before tool execution uses them

#### Likely cause

The live tool schema does not match the add-in implementation.

- MCP/tool schema currently exposes `rows` like `string[]`
- add-in implementation expects `rows` as an array of objects or a JSON array string

Files to inspect:

- `RevitMCP.Addin/Tools/PreviewCreateSheetsFromTableTool.cs`
- `RevitMCP.Addin/Tools/CreateSheetsFromTableTool.cs`
- tool schema generation / bridge serialization for these two tools

#### Fix target

- Align the MCP argument schema with the implementation:
  - `rows` must be an array of objects
- Keep JSON-string fallback if desired, but normal structured calls must work

#### Acceptance criteria

- preview accepts structured rows and returns valid proposals
- apply accepts the same payload and creates sheets successfully

### 2. `revit_set_view_parameters_bulk` and `revit_set_sheet_parameters_bulk` mis-handle the `parameters` object

#### Observed behavior

Both tools failed even with a simple object payload.

Examples:

```json
{
  "viewIds": [4136915, 4136925],
  "parameters": {
    "Comments": "WRITE_TEST_VIEW"
  }
}
```

```json
{
  "sheetIds": [4136903, 4136909],
  "parameters": {
    "Comments": "WRITE_TEST_SHEET"
  }
}
```

Actual warnings:

- `param 'ValueKind' not found.`

That warning proves the incoming payload is not being interpreted as a plain parameter-name/value object.

#### Why this matters

The add-in code is fine with a `JObject` parameter map, but the live tool schema currently exposes `parameters` as a string-like value. The wrapper appears to be serializing metadata instead of the intended object contents.

Files to inspect:

- `RevitMCP.Addin/Tools/SetViewParametersBulkTool.cs`
- `RevitMCP.Addin/Tools/SetSheetParametersBulkTool.cs`
- tool schema generation / bridge serialization for `parameters`

#### Fix target

- Expose `parameters` as a plain object in the tool schema
- Optionally add string JSON fallback parsing in the add-in for resilience

#### Acceptance criteria

- a payload like `{"Comments":"X"}` reaches the tool unchanged
- view bulk set updates matching writable view parameters
- sheet bulk set updates matching writable sheet parameters
- warnings mention the real requested parameter name, not `ValueKind`

### 3. `revit_duplicate_sheets` does not preserve requested `newNameSuffix` in the actual created sheet name

#### Observed behavior

Call:

```json
{
  "sourceSheetIds": [3714661, 1285696],
  "newNumberSuffix": "_SMK1",
  "newNameSuffix": " SMOKETEST"
}
```

Preview reported:

- `Dokumentide loetelu SMOKETEST`
- `Tiitelleht SMOKETEST`

Apply result also reported those suffixed names.

But subsequent rename preview on the created sheets showed actual current names as:

- `Dokumentide loetelu`
- `Tiitelleht`

So the created sheet numbers were correct, but the created sheet names did not actually retain the requested suffix.

#### Files to inspect

- `RevitMCP.Addin/Tools/DuplicateSheetsTool.cs`
- `RevitMCP.Addin/Tools/PreviewDuplicateSheetsTool.cs`

#### Likely cause

One of these is happening:

- `newSheet.Name = newName` is later overwritten during parameter copy
- returned result object is reporting `newName`, but the actual sheet name differs
- sheet name storage is being copied from the source through a path not skipped by the current built-in parameter checks

#### Acceptance criteria

- actual created sheet names match previewed names
- `newNameSuffix` is preserved after duplication
- returned result data matches the real created sheet state

### 4. `revit_create_electrical_circuit` reports a wire-type assignment warning even when a valid wire type name is provided

#### Observed behavior

Call:

```json
{
  "elementIds": [3394913],
  "panelElementId": 2757194,
  "systemType": "FireAlarm",
  "wireTypeName": "XPJ 2x1,5"
}
```

Result:

- circuit was created successfully
- warning returned:
  - `Wire type assignment failed: The id is not a Cable Type id nor invalidElementId.`

Then this follow-up call worked:

```json
{
  "circuitId": 4136953,
  "wireTypeName": "XPJ 2x1,5",
  "cableTypeName": "XPJ 2x1,5"
}
```

So the project contains a resolvable type, but the create path is assigning it through the wrong parameter or wrong API branch.

#### Files to inspect

- `RevitMCP.Addin/Tools/CreateElectricalCircuitTool.cs`
- `RevitMCP.Addin/Tools/ChangeCircuitCableOrWireTypeTool.cs`

#### Acceptance criteria

- creating a circuit with `wireTypeName` does not emit the cable-type-id warning
- created circuit has the requested wire/cable type immediately after creation

### 5. Empty clash preset runs still break cached clash navigation

This was confirmed in the earlier smoke test and is still open.

#### Observed behavior

`revit_run_clash_preset` now completes quickly and safely when no source candidates exist.

Example result:

- preset `Electrical vs HVAC`
- `0 total clash(es)`
- all 6 rules skipped for `no source candidates`
- response says `Saved as last run`

But immediately after that:

- `revit_get_next_clash` returns `No clash run in cache. Run a detection first.`
- `revit_get_previous_clash` returns the same

#### Expected behavior

One of:

- navigation should work against an empty cached run cleanly, or
- tools should return a precise empty-run message such as `Last run contains 0 clashes`

#### Files to inspect

- `RevitMCP.Addin/Tools/RunClashPresetTool.cs`
- clash run cache persistence / retrieval path
- `RevitMCP.Addin/Tools/GetNextClashTool.cs`
- `RevitMCP.Addin/Tools/GetPreviousClashTool.cs`

#### Acceptance criteria

- empty preset runs still persist a valid last-run cache record
- next/previous clash tools do not behave as if no run exists

## Non-issues / expected results

These do not currently look like tool bugs:

- `revit_set_parameter` on a fire alarm device `Comments` parameter worked correctly
- `revit_apply_circuit_numbering` failed to modify the test circuit because `Circuit Number` was not writable on that circuit; that should be surfaced clearly, but the core behavior may be valid
- manual approval gates on `revit_delete_views` and `revit_delete_sheets` worked as intended

## Suggested order

1. Fix schema / argument marshalling for:
   - `create_sheets_from_table`
   - `preview_create_sheets_from_table`
   - `set_sheet_parameters_bulk`
   - `set_view_parameters_bulk`
2. Fix `duplicate_sheets` name persistence mismatch
3. Fix wire-type assignment inside `create_electrical_circuit`
4. Fix empty-run clash cache navigation

