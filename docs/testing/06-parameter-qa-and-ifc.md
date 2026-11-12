# Smoke tests — Parameter QA and IFC Space to Room

Part of the [smoke test checklist](../../TESTING.md).

## 43. Parameter QA Rule Sets

### 43.1 List Rule Sets Returns Default Set

**Prompt:**
```
List all parameter QA rule sets.
```

**Expected:**
- `success: true`
- `ruleSets` contains at least one entry: `"ELENEA Basic QA"`.
- Each rule set entry includes `name`, `description`, `ruleCount`, and `rules` array.
- If `%AppData%\RKTools\RevitMCP\parameter-qa-rules.json` did not exist, it is created automatically with the default content.

### 43.2 Run Rule Set Against Active Model

**Prompt:**
```
Run the 'ELENEA Basic QA' parameter QA rule set.
```

**Expected:**
- `success: true`
- `ruleSetName: "ELENEA Basic QA"`
- `rules` array contains one entry per rule in the set (e.g. `"Fire Alarm Devices"`, `"Lighting Fixtures"`).
- Each rule entry includes `totalElements`, `completeElements`, `incompleteElements`, `completionPercent`, and `parameters`.
- If the model has no elements of a rule's category, `totalElements: 0` is returned for that rule and a warning is added.
- If `returnIssueReport: true` (default), `issueReport` is included with individual element issues.

### 43.3 Unknown Rule Set Returns Descriptive Error

**Prompt:**
```
Run the 'NonExistent QA Set' parameter QA rule set.
```

**Expected:**
- `success: false`
- `message` contains the unknown rule set name and a hint to use `revit_list_parameter_qa_rule_sets`.

### 43.4 Unit Tests Pass (192 tests)

```
dotnet test RevitMCP.slnx
```

**Expected:**
- `Passed! - Failed: 0, Passed: 192`
- New tests: `RevitSheetNumberParserTests` (11 facts), `DeliveryFolderPolicyCheckerTests` (12 facts/theories), `ParameterQaRuleSetServiceTests` (7 facts).

---

## 44. IFC Space to Revit Room — Full Pipeline (Phases 1–4)

These tests cover the complete IFC Space → Room pipeline across all four phases.
All phases must be tested **in sequence** since later phases depend on output from earlier ones.

**Prerequisites:**
- A Revit host model with at least 2 Levels and their floor-plan views.
- A loaded Revit link containing an IFC model with at least one `IfcSpace` element.
- Run **Start Connector** before any test.

---

### 44.1 Phase 1 — Discovery and Preview (`ifc_list_links`, `ifc_preview_spaces`)

#### Step A — List Links

**Prompt:**
```
call ifc_list_links
```

**Expected:**
- `success: true`
- At least one entry with `linkInstanceId` and `linkName`.
- `linkInstanceId` is used in all subsequent calls.

---

#### Step B — Preview Spaces (default, confirmed-only)

**Prompt:**
```
call ifc_preview_spaces with linkInstanceId=<id>
```

**Expected:**
- `success: true`
- `spaces` array: each entry has `linkedElementId`, `number`, `name`, `storeyName`, `detectionConfidence: "Confirmed"`, `targetLevelId`, `targetLevelName`, `status`, `canConvertLater`.
- `summary.totalCandidates >= 1`
- All returned spaces have `detectionConfidence: "Confirmed"` (no Probable unless `includeProbable=true`).
- `numberSource` and `nameSource` are present when values are non-null (e.g. `"Room Number"`, `"LongName"`).
- `status` values: `Ready`, `Warning`, `AlreadyExists`, `MissingMetadata`, `NoLevelMatch`.
- No Revit transaction created.

---

#### Step C — Preview Spaces (including Probable)

**Prompt:**
```
call ifc_preview_spaces with linkInstanceId=<id> and includeProbable=true
```

**Expected:**
- Probable candidates appear with `detectionConfidence: "Probable"` and `canConvertLater: false` and `status: "NotIfcSpace"`.
- Confirmed candidates still appear with `detectionConfidence: "Confirmed"`.
- No model modification.

---

#### Step D — Metadata source tracing

From Step B results:
- Verify at least one space has `numberSource` set to `"Number"`, `"Room Number"`, or `"Reference"` (never `"LongName"` or `"IfcLongName"`).
- Verify at least one space has `nameSource` set to `"LongName"`, `"IfcLongName"`, or `"Room Name"`.
- If `number` and `name` are the same value and `numberSource` = `"LongName"` → this is a **bug** (LongName must NOT be in Number priority list).

---

### 44.2 Phase 2 — Geometry Extraction (`ifc_preview_space_geometry`)

**Prompt:**
```
call ifc_preview_space_geometry with linkInstanceId=<id>
```

