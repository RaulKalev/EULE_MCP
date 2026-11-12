# Family contents

`revit_get_family_contents` reads what is inside a family: its parameters and types, the detail lines, arcs and
circles, filled regions, text, nested families, and the line styles and filled region types they use. With
`asSpec=true` it also returns the family as the arguments of `revit_create_detail_family`
([family-creation.md](family-creation.md)), so an agent can copy a symbol by feeding the spec back to the create tool.

The tool is in the `edit` group (`--tool-groups edit`, or find it with `revit_tools_search`).

| Tool | Permission | Purpose |
|---|---|---|
| `revit_get_family_contents` | Read-only | What a family holds, in millimetres and degrees, and optionally a spec that copies it |

## Which family

Give one of:

- `family`: the name of a family **loaded in the project**. The tool opens an independent copy of it
  (`Document.EditFamily`), reads it and closes the copy without saving. If Revit refuses because the family is
  already open in the Family Editor, or the project has no family of that name but a family document with that name
  is open, the open document is read instead and left open. A name that is not found gets close names suggested.
- `path`: a `.rfa` file. It must pass the file access policy (`FileAccessPolicy.json`). If the file is already open
  in Revit, the open document is read and left open; otherwise it is opened in the background and closed without
  saving.

`source` in the result says which: `project`, `file` or `openDocument`. Nothing is ever saved, and no transaction is
started. `document` names the project (title or path) when it is not the active one; with `path` alone the project is
optional and is used only to check pattern names in the spec.

## Arguments

| Argument | Notes |
|---|---|
| `family` / `path` | One of the two (above) |
| `include` | Sections to return: `parameters`, `types`, `curves`, `filledRegions`, `textNotes`, `nestedInstances`, `lineStyles`, `filledRegionTypes`. Default: all. `counts` always covers every section |
| `limit` | Most items per list, default 500 (at most 5000). A longer list is cut and named in `truncated` |
| `asSpec` | Also return `spec`, `specOmitted`, `specNotes` and `specProblems` (below). Default false |
| `readNested` | Open nested Generic Annotation families to read their text (default true; at most 50 are opened) |

## Result

All geometry is in millimetres on the family's reference level (the family origin is `[0, 0]`), angles in degrees
counter-clockwise from +X. Points are `[x, y]`.

```json
{
  "family": "EL_Kilp_Sümbol", "source": "project", "document": "Projekt.rvt",
  "category": "Detail Items", "isDetailItem": true, "isGenericAnnotation": false,
  "view": { "name": "Ref. Level", "viewType": "FloorPlan" }, "currentType": "EL_Kilp_Sümbol",
  "counts": { "parameters": 3, "types": 1, "curves": 6, "filledRegions": 1, "textNotes": 0,
              "nestedInstances": 2, "lineStyles": 2, "filledRegionTypes": 1 },
  "parameters": [ { "name": "Laius", "dataType": "length", "group": "Dimensions", "instance": true },
                  { "name": "EULE_Tähis", "dataType": "text", "group": "Text", "instance": false, "shared": true, "guid": "…" },
                  { "name": "Pool", "dataType": "length", "group": "Dimensions", "instance": false, "formula": "Laius / 2" } ],
  "types": [ { "name": "EL_Kilp_Sümbol", "values": { "Laius": 20.0, "EULE_Tähis": "K1", "Pool": 10.0 } } ],
  "curves": [ { "id": 1234, "kind": "line", "start": [-10, -6], "end": [10, -6], "lineStyle": "EL_Õhuke" },
              { "id": 1240, "kind": "arc", "start": [4, 0], "mid": [2, 3.4641], "end": [-2, 3.4641],
                "center": [0, 0], "radius": 4, "startAngle": 0, "endAngle": 120 },
              { "id": 1241, "kind": "circle", "center": [0, 0], "radius": 2, "visibleParameter": "Näita" } ],
  "filledRegions": [ { "id": 1300, "type": "EL_Täide", "lineStyle": "<Invisible lines>",
                       "loops": [ { "points": [[-8, -4], [8, -4], [8, 4], [-8, 4]] }, { "center": [0, 0], "radius": 1 } ] } ],
  "textNotes": [],
  "nestedInstances": [ { "id": 1400, "family": "EL_Kilp_Sümbol_Text1", "type": "EL_Kilp_Sümbol_Text1",
                         "category": "Generic Annotations", "position": [0, 9],
                         "texts": [ { "text": "K1", "position": [0, 0], "alignment": "center", "sizeMm": 2.5 } ] },
                       { "id": 1401, "family": "EULE_Silt", "type": "2.5 mm", "category": "Generic Annotations",
                         "position": [12, 0], "linkedParameters": [ { "parameter": "Tähis", "familyParameter": "EULE_Tähis" } ] } ],
  "lineStyles": [ { "name": "<Invisible lines>", "parent": "Lines", "weight": 1, "pattern": "Solid", "builtIn": true },
                  { "name": "EL_Õhuke", "parent": "Lines", "weight": 1, "color": "#000000", "pattern": "Solid" } ],
  "filledRegionTypes": [ { "name": "EL_Täide", "foregroundPattern": "solid", "foregroundColor": "#C0C0C0",
                           "backgroundPattern": "none", "lineWeight": 1, "isMasking": true } ]
}
```

