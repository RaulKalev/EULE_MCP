# Revit test plan: issues #102–#104, #106–#109

All seven domain gaps are built on one branch. Both Revit targets build and the unit tests pass, but none of it
has run inside Revit yet. Run this plan in Revit 2026 first, then repeat the smoke pass in Revit 2024.

| Issue | Area | New tools | Integration notes |
|---|---|---:|---|
| #102 | Families and parameters | 11 | [issue-102.md](issue-102.md) |
| #103 | Schedules | 8 | [issue-103.md](issue-103.md) |
| #104 | View creation | 5 | [issue-104.md](issue-104.md) |
| #106 | MEP routing: cable trays and conduits | 5 | [issue-106.md](issue-106.md) |
| #107 | Geometry queries | 6 | [issue-107.md](issue-107.md) |
| #108 | Transforms and groups | 11 | [issue-108.md](issue-108.md) |
| #109 | Model state and health audit | 6, plus `includeModelState` on three query tools | [issue-109.md](issue-109.md) |

Each integration note holds the full manual test for its issue, the deviations from the issue text and the API
notes per target.

## Before you start

1. Build Release and deploy the add-in as usual, then start the connector in Revit.
2. Use a test copy of an EN model that has: a linked architecture model with rooms, two phases, one design option
   set, worksharing on, at least one legend, a few unused family types, and cable tray and conduit types.
3. The model graph schema changed (version 2 to 3), so the first graph call reports the graph as stale and
   rebuilds it. That is expected.
4. Create a checkpoint before the write tests (`revit_checkpoint_create`) so `revit_checkpoint_diff` can show what
   each test changed.

## Smoke pass (read-only, about 10 minutes)

Every call should return `success: true` and leave no undo entry.

| Issue | Call |
|---|---|
| #102 | `revit_list_families category="Electrical Fixtures"` |
| #102 | `revit_list_project_parameters` and `revit_list_shared_parameters` |
| #102 | `revit_preview_purge_unused` |
| #103 | `revit_list_schedulable_fields category="Electrical Fixtures"` |
| #103 | `revit_get_schedule schedule=<an existing schedule> include=all pageSize=10` |
| #104 | `revit_list_view_family_types` |
| #104 | `revit_preview_create_views` with the per-level example of `docs/view-creation.md` |
| #106 | `revit_list_mep_curve_types` and `revit_get_mep_run_summary` |
| #107 | `revit_get_element_geometry elementIds=[<a detector>]` |
| #107 | `revit_find_elements_near elementId=<panel> radiusMm=3000 categories=["Lighting Fixtures"]` |
| #107 | `revit_find_nearest_surface elementId=<socket> surfaces=["wall"]` |
| #108 | `revit_list_groups` and `revit_preview_mirror_elements` on a few devices |
| #109 | `revit_list_phases countElements=true` and `revit_list_design_options countElements=true` |
| #109 | `revit_get_elements_info` with and without `includeModelState=true` |
| #109 | `revit_audit_model` (publishes the audit page) |

## Regression checks on existing tools

These existing tools were refactored or extended, so check they behave as before:

- **Align and place on face** now share one ray caster. Rerun the steps in `docs/alignment.md` and
  `docs/place-on-face.md` (#107).
- **Inspect selected elements and linked elements in room** keep their old fields (`boundingBoxMm`, the flat
  `bbox.minX…maxZ`) while `legacy=true`, the default, and add the new `bbox` (#107).
- **Clash review view** now picks its view type and section box through the new view services. Run
  `revit_create_clash_review_view` on a clash (#104).
- **Place views on sheets** now places schedules as schedule instances instead of failing (#103).
- **Move, rotate and copy** act on a whole group when given a group instance id. A group member still reports
  `InGroup`, now with its group id (#108).
- **Find elements by parameter** returns exactly the same payload as before when `includeModelState` is not set (#109).

## Things only Revit can settle

The agents flagged these as uncertain. Each needs a live answer before the feature counts as done.

1. **Section direction (#104).** A section from west to east must look north, with the cut plane on the line. The
   result reports the actual view direction and warns when it disagrees. If it is wrong, the fix is one line in
   `SectionBoxMath.Compute`.
2. **Schedule heading rows (#103).** Check that `revit_get_schedule` drops the column headings and the blank row
   after them, and that the data rows match what the schedule view shows.
3. **Percentage fields (#103).** Record whether Revit accepts a percentage field added through the API.
4. **Tray elbows (#106).** Check that elbows are created for tray types without fittings (`IsWithFitting` false), and
   that the vertical bend parameters are the ones Revit uses for risers.
5. **Conduit standards (#106).** Check that the conduit standard names match `ConduitSizeSettings`, so that
   `size_not_standard` is not raised by mistake.
6. **Purge cross-check (#102).** `GetUnusedElements` is used on both versions as a cross-check on instance counts.
   Compare the preview with Revit's own Purge Unused dialog.
7. **Audit speed (#109).** Time `revit_audit_model` on the largest model you have. The target is under 30 s for
   100 000 elements.

## Write tests

Run the manual steps in each integration note, in this order: #108, #104, #103, #106, #102, #109. The early ones
create the views, groups and runs that later checks can reuse. After each write call:

- Undo once in Revit. The whole call should disappear in one step.
- `revit_checkpoint_diff` should list only the elements that call touched.

Destructive tools need manual approval in the connector window: `revit_purge_unused` and
`revit_ungroup_and_delete_type`.
