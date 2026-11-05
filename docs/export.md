# Export: sheets to PDF/DWG/DXF, the model to IFC/NWC

Issue #105. The connector exports a delivery the same way the **EliteSheets** plugin (Jooniste Printimine,
`RaulKalev/EliteSheets`) does, including its rule for merged drawings, so files from the connector and from the
plugin are the same. It can then run the delivery check on the folder it wrote to.

| Tool | Permission | Purpose |
|---|---|---|
| `revit_list_export_setups` | read-only | DWG/DXF export setups (and the active one), sheet sets, available exporters, the EliteSheets settings the export follows, the ODA File Converter, printable sheets and the merged drawings the sheet numbers form |
| `revit_preview_export` | read-only | The exact list of files `revit_export` would write: names, paths, merged drawings, existing files, skipped sheets, delivery-name warnings, options |
| `revit_export` | approval, long-running | Writes the files |
| `revit_export_and_check` | approval, long-running | `revit_export`, then the delivery check (and optionally the Excel document register) on the folders it wrote to |

They are in the `output` tool group (`revit_tools_search group=output`).

## The EliteSheets rule for merged drawings

A sheet whose number contains `-7-{group}_` and ends with a merge order `--{n}` is part of a merged drawing:

```
1626_TP_EL-7-05_Kilbi skeem--1
1626_TP_EL-7-05_Kilbi skeem--2     →  1626_TP_EL-7-05_Kilbi skeem.pdf  (2 pages, --1 first)
                                    →  1626_TP_EL-7-05_Kilbi skeem.dwg  (both sheets side by side in the template)
```

- All sheets with the same group become **one file**, named `{prefix}-7-{group}_{title}`, the sheet number without
  `--n`. En and em dashes count as hyphens.
- **PDF**: one combined PDF, pages in merge order.
- **DWG / DXF**: each sheet goes to DXF, its paper space (title block) and the model seen through its first viewport
  are brought into model space at paper scale, then the sheets go side by side, 220 mm apart (`mergeSpacingMm`), into
  the model space of the **DXF template**. Only content on locked layers stays in the template's paper layouts.
- When the **ODA File Converter** is installed (`Program Files\ODA\ODAFileConverter*`) and *convert merged drawings to
  DWG* is on, the merged DXF is converted to DWG (AutoCAD 2018). Otherwise the merged drawing is delivered as `.dxf`,
  as the plugin does, and the result says so. The delivery check then reports its DWG as missing.
- Every other sheet is exported on its own. `merge=false` turns the rule off.

The rule is `RevitMCP.Addin/Export/Sheets/SheetMergeGrouping.cs`, ported from the plugin's `SheetGroupingService`. The
DXF steps are in `DxfSheetProcessing.cs`, ported from `DxfPaperToModelPromoter` and `DxfMergeService`, with the same
netDxf version (2023.11.10). Change both together if the rule changes.

## The EliteSheets settings

The export reads the plugin's `C:\ProgramData\RK Tools\EliteSheets\config.json` and never writes it:

| Plugin setting | Used for | Argument that overrides it |
|---|---|---|
| `TemplateDxfPath` | the template merged drawings go into | `dxfTemplatePath` |
| `ConvertMergedToDwg` | merged drawings as DWG (with ODA) | `convertMergedToDwg` |
| `PdfExportPaths` / `DwgExportPaths` / `ExportPaths` + `SeparateExportFolders` for this project's file name | default folders | `pdfFolder`, `cadFolder`, `folder` |

`revit_list_export_setups` shows what was found. Without the plugin's config, pass the folders and the template.
`convertMergedToDwg` then defaults to on, so it takes effect wherever ODA is installed.

## Selecting sheets

`sheets` takes element ids, full sheet numbers or the discipline-group-sequence core (`EL-5-01` matches
`1626_TP_EL-5-01_Plaan`; `EL-7-05` matches every part of a merged drawing). `sheetSet` names a saved Print/Export
sheet set. `allSheets=true` takes every sheet. `discipline`, `numberContains` and `nameContains` narrow any of these,
or the whole set when none is given. Placeholders are never exported. At most 500 sheets per call.

Sheets without a title block, or with *Appears In Sheet List* off, are skipped and listed under `skipped`;
`includeUnlisted=true` exports them.

## File names

| `naming` | File |
|---|---|
| `sheetNumber` (default; `company` is the same) | `1626_TP_EL-5-01_Valgustus.pdf`, as EliteSheets names them: the company sheet number already holds project, stage and title |
| `sheetNumberAndName` | `{SheetNumber}_{SheetName}` |
| `pattern:<tokens>` | e.g. `pattern:{SheetNumber}_{Revision}`; tokens `{SheetNumber}`, `{SheetName}`, `{Revision}` |

`includeRevision=true` appends the sheet's current revision as `_r02` (numbers are padded to two digits; `A` gives
`_rA`). The default is off, because many deliveries keep the revision only in the title block and the document
register. Windows-invalid characters become `_`, and Estonian letters stay. Names are capped at 120 characters.

Every name is parsed back with the delivery parser. A name the delivery check would not recognise is flagged as
`nameWarning` on the file, and as `sheet_number_not_company_pattern` in the warnings. It is still exported.

