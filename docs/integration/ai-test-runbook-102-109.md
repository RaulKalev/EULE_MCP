# AI test runbook: issues #102–#104, #106–#109

This runbook is for an AI agent (Claude Code or another MCP client) that tests the seven domain-gap features in
Revit on a machine where nobody built the code. It assumes no knowledge of the source. A human operator sets up
Revit and the test model, then the agent runs every phase below in order and writes one results file.

Background reading, optional: the [test plan](revit-test-plan-102-109.md), the first
[test results](revit-test-results-2026-10-25.md) and the per-issue notes `issue-<n>.md` in this folder.

## Operator inputs

The operator fills these in before the agent starts, in the chat or at the top of the results file. A test whose
input is missing is reported as `skipped: input missing`, never guessed.

| Input | Meaning | Required |
|---|---|---|
| `TEST_MODEL_PATH` | Full path of a **disposable copy** of an EN model, opened in Revit | yes |
| `REVIT_VERSIONS` | `2026`, `2024` or both | yes |
| `RFA_FOLDER` | A folder with 2–3 `.rfa` files, one of them already loaded in the test model | for #102 load tests |
| `SHARED_PARAMETER_FILE` | A shared parameter file the agent may read; a copy, since one test may write to it | for #102 shared tests |
| `MACHINE` | A short name for this machine, used in the results file name | yes |

The test model should have: at least two levels with floor plans, rooms (in the host or in a linked architecture
model), at least one 3D view without a section box, data devices or fire alarm devices, one legend, one sheet with a
plan viewport, the company cable tray and conduit types, and at least one detail group with a placed instance. The
EULE EN template models have all of this.

## Operating rules for the agent

1. **Only the test copy.** Before the first write, `revit_get_connection_status` must report `localModelPath` equal to
   `TEST_MODEL_PATH`. If it does not, stop and tell the operator. Never write to a model in a project folder
   (for example a path containing `__FAILIVAHETUS` or `1_Projektid`).
2. **Read the schema first.** Call `revit_tools_describe` for every tool before its first use. The arguments in this
   runbook are examples; the schema wins. Call non-advertised tools with `revit_tools_call`.
3. **Preview before every write.** Run the matching `revit_preview_*` tool first and check it before the write.
4. **Find ids with the graph.** Use `revit_graph_query` to find ids, then live reads by id. Never quote graph values
   as facts.
5. **Track changes.** At the start of phase 4, create a checkpoint with `revit_checkpoint_create` covering the
   categories you will touch. After each area, run `revit_checkpoint_diff` and confirm only the expected elements
   changed.
6. **Approvals.** In Direct Edit mode writes run without a prompt, which suits a test copy. The two destructive tools,
   `revit_purge_unused` and `revit_ungroup_and_delete_type`, always wait for manual approval in the connector window.
   Tell the operator before calling them.
7. **Undo is the operator's.** The agent cannot press Undo. Tests marked *(operator)* ask the operator to check
   something in Revit and report back; if nobody answers, record `not checked`.
8. **Large responses.** If a response is too large to read, save it to a file and summarise it. Report the size as a
   finding when it is over about 30 KB.
9. **Name everything you create** with the prefix `AI-TEST`, so the operator can find and delete it afterwards.
10. **Record as you go.** After each test, add its row to the results file. Do not wait for the end.

## Phase 0: connect and check the build

1. `revit_list_instances`. If several Revit windows run, ask the operator which one is the test, then
   `revit_select_instance processId=<pid>`.
2. `revit_get_connection_status`. Check all of these:
   - `addin.commit` and `bridge.commit` are equal, and both `matchesPackage` are true.
   - The commit is `e24b688` or later. If not, stop: the machine runs an old build. The fix is to reinstall from
     the Dropbox package and restart Revit and the AI client.
   - `localModelPath` equals `TEST_MODEL_PATH`.
   - Record `revitVersion`, `documentTitle` and `permissionMode`.
3. `revit_graph_status`. Expect `stale: true` with a schema reason on the first run after the update. Then
   `revit_graph_build` and record the node count and seconds.
4. Create the results file `docs/integration/revit-test-results-<yyyy-MM-dd>-<MACHINE>.md` from the template at the
   end of this runbook, or keep it locally and hand it to the operator if the repository is not on this machine.

## Phase 1: read-only smoke pass

Every call should return `success: true`. None may leave an undo entry. Record duration and response size.

