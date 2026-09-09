# Model graph schema

One SQLite file per Revit model: `<root>\<project>\<model-name>.graph.db`
(`root` = `graph.sharedFolder` from the user or company config, else
`%LOCALAPPDATA%\RKTools\RevitMCP\Graph`). Schema version **1**.

The graph is a **routing layer**. It stores ids, names and relationships so an agent can find
the right element ids cheaply. It never stores parameter values and must never be quoted as a
source of truth — fetch live values by id with the regular `revit_*` tools.

## Tables

### `nodes`

| Column | Type | Meaning |
|---|---|---|
| `id` | TEXT PK | Revit element id as a string (`"123456"`). Worksets use `ws:<worksetId>` because worksets have their own id space. |
| `kind` | TEXT | One of `element`, `type`, `panel`, `circuit`, `space`, `level`, `workset`, `sheet`, `view`. |
| `name` | TEXT | Display name. Sheets: `<number> - <name>`; spaces: `<number> <name>`; circuits: `<panel>/<circuit number> <load name>`; types: `<family>: <type>`. |
| `category` | TEXT | Revit category name (`Lighting Fixtures`, `Walls`, …). Levels/worksets/sheets/views use `Levels`/`Worksets`/`Sheets`/`Views`. |
| `level` | TEXT | Level name when the element has one (`LevelId`, `FAMILY_LEVEL_PARAM`, `RBS_START_LEVEL_PARAM`, `SCHEDULE_LEVEL_PARAM`, `LEVEL_PARAM`; plan views use `GenLevel`). Empty otherwise. |
| `workset` | TEXT | Workset name (workshared models only). Empty otherwise. |
| `extra` | TEXT (JSON) | Small routing hints, never parameter values. Blank values are omitted. |

`extra` keys by kind:

| Kind | Keys |
|---|---|
| `element` | `family`, `type` (family instances) or `type` (system families) |
| `type` | `family`, `type` |
| `panel` | `family`, `type`, `panelName` (RBS_ELEC_PANEL_NAME) |
| `circuit` | `circuitNumber`, `loadName`, `panel`, `panelId`, `systemType` |
| `space` | `number`, `name`, `spatialType` (`Room` or `Space`) |
| `level` | `elevationMm` |
| `workset` | `owner`, `isOpen` |
| `sheet` | `sheetNumber`, `sheetName` |
| `view` | `viewType`, `isSchedule` |

### `edges`

| Column | Type | Meaning |
|---|---|---|
| `src` | TEXT | Source node id |
| `dst` | TEXT | Destination node id |
| `rel` | TEXT | Relationship, see below |

Primary key `(src, dst, rel)`. Direction is always `src → dst`. Edges whose endpoints are not
both in `nodes` are dropped at build time (reported as `danglingEdgesDropped`).

| `rel` | `src → dst` | Source in the Revit API |
|---|---|---|
| `fed_by` | element/panel → circuit; circuit → panel | `ElectricalSystem.Elements`, `ElectricalSystem.BaseEquipment` |
| `located_in` | element/panel → space | `FamilyInstance.Room`, then `FamilyInstance.Space` |
| `hosted_on` | element/panel → host element | `FamilyInstance.Host` (levels excluded, see `on_level`) |
| `type_of` | element/panel → type | `Element.GetTypeId()` |
| `tagged_in` | element/space → view | `IndependentTag.GetTaggedLocalElementIds()` + `OwnerViewId`; `RoomTag.Room` / `SpaceTag.Space` |
| `on_sheet` | view → sheet | `ViewSheet.GetAllPlacedViews()` |
| `in_workset` | any → workset | `Element.WorksetId` (user worksets only) |
| `on_level` | any → level | level resolution described under `nodes.level` |

"Everything fed by panel P" is therefore `subtree(id=P, rel=fed_by, direction=in)`: circuits point
at the panel, and elements point at their circuits. A sub-panel appears as a `panel` node with its
own `fed_by` edge to the upstream circuit.

### `meta`

| `key` | `value` |
|---|---|
| `model_path` | Central path for workshared models, otherwise the document path |
| `model_name` | Model file name without extension (central file name for file-based worksharing) |
| `built_at` | UTC ISO-8601 timestamp of the build |
| `central_version` | Version signal captured at build time (see below) |
| `element_count` | `FilteredElementCollector.WhereElementIsNotElementType().GetElementCount()` at build time |
| `schema_version` | `1` |
| `built_by` | Revit username |
| `version_source` | Which API signal produced `central_version` |
| `is_workshared` | `true` / `false` |
| `revit_version` | Revit version number |
| `project_key` | Project Information → Number (fallback Name); the folder segment |
| `node_count`, `edge_count` | Row counts written after the build |
| `build_duration_ms` | Extraction time on the Revit API thread |

## Indexes

`nodes(kind)`, `nodes(category)`, `nodes(level)`, `edges(src)`, `edges(dst)`, `edges(rel)`.

## Version signal (`central_version`)

1. **File-based workshared models** — `BasicFileInfo.Extract(path).LatestCentralVersion` and
   `LatestCentralEpisodeGUID`, formatted `central:<version>:<guid>`. The central file is read when
   reachable, otherwise the local file header (which reflects the last synchronisation).
2. **Everything else** (non-workshared, cloud/server worksharing, unreadable header) —
   `Document.GetDocumentVersion(doc)` → `saves:<NumberOfSaves>:<VersionGUID>`.
3. **Unsaved documents** — empty; only `element_count` is compared.

Limitations: neither signal changes for unsaved edits in the current session (the element count
is the only hint), and a local file whose central is unreachable reports the last synced version.
A graph is reported `stale` when the schema version differs, the model name differs, the version
signal differs, or the element count differs.

## What is *not* in the graph

Annotation elements other than tags/views/sheets, element types not referenced by an instance,
detail items on views that are not model categories, linked-model elements, parameter values,
geometry. Model elements are read with
`FilteredElementCollector.WhereElementIsNotElementType().WhereElementIsViewIndependent()` filtered
to `CategoryType.Model` and non-tag categories, capped by `elementLimit` (default 250 000).
