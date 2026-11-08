# Family creation

The connector creates Detail Item families from a short spec (#147): geometry, line styles that match the project,
family and shared parameters, text through nested Generic Annotation families, purge, save and load.

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

**Limitation:** the text is fixed. Revit's API cannot create *labels* (text bound to a parameter) in a family, so text
that should show a parameter's value has to be turned into a label by hand in the Family Editor.

## Purge

With `purge=true` (the default), the tool deletes what Revit lists as unused (`Document.GetUnusedElements`) before
saving. It repeats the pass up to five times, because deleting can leave more elements unused. The line styles and
patterns the spec uses, the parameters and the label families and text types are never deleted. An element Revit refuses to delete is skipped. The result counts the
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
    "parameters": [
      { "name": "Kilbi tähis", "dataType": "text", "value": "K1" },
      { "name": "Laius", "dataType": "length", "value": 20, "instance": true },
      { "name": "EULE_Tähis", "shared": true }
    ],
    "labels": [ { "text": "K1", "position": [0, 9], "alignment": "center", "textType": "2.5mm Arial" } ],
    "loadIntoProject": true
} }
```

Check `ready` and `problems` in the preview, then call `revit_create_detail_family` with the same arguments.