| ID | Call | Expected |
|---|---|---|
| R1 | `revit_list_families category="Data Devices" includeTypes=true` | Families with type and instance counts matching `revit_count_elements category="Data Devices"` |
| R2 | `revit_list_project_parameters` | All bindings with data type, group, binding and categories |
| R3 | `revit_list_shared_parameters` (and with `sharedParameterFile=<SHARED_PARAMETER_FILE>`) | Groups and GUIDs. Afterwards Revit's shared parameter setting is unchanged *(operator: Manage > Shared Parameters)* |
| R4 | `revit_preview_purge_unused` | Counts, `byCategory`, at most 25 entries per list, every id in `elementIds`, a list of protected types |
| R5 | `revit_list_schedules`, then `revit_get_schedule schedule=<one> include=all pageSize=10` | Definition plus rows. The heading row is in `headingRows`, not in `rows` |
| R6 | `revit_list_schedulable_fields category="Data Devices"` | At most 150 fields, `truncated` and a warning when there are more |
| R7 | `revit_list_view_family_types` | View types, area schemes, legends |
| R8 | `revit_list_mep_curve_types` | Tray and conduit types with fitting availability; conduit diameters per standard |
| R9 | `revit_get_mep_run_summary` | Totals, open ends and disconnected segments (zero is fine in a model without runs) |
| R10 | `revit_get_element_geometry elementIds=[<a device>]` | Location, box, facing, level in host mm. `elementIds=[]` gives `geometry_query_too_broad` |
| R11 | `revit_find_elements_near elementId=<device> radiusMm=3000 categories=["Data Devices","Fire Alarm Devices"]` | Sorted by distance |
| R12 | `revit_find_elements_near elementId=<device> radiusMm=5000 categories=["Walls"] includeLinks=true` | Linked walls with `linkInstanceId` |
| R13 | `revit_find_elements_in_box aroundElementId=<device> paddingMm=2000 categories=["Data Devices"]` | Elements whose box intersects |
| R14 | `revit_list_groups` | Group types with kind, instance count, members |
| R15 | `revit_list_phases countElements=true`, `revit_list_design_options countElements=true` | Phases in order with phase filters; options or `design_option_set_only` |
| R16 | `revit_get_elements_info elementIds=[3 ids] includeModelState=true compact=true`, then the same without `includeModelState` | First: `PhaseCreated`, `PhaseDemolished`, `DesignOption`, `Workset`. Second: none of those keys |
| R17 | `revit_graph_query operation=find kind=element category="Data Devices" phase=<a phase name> link=host` and `revit_graph_summary` | Ids with `extra.phase`; summary has `elementsByPhase` |
| R18 | `revit_audit_model` | 14 sections with ok/warn/bad, a page URL, at most 20 items per section. Under 30 s |

## Phase 2: regression checks on changed tools

| ID | Check | Expected |
|---|---|---|
| G1 | `revit_preview_align_elements surface=ceiling elementIds=[<ceiling device>]` | Finds the ceiling (often a linked one) and reports gap and move distance |
| G2 | `revit_preview_place_on_face` with one ceiling point and a face-based family | Finds the face, as before the refactor |
| G3 | `revit_inspect_selected_elements` with one element selected *(ask the operator to select one)* | Both the old `boundingBoxMm` and the new `bbox` |
| G4 | `revit_get_linked_elements_in_room categories=["Data Devices"] roomNumber=<room>` | The old flat `bbox.minX…maxZ` fields are still present |
| G5 | `revit_find_elements_by_parameter category="Data Devices" filters=[{"parameterName":"Phase Created","operator":"equals","value":"<phase>"}]` | Filtering on the pseudo-parameter works |

## Phase 3: verify the fixes from the first run

These failed in the first run and were fixed in `e24b688`. Each needs a clear pass or fail.

| ID | Check | Pass when |
|---|---|---|
| F1 | `revit_preview_create_views views=[{"kind":"section","name":"AI-TEST S W-E","from":{"x":X1,"y":Y,"z":0},"to":{"x":X2,"y":Y,"z":0},"depthMm":3000}]` with X1 < X2 through a room | `views[0].actual.lookDirection` is about (0, +1, 0), `cutPlaneOffsetFromLineMm` is about 0, and there are no direction warnings |
| F2 | The same with `"flip":true` | `lookDirection` is about (0, −1, 0), offset about 0, no warnings |
| F3 | `revit_find_nearest_surface elementId=<wall-mounted device> surfaces=["wall"]` **without** `searchViewId`, with a plan as the active view | The result names an unclipped 3D view, and `best.wall` is found. If no wall is found, the warnings must say which links are hidden in that view |
| F4 | `revit_preview_purge_unused`, `revit_list_schedulable_fields`, `revit_audit_model` with defaults | Each response is under about 30 KB |

## Phase 4: write tests on the test copy

Start with `revit_checkpoint_create` covering Data Devices, Fire Alarm Devices, Views, Schedules, Cable Trays,
Conduits and Detail Groups. Find a room on the first level with three or more data devices (graph `subtree` or
`find`), and note its plan view id and centre point in mm.

### #108 Transforms and groups

