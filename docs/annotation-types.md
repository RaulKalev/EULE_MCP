# Text types and dimension types

The connector lists text types, creates new ones by duplicating and edits or renames existing ones; it does the same
for dimension types. Both work in a project and in a family that is open in the Family Editor.

All tools are in the `tags` group (`--tool-groups tags`, or find them with `revit_tools_search`).

| Tool | Permission | Purpose |
|---|---|---|
| `revit_list_text_types` | Read-only | The document's text types with every field below and `isDefault`; `includeUsage=true` counts the text notes of each |
| `revit_preview_set_text_types` | Read-only | What `revit_set_text_types` would create or change, field by field, and the problems to fix first |
| `revit_set_text_types` | Requires approval | Creates, edits and renames text types |
| `revit_list_dimension_types` | Read-only | The dimension types and their style; `details=true` adds every field below, `includeUsage=true` counts the dimensions of each |
| `revit_preview_set_dimension_types` | Read-only | What `revit_set_dimension_types` would create or change, field by field, and the problems to fix first |
| `revit_set_dimension_types` | Requires approval | Creates, edits and renames dimension types |

`document` targets an open document other than the active one, a project or a family.

## Text types

```json
{ "tool": "revit_set_text_types", "arguments": { "types": [
    { "name": "EL 2.5 mm", "duplicateFrom": "2.5mm Arial", "font": "Arial Narrow", "size": 2.5, "widthFactor": 0.8 },
    { "name": "3.5mm Arial", "color": "#FF0000", "background": "transparent", "leaderArrowhead": "none" },
    { "name": "Vana tekst", "newName": "EL Vana tekst" }
] } }
```

| Field | Notes |
|---|---|
| `name` | Required. No `\ : { } [ ] ; < > ? ~`, pipe or backtick |
| `duplicateFrom` | The text type a new type is copied from. Default: the document's default text type. Ignored (with a warning) when `name` exists |
| `newName` | Renames an existing type |
| `font` | A font name. Revit does not check that the font is installed |
| `size` | Text size in paper millimetres |
| `widthFactor` | 0.01 to 100; 1 is normal width |
| `bold`, `italic`, `underline`, `showBorder` | `true` or `false` |
| `color` | `"#RRGGBB"`, `"r,g,b"` or `[r, g, b]` |
| `background` | `opaque` or `transparent` |
| `leaderBorderOffset`, `tabSize` | Paper millimetres |
| `lineWeight` | Revit's line weight number, 1 to 16, not millimetres |
| `leaderArrowhead` | An arrowhead type name, or `none` |

## Dimension types

```json
{ "tool": "revit_set_dimension_types", "arguments": { "types": [
    { "name": "EL Lineaar 2.5", "style": "linear", "duplicateFrom": "Linear - 2.5mm Arial",
      "textSize": 2.5, "tickMark": "Diagonal 2mm", "witnessLineGap": 1.5,
      "unitsFormat": { "unit": "mm", "accuracy": 1 } },
    { "name": "EL Nurk", "style": "angular", "color": "#0000FF", "unitsFormat": "project" }
] } }
```

| Field | Notes |
|---|---|
| `name` | Required. The same name may exist once per style |
| `style` | `linear`, `angular`, `radial`, `diameter`, `arcLength`, `spotElevation`, `spotCoordinate`, `spotSlope`, … Picks among types of the same name, and the default source of a new type |
| `duplicateFrom` | The type a new type is copied from; the new type keeps its style. Default: the document's default type of `style` (linear when no style is given) |
| `newName` | Renames an existing type |
| `textSize`, `textOffset` | Paper millimetres (`size` is accepted for `textSize`) |
| `textFont`, `textWidthFactor`, `bold`, `italic`, `underline` | As for text types (`font`, `widthFactor` are accepted) |
| `textBackground` | `opaque` or `transparent` |
| `color`, `lineWeight` | As for text types |
| `tickMark` | An arrowhead type name, or `none` |
| `tickMarkLineWeight` | 1 to 16 |
| `witnessLineExtension`, `witnessLineGap`, `dimensionLineExtension` | Paper millimetres |
| `unitsFormat` | `"project"` (use project settings), or `{ useProjectSettings?, unit?, accuracy? }`. `unit`: `mm`, `cm`, `dm`, `m`, `ft`, `in` or `degrees`; `accuracy` is the rounding in that unit (1, 0.5, 0.01). A unit or accuracy turns project settings off; the other value is kept, or taken from the project units |

A field that a style does not have (spot types lack some of these) is reported as a problem rather than
skipped.

## Rules for both

- A name the document does not have is **created** by duplicating; fields left out keep the source's values.
- A name it has is **edited**: only the fields given change. Names match without regard to case.
- Editing a type changes every text note or dimension that uses it. The preview and the approval show how many.
- One transaction and one undo step. Any problem (a source, style or arrowhead not found, a name already taken, a
  parameter the type lacks, a value Revit refuses) leaves the document unchanged.
- The preview lists, per type, `action` (create | edit | unchanged), `duplicateFrom`, `usage` and `changes` with
  `from` and `to` for each field.

The tools do not delete types and do not create arrowheads or fonts.

## Not yet run in Revit

These tools were written when no Revit was reachable. The parsing, conversions (mm to feet, colours, backgrounds) and
the from/to planning are unit tested (`AnnotationTypeSpecTests`); the Revit side is not. Test steps:

1. In a project, run `revit_list_text_types includeUsage=true`. Compare size, font, width factor, colour, background,
   show border, leader/border offset, tab size, line weight and leader arrowhead of two types with their Type
   Properties dialog. Check that `isDefault` marks the type the Text tool starts with.
2. `revit_preview_set_text_types` with a new name and `duplicateFrom`, `size`, `font`, `color`, `background`,
   `showBorder`, `leaderArrowhead`: the preview is `ready` and lists `from`/`to` per field; the model is unchanged.
3. Run `revit_set_text_types` with the same arguments. Check every field in Type Properties, then press Undo once: the
   new type is gone.
4. Edit an existing type used by text notes: the approval names the usage count; after the run the notes show the
   change. Rename a type with `newName`, including a case-only rename.
5. Give an arrowhead name that does not exist, and a `newName` that is taken: the preview lists both problems and
   `revit_set_text_types` changes nothing.
6. `revit_list_dimension_types details=true includeUsage=true`. Compare text size, text offset, tick mark, tick mark
   line weight, witness line extension and gap, dimension line extension, text background and units format of a
   linear and an angular type with Type Properties.
7. `revit_set_dimension_types` creating a linear type (`style: linear`) and an angular type from defaults; check that
   each kept its style. Set `unitsFormat {unit: mm, accuracy: 1}` on the linear one and `{unit: degrees, accuracy: 0.5}`
   on the angular one; check Type Properties > Units Format. Then `unitsFormat: "project"`.
8. Give `unit: mm` to an angular type: the preview reports that the unit does not fit.
9. Edit a spot elevation type with a field it may lack, such as `witnessLineGap`: when the type has no such parameter,
   expect a "has no such parameter" problem, not a silent skip.
10. Open a detail or annotation family in the Family Editor and repeat steps 1 to 3 and 7 with `document` set to the
    family's title.
