# Smoke tests — Skill builder, dimensions, linked selection, storage, family types

Part of the [smoke test checklist](../../TESTING.md).

## 40. Skill Builder — Guided Skill Creation (issue #20)

End-to-end walkthrough of the guided skill-creation workflow. Works in any conversation language (e.g. Estonian) — the created skill file itself must be authored in English.

**Prompt (or Estonian equivalent):**
```
I want to create a skill that scans our delivery folder and reports which sheets are missing PDFs.
```

**Verify:**
1. The AI calls `revit_skill_builder_guide` first and follows its steps.
2. It calls `revit_list_skill_tasks` (and `revit_list_skills`) to learn the available building blocks; if an existing skill already covers the goal it suggests that instead.
3. It asks follow-up questions (folder path, file extensions, report format, titles) — repeating until every required setting is confirmed, without guessing.
4. It presents a draft summary (name, description, ordered tasks + settings) in the user's language and waits for confirmation.
5. `revit_create_skill` is called with English-only content and a `user.`-prefixed id → `approval_required` → after approval the `.skill.json` file exists in the skill library and `revit_list_skills` shows the new skill.
6. The AI tells the user how to activate it: "Ask: run skill '<name>'" (`revit_run_skill`), with `revit_preview_skill_run` as the dry run.
7. Editing later: "Rename my skill / change the folder" → AI fetches `revit_get_skill_details`, then `revit_update_skill` → `updatedFields` returned and the file is rewritten.
8. Guard rails: creating with an existing company-master id fails with a clear message; `revit_update_skill` on a company master points to `revit_manage_project_skill_override`; unknown task ids are rejected listing the available catalog.

## 41. Dimension Placement Tools (issue #21)

> `revit_place_dimensions` is RequiresApproval — Direct Edit bypasses the approval gate like other RA writers. Every run is a single named transaction (`"Revit MCP - Place Dimensions"`) reversible with Revit Undo.

### 41.1 Aligned / Horizontal / Vertical

**Prompts:**
```
Dimension between these two walls.
Place a horizontal dimension across grids 1, 2 and 3, 1500mm below them.
```

**Verify:**
1. `revit_list_dimension_types` returns types grouped by style; `dimensionTypeId` is honored when passed.
2. One dimension is created referencing all given elements; `offsetMm` moves the dimension line away from the elements (negative flips the side).
3. Wall references resolve to centerlines, grids/levels to their datum line, family instances to their center reference planes, model/detail lines to their curve.
4. Elements without a resolvable reference are skipped with a per-element warning naming the element.

### 41.2 Angular / Radial / Diameter / Arc Length

**Prompts:**
```
Place an angular dimension between these two walls.
Add a radius dimension to this arc wall.
```

**Verify:**
1. Angular needs exactly 2 non-parallel linear elements; the dimension arc radius follows `offsetMm`.
2. Radial/diameter/arcLength need arc geometry — straight elements produce a clear warning.
3. On the Revit 2024 (net48) build, radial/diameter/arcLength report "requires Revit 2025 or newer" instead of failing silently.

### 41.3 Spot Elevation / Spot Coordinate

**Prompt:**
```
Place spot elevations on these floors with a 800mm leader.
```

**Verify:**
1. One spot dimension per element at its measure point; `offsetMm` sets the leader bend distance, `leaderLengthMm` the horizontal leader segment.
2. Spot slope requests are rejected with a clear "not supported by the Revit API" message.

---

## 42. Linked Model Selection Tools (issue #22)

### 42.1 Query Linked Elements

**Prompt:**
```
Find all walls in the linked IFC model.
```

**Verify:**
1. `revit_list_clashable_links` / `ifc_list_links` provide the `linkInstanceId`.
2. `revit_query_linked_elements` requires category and/or nameFilter; unloaded links produce a clear "load it in Manage Links" error.
3. Returned `elementIds` are ids inside the linked document; `totalMatched` vs `returned` reflects the `limit` cap.

### 42.2 Select Linked Elements

**Prompt:**
```
Select those linked walls in Revit.
```

**Verify:**
1. `revit_select_linked_elements` highlights the linked elements in the host UI exactly like a manual pick (`Selection.SetReferences`).
2. `replaceSelection=false` keeps both existing host-element and linked-element selections.
3. `zoomToSelection=true` frames the linked elements in the active view (link transform applied).
4. Invalid ids are reported with a count and sample list.

### 42.3 Read Linked Selection

**Prompt:**
```
What do I have selected? (after manually picking elements inside a link)
```

