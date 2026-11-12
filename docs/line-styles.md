# Line styles

A line style is a subcategory of *Lines* with a line weight, a colour and a line pattern. The connector lists them,
creates new ones, edits and renames existing ones, in a project or in a family that is open in the Family Editor. It
also counts what uses a line style or a filled region type, and deletes them after moving their users to another
style.

All the tools are in the `tags` group (`--tool-groups tags`, or find them with `revit_tools_search`). The other
categories and subcategories of Object Styles have their own tools: see [object-styles.md](object-styles.md).

| Tool | Permission | Purpose |
|---|---|---|
| `revit_list_line_styles` | Read-only | The document's line styles: id, name, weight, colour, pattern, `builtIn`; `canAddStyles`; with `includePatterns=true` the line pattern names |
| `revit_preview_set_line_styles` | Read-only | What `revit_set_line_styles` would create, change or rename, and the problems to fix first |
| `revit_set_line_styles` | Requires approval | Creates styles, edits and renames existing ones |
| `revit_get_style_usage` | Read-only | The elements that use given line styles and filled region types, by view |
| `revit_preview_delete_styles` | Read-only | What `revit_delete_styles` would move and delete, and what it refuses |
| `revit_delete_styles` | **Destructive**, manual approval | Deletes line styles and filled region types, moving their users to a replacement first |

`document` targets an open document other than the active one, a project or a family.

## Creating, editing and renaming

```json
{ "tool": "revit_set_line_styles", "arguments": { "styles": [
    { "name": "EL_Kaabel", "weight": 3, "color": "#FF0000", "pattern": "Dash" },
    { "name": "EL_Õhuke", "weight": 1 },
    { "name": "EL_Vana nimi", "newName": "EL_Uus nimi" }
] } }
```

| Field | Notes |
|---|---|
| `name` | Required. No `\ : { } [ ] ; < > ? ~`, pipe or backtick |
| `weight` | Revit's line weight **number**, 1 to 16, for projection lines. It is not millimetres: the width of each number per view scale is set in Manage > Additional Settings > Line Weights. `lineWeight` and `size` are accepted as the same field |
| `color` | `"#RRGGBB"`, `"r,g,b"` or `[r, g, b]` |
| `pattern` | A line pattern name of the document, or `solid` |
| `newName` | Renames the existing style `name`. Same character rules as `name` |

- A name the document does not have is **created**. Fields left out get weight 1, black and solid.
- A name it has is **edited**: only the fields given change. Names match without regard to case.
- Editing a style changes every line drawn with it.
- **Renaming** keeps every line on the style; only the name changes. It is refused for a name the document does not
  have, for Revit's own styles (`builtIn: true`, such as `<Thin Lines>`), for a new name another style already has, and
  for two items that would end with the same name. A case-only change (`el_kaabel` to `EL_Kaabel`) is a rename.
- One transaction and one undo step. Any problem (a pattern the document lacks, a document that does not accept new
  styles, a change Revit refuses) leaves the document unchanged.
- Revit's own styles can be edited only as far as Revit allows; the preview warns about them.

How the rename works: `Category.Name` is read-only in the Revit 2024 and 2026 APIs (checked against both reference
assemblies), so the tool sets the name of the style's projection `GraphicsStyle` element, on both versions. After the
commit it reads the styles again; if Revit kept the old name, the result has a warning and the other changes stand.

The tool does not create line patterns: a pattern must already exist in the document
(Manage > Additional Settings > Line Patterns).

## What uses a style

```json
{ "tool": "revit_get_style_usage", "arguments": {
    "lineStyles": ["EL_Vana"], "filledRegionTypes": ["EL_Viirutus"], "maxIds": 50 } }
```

Names or ids, of line styles and/or filled region types. Per style: `count`, `byUse` and `byView` (the busiest view
first; model elements under `(model)`) with element ids, at most `maxIds` ids in all (default 50, max 1000).

| Use | Counted from |
|---|---|
| `DetailCurve`, `ModelCurve`, `SymbolicCurve`, … | every curve element by its line style (`CurveElement.LineStyle`), named by `CurveElementType` |
| `filledRegionBoundary` | a curve that a filled or masking region depends on (its boundary sketch line), under the region's view |
| `filledRegion`, `maskingRegion` | every filled region by its type |

`notCounted` lists what the count cannot see:

- lines and filled regions inside loaded families (a family's own line styles appear among the project's Lines);
- view templates, view filters and graphic overrides, including edges changed with the Linework tool;
- line styles or filled region types named in parameters, schedules or other add-ins' settings;
- linked models.

## Deleting

```json
{ "tool": "revit_delete_styles", "arguments": {
    "lineStyles": ["EL_Vana"], "filledRegionTypes": ["EL_Viirutus"],
    "replaceWith": { "lineStyle": "EL_Kaabel", "filledRegionType": "Solid Black" } } }
```

- `replaceWith.lineStyle` moves every curve and region boundary edge that uses a deleted line style to it first;
  `replaceWith.filledRegionType` moves every region of a deleted type. A built-in style may be the replacement.
- Refused, and then **nothing** is deleted: a style still in use without a replacement of its kind (`style_in_use`,
  with the count), Revit's own line styles (`builtin_style`), deleting every filled region type
  (`last_filled_region_type`), a replacement that is missing (`replacement_not_found`) or deleted in the same request
  (`replacement_deleted`), a replacement some curve or region boundary cannot take (`replacement_not_valid`), a name
  that is not found (`style_not_found`, with close names) or listed twice.
- One transaction, one undo. If Revit refuses any move or delete, nothing changes. If deleting a style would also
  delete any line or filled region, the transaction is rolled back.
- A region boundary edge is moved on its own; when Revit refuses that and every edge of the region moves, the region's
  whole boundary takes the replacement (`FilledRegion.SetLineStyleId`) and the result says which regions.
- Like the other deletes it always needs manual approval in Revit, which Direct Edit does not bypass, plans never cover
  it, and it reads its targets again by name when it runs, so the approval may wait for the user
  (see [execution-safety.md](execution-safety.md)). The approval summary starts with `DESTRUCTIVE` and lists each style
  with its uses and replacement.

## In a family being created

`revit_create_detail_family` takes the same definitions as `lineStyles`, for styles the family should have whether
or not the project has them (without `newName`). See [family-creation.md](family-creation.md#line-styles).

## Not yet run in Revit

These tools were written when no Revit was reachable. The parsing, the merge and rename rules, usage counting and the
delete planning are unit tested (`LineStyleDefinitionTests`, `StyleUsagePlannerTests`); creating and editing use the
same Revit calls `revit_create_detail_family` already uses to copy line styles into a family. Earlier test steps are in
[integration/revit-test-plan-147.md](integration/revit-test-plan-147.md). Run these in a test project and in a detail
item family:

1. `revit_set_line_styles` with `{name: "MCP_Test", weight: 3}`, then `{name: "MCP_Test", newName: "MCP_Test2"}`. Expect
   `edited` with a `name` change and no warning; Manage > Additional Settings > Line Styles shows `MCP_Test2` with
   weight 3, and lines drawn with it keep it. One Undo restores `MCP_Test`. If the result warns that Revit kept the old
   name, the GraphicsStyle rename does not work: report it.
2. Try `{name: "<Thin Lines>", newName: "X"}` and a `newName` that another style has: both refused in the preview.
3. Draw two detail lines and a filled region whose boundary uses `MCP_Test2`, and a model line with it in a 3D view.
   `revit_get_style_usage lineStyles=["MCP_Test2"]`: expect `DetailCurve` 2, `filledRegionBoundary` 4 (a rectangle) under
   the region's view, `ModelCurve` 1 under `(model)`. If the boundary edges show as `DetailCurve` instead, or are missing,
   region sketch lines are not returned the way the tool assumes.
4. `revit_preview_delete_styles lineStyles=["MCP_Test2"]`: refused, `style_in_use` with 7 uses. With
   `replaceWith: {lineStyle: "<Thin Lines>"}`: ready, 7 to move.
5. `revit_delete_styles` with that replacement. Approve in Revit; the dialog text starts with `DESTRUCTIVE`. Expect the
   lines and the region boundary on `<Thin Lines>`, the style gone, no line or region deleted, one Undo restoring all.
   Note whether the region needed `SetLineStyleId` (the warning names it).
6. Create a filled region type copy (`revit_set_filled_region_types`), place a region of it, delete the type with
   `replaceWith.filledRegionType`. Expect the region on the replacement type. Repeat with a masking region and a
   non-masking replacement and note what Revit does.
7. In a family with one filled region type, try deleting it: refused (`last_filled_region_type`).
8. Delete an unused style with lines of it inside a loaded family's geometry (load a family that uses a style of the
   same name): note what happens to the family's lines; the usage count does not see them.