| ID | Step | Pass when |
|---|---|---|
| W1 | `revit_preview_mirror_elements` then `revit_mirror_elements` on the room's devices, `copy=true`, `plane={"viewId":<plan>,"fromMm":{"x":LX,"y":Y1},"toMm":{"x":LX,"y":Y2}}` with a vertical line at x = LX | One new id per device; each copy's x equals 2·LX − original x within 1 mm (check with `revit_get_element_geometry`); facing mirrored |
| W2 | Mirror with `plane={"axis":"z","throughMm":{...}}` and no `allowHorizontalPlane` | Refused |
| W3 | `revit_array_elements kind=linear count=4 spacingMm={"x":1200,"y":0,"z":0}` on one device | 3 new elements, 1200 mm apart along x |
| W4 | `revit_array_elements kind=radial count=6 totalAngleDeg=360 centerMm=<room centre>` on one device | 5 new elements, 60° apart around the centre |
| W5 | `revit_preview_create_group` then `revit_create_group name="AI-TEST group"` with the W3 copies | One model group with 3 members |
| W6 | `revit_place_group_instances groupType="AI-TEST group" placements=[{"pointMm":{...},"rotationDeg":90}]` | Placed on the nearest level, rotated 90° |
| W7 | `revit_move_elements` with the placed group's instance id and `deltaXmm=500` | Status `GroupMoved`; the whole group moved |
| W8 | `revit_move_elements` with one member id | Status `InGroup` with the group id and a hint |
| W9 | `revit_ungroup groupIds=[<the W6 instance>]` | Members returned; the group instance is gone |
| W10 | `revit_ungroup_and_delete_type groupType="AI-TEST group"` *(operator approves)* | Instances ungrouped and the type deleted |
| W11 | `revit_place_group_instances` with an existing detail group type and the `viewId` of its owner view | Placed; the result says the path was `copiedFromInstance` |

### #104 View creation

| ID | Step | Pass when |
|---|---|---|
| W12 | `revit_create_views views=[{"kind":"floorPlan","name":"AI-TEST {level}","levels":[<first level name>]}]` | One plan on that level |
| W13 | Rerun W12 with `ifNameExists="fail"` | `invalid` with `view_name_exists`; nothing created |
| W14 | `revit_create_views` with the F1 section spec | Created; `actual` as in F1 *(operator: it looks north and cuts on the line)* |
| W15 | `revit_create_views views=[{"kind":"threeD","name":"AI-TEST 3D","aroundElements":[<device>],"paddingMm":500}]` | Section box around the device plus 500 mm |
| W16 | `revit_create_views views=[{"kind":"drafting","name":"AI-TEST drafting"},{"kind":"legend","name":"AI-TEST legend","sourceLegend":"<a legend>"}]` | Both created; the legend keeps its content |
| W17 | `revit_create_views views=[{"kind":"callout","parentView":"<W12 plan>","aroundRoom":<room id>}]` | A callout around the room |
| W18 | `revit_preview_set_view_extent` then `revit_set_view_extent viewId=<W12 plan> room=<room id> paddingMm=500` (add `roomLinkInstanceId` for a linked room) | The crop is the room plus 500 mm |

### #103 Schedules

| ID | Step | Pass when |
|---|---|---|
| W19 | `revit_preview_create_schedule`, then `revit_create_schedule name="AI-TEST socket list" category="Data Devices" fields=["Family","Type","Level","Mark","Comments",{"count":true}] filters=[{"field":"Level","type":"equals","value":"<first level>"}] sort=[{"field":"Level","header":true,"footer":true},"Mark"] grandTotal="titleCountTotals"` | Created with those fields in that order *(operator: identical to building it by hand)* |
| W20 | `revit_get_schedule schedule="AI-TEST socket list" include=rows` | The data rows match `revit_count_elements` for Data Devices on that level |
| W21 | `revit_preview_edit_schedule`, then `revit_edit_schedule` adding a field at index 1 and a second filter | `fieldsAfter` shows the new order; the edit applies |
| W22 | `revit_place_schedule_on_sheet schedule="AI-TEST socket list" sheet=<sheet number> nextTo={"viewportId":<id>,"side":"right","gapMm":10}` | Placed with no overlap; a second call is refused without `allowDuplicate` |
| W23 | `revit_export_schedule format=csv delimiter=";"`, then `format=xlsx`, then `folder="C:\\Windows"` | Two files in the default export folder; the third call refused by the file policy |

### #106 Cable trays and conduits

