# Label families

Revit's API cannot create a *label* (text bound to a parameter). `revit_create_detail_family` therefore shows a
parameter through a **label family**: a Generic Annotation family that already holds one label bound to one of its
own parameters (see [family-creation.md](family-creation.md#labels-that-show-a-parameter)). Someone makes that family
by hand once; these tools list such families and make variants of one, so the hand-made family goes a long way.

All three tools are in the `edit` group (`--tool-groups edit`, or find them with `revit_tools_search`).

| Tool | Permission | Purpose |
|---|---|---|
| `revit_list_label_families` | Read-only | Generic Annotation families that can serve as label families, with their parameters |
| `revit_preview_create_label_family` | Read-only | What `revit_create_label_family` would make, checked against the seed |
| `revit_create_label_family` | Requires approval | Saves a variant of a seed label family as a new `.rfa`, and optionally loads it |

`document` targets an open document other than the active one.

## Making the seed by hand

Once, in Revit:

1. **File > New > Family**, choose `Metric Generic Annotation.rft` (in the *Annotations* folder of the family
   templates).
2. Delete the template's sample text note if you do not need it.
3. **Create > Family Types** (Properties panel). Under *Parameters* click **New Parameter**: *Family parameter*, a name
   such as `Tekst`, *Data type* **Text**, and choose **Instance** (each placed label can then show its own value) or
   **Type**. Click OK, give it a value (for example `ABC`) and close the dialog.
4. **Create > Label** (Text panel), click where the text goes (the insertion point is the family's origin, where the
   reference planes cross). In *Edit Label* move `Tekst` to *Label Parameters* and click OK.
5. Select the label and, in *Edit Type*, set the text size, font, width factor and colour you want as the default.
6. Optionally add types (**Family Types > New Type**) such as `2.5 mm` and `3.5 mm`.
7. **Save** it under a clear name (for example `EL_Silt.rfa`) and **Load into Project**.

Keep the family to **one label bound to one parameter**: the tools cannot tell which parameter a label shows, so a
second editable parameter makes you name the label's parameter on every call.

## Listing label families

```json
{ "tool": "revit_list_label_families", "arguments": { "families": ["EL_Silt"] } }
```

Each Generic Annotation family loaded in the document is listed with its id, types and whether it can be opened for
editing. Reading its parameters needs the family document, which is slow, so only the first `limit` families by name
(default 5, at most 25), or those named in `families`, are opened (`Document.EditFamily`, closed without saving).
`inspected` and `notInspected` say which. `inspect=false` opens none. `path` reads one `.rfa` file instead.

For an opened family:

| Field | Notes |
|---|---|
| `parameters` | Its editable, user-defined family parameters (no formula, not read-only): `name`, `dataType`, `instance`, `shared`. These are the candidates for `labelParameter` |
| `otherParameters` | User-defined parameters driven by a formula or read-only |
| `hasLabel`, `labels` | Whether, and how many, labels the API sees (below) |
| `labelTextTypes` | The labels' text types: size (paper mm), font, bold, italic, underline, width factor, colour |

**What can be detected.** The API has no *Label* class. In a family document a label is a `TextElement` that is not
a `TextNote`, so the tool counts those: `hasLabel` is true when there is at least one. The API does not expose which
parameter a label is bound to, so the tools never say which parameter a label shows; when the family has exactly one
editable user-defined parameter, that is the one.

## Making a variant

```json
{ "tool": "revit_create_label_family", "arguments": {
    "seed": "EL_Silt",
    "savePath": "C:\\Families\\Annotations", "familyName": "EL_Silt Kilp",
    "types": [ { "name": "3.5 mm", "textSize": 3.5, "font": "Arial", "bold": true } ],
    "renameParameter": { "from": "Tekst", "to": "Kilbi nimi" },
    "makeInstance": false,
    "sampleValue": "K1",
    "loadIntoProject": true } }
```

| Field | Notes |
|---|---|
| `seed` | Required. The label family to copy: a Generic Annotation family loaded in the document (by name) or a `.rfa` path. It is **never changed**: the tool opens a copy (`EditFamily`, or the file), edits that, saves it under the new name and closes it without saving |
| `savePath`, `familyName` | Required, as for `revit_create_detail_family`: a folder with `familyName`, or the full `.rfa` path. The name must differ from the seed's. The file is written to a temporary folder and then copied into place |
| `overwrite` | Replace an existing file at `savePath` (default false) |
| `types` | `[{name, textSize?, font?, bold?, italic?, underline?, widthFactor?, color?}]`, or just names. These become the family's types: a seed type of the same name is kept (renamed when only the case differs), a new name is created from the first kept type, and seed types **not listed are deleted**. Leave `types` out to keep the seed's types |
| `textSize` | Text size on paper in mm (above 0, at most 100) |
| `font`, `bold`, `italic`, `underline`, `widthFactor` (0.01-10), `color` (`#RRGGBB`, `r,g,b` or `[r,g,b]`) | The label's text appearance |
| `parameter` | The seed parameter the label shows, which the parameter changes apply to. Default: `renameParameter.from`, else the seed's only editable user-defined parameter |
| `renameParameter` | `{from?, to}`: renames that parameter (`FamilyManager.RenameParameter`). A shared parameter cannot be renamed |
| `makeInstance` | `true` makes it an instance parameter, `false` a type parameter (`FamilyManager.MakeInstance` / `MakeType`) |
| `sampleValue` | The parameter's value in every type: the text the label shows until a value is given |
| `loadIntoProject`, `ifExists` | Load the saved file into the document; `skip` (default) or `overwrite` when a family of that name is loaded. The path is remembered as for other loaded families |

### Text appearance is one per family

A label's look comes from its **text type** (a `TextElementType` in the family), and the label references one text
type; the API cannot bind a label's text type to a family type. So the appearance fields set that text type and
apply to **every** type of the variant. A field may be given in one type or repeated with the same value; types that
give different values (2.5 mm and 3.5 mm) are refused: make one variant per text size instead. The text type is
edited in the copy; when the seed's text notes use the same text type they change too, and the preview warns.

Each field is set through the text type's parameter (`TEXT_SIZE`, `TEXT_FONT`, `TEXT_STYLE_BOLD`,
`TEXT_STYLE_ITALIC`, `TEXT_STYLE_UNDERLINE`, `TEXT_WIDTH_SCALE`, `LINE_COLOR`). A field Revit refuses is reported in
`labelTextTypes[].refused` and as a warning; the rest is still saved. If the API sees no label in the seed, the
appearance fields are a problem in the preview.

### The preview

`revit_preview_create_label_family` opens the seed the same way and closes it without saving or starting a
transaction. It checks the arguments, the save path (policy, existing file), that the seed exists and is a Generic
Annotation, the type plan (`keep | rename | create | delete`), the parameter (exists, not shared when renamed, the new
name free, no formula when `sampleValue` is given), the label and its text types, and whether the family would load.

## What the tools do not do

- They do not create a label or change which parameter a label shows: the API cannot. The seed must hold the label.
- They cannot report which parameter a label is bound to (see *What can be detected*).
- The label's own *sample value* in the *Edit Label* dialog is not reachable through the API; `sampleValue` sets the
  family parameter's value, which is what a placed label shows.
- Text appearance per family type is not possible with one label (see above).
- They do not move the label, change its alignment or wrap width.

## Not yet run in Revit

These tools were written when no Revit was reachable. The argument parsing, the type plan, the parameter choice and
the colour conversion are unit tested (`LabelFamilyVariantTests`); the Revit calls are not yet verified. Before relying
on them, run these steps in Revit 2024 and 2026:

1. Make a seed by hand as above (`EL_Silt`, one instance text parameter `Tekst`, one label, types `A` and `B`) and load
   it into a project.
2. `revit_list_label_families`: `EL_Silt` is listed with types `A`, `B`, `inspected` contains it, `parameters` shows
   `Tekst` (text, instance), `hasLabel` is **true** and `labels` is 1, `labelTextTypes` shows the size you set. If
   `hasLabel` is false, label detection does not work in that version: report it.
3. Load a Generic Annotation family without a label (a plain text note family): `hasLabel` is false.
4. `revit_list_label_families path=<the seed .rfa>` returns the same as step 2 for the file, and the file is not
   locked afterwards. Open the `.rfa` in Revit first and repeat: the tool refuses because it is open.
5. `revit_preview_create_label_family` with the example above: `ready` true, types plan `3.5 mm: create`, `A: delete`,
   `B: delete`; parameter `Tekst` renamed to `Kilbi nimi`, scope `instance -> type`; no Revit dialog appears; the
   project is not changed (no undo entry).
6. `revit_create_label_family` with the same arguments: the file exists at `savePath`, and opening it shows one type
   `3.5 mm`, a type parameter `Kilbi nimi` with value `K1`, and the label still bound to it, 3.5 mm Arial bold.
7. Check the seed is unchanged: the loaded `EL_Silt` still has types `A`, `B` and parameter `Tekst`; when the seed was
   a `.rfa` path, its file date has not changed.
8. With `loadIntoProject=true`: the variant is loaded, its type is active, and placing it shows `K1`.
9. Run it again with `ifExists=skip` (skipped) and `ifExists=overwrite` (overwritten); without `overwrite=true` the
   preview reports the existing file.
10. Use the variant in `revit_create_detail_family` (`labelFamily: "EL_Silt Kilp"`, a text parameter): the label is
    placed and linked; with `makeInstance=false` a second label of the same type showing another parameter is
    refused as documented in family-creation.md.
11. `types: [{"name":"a"}]` on a seed with type `A`: the type is renamed to `a` (case only), not deleted and re-made.
12. `color: "#FF0000"`, `widthFactor: 0.8`, `italic: true`, `underline: true`: each shows in the label's type
    properties, and none is listed under `refused`.
13. A seed whose parameter is shared: `renameParameter` is refused in the preview; `makeInstance` works.
