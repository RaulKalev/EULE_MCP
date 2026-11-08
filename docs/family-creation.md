# Family creation

The connector creates Detail Item families from a short spec (#147). Phase 1 covers geometry, line styles that match
the project, purge, save and load. Phase 2 will add family and shared parameters, and phase 3 text through a nested
Generic Annotation family.

Both tools are in the `edit` group (`--tool-groups edit`, or find them with `revit_tools_search`).

| Tool | Permission | Purpose |
|---|---|---|
| `revit_preview_create_detail_family` | Read-only | Checks the spec: save path, file access policy, template, geometry, line styles against the project, load status |
| `revit_create_detail_family` | Requires approval | Creates the family from the template, draws it, purges it, saves it and optionally loads it |

Run both from a **project** document. The project's line styles are the source the family copies, and the
project is where the family can load. `document` targets an open project other than the active one.

## Where the family is saved

`savePath` is required and has no default. **Ask the user where the family goes.** It is either:

- a folder, with `familyName`: `C:\Library\Detail` + `EL_Kilp` → `C:\Library\Detail\EL_Kilp.rfa`, or
- the full `.rfa` path. Its file name is the family name, so `familyName` may be left out. If both are given, they
  must match.

An existing file is replaced only when `overwrite=true`. The path must pass the file access policy
(`FileAccessPolicy.json`), and a missing folder is created. The family is saved to a temporary file of the same
name first and copied into place at the end, so a failure never leaves a half-written file at `savePath`.

## Geometry

All values are millimetres on the family's reference level. The origin (0, 0) is the insertion point. Points are
`[x, y]` or `{ "x": …, "y": … }`.

| Argument | Item | Notes |
|---|---|---|
| `lines` | `{start, end, lineStyle?}` | |
| `rectangles` | `{min, max, lineStyle?}` | Any two opposite corners; drawn as four lines |
| `arcs` | `{center, radius, startAngle, endAngle, lineStyle?}` | Degrees from +X, counter-clockwise from start to end |
| `arcs` | `{start, mid, end, lineStyle?}` | Three-point arc |
| `circles` | `{center, radius, lineStyle?}` | |

`lineStyle` at the top level is the default for curves that do not name one. A curve with no style keeps the
family's default line. Curves shorter than 0.8 mm (Revit's short-curve tolerance), zero-sweep or full-circle arcs,
and collinear three-point arcs are refused in the preview. A spec may hold up to 2000 curves.

## Line styles

Every `lineStyle` must be a line style of the project (a subcategory of *Lines*), matched without regard to case.
A name the project does not have is a problem in the preview, with the closest names suggested. The tool never
invents a style.

In the family, the tool reuses a *Lines* subcategory of that name if the template has one. Otherwise it creates one
with the project style's projection weight, colour and line pattern, copying the pattern when the template lacks
it. Because the family's style has the project's name, Revit merges it with the project's style when the family
loads, and in the project the project's settings apply.

If the template does not let detail curves use *Lines* subcategories, the tool falls back to a *Detail Items*
subcategory with the same name, weight, colour and pattern. The result says so (`kind: detailItems`) and adds a
warning. Such a subcategory appears under Detail Items in Object Styles rather than as a line style.

## Purge

With `purge=true` (the default), the tool deletes what Revit lists as unused (`Document.GetUnusedElements`) before
saving. It repeats the pass up to five times, because deleting can leave more elements unused. The line styles and
patterns the spec uses are never deleted. An element Revit refuses to delete is skipped. The result counts the
deleted elements by kind.

## Loading into the project

`loadIntoProject=true` loads the saved file into the document in one transaction (one undo step) and activates its
types. `ifExists` handles a family of the same name that is already loaded:

- `skip` (default): leave the loaded family as it is.
- `overwrite`: replace it with the new file, keeping the project's parameter values.

The path is remembered for `revit_reload_family`.

## Template

The default is `Metric Detail Item.rft`. The tool looks in Revit's family template folder, then its `English` and
`English_I` sub-folders, then anywhere below it. `templatePath` names another `.rft`. A template whose category is
not Detail Items gives a warning but is used.

## Example

```json
{ "tool": "revit_preview_create_detail_family", "arguments": {
    "savePath": "C:\\Library\\Detail",
    "familyName": "EL_Kilp_Sümbol",
    "lineStyle": "EL_Õhuke",
    "rectangles": [ { "min": [-10, -6], "max": [10, 6] } ],
    "lines": [ { "start": [-10, -6], "end": [10, 6], "lineStyle": "EL_Kaabel" } ],
    "circles": [ { "center": [0, 0], "radius": 2 } ],
    "loadIntoProject": true
} }
```

Check `ready` and `problems` in the preview, then call `revit_create_detail_family` with the same arguments.
