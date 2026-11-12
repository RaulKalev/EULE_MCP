# Family creation

The connector creates Detail Item families from a short spec (#147): geometry, line styles that match the project,
filled regions and their types, family and shared parameters, visibility parameters, text and parameter labels through
nested Generic Annotation families, purge, save and load.

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

Every `lineStyle` must be a line style of the project (a subcategory of *Lines*), matched without regard to case,
or one defined in `lineStyles` (below). A name that is neither is a problem in the preview, with the closest names
suggested. The tool never invents a style.

`lineStyles` defines styles of the family itself:

```json
"lineStyles": [ { "name": "EL_Sümbol", "weight": 2, "color": "#0000FF", "pattern": "Dash" } ]
```

| Field | Notes |
|---|---|
| `name` | Required |
| `weight` | Revit's line weight number, 1 to 16 (not millimetres) |
| `color` | `"#RRGGBB"`, `"r,g,b"` or `[r, g, b]` |
| `pattern` | A line pattern **of the project** (copied into the family), or `solid` |

A field left out comes from the project's style of the same name, else weight 1, black and solid. A defined style is
created in the family even when no curve uses it, and when the template already has the name its values are set
(`action: edited`). If the project later has a style of the same name, the project's settings apply to the loaded
family, as for copied styles. To add a style to the project itself, use `revit_set_line_styles`
([line-styles.md](line-styles.md)).

In the family, the tool reuses a *Lines* subcategory of that name if the template has one. Otherwise it creates one
with the project style's projection weight, colour and line pattern, copying the pattern when the template lacks
it. Because the family's style has the project's name, Revit merges it with the project's style when the family
loads, and in the project the project's settings apply.

If the template does not let detail curves use *Lines* subcategories, the tool falls back to a *Detail Items*
subcategory with the same name, weight, colour and pattern. The result says so (`kind: detailItems`) and adds a
warning. Such a subcategory appears under Detail Items in Object Styles rather than as a line style.

## Filled regions

`filledRegions` draws filled regions on the reference level, in millimetres like the curves:

| Field | Notes |
|---|---|
| `points` | `[[x, y], …]`, three or more corners in order. A closing point that repeats the first is dropped |
| `min`, `max` | A rectangle from two opposite corners, instead of `points` |
| `center`, `radius` | A circle, instead of `points` |
| `holes` | Inner boundaries: each a point list, or `{min, max}` or `{center, radius}`. A hole must lie inside the outer boundary and must not overlap another hole |
| `type` | The filled region type. Without it the family's default type is used |
| `lineStyle` | The boundary's line style: a line style of the project, as for curves. The top-level `lineStyle` default does **not** apply to regions; without one the boundary keeps the type's line |
| `visibleParameter` | See *Visibility parameters* |

The preview refuses a boundary with fewer than three points, a side shorter than 0.8 mm, sides that cross or touch,
or no area. A spec may hold up to 200 regions of up to 500 points each. A region Revit refuses at creation is
reported in `filledRegions` with `status: failed` and a warning; the others are still drawn.

### Filled region types

A family keeps its own filled region types: they are not merged with the project's on load, so what the family's
type looks like is what the region looks like in the project. A region's `type` is resolved in this order:

1. A name defined in `filledRegionTypes`: the type is created in the family, or edited when the template already
   has that name.
2. Otherwise a filled region type **of the project** with that name: it is copied into the family with its patterns,
   colours, line weight and masking.
3. Otherwise it is a problem in the preview, with close names suggested.

`filledRegionTypes` items:

| Field | Notes |
|---|---|
| `name` | Required |
| `copyFrom` | A filled region type of the project to start from. Default: the project's type of the same name, if there is one |
| `foregroundPattern`, `backgroundPattern` | A fill pattern of the project by name, `solid`, or `none`. A named pattern the family lacks is copied from the project. A background pattern must be a drafting pattern |
| `foregroundColor`, `backgroundColor` | `"#RRGGBB"`, `"r,g,b"` or `[r, g, b]` |
| `lineWeight` | 1 to 16 |
| `isMasking` | `true` hides what is behind the region |

A field that is left out takes `copyFrom`'s value; with no `copyFrom` either, it stays as the family's default type
has it. Types listed in `filledRegionTypes` are created even when no region uses them, and survive the purge. The
result lists each type with `action: created | edited` and the values Revit holds afterwards; a single value Revit
refuses is a warning.

## Parameters

`parameters` adds parameters to the family. A **family parameter** names its data type:

| Field | Notes |
|---|---|
| `name` | Required. No `\ : { } [ ] ; < > ? ~`, pipe or backtick: Revit refuses them |
| `dataType` | `text`, `multilineText`, `url`, `integer`, `number`, `length`, `area`, `volume`, `angle`, `yesNo`, `material`, … (as in `revit_create_parameters`) |
| `group` | Where it shows in Properties: `Data`, `Text`, `Dimensions`, `Identity Data`, `Graphics`, `Electrical`, … Default: `Text` for text, `Dimensions` for sizes and angles, `Graphics` for yes/no, `Data` otherwise |
| `instance` | `true` for an instance parameter; default is a type parameter |
| `value` | The family type's value: lengths in mm, areas in m², volumes in m³, angles in degrees, yes/no as `true`/`false` |
| `formula` | A Revit formula instead of a value, for example `"Laius / 2"` |

A **shared parameter** is an existing definition of the shared parameter file: `{ "name": "EULE_Tähis", "shared": true }`,
or `{ "guid": "…" }`. `sharedGroup` picks the group when several groups of the file hold the name. It takes its data
type from the definition; a `dataType` given as well must match it. `group`, `instance`, `value` and `formula` work as
for family parameters.

**The tool never creates a shared parameter.** It reads the file and only assigns definitions that are already there.
A name that is not in the file is a problem in the preview, with close names suggested. The file is
`sharedParameterFile` when given, else `parameters.sharedParameterFile` from the project, user or company config, else
Revit's own setting, which is restored after the call. Use `revit_list_shared_parameters` to see what the file holds.

Values and formulas are set on the family's type. When the template has no type, the tool creates one named after
the family, which is what Revit would create on load anyway. A parameter the template already has is kept, and only
its value or formula is set (`status: existing`).

## Labels (text)

`labels` places text in the family through nested **Generic Annotation** families, so the text keeps its size on
paper at every view scale while it stays anchored to the symbol:

| Field | Notes |
|---|---|
| `text` | Required |
| `position` | `[x, y]` in mm on the reference level: the text's insertion point |
| `size` | Text height on paper in mm. Default: the text type's, else 2.5 |
| `textType` | A text type of the project to copy (font, size, width factor, bold, italic, underline, background, colour) |
| `alignment` | `left` (default), `center` or `right`: where the insertion point sits on the text. Vertically it is centred |
| `rotation` | Degrees counter-clockwise |

Each distinct combination of text, size, text type and alignment becomes one annotation family named
`{familyName}_Text1`, `_Text2`, … and every label using it is an instance of that family. The annotation families are
built from `Metric Generic Annotation.rft` (found in Revit's family template folder, including its `Annotations`
sub-folder; `annotationTemplatePath` names another), have the template's own sample text removed, are purged with the
detail item, and live only inside the saved `.rfa`: no separate annotation files are written.

### Labels that show a parameter

Revit's API cannot create a *label* (text bound to a parameter), and a Detail Item family cannot hold one at all:
labels live in annotation families. So a label that shows a parameter is an instance of **your own label family**, a
Generic Annotation family that already holds a label, nested in the detail item with its label parameter linked to
the detail family's parameter:

| Field | Notes |
|---|---|
| `parameter` | Required instead of `text`: a parameter listed in `parameters` |
| `position`, `rotation` | As for text |
| `labelFamily` | The label family: a Generic Annotation family loaded in the project (by name), or a `.rfa` path. The top-level `labelFamily` is the default for labels without one |
| `labelType` | The label family's type to place. Default: its first type by name |
| `labelParameter` | The label family's parameter that its label shows. Needed only when several of its parameters fit |

Make the label family once: a Generic Annotation with one label, bound to an **instance** parameter of the data type
you want to show (text for most uses), and a type per text size. The tool then:

1. takes the family from the `.rfa`, or saves a copy of the loaded one to a temporary file,
2. loads it into the detail family and places an instance at `position`,
3. finds the instance's parameter to link (`labelParameter`, else its only editable, user-defined parameter of the
   same data type as `parameter`) and links it to the detail family's parameter.

