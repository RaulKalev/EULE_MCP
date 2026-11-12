# Smoke tests — Coordination and clash detection

Part of the [smoke test checklist](../../TESTING.md).

## 34. Coordination / Clash Detection Tools — Overview

The 17 coordination tools implement a **Revit-native bounding-box clash detection pipeline** across five sub-areas:

| Sub-area | Tools |
|----------|-------|
| Discovery | `revit_list_clashable_categories`, `revit_list_clashable_links`, `revit_get_clash_candidates` |
| Detection | `revit_detect_hard_clashes`, `revit_detect_clearance_clashes`, `revit_get_clash_summary` |
| Presets | `revit_list_clash_presets`, `revit_get_clash_preset`, `revit_validate_clash_preset`, `revit_run_clash_preset` |
| Reporting | `revit_export_clash_report_to_excel`, `revit_get_clash_dashboard_summary` |
| Review | `revit_get_next_clash`, `revit_get_previous_clash`, `revit_create_clash_review_view`, `revit_focus_clash`, `revit_select_clash_elements` |

**Prerequisites:**
- Revit model open with elements in at least two categories.
- For link-vs-host tests: a Revit link must be loaded (not unloaded).
- Run **Start Connector** before any test.

---

## 35. Coordination Discovery Tools

### 35.1 `revit_list_clashable_categories`

**Prompt:**
```
call revit_list_clashable_categories
```
**Expected:** `{categories: [{category, count, source}], count}`: one entry per physical model category that has
instances with solid geometry, per source (`Host` or link name), common clash categories first, then by name. Not a
fixed list: an EN model also shows e.g. Communication Devices, Data Devices, Security Devices, Lighting Fixtures,
Electrical Equipment; doors and fittings appear when the model has them. Rooms, spaces, circuits, wires, groups and
lines never appear.

### 35.2 `revit_list_clashable_links`

**Prompt:**
```
call revit_list_clashable_links
```
**Expected (no links):** `{success: true, data: [], message: "0 link instances found"}`.

**Expected (links loaded):** Each entry has `linkId`, `linkName`, `isLoaded: true`.

**Expected (links unloaded):** `isLoaded: false` entries appear; no crash.

### 35.3 `revit_get_clash_candidates`

**Prompt:**
```
call revit_get_clash_candidates with sourceCategories=["Electrical Equipment"] and targetCategories=["Mechanical Equipment"]
```
**Expected:** `{success: true, data: {sourceCandidateCount, targetCandidateCount}}`.

**Prompt (link):**
```
call revit_get_clash_candidates with sourceCategories=["Ducts"] and targetCategories=["Conduits"] and linkId=<id>
```
**Expected:** Counts sourced from the link model.

**Prompt (any model category):**
```
call revit_get_clash_candidates with sourceCategories=["Communication Devices", "OST_Doors"] and targetCategories=["Rooms", "Nonexistent"]
```
**Expected:** Source candidates are collected (a document category name and an `OST_` name both resolve). Warnings say
`Rooms` cannot be clash-checked (no physical geometry) and `Nonexistent` is unknown, with suggestions when a similar name exists.

---

## 36. Coordination Detection Tools

### 36.1 `revit_detect_hard_clashes`

**Prompt:**
```
call revit_detect_hard_clashes with sourceCategories=["Electrical Equipment"] and targetCategories=["Mechanical Equipment"]
```
**Expected:** `{success: true, data: {clashes: [...], totalCount: N}}`. Each clash has `clashId`, `source.elementId`, `target.elementId`, `location.x/y/z`.

**No-clash model:** Returns `data.totalCount: 0` and empty array.

### 36.2 `revit_detect_clearance_clashes`

**Prompt:**
```
call revit_detect_clearance_clashes with sourceCategories=["Fire Alarm Devices"] and targetCategories=["Ducts"] and toleranceMm=100
```
**Expected:** Clashes include `distanceMm` and `requiredClearanceMm=100`.

### 36.3 `revit_get_clash_summary`