| ID | Step | Pass when |
|---|---|---|
| W24 | `revit_preview_create_mep_run`, then `revit_create_mep_run kind=cableTray type=<a tray type with fittings> level=<first level> offsetMm=2800 widthMm=300 heightMm=60 points=[5 points, 4 straight legs, no z]` | 4 segments and 3 elbows, all `created` |
| W25 | `revit_get_mep_run_summary runId=<W24 runId>` | 2 open ends (the run ends), 0 disconnected segments, length equals the drawn polyline within 1 mm per segment |
| W26 | A conduit run with one 45° bend on a conduit type whose list entry says `missingElbows: true` | Segments created; that bend is `fitting_failed` with Revit's message |
| W27 | A second tray run starting within 50 mm of W24's open end with `connectStart="auto"` | `connected` |
| W28 | `revit_connect_mep_curves` on two conduit segments end to end in line | `connected`; on parallel offset segments, `skew_lines` |

### #102 Families and parameters

| ID | Step | Pass when |
|---|---|---|
| W29 | `revit_preview_load_family folder=<RFA_FOLDER>`, then `revit_load_family folder=<RFA_FOLDER>` | The already loaded one is `skipped`, the others `loaded`; a damaged file fails without undoing the rest |
| W30 | `revit_list_families nameContains=<a loaded family>` | `sourcePath` is the RFA path |
| W31 | `revit_reload_family family=<that family>` (no path) | Reloads from the recorded path |
| W32 | `revit_preview_create_parameters`, then `revit_create_parameters parameters=[{"name":"AI-TEST Text","source":"project","dataType":"text","parameterGroup":"Data","binding":"instance","categories":["Data Devices"]}]` | Created; the result says it is a shared definition in the connector's scratch file; `revit_get_element_parameters` on a device shows it |
| W33 | `revit_bind_parameter_categories name="AI-TEST Text" addCategories=["Fire Alarm Devices"]`, then a call removing every category | First extends the binding; second refused |
| W34 | With `SHARED_PARAMETER_FILE`: create one shared parameter from it; then ask for a missing name | First `willCreate` and created; second `missingDefinition`; the file is unchanged (size and time) |
| W35 | `revit_preview_purge_unused categories=["Detail Items"]`, then `revit_purge_unused elementIds=<the first 5 ids>` *(operator approves)* | 5 removed; result shape `deleted`, `removedAsDependent`, `failed` |

### #109 Model state

| ID | Step | Pass when |
|---|---|---|
| W36 | `revit_preview_set_phase`, then `revit_set_phase elementIds=<the W3 copies> phaseDemolished=<last phase>` | Before and after per element; then `includeModelState` shows the demolished phase |
| W37 | `revit_preview_set_phase` with `phaseCreated` later than `phaseDemolished` | `phase_order_invalid` |
| W38 | `revit_preview_set_phase` with an annotation id (a tag or text note) | That id `skipped` |
| W39 | `revit_set_view_phase viewIds=[<W12 plan>] phase=<a phase>` | Set; a view whose template controls the phase gives `controlled_by_template` |
| W40 | `revit_audit_model` again | Counts reflect the new views, schedule and parameter |

Finish with `revit_checkpoint_diff checkpointId=<the phase 4 checkpoint>`. Every change should come from an
`agent:` tool and match the steps above.

## Phase 5: Revit 2024

If `REVIT_VERSIONS` includes 2024, ask the operator to open the same test copy in Revit 2024. Rerun phase 0, phase 1,
F1–F3, W12, W14, W19, W24 and W1. Record the 2024 results in their own column.

## Phase 6: clean up and report

1. Ask the operator whether to keep the test copy. Do not delete anything yourself.
2. List everything named `AI-TEST` in the results file, so it can be removed by hand.
3. For every failure, record: test ID, the exact call and arguments, the relevant part of the response, expected
   versus actual, and the Revit version.
4. Give the operator a short summary: passed, failed, skipped, not checked, and the three most important failures.

## Results file template

```markdown
# Revit test results, <yyyy-MM-dd>, <MACHINE>

- Build: addin <commit>, bridge <commit>, package <version.txt>
- Revit: <version>; model <documentTitle> at <localModelPath>; <elements> elements; permission mode <mode>
- Graph: <nodes> nodes, built in <s> s

| ID | Revit 2026 | Revit 2024 | ms | KB | Notes |
|---|---|---|---:|---:|---|
| R1 | pass |  |  |  |  |

## Failures

### <ID>: <one-line summary>
- Call: `<tool> <arguments>`
- Expected: ...
- Actual: ... (response excerpt)

## Created test elements
- <id> <name> <category>
```

## Known limits that are not bugs

- **Formula fields** cannot be created in schedules: the Revit API has no call to set a formula.
- **Legends** can only be made by duplicating an existing legend.
- **Design option membership** cannot be changed through the API, and views per option are not reported.
- **`source=project` parameters** are shared definitions in a connector-owned scratch file.
- **Reference callouts** create no new view.
- **Phase element counts** are left out above 20 000 elements; the graph summary has them instead.
- **Ray casts** only see what the chosen 3D view shows. Hidden links or a section box hide geometry.
