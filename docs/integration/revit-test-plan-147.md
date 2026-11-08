# Revit test plan: #147 (Detail Item family creation)

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

## Phases 2 and 3: parameters and labels

Use a project whose shared parameter file (Manage > Shared Parameters) holds a definition you can name, for example
`EULE_Tähis`, and a text type such as `2.5mm Arial`.

| # | Step | Expected |
|---|---|---|
| 15 | Preview with `parameters`: a text, a length with `value: 20`, an instance yes/no, a number with `formula`, and the shared one | `ready: true`; `parameters` lists each with its data type, group and `shared`; `sharedParameterFile` names the file |
| 16 | Preview with a shared name that is not in the file | `ready: false`; the problem says the tool never creates shared parameters and suggests close names. The file is unchanged (check its timestamp) |
| 17 | Create with step 15's parameters | `parameters[].status = added`; in the Family Editor the Family Types dialog shows them in the right groups, type/instance as asked, with the values and formula |
| 18 | Load into the project, place it, open the schedule or tag of that category | The shared parameter appears and can be scheduled or tagged; Revit's shared parameter file setting is what it was before the call |
| 19 | Preview with `labels`: two with the same text, one other, one with `textType` | `labels.families` lists two or three families named `{familyName}_Text1…`, with instance counts |
| 20 | Create with step 19's labels | In the Family Editor each text is a nested Generic Annotation at its position, rotated as asked; the template's sample note is gone |
| 21 | Place the family in plans at 1:50 and 1:100 | The text stays the same size on paper and stays at its anchor; the `textType` text uses the project type's font and size |
| 22 | A family with only `labels`, no curves | Created; no "nothing to draw" error |
| 23 | Revit 2024: steps 17 and 20 | Same results |