**Prompt:**
```
call revit_get_clash_summary with clashes=<paste clash array from above>
```
**Expected:** `{byRule: {...}, bySeverity: {...}, byStatus: {...}, byLevel: {...}, totalCount: N}`.

---

## 37. Coordination Preset Tools

### 37.1 `revit_list_clash_presets`

**Prompt:**
```
call revit_list_clash_presets
```
**Expected:** At least 2 built-in presets (`"Electrical vs HVAC"`, `"Fire Alarm Devices Placement QA"`).

### 37.2 `revit_get_clash_preset`

**Prompt:**
```
call revit_get_clash_preset with name="Electrical vs HVAC"
```
**Expected:** Full preset JSON with `name`, `rules[]` where each rule has `sourceCategoryNames`, `targetCategoryNames`, `type`.

### 37.3 `revit_validate_clash_preset`

**Valid preset:**
```
call revit_validate_clash_preset with preset=<valid preset JSON>
```
**Expected:** `{isValid: true, ruleCount: N, errors: []}`.

**Invalid preset (missing rules):**
```
call revit_validate_clash_preset with preset={}
```
**Expected:** `{isValid: false, errors: ["missing rules"]}`.

### 37.4 `revit_run_clash_preset`

**Prompt:**
```
call revit_run_clash_preset with presetName="Electrical vs HVAC"
```
**Expected:** `{success: true, data: {totalClashCount, ruleResults: [...]}}`. Result is cached for step-through review.

---

## 38. Coordination Reporting Tools

### 38.1 `revit_export_clash_report_to_excel`

**Prompt:**
```
call revit_export_clash_report_to_excel with run=<clash run result JSON>
```
**Expected:** `{success: true, data: {filePath: "...ClashReport_*.xlsx"}}`. File opens in Excel with Summary and Details sheets.

**Verify:**
1. Summary sheet has totals by rule.
2. Details sheet has one row per clash with `ClashId`, `RuleName`, `ClashType`, `Severity`, `Status`, source/target element IDs and categories, location X/Y/Z, DistanceMm, RequiredClearanceMm.

### 38.2 `revit_get_clash_dashboard_summary`

**Prompt:**
```
call revit_get_clash_dashboard_summary with run=<clash run result JSON>
```
**Expected:** `{totalClashes, byRule: {...}, bySeverity: {...}, byStatus: {...}}`.

---

## 39. Coordination Review Tools

### 39.1 `revit_get_next_clash` / `revit_get_previous_clash`

**Prompt (after running a preset):**
```
call revit_get_next_clash
```
**Expected:** Returns next clash in sequence. Wraps around at end. Returns `{currentIndex, totalCount, clash}`.

```
call revit_get_previous_clash
```
**Expected:** Steps back. Wraps around at beginning.

**No cache (cold start):**
```
call revit_get_next_clash
```
**Expected:** `{success: false, message: "No clash run cached..."}`.

### 39.2 `revit_select_clash_elements` *(requires approval)*

**Prompt:**
```
call revit_select_clash_elements with sourceElementId=<id> and targetElementId=<id>
```
1. Approval in Pending tab.
2. After approval — both elements highlighted in Revit UI.
3. Rejection — no selection change.

### 39.3 `revit_focus_clash` *(requires approval)*

**Prompt:**
```
call revit_focus_clash with sourceElementId=<id> and targetElementId=<id> and location={"x":1,"y":2,"z":3}
```
1. Approval in Pending tab.
2. After approval — active 3D view zooms to clash location; both elements selected.

### 39.4 `revit_create_clash_review_view` *(requires approval)*

**Prompt:**
```
call revit_create_clash_review_view with clashId="CL-0001" and sourceElementId=<id> and targetElementId=<id>
```
1. Approval in Pending tab.
2. After approval — the reusable 3D view named `MCP Clash Review` appears in Project Browser (created if missing, otherwise reused) with a section box isolating the two elements.
3. Rejection — no view created.
4. Transaction name: `"Revit MCP - Create Clash Review View"`.

---

## 40. Coordination Unit Tests

Run via:
```
dotnet test RevitMCP.slnx
```

