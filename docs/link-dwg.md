# Linking DWG files

`revit_preview_link_dwg` and `revit_link_dwg` link DWG files into views, as Insert > Link CAD does. They are in the
`documents` group, next to reloading and removing links. Both take `document` (a title or path from
`revit_list_open_documents`), so they also work on an open project that is not the active one.

| Tool | Permission | Purpose |
|---|---|---|
| `revit_preview_link_dwg` | Read-only | Per file: the target view, the options, and willLink / willSkip / invalid with the reason |
| `revit_link_dwg` | Requires approval | Links the files: one transaction per file, all of them one undo step |

## The office rules

- **Current view only.** A DWG linked into a plan (floor, ceiling, structural or area plan), a section, an
  elevation, a callout or a drafting view is linked *current view only*, so it shows in that view alone.
  - For plans and sections, `thisViewOnly=false` makes the link model-wide. The preview says so.
  - A drafting view only takes view-specific links.
  - A 3D view cannot take a current-view-only link: a DWG linked there is model-wide, and asking for
    `thisViewOnly=true` in a 3D view is refused.
- **Scale: automatic.** `unit=auto` (the default) is Revit's *Auto-Detect* import unit. After linking, the result
  gives each DWG's extents in metres and warns when the drawing is over 5 km or under 0.5 m across. Either usually
  means the units were detected wrong; relink with an explicit `unit` (`mm`, `cm`, `dm`, `m`, `in`, `ft`).

## Arguments

Give files in `links` as `[{path, viewId?, view?, …}]`, or as plain paths in `paths` that all go into the same view.
Top-level values are defaults, and per-file values override them.

| Argument | Default | Values |
|---|---|---|
| `viewId` / `view` | the active view | A view id, or a view name (a name shared by two views is refused; pass the id) |
| `thisViewOnly` | per the rule above | `false` links a plan or section model-wide |
| `unit` | `auto` | `auto`, `mm`, `cm`, `dm`, `m`, `in`, `ft` |
| `placement` | `origin` | `origin` (origin to origin), `center` (center to center), `shared` (shared coordinates) |
| `colors` | `preserve` | `preserve`, `blackAndWhite`, `invert` |
| `layers` | `all` | `all`, `visible` |
| `correctLinesOffAxis` | `true` | Revit's "Correct lines that are slightly off axis" |
| `pin` | `false` | Pin each linked instance |
| `ifExists` | `skip` | `skip` leaves a file already linked into that view (model-wide links are compared with model-wide ones); `linkAgain` adds a second instance |

Each path must be an absolute `.dwg` path that exists and is allowed by the file access policy
(`FileAccessPolicy.json`). The same file twice for one view in one request is refused. Up to 50 files per call.

## Result

Per file: `path`, `view` (`id`, `name`, `type`), `status` (`linked`, `skipped` or `failed`), `thisViewOnly`, the
options used, `instanceId` and `typeId` of the new link, `extentsM` (`width`, `height`), `reason`, and `notes`.
Messages from dialogs Revit closed while linking go into `warnings`.

The link is an ordinary CAD link. `revit_list_cad_imports` and `revit_set_cad_overrides` handle its layers,
`revit_reload_links_from` reloads it from a new file, and `revit_remove_links` removes it.

## Example

Link each floor's architectural plan into the matching EN floor plan:

```json
{ "tool": "revit_preview_link_dwg", "arguments": {
    "links": [
      { "path": "C:\\Proj\\AR\\AR_1korrus.dwg", "view": "EN 1. korrus" },
      { "path": "C:\\Proj\\AR\\AR_2korrus.dwg", "view": "EN 2. korrus" }
    ],
    "colors": "blackAndWhite",
    "pin": true
} }
```

Both files are linked current view only, with the scale auto-detected and origin-to-origin placement. Check
`links[].status` in the preview, then call `revit_link_dwg` with the same arguments.

## Revit test checklist

1. Link a DWG into a floor plan. In Manage Links / Properties it is current view only, and it doesn't show in the
   floor plan above.
2. Link a millimetre DWG and a metre DWG with `unit=auto`. Both come in at real size, and `extentsM` matches the
   drawing.
3. Link the same file into the same plan again: `skipped`. With `ifExists=linkAgain`: a second instance.
4. Link into a 3D view: model-wide. With `thisViewOnly=true`: refused before anything changes.
5. One undo removes every link of the call.
6. Run with `document` set to a second open project: the links go into that project.
