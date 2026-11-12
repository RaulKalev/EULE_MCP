# View templates

The connector lists view templates and applies them to views (`revit_list_view_templates`, `revit_apply_view_template`).
These tools add the rest: read what one template holds and controls, create templates from a view or as a copy of a
template, and edit them.

All tools are in the `views` group (`--tool-groups views`, or find them with `revit_tools_search`).

| Tool | Permission | Purpose |
|---|---|---|
| `revit_get_view_template` | Read-only | One template: settings, controlled parameters, category visibility and overrides, filters, the views that use it |
| `revit_preview_create_view_templates` | Read-only | What `revit_create_view_templates` would create, and the problems to fix first |
| `revit_create_view_templates` | Requires approval | New templates from a view or as copies, optionally edited |
| `revit_preview_set_view_templates` | Read-only | From/to per field, the views each template reaches, warnings and problems |
| `revit_set_view_templates` | Requires approval | Edits templates |

`document` targets an open document other than the active one. A template or view is referred to by **name** (exact,
case-insensitive) or **element id**; a name that matches two is reported, never guessed, and a name that is not found
comes back with close names.

## Reading a template

```json
{ "tool": "revit_get_view_template", "arguments": { "template": "EN Plaan" } }
```

Returns `viewType`, `scale` (`"1:100"`), `detailLevel`, `discipline`, `displayStyle`, `phase`, `phaseFilter`,
`viewRange` (plan templates: top, cut, bottom and depth, each a level such as `Associated level` and an offset in mm),
`controlled` and `notControlled` (the template parameters by name, from `GetTemplateParameterIds` and
`GetNonControlledTemplateParameterIds`), `categories` (`hidden`; `overridden` with their graphics, at most
`maxCategories`, default 50), `filters` in order with `enabled`, `visible` and `graphics` (the same shape as
`revit_get_view_filters`), and `viewCount` with `views` (at most `maxViews`, default 100).

A subcategory is named `Parent: Sub`, for example `Doors: Frame/Mullion`. A hidden subcategory is listed only when its
category is shown.

## Creating

```json
{ "tool": "revit_create_view_templates", "arguments": { "templates": [
    { "name": "EN Plaan 1-50", "duplicateFrom": "EN Plaan", "scale": 50 },
    { "name": "EN Lagi", "fromView": "EN 1. korrus lagi" }
] } }
```

