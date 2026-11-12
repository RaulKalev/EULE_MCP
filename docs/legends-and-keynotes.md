# Legends and keynotes

The connector lists the legend views of a model and fills an existing legend with a symbol key: one symbol per family
type with a text label beside it. It also reads the keynote table and reloads it from its file.

The legend tools are in the `views` group, the keynote tools in the `tags` group (keynote tags show the table's
texts). Use `--tool-groups views,tags`, or find them with `revit_tools_search`.

| Tool | Permission | Purpose |
|---|---|---|
| `revit_list_legends` | Read-only | Legend views: scale, sheets, legend components (family, type, view direction, position), text note and detail item counts, `canSeed` |
| `revit_preview_populate_legend` | Read-only | What `revit_populate_legend` would place and delete, with a rolled-back trial run |
| `revit_populate_legend` | Requires approval | Places one symbol and label per type in a grid; can first delete what the legend holds |
| `revit_get_keynotes` | Read-only | The keynote table's entries, its file and status; optionally which types, materials and elements carry each key |
| `revit_preview_reload_keynote_table` | Read-only | Reads the keynote file and compares it with the table in the model |
| `revit_reload_keynote_table` | Requires approval | Reloads the table from its file, or loads it from another file |

`document` targets an open document other than the active one.

## What Revit's API does not allow