The label then shows whatever value the parameter has, per type or per instance as the parameter is defined.
`size`, `textType` and `alignment` do not apply: the label family decides how its text looks.

The preview checks that the label family exists, is a Generic Annotation and has the `labelType`. The label
parameter is matched when the label is placed, because that needs the family open; a label whose parameter cannot be
linked is removed again and reported in `parameterLabels` with `status: failed` and the reason (no parameter of that
data type, several that fit, a type parameter already linked to another parameter).

## Visibility parameters

`visibleParameter` on a line, rectangle, arc, circle, filled region or label names a **yes/no** parameter listed in
`parameters`. The element's *Visible* property is linked to it, so the parameter switches the element on and off per
type or per instance:

```json
"parameters": [ { "name": "Näita täidet", "dataType": "yesNo", "value": true, "instance": true } ],
"filledRegions": [ { "min": [-10, -6], "max": [10, 6], "type": "Solid Black", "visibleParameter": "Näita täidet" } ]
```

A name that is not in `parameters`, or a parameter that is not yes/no, is a problem in the preview. A link Revit
refuses at creation is a warning and the element stays always visible; `visibility` in the result counts the linked
and failed elements per parameter.

## Purge

With `purge=true` (the default), the tool deletes what Revit lists as unused (`Document.GetUnusedElements`) before
saving. It repeats the pass up to five times, because deleting can leave more elements unused. The line styles and
patterns the spec uses, the filled region types and their fill patterns, the parameters and the label families and text types are never deleted. An element Revit refuses to delete is skipped. The result counts the
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