**Expected:**
- `success: true`
- Each item has `status` (see `IfcGeometryStatus` constants).
- Items with `status: "GeometryReady"` have `approxAreaM2 > 0` and `placementPoint.xMm/yMm/zMm`.
- `outerLoopCurveCount >= 3` for GeometryReady items.
- `summary.geometryReady >= 1`
- No Revit transaction created.

**Prompt (with loop coordinates):**
```
call ifc_preview_space_geometry with linkInstanceId=<id> and includeLoopCoordinates=true and maxCoordinatePoints=50
```

**Expected:**
- `loops[0].points` is present and has ≤ 50 entries.
- `loops[0].isOuter: true`.
- No Revit transaction created.

---

### 44.3 Phase 3 — Room Creation (`convert_ifc_spaces_to_rooms`)

#### Step A — Dry Run

**Prompt:**
```
call convert_ifc_spaces_to_rooms with linkInstanceId=<id> and dryRun=true
```

**Expected:**
- `success: true`
- `dryRun: true`
- Items with `status: "DryRunReady"` indicate they would be created.
- **No model modifications** — check Revit Undo, no new entry should appear.
- No new Rooms in the model.

---

#### Step B — Explicit Element IDs + Dry Run

From Phase 2, collect `linkedElementId` values for `GeometryReady` spaces.

**Prompt:**
```
call convert_ifc_spaces_to_rooms with linkInstanceId=<id> and linkedElementIds=[<id1>,<id2>] and dryRun=true
```

**Expected:**
- Only the specified spaces are processed.
- `items` has exactly as many entries as supplied IDs (where elements could be found).

---

#### Step C — Live Conversion (Requires Approval)

**Prompt:**
```
call convert_ifc_spaces_to_rooms with linkInstanceId=<id> and linkedElementIds=[<id1>] and dryRun=false
```

**Approval flow:**
1. Approval in Pending tab.
2. Summary clearly shows link ID and space count.
3. **Reject** → no model change, Revit Undo unchanged, `status: "SkippedExisting"` NOT returned (never reached write step).
4. Re-call, **Approve**:
   - `status: "Created"` for each successful space.
   - New Room appears in Revit on the correct Level.
   - Room Number and Name match IFC metadata (no IFC GUIDs, no Comments, no shared parameters written).
   - Revit Undo shows `"MCP: Create Room '<number> <name>'"` entries.
   - `createdBoundaryLineCount > 0` when `createRoomSeparationLines=true`.

---

#### Step D — Duplicate conflict handling

After Step C, re-run with the same spaces and default `duplicateMode="skip_existing"`.

**Expected:**
- `status: "SkippedExisting"` for exact Number+Name+Level matches.
- `status: "SkippedDuplicateWarning"` when Number+Level match but Name differs.

With `duplicateMode="allow_conflicts"`:
- Number+Level conflict (but different Name) is allowed through → Room created (with warning).

---

#### Step E — Missing boundary view guard

Test with `allowCreateMissingBoundaryViews=false` (default) on a Level that has no floor-plan view.

**Expected:**
- `status: "SkippedNoView"` for that space.
- Error message mentions `allowCreateMissingBoundaryViews=true`.

With `allowCreateMissingBoundaryViews=true` (Requires Approval):
- A view named `"MCP_IFC Room Boundary - {LevelName}"` is created inside the run's setup transaction.
- After approval, the Room is created.
- Transaction `"MCP: Create IFC Room Boundary Views"` appears in Undo history (before per-space transactions).

---

#### Step F — Probable conversion guard

**Prompt:**
```
call convert_ifc_spaces_to_rooms with linkInstanceId=<id> and allowProbableConversion=false
```
(auto-collect mode — no `linkedElementIds`)

**Expected:** Only Confirmed elements are processed. Probable candidates are excluded silently.

**Prompt:**
```
call convert_ifc_spaces_to_rooms with linkInstanceId=<id> and allowProbableConversion=true
```

**Expected:** Probable candidates are included. Each has an advisory `warnings` entry stating the element could not be confirmed as IfcSpace.

---

### 44.4 Phase 4 — Validation (`validate_ifc_space_room_conversion`)

Run after Phase 3 has created at least some Rooms.

**Prompt:**
```
call validate_ifc_space_room_conversion with linkInstanceId=<id>
```

**Expected:**
- `success: true`
- `permission: ReadOnly` — no model changes.
- Each item has `confidence` (`High`, `Medium`, `Low`, `Ambiguous`, `None`) and `status` (see `ValidationStatus` constants).
- High-confidence matches: `status: "HighConfidenceMatch"`, `recommendedAction: "None"`.
- Missing rooms (not yet created): `status: "MissingRoom"`, `confidence: "None"`, `recommendedAction: "CreateRoom"`.
- Ambiguous matches: `status: "AmbiguousMatch"`, `recommendedAction: "ReviewAndResolve"`, `warnings` lists both candidate IDs.
- `summary.totalIfcSpaces >= 1`.
- No Revit transaction created.

---

### 44.5 Phase 4 — Controlled Sync (`sync_ifc_space_room_data`)