| Section | Item |
|---|---|
| `parameters` | `name`, `dataType` (the friendly name the parameter tools use, such as `text`, `length`, `yesNo`, else Revit's label), `group` (Revit's label), `instance`, `shared` + `guid`, `formula`, `isReporting`, `builtIn` (Revit's own) |
| `types` | Each family type with its values: lengths mm, areas m², volumes m³, angles degrees, yes/no `true`/`false`, integers, text, element references by name; other units as the family displays them. `null` = no value |
| `curves` | Detail (and symbolic) lines: `kind` `line` (`start`, `end`), `arc` (`start`, `mid`, `end`, `center`, `radius`, `startAngle`, `endAngle`), `circle` (`center`, `radius`) or `other` (`curveType`, such as an ellipse or spline). `lineStyle` is left out for the category's own line. `visibleParameter` is the yes/no parameter linked to the element's *Visible* property |
| `filledRegions` | `loops`, the outer boundary first: `points`, a circle (`center`, `radius`), or `segments` (`line` / `arc` with `start`, `mid`, `end`) when a boundary has arcs. `type`, the boundary's `lineStyle` (`mixedLineStyles` when its edges differ), `visibleParameter` |
| `textNotes` | `text`, `position`, `textType`, `sizeMm` (paper size), `rotation`, `alignment` (`left` / `center` / `right`), `visibleParameter` |
| `nestedInstances` | `family`, `type`, `category`, `position`, `rotation`, `mirrored`, `linkedParameters` (`parameter` of the nested instance or, with `scope: type`, its type, linked to `familyParameter`), `visibleParameter`; for a Generic Annotation family that was opened, `texts` |
| `lineStyles` | The styles the curves and region boundaries use: `name`, `parent` (`Lines`, or the family's own category), `weight` (1-16), `color`, `pattern` (`Solid` when none), `builtIn` |
| `filledRegionTypes` | The types the regions use: `foregroundPattern` / `backgroundPattern` (a fill pattern name, `solid` or `none`), the colours, `lineWeight`, `isMasking` |

## Copying a family: asSpec

`spec` is shaped like the arguments of `revit_create_detail_family`: `familyName`, `parameters`, `lineStyles`, `lines`,
`arcs`, `circles`, `filledRegionTypes`, `filledRegions` and `labels`. It has **no `savePath`**: ask the user where the
copy goes, add it (and change `familyName` if the copy must have another name), then run
`revit_preview_create_detail_family` and `revit_create_detail_family` with it.

- Arcs are given by `start`, `mid` and `end`. Curves keep their line style; every non-built-in style they use is
  defined in `lineStyles` with its weight, colour and pattern, so the copy does not depend on the project having it.
  Revit's own styles (`<Hidden>`, `<Invisible lines>`, …) are named but not defined.
- Filled regions keep their outer boundary, holes, type, boundary line style and visibility parameter; the types
  they use are defined in `filledRegionTypes`.
- Parameters keep their data type, group, instance/type, formula, and the value of the current type. Shared ones are
  given by `guid`.
- Labels: a text note becomes a text label; a nested Generic Annotation family that holds one text (as the create tool
  makes them) becomes a text label at the same place and rotation; a nested Generic Annotation instance with one
  linked parameter becomes a parameter label (`labelFamily`, `labelType`, `labelParameter`).
- When a project is known, pattern names the project lacks are left out of the spec rather than guessed.

`specOmitted` lists, one line each, what the spec leaves out: types other than the current one, reporting parameters,
data types or values the create tool cannot take, curves that are too short or not lines, arcs or circles, boundaries
with arc edges, nested families that are not text or a single-parameter label, visibility parameters that are not
yes/no parameters of the spec, and patterns the project lacks. `specNotes` says what must be true before the spec is
fed back: shared parameters must be in the shared parameter file, label families must be loaded in the project (or
`labelFamily` set to a `.rfa` path), text types are not copied (sizes are). `specProblems` lists anything the create
tool's own spec check still refuses; it is empty for a spec that parses.

What the spec never carries: dimensions, reference planes and their parameters, constraints, the family's other types,
detail level visibility settings (*Visibility/Graphics Overrides* per element), labels drawn directly in an annotation
family (the API cannot read them), and nested families other than Generic Annotation text and labels.

## Not yet run in Revit

The tool was written when no Revit was reachable. The result shape, unit conversion, arc angles, outer-loop detection,
truncation and the spec builder are unit tested (`FamilyContentsTests`), including round trips: a spec parsed by
`DetailFamilySpecParser.Parse`, read back and parsed again gives the same curves, regions, parameters and labels. The
Revit calls are not tested. Steps:

1. In a project, create a family with `revit_create_detail_family` using the example in
   [family-creation.md](family-creation.md#example) with `loadIntoProject=true`.
2. Run `revit_get_family_contents family=<that name>`. Check `source: project`, `isDetailItem: true`, the curve
   count (rectangles count as four lines), arc angles, the circle, the filled region and its type, and the
   parameters with their values in mm. Check in Revit that the family was not changed and no family window opened.
3. Open the family in the Family Editor and run the same call: expect `source: openDocument` and the document still
   open afterwards.
4. Run `revit_get_family_contents path=<the saved .rfa>` with the family closed: expect `source: file`, and that the
   file's modified time did not change.
5. Check `nestedInstances`: the text label families with `texts`, the parameter label with `linkedParameters`, and
   `visibleParameter` on the elements that had one.
6. Run with `asSpec=true`. Add a new `savePath` and `familyName` to `spec`, run `revit_preview_create_detail_family`
   with it (expect `ready: true`), then `revit_create_detail_family`, and compare the copy with the original.
7. Read a family from a library that has dimensions, reporting parameters, several types, arcs in a filled region
   and a nested detail component: check `specOmitted` names each of them.
8. Run with `include=["curves"] limit=2` on a family with more than two curves: expect `truncated: ["curves"]` and
   full `counts`.
9. Try a family name that is not loaded (expect suggestions) and a path outside the file access policy (expect a
   refusal).