**Verify:**
- `revit_get_selected_elements` returns host picks in `elements` and linked picks in `linkedElements` (linkInstanceId, linkName, elementId, category, family, type, name).

### Matrix rows (Section 30)

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Docs | `revit_list_dimension_types` | RO | "List dimension types." | Array of `{typeId, name, style}` | N/A | N/A | `styleFilter` narrows |
| Docs | `revit_place_dimensions` | RA | "Dimension between walls A and B." | `approval_required` → dimension created, `createdCount` ≥ 1 | Required (DE bypasses) | Single Undo | Per-element warnings for unresolvable references; radial/diameter/arcLength need Revit 2025+ |
| Selection | `revit_query_linked_elements` | RO | "Find walls in link `<id>`." | `{linkName, totalMatched, elements[]}` with linked-doc ids | N/A | N/A | Requires category and/or nameFilter |
| Selection | `revit_select_linked_elements` | DE | "Select linked elements [ids] in link `<id>`." | Elements highlighted in host UI, `selectedCount` | N/A | N/A | Uses link references (SetReferences); zoom transforms link coordinates |

---

## 45. Extensible Storage Queries (issue #29)

Requires a model containing data written by another add-in. A model where
SmartTags-compatible tags were placed by this connector works as a fallback: it
carries the `SmartTagsMarker` schema (`A7B3C4D5-E6F7-8901-2345-6789ABCDEF01`).

### 45.1 Schema Discovery

**Prompt:**
```
List the extensible storage schemas used in this model.
```

**Verify:**
1. `revit_list_extensible_storage_schemas` returns `schemaGuid`, `schemaName`, `vendorId`, both access levels, and `readAccessGranted`.
2. `fields[]` reports `fieldName`, `valueType`, `containerType` (Simple/Array/Map), and `isMeasurable` per field.
3. `elementCount` is present by default and is 0 for schemas loaded in the session but unused in this model.
4. `onlyUsedInDocument=true` drops the zero-count schemas; `includeFields=false` drops the field arrays.
5. A model with no extensible storage still returns `success: true` with a warning, not an error.

### 45.2 Read by Document Scan

**Prompt:**
```
Read the values stored under schema <guid> in this model.
```

**Verify:**
1. `revit_read_extensible_storage` with `scanDocument=true` finds every carrier, including `DataStorage` elements (`elementClass: "DataStorage"`, empty category) that no category browse would reach.
2. `fields` holds decoded values; arrays come back as JSON arrays and maps as `{key, value}` pairs.
3. Measurable fields report their unit in `fieldUnits`, and the value matches the document's display unit.
4. `scanDocument=true` without `schemaGuid`/`schemaName` fails with a message pointing at the schema list tool.

### 45.3 Read by Selection and Ids

**Prompt:**
```
What extensible storage data is on the elements I have selected?
```

**Verify:**
1. `useSelection=true` reads the current selection; omitting the schema filter returns every schema present on those elements.
2. `elementIds` works the same way; missing ids produce per-id warnings without failing the call.
3. Elements carrying no entity are omitted rather than returned empty.
4. `includeValues=false` lists only which schemas each element carries.

### 45.4 Access, Limits, and Failure Handling

**Verify:**
1. A `Vendor`- or `Application`-level schema owned by another add-in returns `readAccessGranted: false` with an explanatory error, and the call still succeeds.
2. An element carrying a schema not loaded in the session is reported with an explanatory error, not a crash.
3. `maxElements` caps results (default 25, hard cap 500); `totalElementsMatched` reveals how many matched.
4. Nested sub-entities are followed to `maxDepth` (default 3); beyond that the value is `{truncated: true}`.
5. No transaction is opened and the model is never modified — the Revit undo stack stays unchanged after every call in this section.

### Matrix rows (Section 30)

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Query | `revit_list_extensible_storage_schemas` | RO | "List the extensible storage schemas used in this model." | `{schemaCount, schemas[]}` with fields and `elementCount` | N/A | N/A | Session-loaded schemas only; `onlyUsedInDocument` filters to this model |
| Query | `revit_read_extensible_storage` | RO | "Read schema `<guid>` values in this model." | `{elementCount, entityCount, elements[]}` with decoded `fields` | N/A | N/A | `scanDocument` needs a schema; finds `DataStorage`; units in `fieldUnits` |

---

## 46. Family Types — Duplicate, Rename, Edit (issue #31)

Requires a model with at least one loadable family (e.g. a door or lighting device)
and one system family (e.g. Basic Wall). Section 30's matrix rows are at the end.

