# Line styles

A line style is a subcategory of *Lines* with a line weight, a colour and a line pattern. The connector lists them,
creates new ones and edits existing ones, in a project or in a family that is open in the Family Editor.

All three tools are in the `tags` group (`--tool-groups tags`, or find them with `revit_tools_search`).

| Tool | Permission | Purpose |
|---|---|---|
| `revit_list_line_styles` | Read-only | The document's line styles: id, name, weight, colour, pattern, `builtIn`; `canAddStyles`; with `includePatterns=true` the line pattern names |
| `revit_preview_set_line_styles` | Read-only | What `revit_set_line_styles` would create or change, and the problems to fix first |
| `revit_set_line_styles` | Requires approval | Creates styles and edits existing ones |

`document` targets an open document other than the active one, a project or a family.

## Creating and editing

```json
{ "tool": "revit_set_line_styles", "arguments": { "styles": [
    { "name": "EL_Kaabel", "weight": 3, "color": "#FF0000", "pattern": "Dash" },
    { "name": "EL_Õhuke", "weight": 1 }
] } }
```

| Field | Notes |
|---|---|
| `name` | Required. No `\ : { } [ ] ; < > ? ~`, pipe or backtick |
| `weight` | Revit's line weight **number**, 1 to 16, for projection lines. It is not millimetres: the width of each number per view scale is set in Manage > Additional Settings > Line Weights. `lineWeight` and `size` are accepted as the same field |
| `color` | `"#RRGGBB"`, `"r,g,b"` or `[r, g, b]` |
| `pattern` | A line pattern name of the document, or `solid` |

- A name the document does not have is **created**. Fields left out get weight 1, black and solid.
- A name it has is **edited**: only the fields given change. Names match without regard to case.
- Editing a style changes every line drawn with it.
- One transaction and one undo step. Any problem (a pattern the document lacks, a document that does not accept new
  styles, a change Revit refuses) leaves the document unchanged.
- Revit's own styles (`builtIn: true`, such as `<Thin Lines>`) can be edited only as far as Revit allows; the preview
  warns about them.

The tool does not rename or delete styles, and it does not create line patterns: a pattern must already exist in
the document (Manage > Additional Settings > Line Patterns).

## In a family being created

`revit_create_detail_family` takes the same definitions as `lineStyles`, for styles the family should have whether
or not the project has them. See [family-creation.md](family-creation.md#line-styles).

## Not yet run in Revit

These tools were written when no Revit was reachable. The parsing and the merge rules are unit tested
(`LineStyleDefinitionTests`); the Revit calls are the same ones `revit_create_detail_family` already uses to copy
line styles into a family. Test steps are in
[integration/revit-test-plan-147.md](integration/revit-test-plan-147.md).
