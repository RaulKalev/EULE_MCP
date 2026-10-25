# Families and parameters

Tools that prepare a model for placement work without a person at the keyboard: bring families into the project,
reload them from their files, purge what is unused, and create the project and shared parameters the company standard
requires (#102). They sit in the `edit` group (`--tool-groups edit`, or find them with `revit_tools_search`). Every
tool takes `document` (a title or path from `revit_list_open_documents`; empty = the active document).

| Tool | Permission | Purpose |
|---|---|---|
| `revit_list_families` | Read-only | Families per category: name, kind (`loadable` / `system` / `inPlace`), type count, instance count, editable, source path when known |
| `revit_preview_load_family` | Read-only | Which `.rfa` files would load, be skipped or overwrite a loaded family |
| `revit_load_family` | Requires approval | Load up to 50 `.rfa` files; overwrite policy; activate types |
| `revit_reload_family` | Requires approval | Reload a loaded family from its file, keeping or overwriting parameter values |
| `revit_preview_purge_unused` | Read-only | Unused family types and families, (opt-in) other unused elements, and what is protected |
| `revit_purge_unused` | **Destructive**, manual approval | Delete them, in batches of 500, one undo step |
| `revit_list_project_parameters` | Read-only | Project and shared parameter bindings |
| `revit_list_shared_parameters` | Read-only | Definitions of the company shared parameter file |
| `revit_preview_create_parameters` | Read-only | What creating each requested parameter would do |
| `revit_create_parameters` | Requires approval | Create project parameters (from the shared file, or project-only) and bind them to categories |
| `revit_bind_parameter_categories` | Requires approval | Add or remove categories on an existing binding |

Family *types* (duplicate, rename, set type parameters) have their own tools: see [`family-types.md`](family-types.md).

## Families

### List

```json
{ "tool": "revit_list_families", "arguments": { "category": "Electrical Fixtures", "unusedOnly": true, "includeTypes": true } }
```

`kind` filters `loadable`, `system` (wall, duct, cable tray … types grouped by their system family) or `inPlace`.
Results are paged (`pageSize`, `page`). Instance counts come from one pass over the model. `sourcePath` is the file
`revit_load_family` last loaded the family from — Revit itself does not keep a family's path.

### Load

```json
{ "tool": "revit_preview_load_family", "arguments": { "folder": "C:\\Projects\\Library\\Electrical", "pattern": "EL_*.rfa", "ifExists": "skip" } }
{ "tool": "revit_load_family",         "arguments": { "folder": "C:\\Projects\\Library\\Electrical", "pattern": "EL_*.rfa", "ifExists": "skip" } }
```

- `paths` (up to 50 `.rfa` files) or `folder` + `pattern` (default `*.rfa`; `recursive=true` includes subfolders).
  Every path is checked against the file access policy (`FileAccessPolicy.json`; network paths need
  `AllowNetworkPaths`). Revit backups (`Name.0001.rfa`) and a second file that would load under the same family name
  are skipped and reported.
- `ifExists` decides what happens to a family of the same name (the file name) that is already loaded:

  | `ifExists` | Revit's answer | Shared nested families |
  |---|---|---|
  | `skip` (default) | not loaded, reported as `skipped` | — |
  | `overwrite` | the file's version, the project's parameter values kept | the project's version |
  | `overwriteParameters` | the file's version and its parameter values | the project's version |
  | `overwriteAll` | the file's version and its parameter values | the file's version |

- `activateTypes` (default `true`) activates every type so `revit_place_family_instances` and the device placement tools
  can use it at once.
- Each file loads in its own transaction inside one transaction group: a damaged file, one from a newer Revit, or one
  Revit refuses fails alone and never undoes the others; Undo in Revit removes the whole load at once.
- Result per file: `{path, family, familyId, status: loaded | skipped | overwritten | failed, types, reason}`.
- The preview does not open the files (opening 50 family documents is too slow): it checks existence, extension,
  policy and size, and whether the family is loaded, and states which policy applies.

### Reload

```json
{ "tool": "revit_reload_family", "arguments": { "family": "EL_Socket", "ifExists": "overwrite" } }
```

`path` defaults to the file `revit_load_family` recorded for this model in
`%LOCALAPPDATA%\RevitMCP\Families\<modelId>.json`. A family loaded another way has no recorded path: the tool then asks
for `path`. The file name must match the family name — Revit names a loaded family after its file, so a different
name would add a second family. `ifExists` here is `overwrite` (default), `overwriteParameters` or `overwriteAll`. The
result lists the types added and removed and the instance count.

### Purge unused

```json
{ "tool": "revit_preview_purge_unused", "arguments": { "categories": ["Electrical Fixtures", "Data Devices"], "passes": 2 } }
{ "tool": "revit_purge_unused",         "arguments": { "categories": ["Electrical Fixtures", "Data Devices"], "passes": 2, "elementIds": [/* from the preview */] } }
```

A family type is purged when it has no placed instance **and** Revit's own purge (`Document.GetUnusedElements`, in both
the Revit 2024 and 2026 APIs) also reports it unused. A family goes as a whole when every one of its types does.
`includeOther=true` adds the other elements Revit's purge lists (line and fill patterns, materials, text and dimension
types, system types).

Never purged, and listed under `protected` with the reason:

