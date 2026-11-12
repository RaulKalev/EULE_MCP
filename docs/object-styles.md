# Object styles

Object Styles (Manage > Object Styles) set the projection and cut line weight, line colour, line pattern and material
of each category and subcategory. The connector lists them, edits them and creates subcategories, in a project or in a
family that is open in the Family Editor.

The tools are in the `tags` group, next to the line style tools (`--tool-groups tags`, or find them with
`revit_tools_search`).

| Tool | Permission | Purpose |
|---|---|---|
| `revit_list_object_styles` | Read-only | Categories and subcategories with their weights, colour, pattern, material and `canAddSubcategory` |
| `revit_preview_set_object_styles` | Read-only | What `revit_set_object_styles` would create or change, and the problems to fix first |
| `revit_set_object_styles` | Requires approval | Edits categories and subcategories, creates subcategories |

`document` targets an open document other than the active one, a project or a family.

Subcategories of *Lines* are line styles: `revit_set_object_styles` refuses the Lines category and points to
`revit_set_line_styles` ([line-styles.md](line-styles.md)), which also renames styles and, with
`revit_delete_styles`, deletes them.

## Listing

```json
{ "tool": "revit_list_object_styles", "arguments": { "category": "Electrical", "kind": "model", "limit": 20 } }
```

| Argument | Notes |
|---|---|
| `category` | Only categories whose name contains this (ignoring case) |
| `kind` | `model`, `annotation` or `imported`; empty for all three |
| `includeSubcategories` | Default `true` (at most 200 per category; `subcategoriesTruncated` says when there are more) |
| `offset`, `limit` | Page the categories: default 50, at most 200; `nextOffset` is the next page |

Each category: `id`, `name`, `kind`, `builtInCategory` (`OST_…`), `projectionWeight`, `cutWeight` (null when the
category is not cut in sections), `color`, `pattern`, `material`, `canAddSubcategory`, `subcategoryCount` and
`subcategories` (the same fields, and `builtIn` for Revit's own subcategories).

Which tab a category belongs to: a top-level category with a positive id is **imported** (a DWG import's layers become
its subcategories), as is *Imports in Families*; otherwise the category type decides (model or annotation). Internal
and analytical categories, and categories Revit hides from the user interface, are not listed.

In a family, `familyCategory` is the family's own category and it is listed first: its subcategories are the ones a
family's geometry and lines are assigned to.

## Editing and creating subcategories

```json
{ "tool": "revit_set_object_styles", "arguments": { "styles": [
    { "category": "Electrical Fixtures", "subcategory": "EL_Sümbol", "projectionWeight": 2, "color": "#0000FF" },
    { "category": "Walls", "cutWeight": 5 },
    { "category": "OST_GenericModel", "subcategory": "Hidden", "pattern": "Hidden", "material": "Concrete" }
] } }
```

| Field | Notes |
|---|---|
| `category` | Required. The category's name, its built-in name (`OST_Walls`) or its id |
| `subcategory` | Edited when the category has it; **created** when missing and the category accepts new subcategories. Without it, the category itself is edited |
| `projectionWeight` | Line weight number 1-16 (`weight` and `lineWeight` are accepted too) |
| `cutWeight` | Line weight number 1-16; refused for a category that is not cut |
| `color` | `"#RRGGBB"`, `"r,g,b"` or `[r, g, b]` |
| `pattern` | A line pattern name of the document, or `solid` |
| `material` | A material name of the document, or `none` to clear it; refused for annotation categories |

- Only the fields given change. A new subcategory gets Revit's defaults for the fields that are not given.
- Editing a category or subcategory changes every element drawn with it.
- One transaction and one undo step. Any problem (a category, pattern or material that is not found, a cut weight on
  a category that is not cut, a material on an annotation category, a category that does not accept new
  subcategories) leaves the document unchanged. Not-found names come with close names.
- Revit's own subcategories (negative ids) can be edited only as far as Revit allows; the preview warns about them.
- Subcategories are not renamed or deleted by these tools.

Plans count `revit_set_object_styles` as creating elements, because a missing subcategory is created.

## Not yet run in Revit

These tools were written when no Revit was reachable. The argument parsing and the Object Styles tab rule are unit
tested (`LineStyleDefinitionTests`, `StyleUsagePlannerTests`); the Revit calls are the ones `revit_set_line_styles`
uses, plus `Category.Material` and the cut line weight. Run these in a test project and in a family:

1. `revit_list_object_styles kind=model category=Walls`: compare the weights, colour, pattern and material with
   Manage > Object Styles. `cutWeight` should be a number for Walls and null for a category without a cut column
   (for example Furniture Systems, which Object Styles shows without a cut weight).
2. `kind=imported` with a DWG imported (not linked) into the project: the DWG appears as a category with its layers
   as subcategories. Report it if it is listed as `model`, or not at all.
3. `kind=annotation`: Generic Annotations and tag categories appear, without material.
4. `revit_preview_set_object_styles` with `{category: "Walls", cutWeight: 5, color: "#FF0000"}`, then apply. Object
   Styles shows the change; one Undo reverts it.
5. Create `{category: "Generic Models", subcategory: "MCP_Test", projectionWeight: 3, material: "Concrete"}` in a
   project and in a Generic Model family. Expect `created` with an id, and the subcategory in Object Styles with weight
   3 and the material. In the family, `familyCategory` is Generic Models and is listed first.
6. Try `material` on `Generic Annotations` (refused), `cutWeight` on a category Object Styles shows without a cut
   column (refused, or report what Revit does when the check lets it through), and `category: "Lines"` (refused,
   pointing to `revit_set_line_styles`).
7. `material: "none"` on a subcategory that has a material: Object Styles shows no material (`<By Category>`).
8. Edit a built-in subcategory (for example Doors / Frame/Mullion): note any change Revit refuses; the whole
   transaction should be rolled back with Revit's message.
