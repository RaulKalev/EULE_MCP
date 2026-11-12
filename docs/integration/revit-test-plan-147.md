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

## Filled regions, parameter labels and visibility parameters

**Not yet run in Revit.** These were written with the feature, when no Revit was reachable; the pure checks are unit
tested (`DetailFamilyRegionsTests`), the Revit calls are not. Use a project with a filled region type such as
`Solid Black`, a drafting fill pattern such as `Diagonal up`, and a Generic Annotation family that holds one label
bound to an instance text parameter (called `EULE_Silt` below, parameter `Tekst`, with a type `2.5 mm`).

| # | Step | Expected |
|---|---|---|
| 24 | Preview with `filledRegions`: a polygon with `type: "Solid Black"`, a rectangle with a circular hole and no type, a circle with `lineStyle` | `ready: true`; `filledRegions.types` lists `Solid Black` with `status: project` and its pattern and colours; `defaultType` mentions the one region without a type |
| 25 | Preview with a `type` the project does not have, and with a bow-tie polygon | `ready: false`; one problem names the type with suggestions, the other says the sides cross |
| 26 | Create with step 24's regions | `filledRegions[].status = drawn`; in the Family Editor the regions are there, the hole is cut, the first uses a type `Solid Black` that looks like the project's, the circle's boundary has the line style |
| 27 | Create with `filledRegionTypes`: `{name:"T_Hall", foregroundPattern:"Diagonal up", foregroundColor:"#808080", backgroundPattern:"solid", backgroundColor:"#FFFFFF", lineWeight:1}` and a region using it | `filledRegionTypes[0].action = created`; the family has the type with those values and a copied `Diagonal up` pattern. Load and place it: the region looks the same in the project |
| 28 | Step 27 with `copyFrom: "Solid Black"` and only `foregroundColor` | The type has the project type's patterns with the new colour |
| 29 | A type with `isMasking: true` over a line | The region hides the line in the project |
| 30 | `visibleParameter` on a region, a line and a text label, with a yes/no instance parameter in `parameters` | `visibility[].linked` counts all three; in the project, unticking the parameter on an instance hides them |
| 31 | `visibleParameter` naming a text parameter, and one that is not listed | `ready: false` for each, saying a yesNo parameter of `parameters` is needed |
| 32 | Preview a label `{parameter:"Kilbi tähis", position:[0,-9], labelFamily:"EULE_Silt", labelType:"2.5 mm"}` with the text parameter in `parameters` | `ready: true`; `parameterLabels.families` shows `EULE_Silt` with `source: project` and its types |
| 33 | Create with step 32 | `parameterLabels[0].status = placed`, `labelParameter = Tekst`; in the Family Editor the nested instance's `Tekst` shows the `=` link to `Kilbi tähis`. In the project the label shows the parameter's value and follows it when changed |
| 34 | Step 33 with `labelFamily` as a `.rfa` path | Same result; `source: file` in the preview |
| 35 | A `labelFamily` that is a Detail Item, one that is not loaded, and a label family with two text instance parameters and no `labelParameter` | The first two are problems in the preview; the third creates the family but reports the label `failed` with both parameter names, and no stray instance is left |
| 36 | Two parameter labels showing different parameters through a label family whose label parameter is a **type** parameter, same `labelType` | The second is `failed`, saying the type already shows the other parameter |
| 37 | Purge on (default) with steps 27 and 33 | The defined type, its patterns and the label family survive the purge |
| 38 | Revit 2024: steps 26, 27, 30 and 33 | Same results |

If step 26 fails for every region, check whether `FilledRegion.Create` accepts the template's reference level view;
if step 33 fails with "Revit does not let … be linked", the label family's parameter is probably a type parameter or
reporting parameter.

## Line styles

**Not yet run in Revit.** See [line-styles.md](../line-styles.md).

| # | Step | Expected |
|---|---|---|
| 39 | `revit_list_line_styles includePatterns=true` in a project | Every line style with weight, colour and pattern; `<Thin Lines>` and the like have `builtIn: true`; `patterns` lists the line patterns |
| 40 | `revit_preview_set_line_styles` with a new name (weight 3, `#FF0000`, pattern `Dash`), an existing name with only `weight`, and one with a pattern the project lacks | The first is `create`, the second `edit` with `changes.weight` from/to, the third a problem with suggestions; `ready: false` |
| 41 | `revit_set_line_styles` with the first two | Approval prompt names both; afterwards Manage > Additional Settings > Line Styles shows the new style with those values and the edited weight; one Undo removes both changes |
| 42 | Step 41 with one style Revit refuses (for example a change to a built-in style it does not allow) | Nothing changed; the message names the style |
| 43 | Open a family in the Family Editor and run steps 39 to 41 with `document` naming it | The style is created in the family (or the problem says the family does not accept new line styles) |
| 44 | `revit_create_detail_family` with `lineStyles: [{name:"T_Oma", weight:4, color:"#0000FF", pattern:"Dash"}]` and a line using `T_Oma`, which the project does not have | Preview `lineStyles[].status = defined`; the family has the style with those values and the line uses it; loaded into the project the line is blue, dashed, weight 4 |
| 45 | Step 44 with a name the project has and only `weight` given | The family's style has the project's colour and pattern with the new weight |
| 46 | Revit 2024: steps 41 and 44 | Same results |