| Reason | What |
|---|---|
| `inPlace` | in-place families and their types |
| `deviceMap` | types the company device map names (`deviceCodes` in the project config, and their `sourceType`) |
| `titleBlockOnSheet` | title block types placed on sheets |
| `viewTemplate` | view templates (with `includeOther`) |
| `usedPerRevit` | types with no instance that Revit still uses (a category's default tag, a profile, a nested type) |
| `lastTypeOfKeptFamily` | a family that stays keeps one type: Revit cannot delete the last type of a family |

`passes` (1–3, default 1): purging a family can leave a nested family unused that only the next pass sees — the reason
Revit's own Purge Unused is run several times. The preview runs more than one pass in a trial transaction that is
rolled back. `elementIds` limits the purge to the ids the preview returned; nothing else is deleted. The approval reads
"DESTRUCTIVE: purge N unused family types in M families (+ K other unused elements). Manual approval required." The
result has the shape of `revit_delete` (`deleted`, `removedAsDependent`, `totalRemoved`, `failed`, `failures[]`); one
undo step restores everything.

If `Document.GetUnusedElements` fails at run time, the purge falls back to instance counts for family types only and
says so in its warnings.

## Parameters

### Which shared parameter file

`revit_list_shared_parameters` and `revit_create_parameters` use, in this order:

1. `sharedParameterFile` (checked against the file access policy),
2. `parameters.sharedParameterFile` in the project config (`.rktools` above the model), else the user config, else the
   company config — the first that defines the key,
3. Revit's own setting (Manage > Shared Parameters).

```json
{ "parameters": { "sharedParameterFile": "\\\\server\\standards\\EULE_SharedParameters.txt" } }
```

Revit reads one shared parameter file at a time. Each call sets `Application.SharedParametersFilename`, reads the file
and restores the user's own setting in a `finally`, whatever happens.

**The shared parameter file is company property.** It is never modified unless the call passes
`createInFileIfMissing=true`; without it a missing definition is reported as `missingDefinition`.

### List

```json
{ "tool": "revit_list_project_parameters", "arguments": { "category": "Data Devices", "sharedOnly": true } }
{ "tool": "revit_list_shared_parameters",  "arguments": { "group": "EULE", "nameContains": "Device" } }
```

Bindings report `{name, isShared, guid, dataType (SpecTypeId), dataTypeLabel, group (GroupTypeId), groupLabel, binding:
instance|type, categories[], variesAcrossGroups}`. Shared definitions report `{group, name, guid, dataType, description,
visible, userModifiable}` and whether the definition is already bound in the document.

### Create

```json
{
  "tool": "revit_preview_create_parameters",
  "arguments": {
    "parameters": [
      { "name": "EULE_DeviceCode", "source": "shared", "group": "EULE", "parameterGroup": "Identity Data",
        "binding": "instance", "categories": ["Electrical Fixtures", "Data Devices"] },
      { "name": "EULE_MountHeight", "source": "shared", "dataType": "length", "parameterGroup": "Electrical",
        "binding": "instance", "categories": ["Electrical Fixtures", "Data Devices"], "ifExists": "rebind" }
    ]
  }
}
```

Per parameter (up to 50): `name`; `source`: `shared` | `project`; `guid` (shared: pick the definition by GUID, else by
name within `group`, else by name across groups); `group` (the shared file group); `dataType` — `text`, `integer`,
`number`, `length`, `area`, `volume`, `angle`, `yesNo`, `material`, `url`, `currency`, `voltage`, … or a SpecTypeId string
(`autodesk.spec.aec:length-2.0.0`); `parameterGroup` — Revit's label (`Data`, `Electrical`, `Identity Data`, `Text`,
`Dimensions`, `Other`, …; default `Data`); `binding`: `instance` | `type`; `categories` (Revit category names);
`variesAcrossGroups` (instance bindings); `ifExists`: `skip` | `rebind`.

The preview reports per parameter:

| `action` | Meaning |
|---|---|
| `willCreate` | the definition is bound to the categories |
| `willRebind` | an existing binding gets the extra categories (the union; nothing is removed) |
| `exists` | already bound to every requested category, or bound without them and `ifExists=skip` |
| `missingDefinition` | not in the shared file (and `createInFileIfMissing` is not set), or no file |
| `unknownCategory` | a category is unknown or does not take bound parameters |
| `invalidType` | an unknown `dataType`, or one that disagrees with the file's definition |
| `invalid` | a bad name, binding, group, a name conflict, or a change of binding kind (refused) |

Existing bindings are extended, never replaced. Changing an instance binding to a type binding (or back) is refused:
it would drop the values. Each parameter is written in its own transaction inside one group, so one refusal does not
undo the others; one undo step. The approval reads "Create N project parameter(s) from the shared file '<file>':
<name> (<type>, instance, 3 categories), …".

#### `source=project`

Revit 2024 and 2026 have no API that creates a non-shared project parameter. `source=project` is therefore created as
a shared definition in a connector-owned group `RevitMCP` of a local scratch file
(`%LOCALAPPDATA%\RevitMCP\Parameters\RevitMCP-project-parameters.txt`) and bound from there. In Revit it appears in
Project Parameters as a **shared** parameter with a GUID; the result says so. The company file is not touched.

### Bind categories

```json
{ "tool": "revit_bind_parameter_categories", "arguments": { "name": "EULE_DeviceCode", "addCategories": ["Lighting Devices"], "removeCategories": ["Data Devices"] } }
```

`name` or `guid`. The binding kind and group stay. Removing the last category is refused; removing a category drops the
parameter's values on that category's elements.

## Notes

- Definitions created with `createInFileIfMissing=true` are written to the file when the call runs; Revit's undo does
  not remove them from the file.
- Graph data never replaces these live reads: after loading families or creating parameters, verify with
  `revit_list_families`, `revit_list_project_parameters` and `revit_get_element_parameters`.
- Plans classify `revit_load_family` and `revit_create_parameters` as creating, `revit_reload_family`,
  `revit_bind_parameter_categories` and `revit_purge_unused` as modifying; a purge is a delete and is never covered by a
  plan envelope.