#### Step A — Dry Run (default)

From Phase 4 validation, identify a High- or Medium-confidence match where Room Name differs from IFC Name.

**Prompt:**
```
call sync_ifc_space_room_data with linkInstanceId=<id> and syncItems=[{"linkedElementId":<spaceId>,"roomId":<roomId>,"updateName":true,"updateNumber":false}] and dryRun=true
```

**Expected:**
- `success: true`
- `dryRun: true`
- `status: "DryRunWouldUpdateName"` for the targeted item.
- No model changes — Revit Undo unchanged.

---

#### Step B — Live Sync (Requires Approval)

**Prompt:**
```
call sync_ifc_space_room_data with linkInstanceId=<id> and syncItems=[{"linkedElementId":<spaceId>,"roomId":<roomId>,"updateName":true,"updateNumber":false}] and dryRun=false
```

**Approval flow:**
1. Approval in Pending tab.
2. After approval: `status: "UpdatedName"`.
3. Room.Name in Revit updated to IFC Name.
4. Revit Undo shows the transaction.
5. No shared parameters, no IFC GUIDs, no Comments modified.

---

#### Step C — Ambiguous match is blocked

Identify an Ambiguous match from Phase 4 validation.

**Prompt:**
```
call sync_ifc_space_room_data with linkInstanceId=<id> and syncItems=[{"linkedElementId":<ambiguousSpaceId>,"roomId":<anyRoomId>,"updateName":true}] and dryRun=false
```

**Expected (even after approval):**
- `status: "BlockedAmbiguousMatch"` — sync is refused, no write performed.
- Error message explains the ambiguity.

---

#### Step D — Low-confidence match is blocked by default

**Expected:**
- `status: "BlockedLowConfidence"` without explicit opt-in.

---

### 44.6 Non-Negotiables Checklist

After the full pipeline run, verify:

| Requirement | How to verify |
|---|---|
| No shared parameters created | Open Revit's Manage → Shared Parameters — no new entries from MCP |
| No IFC GUIDs in Room fields | Inspect created Room parameters — no GUID strings in Number, Name, Comments |
| No Comments field written | Check Room.Comments — must be empty/unchanged |
| No Extensible Storage used | Check room element properties — no data storage schemas attached |
| Existing Rooms not overwritten | Run Phase 3 twice on same spaces — second run returns `SkippedExisting` not `Created` |
| Ambiguous matches always blocked in sync | Verify Step C above |
| One bad space does not abort batch | Corrupt one element's geometry artificially (or use a non-IfcSpace element ID) and verify others still succeed |
| Dry-run makes zero model changes | Run dry-run and confirm Revit Undo history is empty |
| Read-only tools have no transactions | `ifc_list_links`, `ifc_preview_spaces`, `ifc_preview_space_geometry`, `validate_ifc_space_room_conversion` must NOT appear in Revit Undo |

---

### 44.7 Section 30 Matrix Rows for IFC Tools

Add these rows to the Section 30 matrix:

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Coord | `ifc_list_links` | RO | "List all IFC links." | Array of `{linkInstanceId, linkName, isLoaded}` | N/A | N/A | Empty array when no links loaded |
| Coord | `ifc_preview_spaces` | RO | "Preview IFC spaces for link `<id>`." | `{spaces:[...], summary:{totalCandidates, ready, ...}}` with `detectionConfidence`, `numberSource`, `nameSource` | N/A | N/A | Default: confirmed-only. `includeProbable=true` shows Probable candidates |
| Coord | `ifc_preview_space_geometry` | RO | "Preview geometry for IFC spaces in link `<id>`." | `{items:[...], summary:{geometryReady:N, ...}}` | N/A | N/A | `status=GeometryReady` items have `approxAreaM2` and `placementPoint` |
| Coord | `convert_ifc_spaces_to_rooms` | RA | "Convert IFC spaces to rooms for link `<id>`, dryRun=true first." | DryRun: `{items:[{status:DryRunReady}]}` → no change. Live: `{status:Created}` for each space → Rooms in Revit | Required (DE bypasses) | Per-space `"MCP: Create Room"` Undo entry | Default: skip\_existing blocks Number+Level conflicts. `allow_conflicts` to opt in |
| Coord | `validate_ifc_space_room_conversion` | RO | "Validate IFC space to room conversion for link `<id>`." | `{items:[{confidence, status, matchedRoomId}], summary}` | N/A | N/A | No model changes. Ambiguous matches flagged with both candidate IDs |
| Coord | `sync_ifc_space_room_data` | RA | "Sync room name for space `<spaceId>` to room `<roomId>`, dryRun=true." | DryRun: `{status:DryRunWouldUpdateName}`. Live after approval: `{status:UpdatedName}` | Required (DE bypasses) | Per-item sync transaction Undo entry | Default dryRun=true. Ambiguous always blocked. Low/Medium blocked unless opted in |