## Types and editing an existing family

**Not yet run in Revit.** Written with the feature, when no Revit was reachable; the pure checks are unit tested
(`DetailFamilyEditTests`), the Revit calls are not. See [family-creation.md](../family-creation.md) (*Types*, *Editing
an existing family*). Use a project with `EL_Kilp` from step 17 loaded (parameters `Laius` length, `Tähis` text
instance, a yes/no `Näita` linked to a line's visibility) and a copy of its `.rfa` in a folder the file access policy
allows.

| # | Step | Expected |
|---|---|---|
| 47 | `revit_preview_create_detail_family` with step 15's parameters and `types: [{name:"K600", values:{Laius:600}}, {name:"K800", values:{Laius:800, Tähis:"K8"}}]` | `ready: true`; `types` lists both with `action: create`, their values and the parameters whose own value is the default |
| 48 | Create with step 47 | `types[].action = created`, every value `status: set`; the Family Types dialog shows exactly `K600` and `K800` (no type named after the family) with those values; an instance parameter's value is the type's default |
| 49 | Step 47 with a type value for a formula parameter, an unknown name and `"Laius":"lai"` | `ready: false`; three problems: the formula, the name with a suggestion, the unit (mm) |
| 50 | `revit_preview_edit_detail_family family:"EL_Kilp" remove:{textNotes:true}` | `ready: true`; `existing` lists the types, parameters (with formulas and linked element counts) and element counts per kind; the family is not left open (no Family Editor window, no extra document in `revit_list_open_documents`) |
| 51 | Edit `family:"EL_Kilp"` with a line, `parameters:[{name:"Sügavus", dataType:"length", value:200}]` and `types:[{name:"K1000", values:{Laius:1000}}]` | One approval; the family in the project has the line, the parameter and the new type `K1000`; **one Undo** reverts the whole reload; placed instances keep their position and type |
| 52 | Step 51 with `types:[{name:"K600", values:{Laius:650}}]` (an existing type), without and then with `overwriteParameterValues:true` | Without: the preview warns that the project keeps its values; in the project `K600` still has 600. With: 650 |
| 53 | `remove:{parameters:["Näita"]}` | Refused: linked to the line that stays (its id is named). With `remove:{curves:[<that id>], parameters:["Näita"]}` it is allowed and `linkedElementsRemoved` names the line |
| 54 | `remove:{parameters:["Laius"]}` while `Pool = Laius / 2`; then `["Laius","Pool"]`; then a built-in such as `Type Comments` | First refused naming `Pool` and its formula; second allowed (`Pool` removed first); third refused as built in |
| 55 | `remove:{curves:[<a filled region id>]}` and `remove:{curves:[999999999]}` | Problems: "is a FilledRegion, not a detail line" and "has no element" |
| 56 | `remove:{curves:true}` on a family with a filled region | Every detail line goes; the filled region and its boundary stay |
| 57 | Edit with `labels:[{text:"Uus", position:[0,12]}]` on a family created with text labels | A new nested family `EL_Kilp_Text<n+1>`; the old labels keep their text |
| 58 | `path:"…\EL_Kilp.rfa"` without `overwrite` | Problem asking for `overwrite=true` or `savePath` |
| 59 | `path` with `overwrite:true` and a line | The file is replaced (timestamp changes), no `.tmp` file is left in its folder, the family is not open afterwards |
| 60 | `path` with `savePath:"…\Out\EL_Kilp_2.rfa"` | The original file is unchanged; the new file opens as family `EL_Kilp_2` |
| 61 | The same `.rfa` open in Revit, then edited by `path`; `EL_Kilp` open in the Family Editor, then edited by `family` | Both refused with "close it first"; the open windows are untouched |
| 62 | An edit that fails in Revit (for example a label family that cannot be nested, forced by a bad `labelFamily` path after the preview) | Nothing saved or reloaded; the file timestamp and the project are unchanged; no family document stays open |
| 63 | A Revit 2023 `.rfa` by `path` with `savePath` | Warning that the file is upgraded and saved in this version |
| 64 | Revit 2024: steps 48, 51, 53 and 59 | Same results |

If step 51 fails with a message about an open transaction, `LoadFamily(Document, IFamilyLoadOptions)` cannot run from
the add-in's context as written; if Undo takes more than one step, Revit records the reload in several parts.