### 46.1 Type Discovery

**Prompt:**
```
List the lighting device types in this model with their parameters.
```

**Verify:**
1. `revit_list_family_types` returns `typeId`, `familyName`, `typeName`, `category`, and `kind` (`Loadable` / `System`).
2. `category`, `familyName`, `typeName`, and `typeIds` all narrow the result; an unknown category fails with category suggestions.
3. `includeParameters=true` lists only writable type parameters with their current display values, for the first 25 types (a warning names the cap).
4. `includeInstanceCounts=true` reports placed instances per type; types with no instances report 0.
5. `includeSystemTypes=false` drops wall/duct/pipe types; `includeLoadableTypes=false` drops family symbols; both false is refused.
6. `limit` caps the result and `totalMatched` reveals how many matched.

### 46.2 Duplicate Preview

**Prompt:**
```
Preview duplicating type <id> as "<name>" with Width = 200 mm.
```

**Verify:**
1. `revit_preview_duplicate` with `entity=familyTypes` opens no transaction and leaves the undo stack unchanged.
2. Each proposal reports `requestedName`, the resolved `newTypeName`, and `renamedForUniqueness` when the name was already taken.
3. Per parameter, `status` is `willSet`, `notFound`, `readOnly`, or `invalidValue`, with the current value alongside.
4. An invalid name (empty, padded, or containing a character Revit forbids in names — `` \ : { } [ ] | ; < > ? ` ~ ``) comes back as `canDuplicate: false` with the reason.
5. The preview's names and parameter verdicts match what the write tool then does.

### 46.3 Duplicate and Set Values

**Prompt:**
```
Duplicate type <id> into DN100 and DN150 with matching diameters.
```

**Verify:**
1. `revit_duplicate` with `entity=familyTypes` requires approval; rejecting it leaves the model unchanged.
2. `variants` creates one copy per entry with its own name and values; `newTypeNames` names one copy per source type; `nameSuffix`/`namePrefix` with `numberOfCopies` covers the bulk case, and `{index}` is substituted in the affixes.
3. Numeric values pass through project display units: `"200"` and `"200 mm"` both produce 200 mm in a metric model.
4. A parameter that cannot be set is reported per copy and as a warning; the copy is still created.
5. `requireAllParameters=true` rolls that copy back instead and reports `created: false` with the reason.
6. A single Revit undo removes every type created by one call.
7. Duplicating a system type (e.g. a wall type) works the same way as a loadable type.

### 46.4 Rename and Edit Existing Types

**Prompt:**
```
Rename type <id> to "<name>" and set its Comments.
```

**Verify:**
1. `revit_preview_edit_family_types` reports the planned rename and parameter writes without touching the model.
2. `revit_edit_family_types` requires approval and renames the type for every placed instance.
3. `edits: [{typeId, newName, parameters}]` gives each type its own name and values; `typeIds` + `parameters` applies the same values to all of them.
4. Renaming onto a name already used in the family is blocked with a reason — it is never silently renamed to something else.
5. A `newName` equal to the current name is reported as a no-op rather than an error.
6. `newName` with more than one resolved type is refused, pointing at the `edits` form.
7. A request with neither `newName` nor `parameters` fails with "nothing to do".

### Matrix rows (Section 30)

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Query | `revit_list_family_types` | RO | "List the lighting device types in this model." | `{totalMatched, returned, types[]}` with `typeId`, `kind` | N/A | N/A | `includeParameters` capped at 25 types; `includeInstanceCounts` scans the model |
| Elements | `revit_preview_duplicate` (`entity=familyTypes`) | RO | "Preview duplicating type `<id>` as `<name>`." | `{total, canDuplicate, proposals[]}` with resolved names | N/A | N/A | Same plan the write tool executes |
| Elements | `revit_duplicate` (`entity=familyTypes`) | Approval | "Duplicate type `<id>` into DN100 and DN150." | `{created, parametersSet, results[]}` | Yes | Yes | `variants` / `newTypeNames` / `nameSuffix`; `requireAllParameters` rolls a copy back |
| Elements | `revit_preview_edit_family_types` | RO | "Preview renaming type `<id>` to `<name>`." | `{canEdit, plannedRenames, plannedParameterWrites, proposals[]}` | N/A | N/A | Name collisions are reported, not resolved |
| Elements | `revit_edit_family_types` | Approval | "Rename type `<id>` and set its Comments." | `{edited, renamed, parametersSet, results[]}` | Yes | Yes | Rename affects every placed instance |
