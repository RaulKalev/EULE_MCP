# Revit test plan: export (#105)

Run this in Revit 2026 and 2024 with a copy of an EULE project that has EL sheets, including at least one merged drawing
(`…-7-05_…--1` and `--2`), with EliteSheets installed and configured (template DXF, folders). The unit tests cover
naming, merge grouping, the plan and the settings reader. The steps below cover what only Revit can show.

| # | Step | Expected |
|---|---|---|
| 1 | `revit_list_export_setups` | DWG setups and the active one, sheet sets, `exporters` (pdf/dwg/dxf true; ifc true; nwc as installed), `eliteSheets.found=true` with the template and folders, `odaConverter` path or null, merged drawings listed |
| 2 | `revit_preview_export formats=[pdf,dwg] sheets=[5 EL sheets + both parts of one merged drawing] folder=C:\Temp\export105` | 5 single PDFs and DWGs, 1 merged PDF and 1 merged DWG (DXF without ODA, with the warning), names = sheet numbers, no `nameWarning` |
| 3 | `revit_export` with the same arguments, approve | All files `written`. The PDFs open at their sheet size with no crop boundaries. The merged PDF has 2 pages in merge order. |
| 4 | Compare with EliteSheets | Export the same sheets with the plugin into another folder. File names match, and the merged DWG/DXF opens with both sheets side by side in the template, like the plugin's |
| 5 | Repeat step 3 without `overwrite` | Every file `renamed` with a `_yyyy-MM-dd_HHmmss` suffix, old files untouched |
| 6 | `revit_export_and_check … overwrite=true` on an emptied folder | `deliveryCheck` has 0 issues per folder |
| 7 | Leave the step 5 copies in the folder and run step 6 again | duplicates reported for every sheet |
| 8 | `registerPath=<Dok-reg.xlsx>` | register issues merged into the report; a missing row is reported |
| 9 | `formats=[dxf]` on one single sheet | DXF opens with the title block and model content in model space at paper scale |
| 10 | `formats=[ifc] viewId=<3D view>` | `.ifc` written; the model has no new undo entry |
| 11 | `formats=[nwc]` on a machine without the Navisworks exporter | fails before writing, `exporter_missing` with a hint |
| 12 | `folder=\\server\share\…` with the default policy | `export_folder_denied`, naming `AllowNetworkPaths` |
| 13 | 50-sheet PDF export, cancel from the client midway | the call returns the written files and the rest as `cancelled`; no timeout before 15 min |
| 14 | Open an edit (e.g. start moving an element) and call `revit_export` | refused: open transaction |
| 15 | `document=<a background local>` | exports from that document; the approval names it |
