# View creation

The connector creates views from a list of specs: one floor plan per level with the company template, scope box,
name pattern and sheet, a section through a corridor from two plan points, a callout around a room, a 3D view around a
panel, a drafting view, a legend copied from an existing one. A second tool sets the crop region or section box of an
existing view from a room, elements, a box or a scope box (#104).

| Tool | Permission | Purpose |
|---|---|---|
| `revit_list_view_family_types` | read-only | The `ViewFamilyType`s per `ViewFamily` with their default template, the document default, the area schemes and the legends |
| `revit_preview_create_views` | read-only | Resolves every spec, fans plans out per level, computes names and section boxes, and (by default) tries the creation in a rolled-back transaction |
| `revit_create_views` | approval | Creates the views: one transaction, one Undo, a sub-transaction per view |
| `revit_preview_set_view_extent` | read-only | The box, the mode and the new crop a `revit_set_view_extent` call would give |
| `revit_set_view_extent` | approval | Crop region (plans, sections, elevations, callouts), section box (3D), scope box, plan view range |

All lengths are millimetres in host model internal coordinates (the same coordinates the read tools return). A
reference to a level, type, template, scope box, view, legend, area scheme or sheet is a **name** (exact,
case-insensitive) or an **element id**; sheets are found by number first, then name, then id.

## Workflow

1. `revit_list_view_family_types` (and `revit_list_view_templates`, `revit_list_sheets`) to pick types, templates and sheets.
2. `revit_preview_create_views` with the specs. Check `views[].status` (`willCreate`, `skipped`, `invalid`), `code`,
   `reason`, the unique `name`, the `sectionBox` in mm and the trial run's `warnings`.
3. `revit_create_views` with the same specs (approval). The result lists per view
   `{index, name, viewId, kind, level, templateApplied, sheetId, viewportId, warnings}`.
4. Adjust existing views with `revit_preview_set_view_extent` / `revit_set_view_extent`.

## The request

```json
{
  "views": [ { "kind": "floorPlan", "...": "..." } ],
  "ifNameExists": "suffix",
  "atomic": false
}
```

- `views` — at most **100 views per call, counted after the fan-out per level**.
- `ifNameExists` — `suffix` (default; `"EN 1. korrus"` becomes `"EN 1. korrus 1"`, the rule of `revit_duplicate`),
  `skip` (status `skipped`, code `view_name_exists`) or `fail` (status `invalid`, code `view_name_exists`). Names are
  compared case-insensitively against every view and view template of the document, and against the names the same
  request gives out.
- `atomic` — `false` (default): each view has its own sub-transaction, so a refusal costs only that view and invalid
  specs are left out. `true`: an invalid spec rejects the whole request before anything opens, and a Revit refusal
  rolls every view back.
- `dryRun` (preview only, default `true`) — run the creation in a transaction that is rolled back, so the preview also
  reports what Revit itself refuses (a view range it cannot take, a template for another view type, a sheet the view
  cannot go on). The trial's ids are meaningless afterwards.

### Common fields (every kind)

| Field | Meaning |
|---|---|
| `kind` | `floorPlan`, `ceilingPlan`, `structuralPlan`, `areaPlan`, `section`, `callout`, `threeD`, `drafting`, `legend` |
| `name` | The view name. `{level}` (plans) and `{n}` (1-based position in the fan-out) are filled in. Omitted: Revit's default name. Revit refuses `\ : { } [ ] \| ; < > ? ` ~` |
| `viewFamilyType` | Type name or id. Omitted: the document's default type of the family (`Document.GetDefaultElementTypeId`), else the first |
| `template` | View template name or id, applied first. The type's default template is applied by Revit when none is given |
| `scale` | `50` or `"1:50"`. Ignored, with a warning, when the template controls the scale |
| `detailLevel` | `Coarse`, `Medium`, `Fine` |
| `discipline` | `Architectural`, `Structural`, `Mechanical`, `Electrical`, `Plumbing`, `Coordination` |
| `phase` | Phase name (the `VIEW_PHASE` parameter) |
| `scopeBox` | Scope box name or id (the `VIEWER_VOLUME_OF_INTEREST_CROP` parameter) |
| `placeOnSheet` | `{sheet, xMm, yMm}` — sheet number, name or id, may contain `{level}` and `{n}`; or `{sheets: [...], xMm, yMm}` with one sheet per fanned-out level, in level order. Without `xMm`/`yMm` the view goes to the sheet's centre. The viewport is created after `doc.Regenerate()` |

Scale, detail level, discipline, phase, scope box and view range are skipped with a warning when the applied template
controls them.

### Plans: `floorPlan`, `ceilingPlan`, `structuralPlan`, `areaPlan`

| Field | Meaning |
|---|---|
| `level` | One level (name or id), or |
| `levels` | Several levels, or `"all"` (every level, by elevation). One view per level |
| `areaScheme` | `areaPlan` only: scheme name or id. Required when the document has more than one (code `area_scheme_required`). An area plan takes its type from the scheme |
| `viewRange` | `{topOffsetMm, cutOffsetMm, bottomOffsetMm, depthOffsetMm}` via `PlanViewRange.SetOffset`; every plane keeps the level it is measured from. A refused view range fails the view |
| `cropTo` | `{room \| elements \| box \| scopeBox, paddingMm}` — a rectangular crop region around a room (`room: id` or `{id, linkInstanceId}`), elements, a box, or a scope box (assigned). Ignored when `scopeBox` is given. `paddingMm` default 500 |

Revit calls: `ViewPlan.Create(doc, typeId, levelId)`, `ViewPlan.CreateAreaPlan(doc, areaSchemeId, levelId)`.

When a spec fans out over several levels and its `name` has neither `{level}` nor `{n}`, the level name is appended
(`"EN plaan"` → `"EN plaan 1. korrus"`), so the views do not all end up as `"EN plaan 1"`, `"EN plaan 2"`, ….

### `section`

| Field | Meaning |
|---|---|
| `from`, `to` | `{x, y, z}` mm — the section line in plan. z only picks the default levels |
| `depthMm` | Far clip distance behind the cut plane, default 3000 |
| `bottomMm` | Absolute elevation of the bottom. Default: the level at or just below (150 mm) the lower point, else the lowest level |
| `heightMm` | Height above the bottom. Default: up to the next level above, or 4000 mm when there is none |
| `flip` | Look the other way |

Revit call: `ViewSection.CreateSection(doc, typeId, sectionBox)`.

#### The section convention

Stand at `from` and look towards `to`:

- The section **looks to your LEFT**. A line drawn west → east (+X) looks north (+Y).
- **`flip: true` reverses it**: the section looks to your **RIGHT**.
- Revit looks along the section box's **+BasisZ**, from its `Min.Z` face (the cut plane) towards its `Max.Z` face
  (the far clip). `View.ViewDirection` points the other way, towards the viewer, so Revit reports it as −BasisZ.
  This was measured in Revit 2026.
- So **BasisZ** is the look direction, **BasisY** is up (model +Z) and **BasisX = BasisY × BasisZ**, which keeps the
  frame right-handed. BasisX is the section view's right direction: it runs against the line without flip, so `from`
  is on the right, and along it with flip, so `to` is on the right.
- The box origin is the midpoint of the line at elevation 0, so local y is the model elevation:
  `Min = (−length/2, bottom, 0)`, `Max = (+length/2, top, depth)`. The cut plane (local z = 0) runs through the drawn
  line and the far clip lies `depthMm` beyond it, in the looking direction.

The preview returns the box as `views[].sectionBox` (`originMm`, `basisX/Y/Z`, `lookDirection`, `minLocalMm`,
`maxLocalMm`, `lengthMm`, `bottomMm`, `topMm`, `depthMm`), so the direction can be checked before anything is made.
After creation the tool compares Revit's actual `ViewDirection` and cut plane with the convention and reports them in
`views[].actual` (`viewDirection`, `lookDirection`, `cutPlaneOffsetFromLineMm`), with a warning if they disagree.

### `callout`

| Field | Meaning |
|---|---|
| `parentView` | The plan, section, elevation or detail view it is drawn in (name or id) |
| `from`, `to` | Two opposite corners in model mm, or |
| `aroundElements` / `aroundRoom` / `box` + `paddingMm` | The padded box of the elements, the room (`id` or `{id, linkInstanceId}`) or the box, projected onto the parent's right/up plane (`paddingMm` default 500) |
| `viewFamilyType` | Default: a Detail type (a CeilingPlan type in a ceiling plan) |
| `referenceView` | Make a reference callout to this existing view instead: no new view, so name, template, scale and sheet do not apply |

Revit calls: `ViewSection.CreateCallout(doc, parentId, typeId, p1, p2)`, `ViewSection.CreateReferenceCallout(doc,
parentId, referenceViewId, p1, p2)`.

Compatibility (from the Revit API): Detail types work in every parent except ceiling plans; FloorPlan, CeilingPlan,
StructuralPlan, Section and Elevation types only in a parent of the same family. An incompatible type is reported in
the preview with code `view_family_type_incompatible` and the list of compatible types in `reason`.

### `threeD`

| Field | Meaning |
|---|---|
| `box` | `{min: {x,y,z}, max: {x,y,z}}` mm, or |
| `aroundElements` / `aroundRoom` + `paddingMm` | The union of the elements' bounding boxes, or the room's box (boundary in plan, its height in z), padded (default 500) |
| `isometric` | `true` (default): `View3D.CreateIsometric`; `false`: `View3D.CreatePerspective` |
| `orientation` | `iso-se` (Revit's default {3D}), `iso-ne`, `iso-nw`, `iso-sw`, `top`, or `{eye, forward, up}` (up is made perpendicular to forward) |

The section box is set with `SetSectionBox` and `IsSectionBoxActive = true`. With a box and no orientation the view
looks iso-se at the box centre. Without a box the view has no section box.

### `drafting`

`ViewDrafting.Create(doc, typeId)`; `scale` applies.

### `legend`

**Revit's API cannot create a legend view.** `sourceLegend` (name or id) is duplicated with
`View.Duplicate(ViewDuplicateOption.WithDetailing)` and renamed; template, scale and sheet placement apply as usual (a
legend can be on several sheets). Without `sourceLegend` the spec is invalid with code `legend_source_required`; when the
model has no legend at all, make one by hand (View > Legends > Legend) first. `revit_list_view_family_types` lists the
legends of the model.

## Status codes

Per view (`views[].code`), and as the result's `status` when nothing can be created:

| Code | Meaning |
|---|---|
| `level_not_found` | A level of `level`/`levels` does not exist (the reason lists the levels) |
| `view_family_type_incompatible` | The callout's type cannot be used in the parent (the reason lists the compatible types) |
| `view_family_type_not_found` | No type of that name or id in the family |
| `legend_source_required` | A legend without (an existing) `sourceLegend` |
| `view_name_exists` | The name is taken and `ifNameExists` is `skip` or `fail` |
| `template_not_found`, `scope_box_not_found`, `sheet_not_found`, `parent_view_not_found`, `phase_not_found`, `area_scheme_required` | A reference does not resolve |
| `extent_not_found` | `cropTo`, `aroundElements`, `aroundRoom` or `box` gives no usable box |
| `invalid_spec` | The spec itself is wrong (missing field, bad value, forbidden name character) |
| `revit_refused` | Revit refused the view in the (trial) run; `reason` holds Revit's message |

## Setting the extent of an existing view

`revit_set_view_extent` / `revit_preview_set_view_extent`:

| Argument | Meaning |
|---|---|
| `viewId` | The view |
| `room` (+ `roomLinkInstanceId`) | A room or space; a linked room is read in its link and moved into host coordinates with the link transform (`RoomSource.ToHostMm`) |
| `elements` | Element ids; the union of their bounding boxes |
| `box` | `{min, max}` in mm |
| `scopeBox` | Name or id: **assigned** to the view (plans, sections, 3D), so the crop follows the scope box; padding does not apply |
| `paddingMm` | Default 500 |
| `cropVisible` | Show the crop region; default false |
| `viewRange` | Plans: `{topOffsetMm, cutOffsetMm, bottomOffsetMm, depthOffsetMm}` |

Plans, sections, elevations and callouts get a rectangular crop box: the box's corners are taken into the view's own
crop frame and the crop keeps its depth (near/far) range; a custom crop shape is removed (the copy code of
`revit_set_view_crop_regions` does it). A scope box that controlled the crop is cleared, with a warning. 3D views get a
section box. The preview returns `boxMm` (model), `newCropLocalMm` (the view's frame) and `currentCrop`.

## Example: EN floor plans for every level, with template, scope box and sheets

The sheets `EN-101` … `EN-103` exist (`revit_create_sheets_from_table`), the template `EN_Plaan` and the scope box
`EN hoone` too.

```json
{
  "views": [
    {
      "kind": "floorPlan",
      "name": "EN {level}",
      "levels": "all",
      "template": "EN_Plaan",
      "scopeBox": "EN hoone",
      "discipline": "Electrical",
      "viewRange": { "topOffsetMm": 3000, "cutOffsetMm": 1200, "bottomOffsetMm": 0, "depthOffsetMm": 0 },
      "placeOnSheet": { "sheet": "EN-10{n}", "xMm": 420, "yMm": 297 }
    },
    {
      "kind": "ceilingPlan",
      "name": "EN lagi {level}",
      "levels": ["1. korrus", "2. korrus"],
      "template": "EN_Lagi",
      "scopeBox": "EN hoone",
      "placeOnSheet": { "sheets": ["EN-201", "EN-202"] }
    }
  ],
  "ifNameExists": "fail",
  "atomic": true
}
```

With levels `1. korrus` (0), `2. korrus` (3600) and `3. korrus` (7200) this makes `EN 1. korrus` on `EN-101`,
`EN 2. korrus` on `EN-102`, `EN 3. korrus` on `EN-103` and the two ceiling plans. `ifNameExists: "fail"` with
`atomic: true` refuses the whole set when any of the names is already used, so a re-run never makes duplicates; use
`"skip"` to add only the missing levels. The approval reads "Create 5 view(s): 3 floor plans (levels 1. korrus,
2. korrus, 3. korrus, template EN_Plaan) and 2 ceiling plans (levels 1. korrus, 2. korrus, template EN_Lagi) and place 5
on sheets."

More examples:

```json
{ "views": [
  { "kind": "section", "name": "EN lõige koridor", "from": { "x": 12000, "y": 4500, "z": 0 },
    "to": { "x": 30000, "y": 4500, "z": 0 }, "depthMm": 2500, "template": "EN_Lõige" },
  { "kind": "callout", "name": "EN detail ruum 112", "parentView": "EN 1. korrus", "aroundRoom": 412233, "paddingMm": 300, "scale": 20 },
  { "kind": "threeD", "name": "Kilp K1 3D", "aroundElements": [987654], "paddingMm": 800, "orientation": "iso-ne" },
  { "kind": "drafting", "name": "EN paigaldusdetail", "scale": 10 },
  { "kind": "legend", "name": "EN tingmärgid 2. korrus", "sourceLegend": "EN tingmärgid", "placeOnSheet": { "sheet": "EN-102", "xMm": 760, "yMm": 520 } }
] }
```

The section runs west → east along y = 4500 and looks north (left of the line), 2.5 m deep, from the level below the
line to the next level.

## Out of scope

Elevations (`ViewSection.CreateElevation` needs an `ElevationMarker`), view filters and graphic overrides, sheet
creation (`revit_create_sheets_from_table`), schedules, rendering settings.