## Reading a family back

`revit_get_family_contents` reads an existing family (loaded in the project, or a `.rfa` file) without changing it,
and with `asSpec=true` returns it as the arguments of `revit_create_detail_family`, so a symbol can be copied. See
[family-contents.md](family-contents.md).

## Example

```json
{ "tool": "revit_preview_create_detail_family", "arguments": {
    "savePath": "C:\\Library\\Detail",
    "familyName": "EL_Kilp_Sümbol",
    "lineStyle": "EL_Õhuke",
    "rectangles": [ { "min": [-10, -6], "max": [10, 6] } ],
    "lines": [ { "start": [-10, -6], "end": [10, 6], "lineStyle": "EL_Kaabel" } ],
    "circles": [ { "center": [0, 0], "radius": 2 } ],
    "parameters": [
      { "name": "Kilbi tähis", "dataType": "text", "value": "K1" },
      { "name": "Laius", "dataType": "length", "value": 20, "instance": true },
      { "name": "EULE_Tähis", "shared": true }
    ],
    "filledRegionTypes": [ { "name": "EL_Täide", "foregroundPattern": "solid", "foregroundColor": "#C0C0C0", "isMasking": true } ],
    "filledRegions": [ { "center": [0, 0], "radius": 2, "type": "EL_Täide" } ],
    "labels": [
      { "text": "K1", "position": [0, 9], "alignment": "center", "textType": "2.5mm Arial" },
      { "parameter": "Kilbi tähis", "position": [0, -9], "labelFamily": "EULE_Silt", "labelType": "2.5 mm" }
    ],
    "loadIntoProject": true
} }
```

Check `ready` and `problems` in the preview, then call `revit_create_detail_family` with the same arguments.
