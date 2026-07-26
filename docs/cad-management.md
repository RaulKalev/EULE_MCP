# CAD Import and Layer Management

The connector exposes the useful model operations from CAD Manager as MCP tools.
The WPF tree, search box, and file dialogs are intentionally not copied: MCP
clients provide selection and orchestration, while the add-in retains all Revit
API reads, approval checks, and transactions.

## Tools

| Tool | Permission | Purpose |
|---|---|---|
| `revit_list_cad_imports` | Read-only | Inspect CAD imports, layers, visibility, halftone, and projection-line overrides |
| `revit_preview_set_cad_overrides` | Read-only | Resolve and review an override change set |
| `revit_set_cad_overrides` | Requires approval | Apply visibility and graphic overrides |
| `revit_preview_copy_cad_overrides` | Read-only | Review copying settings from one view to other views |
| `revit_copy_cad_overrides` | Requires approval | Copy settings from one view to other views |

Both imported and linked CAD files are represented by Revit `ImportInstance`
elements and are handled by the same tools.

## Inspect and reuse settings

`revit_list_cad_imports` defaults to the active view and returns a
`presetChanges` array. That array uses the same schema accepted by
`revit_preview_set_cad_overrides` and `revit_set_cad_overrides`, so an MCP
client can save it with the existing file tools and reapply it later.

When `useViewTemplate=true` (the default), the tool reads settings from the
assigned view template. The response always reports both the requested view and
the actual settings owner.

## Change schema

Each object in `changes` selects a CAD import with one of:

- `importInstanceId`
- `importName`
- `allImports=true`

Omit `layerName` to target the import category itself. Use an exact layer name
for one layer or `layerName="*"` for every layer in the selected import.

Supported setting fields:

- `visible`: boolean
- `halftone`: boolean
- `lineColor`: `#RRGGBB`
- `lineWeight`: integer from 1 through 16
- `linePatternId` or `linePatternName`
- `clearGraphics`: reset graphic overrides before applying supplied values

Example:

```json
[
  {
    "importName": "site.dwg",
    "layerName": "A-WALL",
    "visible": true,
    "halftone": true,
    "lineColor": "#808080",
    "lineWeight": 2
  }
]
```

Always run the matching preview tool before a write tool.

## View templates

`useViewTemplate=true` and `useTargetViewTemplates=true` reproduce CAD Manager's
effective-settings behavior: if a view has a template, the template is changed.
That can affect every view using the template, so previews report the unique
settings owners before approval. Set the option to `false` to target project
views directly; Revit may reject fields controlled by a template.

## Safety and compatibility

- All writes execute on Revit's API thread through the connector's existing
  `ExternalEvent` dispatcher.
- Each write uses `RevitTransactionRunner`, including rollback diagnostics.
- Write tools require connector approval.
- The implementation builds for Revit 2024 (`net48`) and Revit 2026
  (`net8.0-windows`) and uses Revit API patterns available in Revit 2021+.