Two files with the same name in one call get `_2`, `_3`. An existing file is replaced only with `overwrite=true`.
Otherwise the new file gets a timestamp suffix (`…_2026-11-05_143015.pdf`), the old one stays, and the file's status is
`renamed`. The delivery check then reports the pair as duplicates, which is true.

## Formats

| Format | How |
|---|---|
| `pdf` | Revit's PDF export, one call per file: the sheet's own paper size, zoom 100 %, centred, crop boundaries, scope boxes, reference planes and unreferenced view tags hidden, vector. A merged drawing is one combined call. |
| `dwg` | `doc.Export` with the DWG/DXF export setup `dwgSetup`. The default is the active setup, else the first by name, as the plugin's list shows it. `mergedViews=true` overrides the setup to write one DWG without view xrefs. Xrefs Revit writes beside the DWG are listed as `extraFiles`. |
| `dxf` | DXF with the same setup, then paper space promoted to model space, as in the plugin. |
| `ifc` | `IFCExportOptions`: `ifcVersion` IFC2x3CV2 (default) / IFC4RV / IFC4DTV, `ifcSpaceBoundaries` 0–2, `ifcBaseQuantities`, `viewId` as the filter view. Runs in a transaction that is rolled back, so the model is unchanged. A named IFC export configuration from the IFC dialog cannot be read through the API; only these options apply. |
| `nwc` | `NavisworksExportOptions`: whole model, or the 3D view `viewId`; `nwcExportLinks`; `nwcCoordinates` shared (default) / internal. Needs the Navisworks exporter for this Revit version. |

IFC and NWC export the model, not sheets. They are named `fileName` (default: the project file name) and go to
`cadFolder`, else `folder`.

A format whose exporter is missing fails before anything is written, with status `exporter_missing` and a hint.

## Folders, policy and safety

- Folders come from `folder`, or from `pdfFolder` and `cadFolder` (EliteSheets' separate folders). The default is the
  project's EliteSheets folders.
- Every folder must pass the file access policy (`%ProgramData%\RKTools\MCP\Config\FileAccessPolicy.json`). A
  delivery share needs `"AllowNetworkPaths": true`, and the refusal (`export_folder_denied`) says so.
- Each file is exported into a temporary folder first and then copied to its planned path. A failed export never
  destroys an existing file, and Revit's own name decoration never reaches the delivery folder.
- Exports run outside a transaction. An open edit in the document refuses the call.
- `revit_export` and `revit_export_and_check` are long-running tools (15 min timeout). A cancel stops between files:
  the files already written are reported, and the rest have status `cancelled`.
- A file that fails is recorded with its reason, and the next file continues.

## Result

```json
{
  "totals": { "files": 6, "written": 5, "overwritten": 0, "renamed": 1, "failed": 0, "cancelled": 0, "bytes": 2134567 },
  "files": [
    { "format": "pdf", "kind": "single", "sheetNumbers": ["1626_TP_EL-5-01_Valgustus"], "fileName": "1626_TP_EL-5-01_Valgustus.pdf",
      "path": "C:\\Projects\\1626\\TP\\PDF\\1626_TP_EL-5-01_Valgustus.pdf", "bytes": 412345, "status": "written" },
    { "format": "dwg", "kind": "merged", "sheetNumbers": ["1626_TP_EL-7-05_Kilbi skeem--1", "1626_TP_EL-7-05_Kilbi skeem--2"],
      "fileName": "1626_TP_EL-7-05_Kilbi skeem.dwg", "status": "written" }
  ],
  "mergedDrawings": [ { "group": "05", "fileStem": "1626_TP_EL-7-05_Kilbi skeem", "sheets": ["…--1", "…--2"] } ],
  "skipped": [],
  "deliveryCheck": [ { "folder": "…\\PDF", "extensions": ["pdf"], "summary": { "files": 12, "sheetsChecked": 12, "issues": 0 } } ]
}
```

## Delivery check after export

`revit_export_and_check` scans each folder it wrote to, for the formats it wrote, and checks the following:

- the exported sheets, plus every sheet that already has a file there, against the folder: **missing** files,
  **duplicates** (for example an older timestamped copy) and wrong stage;
- every company-named file in the folder against the document's sheets: **orphans**, meaning files for sheets that no
  longer exist;
- with `registerPath` (an `.xlsx` document register, `registerWorksheet` optional), the folder against the register,
  the same way as `delivery_check_against_excel_register`.

Each folder gets `{folder, extensions, summary, issueReport}`.

## Example: a TP delivery

```
revit_list_export_setups
revit_preview_export formats=[pdf,dwg] discipline=EL numberContains=_TP_ pdfFolder="C:\Projects\1626\TP\PDF" cadFolder="C:\Projects\1626\TP\DWG"
revit_export_and_check formats=[pdf,dwg] discipline=EL numberContains=_TP_ pdfFolder=… cadFolder=… overwrite=true registerPath="C:\Projects\1626\TP\1626_Dok-reg.xlsx"
```

A green check has zero issues in every folder. Merged drawings count for each of their sheets. `EL-7-05_…--1` and
`--2` are both satisfied by `1626_TP_EL-7-05_….pdf`.

## Not covered

Printing to physical printers or print drivers, publishing to ACC/BIM 360, DWF/DGN/SAT/FBX, image export through these
tools (`revit_export_view_image` stays as it is), and installing exporters.
