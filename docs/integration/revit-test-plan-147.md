# Revit test plan: #147 phase 1 (Detail Item family creation)

Run in Revit 2026, and once in Revit 2024, with a project open that has custom line styles (for example
`EL_Kaabel` with a dashed pattern). Use a scratch folder such as `C:\Temp\mcp-147`.

| # | Step | Expected |
|---|---|---|
| 1 | `revit_preview_create_detail_family` without `savePath` | `ready: false`; the problem says savePath is required and to ask the user |
| 2 | Preview with `savePath=C:\Temp\mcp-147`, `familyName=T147_Box`, one rectangle and one line, `lineStyle` = a project style | `ready: true`; template is a `Metric Detail Item.rft` path; that style shows `project` with its weight, colour and pattern |
| 3 | Preview with `lineStyle: "Does Not Exist"` | `ready: false`; the problem names the curves that use it and suggests close names |
| 4 | `revit_create_detail_family` with the arguments of step 2 | `C:\Temp\mcp-147\T147_Box.rfa` exists; `lineStyles[].action` is `created` (or `reused`) and `kind` is `lines`; `purged` lists counts |
| 5 | Open `T147_Box.rfa` in the Family Editor | Category Detail Items; the curves are drawn at the origin and use the named line style; Manage > Line Styles shows it with the project's weight, colour and pattern |
| 6 | Run step 4 again | Refused: the file exists and overwrite is false; nothing changes |
| 7 | Run step 4 with `overwrite=true`, `loadIntoProject=true` | File replaced; `load.status = loaded`; the family is in the project browser and its type can be placed |
| 8 | Place the family; check the line style in the project | The lines use the project's line style (same name); changing the project style's weight changes the family's lines |
| 9 | Step 7 again with `ifExists=skip` | `load.status = skipped` |
| 10 | Step 7 again with `ifExists=overwrite` | `load.status = overwritten`; placed instances keep their parameter values |
| 11 | An arc by angles (`0` → `90`), a three-point arc and a circle | All drawn; the angle arc runs counter-clockwise from +X |
| 12 | Revit's family template folder points to a language pack folder without the file at its root | The template is found in a sub-folder, or the problem asks for `templatePath` |
| 13 | Run from a family document | Refused: run from a project |
| 14 | Undo in the project after step 7 | One undo step removes the loaded family; the `.rfa` stays on disk |

If step 5 shows a `kind: detailItems` warning, the template does not let detail curves use Lines styles. Record which
template and Revit version, because the line style approach then needs revisiting.