**Coordination-specific tests (19 facts in `ClashDetectionTests.cs`):**

| Test | Asserts |
|------|---------|
| `BoundingBoxMath_Overlaps_WhenIntersecting` | Two identical boxes → overlap = true |
| `BoundingBoxMath_NotOverlaps_WhenApart` | Non-touching boxes → overlap = false |
| `BoundingBoxMath_Overlaps_AfterExpand` | Originally apart boxes overlap after 50 mm expand |
| `BoundingBoxMath_ApproximateDistance_IsPositive` | Distance between non-overlapping boxes > 0 |
| `BoundingBoxMath_ApproximateDistance_ZeroWhenOverlapping` | Overlapping boxes → distance = 0 |
| `ClashSummaryService_GroupByRule` | Clashes grouped by rule name correctly |
| `ClashSummaryService_GroupByLevel` | Clashes grouped by level correctly |
| `ClashSummaryService_TotalCount` | Total count matches input list size |
| `ClashSummaryService_AllStatusesPresent` | All 3 statuses appear in summary |
| `ClashPresetService_DefaultPresets_Count` | GetDefaultPresets returns ≥ 2 built-in presets |
| `ClashPresetService_Validate_ValidPreset` | Valid preset → isValid = true, no errors |
| `ClashPresetService_Validate_MissingRules` | Preset with empty rules → isValid = false |
| `ClashPresetService_Validate_NullSourceCategories` | Null sourceCategoryNames → isValid = false |
| `ClashRunCacheService_SaveAndLoad` | Saved run → loaded run has same total count |
| `ClashRunCacheService_HasCache_FalseWhenEmpty` | No cache → HasCache = false |
| `ClashRunCacheService_GetNext_WrapsAround` | GetNext twice on 2-clash run → wraps to first |
| `ClashRunCacheService_GetPrevious_WrapsAround` | GetPrevious from start → wraps to last |
| `ClashRunCacheService_EmptyRunReturnsNull` | GetNext on 0-clash run → returns null |
| `ClashResultDto_Defaults` | New ClashResultDto has Status="New", Severity="Medium" |

---

## 41. Coordination Tool Safety Notes and Known Limitations

### 41.1 Linked Model Element Selection

When a clashing element resides inside a linked Revit model, the Revit API does **not** allow selecting linked elements the same way as host elements.

Expected behavior in `revit_select_clash_elements` and `revit_focus_clash`:
- If the source element is in the host model: selects it directly.
- If the source element is in a linked model: selects the `RevitLinkInstance` as a fallback.
- The tool result includes a `warnings[]` entry explaining that linked element selection is approximate — the `linkInstanceId` is provided for reference.

Do **not** treat a warning about linked-element fallback as a bug.

### 41.2 Clearance Clash Approximation (Expanded Bounding Box)

MVP clearance clash detection uses the **ExpandedBoundingBox** method:
- Source element bounding box is expanded uniformly by `toleranceMm` in all directions.
- An overlap with the target element's bounding box is reported as a clearance violation.

**This is an approximation.** Bounding boxes are axis-aligned and do not follow element geometry curves or offsets. The reported `distanceMm` is the minimum axis-aligned distance between the two boxes and may differ from the true geometric clearance.

Include `"clearance check uses bounding-box approximation"` in smoke test notes. Do not flag approximate clearance results as failures unless the distance value is wildly inconsistent.

### 41.3 Imported / Generic Model Geometry Warnings

When source or target categories include `Generic Models`, `ImportInstance`, or IFC-origin elements:
- Geometry extraction may fail for some elements (no solid, no geometry iterator).
- These elements are **skipped silently** and counted in `warnings[]` as `{skippedGeometryCount}`.
- A non-zero `skippedGeometryCount` is **expected** for IFC-heavy models and is not a failure.

Verify that the result still returns `success: true` and that the `skippedGeometryCount` warning appears when IFC or generic geometry is present.

### 41.4 Unloaded Links

