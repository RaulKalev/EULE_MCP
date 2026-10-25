# Revit test results, 2026-10-25

First live run of the [test plan](revit-test-plan-102-109.md) against build 25b21eb.

- **Revit:** 2026, model 73226_PP_EN (Tarvastu Gümnaasium), not workshared, 34 041 elements, 7 links (6 IFC, 1 RVT).
- **Scope:** read-only calls and previews whose trial transactions are rolled back. No write tool ran, because the
  open model was a live project file and the connector was in Direct Edit mode.
- **Not run:** Revit 2024, the write tests, and part of the regression pass. Revit was closed before the align
  preview and the linked-elements-in-room check.

## Results

| Area | Call | Result |
|---|---|---|
| #102 | `revit_list_families`, `revit_list_project_parameters`, `revit_list_shared_parameters` | Pass. 72 bindings; the shared file is read from Revit's setting. |
| #102 | `revit_preview_purge_unused` | Pass, but the response was 99 KB. Counts are consistent: 307 loose types and 105 whole families make up the 412 ids, confirmed by Revit's unused list. |
| #103 | `revit_list_schedules`, `revit_get_schedule include=all` | Pass. Rows are paged and the heading row is kept apart from the data. |
| #103 | `revit_list_schedulable_fields` | Pass, but 406 fields came back in 61 KB. |
| #103 | `revit_preview_create_schedule` with a misspelt field | Pass. `field_not_found`, suggests "Comments". |
| #104 | `revit_list_view_family_types` | Pass. 19 types, 2 area schemes, 19 legends. |
| #104 | `revit_preview_create_views` | Plans per level, 3D box, legend duplication, `legend_source_required` and `view_family_type_incompatible` pass. **The section looked the wrong way** (see below). |
| #106 | `revit_list_mep_curve_types`, `revit_get_mep_run_summary` | Pass. Tray and conduit fittings from the company template; the model has no trays or conduits yet. |
| #107 | `revit_get_element_geometry`, `revit_find_elements_near`, `revit_find_elements_in_box` | Pass. Distances in host mm; link elements carry their link. Missing ids give `geometry_query_too_broad`. |
| #107 | `revit_find_nearest_surface`, `revit_measure_clearance`, `revit_check_line_of_sight` | Pass in the EXPORT 3D view. **With no view given, every ray missed** (see below). |
| #108 | `revit_list_groups` | Pass. 34 detail groups. Sketch helper elements show up among member categories. |
| #109 | `revit_list_phases`, `revit_list_design_options` | Pass. Element counts are omitted above the 20 000-element scan cap, with a pointer to the graph summary. |
| #109 | `includeModelState` on `revit_get_elements_info` | Pass. The default payload is unchanged. |
| #109 | Graph rebuild after the schema change, `find phase=`, `elementsByPhase` | Pass. 32 743 nodes in 15 s. |
| #109 | `revit_audit_model` | Pass in 0.4 s, 14 sections, page published. The response was 54 KB. |

## Fixed after the run

1. **Section direction (#104).** Revit looks along the section box's +Z axis from its minimum Z face, and reports the
   view direction as −Z. The first build assumed the opposite, so a west → east section looked south and cut 3000 mm
   off the line. `SectionBoxMath` now makes Z the look direction and runs the box from 0 to the depth.
2. **Ray-cast view choice (#107, also align and place on face).** With no active 3D view the tools took the first 3D
   view, {3D}, whose section box hid the surroundings. They now prefer an isometric view without a section box and
   warn about perspective views and hidden links.
3. **Oversized responses.** The purge preview lists 25 entries per list with counts per category (`listAll=true` for
   all). Schedulable fields take `limit`, default 150. The audit JSON lists 20 rows per section (`itemsPerSection`);
   the page keeps every row.

## Still to check in Revit

- The section fix: a west → east section looks north, with the cut plane on the line.
- The ray tools without `searchViewId` pick EXPORT (or another unclipped view) and hit walls.
- In the EXPORT and perspective views the architecture IFC links seemed hidden, so rays hit only the structural link.
  The new hidden-link warning should name them.
- Every write test in the test plan, on a test copy of a model.