| Field | Notes |
|---|---|
| `name` | Required. Must not be taken by another template. No `\ : { } [ ] \| ; < > ? ` ~` |
| `fromView` | A view (not a template): the template takes its settings (`View.CreateViewTemplate`) |
| `duplicateFrom` | A template to copy: `View.Duplicate` when Revit allows it for the template, else `ElementTransformUtils.CopyElements` in the same document. The preview's `method` says which (`duplicate` or `copy`) |
| edits | Any field of the edit below except `newName`, applied to the new template |

Give `fromView` or `duplicateFrom`, not both. No view uses a new template until it is applied with
`revit_apply_view_template`.

## Editing

```json
{ "tool": "revit_set_view_templates", "arguments": { "templates": [
    { "template": "EN Plaan", "scale": "1:50", "detailLevel": "Fine",
      "include": ["Detail Level"], "exclude": ["Phase Filter"],
      "hideCategories": ["Furniture", "OST_Planting"], "showCategories": ["Doors: Frame/Mullion"],
      "parameters": { "Phase": "New Construction" } }
] } }
```

| Field | Notes |
|---|---|
| `template` | Required. Name or id. Each template once per call |
| `newName` | Rename. Must not be taken by another template |
| `scale` | `50` or `"1:50"`, 1 to 24000 |
| `detailLevel` | `Coarse`, `Medium`, `Fine` |
| `discipline` | `Architectural`, `Structural`, `Mechanical`, `Electrical`, `Plumbing`, `Coordination` |
| `displayStyle` | `Wireframe`, `Hidden Line`, `Shaded`, `Shaded with edges`, `Consistent Colors`, `Realistic`, `Realistic with edges`, `Textures` |
| `include` | Template parameters the template should control: names as `revit_get_view_template` lists them, ids or `BuiltInParameter` names |
| `exclude` | Template parameters it should not control: the views keep their own value |
| `hideCategories`, `showCategories` | Category names, ids or `OST_` names; subcategories as `Parent: Sub` |
| `parameters` | `{name: value}` for other parameters the template holds. A name, a `BuiltInParameter` name or the name in another case. Text, a number, `true`/`false` (Yes/No), or `null` (clear). An element value (phase, phase filter, scope box) by name or id; `none` clears it |

- **Editing a template changes every view that uses it.** The preview gives `viewCount` and the first views per
  template, and `affectedViews` in all; the approval text names the templates and how many views use each.
- Only the fields given change. A field that already has the value is not a change; an edit without changes is
  `unchanged`.
- A field the template does not control reaches no view: setting the scale on a template that excludes `View Scale`
  is allowed, and the preview warns and says to add it to `include`. The same for hidden categories (`V/G Overrides
  Model`, `Annotation`, `Analytical Model`) and for `parameters`.
- A field the template's view type does not have (the scale of a 3D template, the detail level of a schedule template)
  is a problem, as is a read-only parameter.
- One transaction and one undo step. Any problem, or a change Revit refuses, leaves the document unchanged.
- The previews take `dryRun` (default `true`): the writes run in a transaction that is rolled back, so the preview
  also reports what Revit itself refuses. A preview of a create plans the edits against the source view or template;
  the trial run plans them again against the new template.

### Filters on a template

The view filter tools already take a template as their view: `revit_apply_view_filters views=["EN Plaan"]` adds,
removes, orders and sets the graphics of filters on the template, and `revit_get_view_filters view="EN Plaan"` reads
them (see [view-filters.md](view-filters.md#templates)). These tools add no filter arguments of their own.
`revit_get_view_template` lists the template's filters in the same shape.

## Out of scope

- View range, crop and scope box of a template (use `parameters` for what is a plain parameter; `revit_set_view_extent`
  works on views).
- Category graphics overrides (projection and cut lines, patterns, halftone): they are read, not set.
- Deleting templates (`revit_delete`).

## Not yet run in Revit

These tools were written when no Revit was reachable. The parsing, the from/to planning, the controlled-parameter and
category rules and the approval texts are unit tested (`ViewTemplateTests`); the Revit calls are not. Test steps, in a
copy of a project with plan templates:

1. `revit_get_view_template template=<a floor plan template>`. Check `scale`, `detailLevel`, `discipline`,
   `displayStyle`, `phase`, `phaseFilter` and `viewRange` against Properties and Edit View Template. Check that
   `controlled` / `notControlled` match the Include column, and that the names match the dialog's names.
2. Hide a category and override another in that template's V/G by hand; read again: both appear under `categories`.
   Add a filter; it appears under `filters` as in `revit_get_view_filters`.
3. `revit_preview_create_view_templates templates=[{name:"T1", fromView:<a plan>}, {name:"T2", duplicateFrom:<a template>}]`:
   `ready: true`, note `method` for T2. Then `revit_create_view_templates` with the same; both appear in the Project
   Browser and in Edit View Template with their names. Undo removes both in one step.
4. Create `T3` with `duplicateFrom` and `scale: 50, detailLevel: "Fine", hideCategories: ["Furniture"]`; check the new
   template has them and the source template is unchanged.
5. `revit_preview_set_view_templates` on T2 with `scale`, `detailLevel`, `include`, `exclude`, `hideCategories`,
   `showCategories` and `parameters: {"Phase": <a phase name>}`: check every from/to, `viewCount` and warnings. Apply
   with `revit_set_view_templates`; check Edit View Template (values and Include ticks) and V/G.
6. Apply T2 to a view, then set `exclude: ["View Scale"]` and `scale: 20` on T2: the preview warns; after the edit the
   view keeps its own scale. Then `include: ["View Scale"]`: the view takes 1:20.
7. Rename T2 with `newName` to the name of another template: a problem, nothing changed. Rename to a free name: done.
8. Refusals: `displayStyle` on a plan template where Revit refuses it, a read-only parameter, an unknown category
   (expect close names). Nothing changes.
9. Repeat 3 and 5 in Revit 2024 and 2026, and once with `document` naming a non-active open project.