If a Revit link is unloaded:
- `revit_list_clashable_links` returns it with `isLoaded: false`.
- `revit_get_clash_candidates`, `revit_detect_hard_clashes`, and `revit_detect_clearance_clashes` skip unloaded links entirely — no crash, no error.
- The tool result includes a warning listing the skipped link names.

### 41.5 No-Cache State

If `revit_get_next_clash` or `revit_get_previous_clash` is called before any preset has been run:
- The tool returns `{success: false, message: "No clash run cached. Run a preset first."}` (or similar).
- This is the expected cold-start behavior — not a bug.

Always run `revit_run_clash_preset` at least once per Revit session before using the step-through review tools.

---

## 42. Hard Clash False Positive Regression Tests

These tests verify that the strict solid-intersection path does **not** report false positives for elements that are spatially adjacent but do not physically intersect, and that the `allowBoundingBoxFallback` flag behaves correctly.

### 42.1 Fire Alarm Detector vs Pipe — Strict Mode (default)

**Setup:** A fire alarm smoke detector mounted near (but not intersecting) a sprinkler pipe. Both are present in the host model or a loaded link.

**Prompt:**
```
Detect hard clashes between Fire Alarm Devices and Pipe Accessories with toleranceMm=5 and allowBoundingBoxFallback=false.
```

**Expected:**
- `clashes` array is **empty** (or contains only genuine intersections if any exist).
- `warnings` contains the strict-mode notice: `"Hard clash detection uses bounding-box overlap only as a candidate pre-check..."`
- If solids could not be extracted, `warnings` includes a line starting `"Skipped N candidate pair(s) because usable solids could not be extracted."`
- **No false positives** — the detector is not reported as clashing with the pipe due to bounding-box proximity alone.

### 42.2 Fire Alarm Detector vs Pipe — Fallback Mode (opt-in)

**Setup:** Same geometry as 42.1.

**Prompt:**
```
Detect hard clashes between Fire Alarm Devices and Pipe Accessories with toleranceMm=5 and allowBoundingBoxFallback=true.
```

**Expected:**
- If solids are unavailable for either element, a clash result **may** appear with `detectionMethod: "BoundingBoxFallback"` and `confidence: "Low"`.
- The clash `message` contains the phrase `"bounding-box fallback only; verify visually"`.
- `warnings` includes `"Bounding-box fallback was enabled. Some hard clash results are low-confidence and must be reviewed visually."`

### 42.3 Solid-Confirmed Hard Clash Returns High Confidence

**Setup:** Two pipes that genuinely intersect in the model (or a host + link element that physically overlaps).

**Prompt:**
```
Detect hard clashes between Pipes and Pipes (or Pipes and Mechanical Equipment) with toleranceMm=1.
```

**Expected:**
- At least one clash result has `detectionMethod: "SolidIntersection"` and `confidence: "High"`.
- `intersectionVolume` is a positive number (cubic mm).
- No `"BoundingBoxFallback"` results appear in the same run.

### 42.4 Clearance Result Has Medium Confidence

**Prompt:**
```
Detect 100 mm clearance clashes between Fire Alarm Devices and Ducts.
```

**Expected:**
- Every clash result (if any) has `detectionMethod: "ExpandedBoundingBox"` and `confidence: "Medium"`.
- `warnings` includes `"Clearance detection uses expanded bounding-box approximation. Results should be visually reviewed."`

### 42.5 Summary Includes byDetectionMethod and byConfidence

**Prompt:**
```
Summarise the last clash run results.
```

**Expected:**
- Response includes a `byDetectionMethod` key (e.g. `{"SolidIntersection": 5, "BoundingBoxFallback": 2}`).
- Response includes a `byConfidence` key (e.g. `{"High": 5, "Low": 2}`).

### 42.6 Excel Export Includes Detection Method and Confidence Columns

**Prompt:**
```
Export the last clash run to Excel.
```

**Expected:**
- The generated `.xlsx` file has a `Clashes` sheet with columns `Detection Method` (column 15) and `Confidence` (column 16).
- High-confidence solid-intersection rows show `SolidIntersection` / `High`.
- Low-confidence fallback rows (if any) show `BoundingBoxFallback` / `Low`.