- **A legend view cannot be created from nothing.** An existing legend can be duplicated: that is
  `revit_create_views` with `kind: "legend"` and `sourceLegend` (see [view-creation.md](view-creation.md#legend)).
  When the model has no legend, make one by hand (View > Legends > Legend).
- **A legend component cannot be created from nothing either.** `revit_populate_legend` copies a legend component that
  is already in the legend (the *seed*) and switches the copy to another type. A legend without any legend component
  can still take detail item and generic annotation types, but not model types: place one legend component by hand
  (Annotate > Component > Legend Component, any type) and run again.
- Revit decides which types a legend component may show. The connector offers model types (family types and system
  types of model categories); Revit can still refuse one, and that type is reported as `refused` with Revit's message.
  Tags and other view-based annotation families are `unsupported`.
- The legend component's view direction, detail level and host length stay those of the seed. Set them on the seed
  first (or in Revit afterwards); the tool does not change them.

## Legends

### `revit_list_legends`

Per legend: `id`, `name`, `scale`, `sheets` (sheet numbers it is placed on), `componentCount`, `textNotes`,
`detailItems` (detail item and generic annotation instances), `canSeed` and `components`
`[{id, typeId, family, type, category, viewDirection, detailLevel, position: [xMm, yMm]}]`. `legend` (name or id)
lists one legend; `includeComponents=false` gives counts only; `maxComponents` caps the list (default 100).

Positions are **printed mm**: model mm divided by the legend's scale, along the view's right and up directions.

### `revit_populate_legend`

```json
{ "tool": "revit_populate_legend", "arguments": {
    "legend": "EN tingmärgid",
    "category": "Fire Alarm Devices",
    "columns": 2, "rowSpacingMm": 10, "columnSpacingMm": 90,
    "label": "Description", "textType": "2.5mm Arial",
    "clearExisting": true
} }
```

| Argument | Notes |
|---|---|
| `legend` | Required. The legend view's name or id |
| `types` | Type ids, `{family, type}`, `{family}` (every type of the family), `{id}` or `"Family: Type"`. Placed in the order given unless `sortBy` says otherwise |
| `category`, `families` | Instead of `types`: every type of the category, of the families, or of both. Sorted by family, then type |
| `columns` | Grid columns, 1 to 50. Default 1. Cells fill left to right, then down |
| `rowSpacingMm`, `columnSpacingMm` | Printed mm between symbol centres. Defaults 10 and 90 |
| `origin` | `[xMm, yMm]` of the first symbol's centre, printed mm. Default: one row under the lowest element that stays in the legend, aligned with the seed |
| `label` | `typeName` (default), `familyAndType` (`Family: Type`), `none`, or the name of a **type** parameter (for example `Description` or `Type Mark`); a type without a value gets no label and a warning |
| `textType` | Text note type name or id. Default: the document's default text type |
| `textOffsetMm` | The label's left edge, printed mm right of the symbol's centre (vertically centred on the row). Default 12. A label that overlaps its symbol gives a warning |
| `clearExisting` | Delete every legend component, text note, detail item and generic annotation already in the legend, **except the seed**, before placing. Default false |
| `sortBy` | `request`, `family`, `type`, `category` or `label` |
| `seedId` | The legend component to copy. Default: the legend's first (lowest id) |
| `dryRun` | Preview only: try the placement in a rolled-back transaction. Default true |

How each type is placed (`kind` in the result):

- `component`: a model type. The seed is copied (`ElementTransformUtils.CopyElement`) and the copy's *Component
  Type* parameter (`LEGEND_COMPONENT`) is set to the type. The tool reads the parameter back and treats a type Revit
  did not take as refused.
- `detailInstance`: a detail item or generic annotation type, placed with `NewFamilyInstance` in the legend view. A
  line-based detail item is drawn as a short horizontal line that ends 2 mm before its label.
- `unsupported`: anything else, reported with the reason and skipped.

Each placed symbol is moved so that the centre of its box sits on its grid cell, since a new type has another size.

Per type the result gives `typeId`, `family`, `type`, `category`, `kind`, `status` (`willPlace` and, after the trial,
`placed`, `refused`, `unsupported`, `needsSeed`), `reason`, `elementId`, `label`, `labelId`, `viewDirection` and the
`cell`.

- Unknown ids, names and families (with close names), an unknown category or text type, model types without a seed,
  and requests where nothing can be placed are **problems**: nothing is changed.
- A type Revit refuses is rolled back on its own (sub-transaction) and reported; the others are placed. If Revit
  refuses every type, nothing is changed, including the deletions of `clearExisting`.
- One transaction and one undo step.

**clearExisting deletes elements.** The preview lists them in `toDelete` (id and kind) and the approval prompt states
how many legend components, text notes and detail items it deletes. Lines, filled regions and dimensions in the legend
are never touched. A call that deletes elements is never covered by a plan: it always waits for its own approval and,
inside a plan, is rolled back with `plan_envelope_violated`.

## Keynotes

### `revit_get_keynotes`

Returns `table` (`path`, `pathType`, `status` as Revit reports it, `fileExists`), `total`, `matching`, paging
(`offset`, `limit` default 100 and at most 1000, `hasMore`) and `entries [{key, text, parent, children}]` in natural
key order (`E.2` before `E.10`).

- `search`: text in the key or the keynote text, any case.
- `parent`: only the direct children of that key; `"root"` or `""` for the top level.
- `usage=true`: per listed key, how many **types**, **materials** and other **elements** have that value in their
  Keynote parameter (`KEYNOTE_PARAM`), with up to 5 sample ids; and `usage.unknownKeys`: values used in the model that
  the table does not have. `elements` counts every non-type element whose own Keynote parameter has a value; whether
  keynote tags expose their key that way is not yet checked in Revit (see below).

### Assigning a keynote to a type

No separate tool: the Keynote parameter is a type parameter, which the generic parameter tool writes on the type id.

```json
{ "tool": "revit_set_parameter", "arguments": {
    "elementIds": [123456], "parameterName": "Keynote", "builtInParameter": "KEYNOTE_PARAM", "value": "E.10"
} }
```

`builtInParameter` makes the call independent of the Revit language. For a material, pass the material's id. Revit
does not check the value against the table: check the key with `revit_get_keynotes search=…` first.

### `revit_reload_keynote_table`

```json
{ "tool": "revit_reload_keynote_table", "arguments": { "path": "C:\\Projects\\P125\\Keynotes\\EULE_keynotes.txt" } }
```

- Without `path`, Revit reloads the table from the file it points to (`KeyBasedTreeEntryTable.Reload`).
- With `path`, the table is loaded from that local file and points to it from then on (`LoadFrom` with an absolute
  path). The path must pass the file access policy (`FileAccessPolicy.json`, see [execution-safety.md](execution-safety.md))
  and exist. Relative and server paths are not set by this tool.
- The result gives `loadStatus` (`Success`, `ResourceAlreadyCurrent`, …), the table's file after the call, the
  entries before and after and `change` (added, removed and changed keys, at most 50 of each listed), and
  `revitMessages` (read, syntax and entry errors Revit reported). Any status other than `Success` or
  `ResourceAlreadyCurrent` rolls back and changes nothing.

The preview reads the file itself (key TAB text TAB parent key; blank lines and lines starting with `#` are skipped)
and compares it with the table in the model. It also lists lines it could not read and parent keys the file does not
define. Revit's own read during the reload is what counts.

## Not yet run in Revit

These tools were written when no Revit was reachable. Type matching, the grid, labels, what `clearExisting` deletes,
keynote search, the keynote file parser, the table comparison and usage counting are unit tested
(`LegendsAndKeynotesTests`). The code compiles against the Revit 2024 and 2026 APIs. These behaviours are assumed and
must be checked:

1. `ElementTransformUtils.CopyElement` copies a legend component inside its legend, and setting `LEGEND_COMPONENT` on
   the copy switches its type for model family types and system types (walls, floors).
2. Which types Revit refuses as legend components, and how it refuses (an exception, `Set` returning false, or the
   parameter keeping its old value): all three are reported as `refused`.
3. `NewFamilyInstance(point, symbol, legendView)` places detail items and generic annotations in a legend view, and
   `NewFamilyInstance(line, symbol, legendView)` places line-based detail items.
4. A legend component's bounding box in its view is its symbol, so centring works.
5. The keynote table rolls back with the transaction when `Reload`/`LoadFrom` succeeds but the commit fails.
6. User keynote tags store their key in the tag's own Keynote parameter: they would then be counted under `elements`.

Test steps:

1. In a project with a legend that holds one legend component, run `revit_list_legends`. Check `canSeed: true`, the
   component's family, type and view direction, and that its position matches the legend (printed mm).
2. `revit_preview_populate_legend legend=<name> category="Electrical Fixtures" columns=2`: every type `placed` in the
   trial, the model unchanged afterwards (no new elements, nothing in Undo).
3. Run `revit_populate_legend` with the same arguments and approve. Check the symbols and labels in the legend: grid,
   spacing at the legend's scale, labels to the right and vertically centred, one Undo removes everything.
4. Add `types` with a wall type, a tag type and a detail item type: the wall shows as a legend component (or is
   reported refused), the tag is `unsupported`, the detail item is placed as an instance.
5. Run with `clearExisting=true`: the approval prompt names the counts to delete, only the seed remains of the old
   content, lines and filled regions in the legend stay.
6. Try a legend without legend components: model types report `needsSeed` and nothing changes; with only detail item
   types, they are placed.
7. Use `label="Description"` with a type that has no description: warning, no label for it.
8. `revit_get_keynotes`: entries and the file path match Manage > Additional Settings > Keynoting Settings; `search`,
   `parent=root` and paging work. With `usage=true`, assign a key to a type with `revit_set_parameter` and check its
   `types` count rises; a key that is not in the table shows in `unknownKeys`.
9. Edit the keynote file (add, remove, change a line). `revit_preview_reload_keynote_table` shows the three changes;
   `revit_reload_keynote_table` reports the same `change`, keynote tags show the new text, one Undo restores the old table.
10. `revit_reload_keynote_table path=<another file>`: the Keynoting Settings dialog then shows that file. A path
    outside the file access policy and a missing file are refused without a change.
