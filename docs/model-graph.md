# Model graph (routing layer)

The model graph is a per-model SQLite index of **ids and relationships** — elements, types, panels,
circuits, rooms/spaces, levels, worksets, sheets and views, plus the edges between them. An agent
queries it first to find the right element ids cheaply, then reads live values from Revit by id.

The graph is **not a source of truth**. It stores no parameter values, and every read response
carries freshness metadata:

```json
{ "built_at": "…", "central_version": "…", "stale": false, "stale_reason": "…",
  "note": "Graph values are for routing only; fetch live data by ID for actual values." }
```

Schema: [`RevitMCP.Addin/Graph/SCHEMA.md`](../RevitMCP.Addin/Graph/SCHEMA.md).

---

## Tools

| Tool | Purpose |
|---|---|
| `revit_graph_build` | Full rebuild from the open document (Revit API thread, one collector pass), or with `incremental=true` an update of only the elements changed since the last build in this session (see [Incremental updates](#incremental-updates-61)). Reports node/edge counts and timings. |
| `revit_graph_status` | Does a graph exist for the open model, where, `built_at`, `central_version`, the current document version, and `stale` with a human-readable reason. |
| `revit_graph_query` | Structured queries only (no SQL): `neighbors`, `find`, `path`, `subtree`. |
| `revit_graph_summary` | Counts per kind/category/level/workset/panel, orphan circuits, elements without a room/space. Cheap orientation at session start. |

All four are read-only tools for Revit (the build writes only the graph file). They run on the
Revit API thread like the other `revit_*` query tools; reads are indexed lookups and take
milliseconds.

### `revit_graph_query` operations

| `operation` | Arguments | Returns |
|---|---|---|
| `neighbors` | `id`, `rel?`, `direction?` (`in` \| `out` \| `both`, default `both`), `limit?` (1–500, default 100), `link?` | The node plus its neighbours with `rel` and `direction` |
| `find` | `kind?`, `category?`, `level?`, `workset?`, `nameContains?`, `link?`, `param?`, `paramValue?`, `paramContains?`, `page?`, `pageSize?` (default 100, max 500) | Paginated ids + names (`itemsReturned`, `totalAvailable`, `hasMore`, `nextPage`); items carry `routingParams` when the graph indexes any |
| `path` | `fromId`, `toId`, `maxHops?` (1–10, default 6) | Shortest undirected path; each step has `rel` and `direction` |
| `subtree` | `id`, `rel`, `depth?` (1–10, default 3), `direction?` (`in` \| `out`, default `in`), `maxNodes?` (1–2000, default 500) | Everything reachable via one relationship, with `depth` and `parentId` |

Examples:

```
revit_graph_query operation=find kind=panel nameContains=JK
revit_graph_query operation=subtree id=123456 rel=fed_by depth=2        # everything fed by panel 123456
revit_graph_query operation=neighbors id=234567 rel=located_in direction=out
revit_graph_query operation=path fromId=234567 toId=123456 maxHops=4
revit_graph_query operation=find kind=element category="Lighting Fixtures" level="2. korrus" page=0 pageSize=200
revit_graph_query operation=find param="Loop number" paramValue=L1 link=host   # needs graph.routingParameters
```

Name matching (`nameContains`) is a SQLite `LIKE`, so it is case-insensitive for ASCII letters only.
Category/level/workset filters are exact, case-insensitive matches.

---

## Storage

One file per model:

```
<root>\<project>\<model-name>.graph.db
```

- `project` = Project Information → Number (fallback: Name; fallback: `_unfiled`)
- `model-name` = the model file name without extension (the central file name for file-based
  worksharing, so every team member resolves the same file)
- `root` is resolved in this order:
  1. `dbPath` argument (explicit file, bypasses everything)
  2. `sharedFolder` argument
  3. `graph.sharedFolder` in the **user** config (`%AppData%\RKTools\MCP\user.config.json`)
  4. `graph.sharedFolder` in the **company** config (`%ProgramData%\RKTools\MCP\Config\company.config.json`)
  5. local fallback `%LOCALAPPDATA%\RKTools\RevitMCP\Graph`

Configure the shared folder with the existing configuration tools:

```
config_update scope=user updates={"$.graph.sharedFolder": "\\\\server\\bim\\graphs"}
```

or for the whole company, `scope=company`. Environment variables in the value are expanded.
`revit_graph_status` reports which source was used (`rootSource`) and the resolved path.

### Shared-folder safety

- A build writes to a local temp file under `%LOCALAPPDATA%\RKTools\RevitMCP\Graph\tmp`, copies it
  next to the target, and then swaps it in with `File.Replace` / `File.Move`. Readers never see a
  half-written file, and no write lock is ever held on the shared file.
- Readers open the database **read-only**. A file that is not under the local root is first copied
  into `%LOCALAPPDATA%\RKTools\RevitMCP\Graph\cache` (only when its size or timestamp changed) and
  the copy is read, so synced folders (Dropbox, OneDrive) and network shares are never held open.
- Build-time pragmas use an in-memory journal so no `-journal` / `-wal` side files are left behind.

---

## Freshness

`central_version` is captured at build time and compared on every read:

| Model | Signal | Changes when |
|---|---|---|
| File-based workshared | `BasicFileInfo.LatestCentralVersion` + `LatestCentralEpisodeGUID` from the open document's file header | That local document synchronises with central |
| Non-workshared, cloud/server workshared | `Document.GetDocumentVersion` → `NumberOfSaves` + `VersionGUID` | The document is saved (or synced, for cloud locals) |
| Unsaved | none | — |

In addition the element count (`FilteredElementCollector.WhereElementIsNotElementType().GetElementCount()`)
is compared, which catches most unsaved in-session edits. `stale` is `true` when the schema
version, model name, version signal or element count differs; `stale_reason` says which.

Known limitations: unsaved edits that do not change the element count (parameter edits, moves)
are not detected; freshness is relative to the open local document's last synchronised version,
not to a newer central version that the document has not reloaded.
When in doubt, rebuild — a full build of a 100k-element model takes a few seconds.

---

## Routing parameters (#63)

By default the graph stores no parameter values. When a distinction exists only in a parameter
(loop number, device number, system code, a project classification), a **bounded allowlist** of
parameters can be indexed as routing hints:

```json
{
  "graph": {
    "routingParameters": [
      "Loop number",
      { "name": "Seadme Nr.", "categories": ["Fire Alarm Devices"] },
      { "guid": "0d8b5b3a-3c3b-4c41-9a35-4c8c9a1b2c3d", "name": "System code" }
    ]
  }
}
```

- **Where:** the project config (`<projectRoot>\.rktools\mcp.project.config.json`, found above the
  model file), else the user config, else the company config. The first scope that defines the key
  wins, so a project can narrow a company list or switch it off with `[]`.
  `config_set_project_config` / `config_update` write these files.
- **Bounds:** at most 20 parameters. Values are trimmed, collapsed to one line and cut at 200
  characters. Names that look like secrets (password, token, secret, API key, credential) are never
  indexed, even when listed. Only allowlisted parameters are read — never all parameters.
- **Reading:** the instance parameter first, then the type parameter; by shared-parameter GUID when
  one is given, else by name. Empty values are not stored. `categories` limits a parameter to those
  categories. Host and linked elements, panels, circuits and rooms/spaces are covered.
- **Querying:** `find param=<name>` (has a value), `paramValue=<exact>`, `paramContains=<substring>`.
  Matching is case-insensitive for any script (values are lower-cased with the invariant culture),
  and combines with every other filter, including `link`. Without `param` the value filters match any
  indexed parameter. Returned items carry `routingParams`, and the response says they are hints.
- **Staleness:** values are captured at `built_at` like everything else in the graph. Incremental
  builds refresh the parameters of changed elements; changing the allowlist forces a full rebuild.
  **Never report or act on a graph parameter value** — read it live by id first.
- `revit_graph_build` and `revit_graph_status` report the indexed parameters with node and
  distinct-value counts. Graphs built without the feature still work: their param filters simply
  match nothing, with a warning saying how to enable them.

---

## Linked models (#62)

A full build also indexes every **loaded** Revit or IFC link of the host model (`includeLinks`,
default true; `linkElementLimit`, default 100,000 linked elements in total).

| Node | Id | Notes |
|---|---|---|
| Link instance | its host element id (kind `link`) | `extra.document` = linked file, `extra.loaded` |
| Linked element | `link:<linkInstanceId>:<linkedElementId>` | kind `element`; physical elements only (a bounding box is required) |
| Linked room/space | `link:<linkInstanceId>:<linkedElementId>` | kind `space` |
| Linked type | `link:<linkInstanceId>:<linkedTypeId>` | kind `type`, only when referenced |

The link instance id namespaces each linked document, so equal element ids in the host and in
different links never collide; two instances of the same link are indexed separately. Every linked
node carries `extra.linkName` / `extra.linkDocument`, and query results add a `linkInstanceId` field
(null for host nodes).

Linked relationships:

| `rel` | Meaning |
|---|---|
| `in_link` | linked node → its link instance (`subtree id=<link> rel=in_link` lists a link's content) |
| `type_of` | linked element → linked type |
| `located_in` | linked element → linked room/space (`FamilyInstance.Room/Space` inside the link) |
| `located_in` | linked element → **host** room/space, computed geometrically through the link transform: a family instance's insertion point, otherwise the bounding-box centre of elements no larger than 3 m (IFC DirectShapes have no location); retried 1 m lower for ceiling-mounted items |

Levels, worksets, hosts, circuits and tags inside links are not indexed (a linked element's `level`
is the linked level's name, without an `on_level` edge). Nested links are not followed. An unloaded
link appears as a `link` node with `loaded: false` and a warning; its content is simply absent.

Querying:

- `find link=host` — host nodes only; `link=links` — every linked node; `link=<instance id>` or
  `link=<part of the link name>` — one link. Combines with the other filters.
- `neighbors` takes the same `link` filter. **Pass `link=host` before reading results live by id**:
  host tools (`revit_get_elements_info`, …) cannot read `link:` ids. Read linked elements live with
  `revit_query_linked_elements linkInstanceId=<id>`.

Linked content reflects the links as loaded at the last build. Reloading or moving a link marks
nothing stale by itself; an incremental build falls back to a full one when a link instance or link
type changed.

---

## Incremental updates (#61)

The add-in records every added, modified and deleted element id from Revit's `DocumentChanged`
event, per open document. `revit_graph_build incremental=true` then re-extracts only those
elements instead of walking the whole model:

1. A private copy of the published graph is opened.
2. For every changed id its node and the edges it **owns** are removed; for every deleted id every
   edge touching it is removed as well.
3. The changed elements are re-extracted (same rules as a full build) and their nodes and edges
   inserted. Edges whose endpoints are not in the graph are dropped, as in a full build.
4. Type nodes no longer referenced by a `type_of` edge are pruned; meta (`built_at`,
   `central_version`, `element_count`, counts, `incremental_updates`) is refreshed.
5. The copy is published atomically, exactly like a full build.

Edge ownership (`edge_owners` table, schema 2) records which element produced each edge: the
element itself for its `on_level`, `in_workset`, `type_of`, `hosted_on` and `located_in` edges, the
circuit for `fed_by` (both circuit → panel and element → circuit), the sheet for `on_sheet` and the
tag for `tagged_in`. Nodes whose stored data comes from another element are refreshed with it: the
panel of a changed or deleted circuit, the circuits of a changed panel, the instances of a changed type.

The update **falls back to a full rebuild** — and says why in `incremental.fallbackReason` and
`warnings` — when it cannot be sure the result equals a full build:

- no graph exists yet, or it was written with schema 1 (no edge ownership) — rebuilt once;
- the changes since the last build were not tracked: the graph was built by another session or
  machine, or Revit / the add-in was restarted or reloaded since (the first incremental request
  after a restart is always a full build);
- more than 50,000 element changes were tracked, or the last build hit `elementLimit`;
- a level was changed or deleted (level names are stored on every node);
- a room or space was added or changed (other elements change room without changing themselves);
- a workset changed;
- the routing-parameter allowlist changed;
- a linked model changed (link added, moved, reloaded or removed).

Deleting a room only removes the `located_in` edges to it, so it stays incremental. An incremental
build produces the same nodes and edges as a full build of the same model state; the full rebuild
remains the recovery path (`incremental=false`, the default).

---

## Graph-first routing (#64)

The connector routes discovery through the graph by itself, without relying on repo prompt files:

- **Server instructions.** Every MCP client receives a short graph-first workflow in the `initialize`
  handshake (`RevitMCP.Bridge/ServerInstructions.cs`, about 310 tokens).
- **`revit_graph_route`.** Give it the user's intent in plain words; it returns the cheapest call plan
  and the graph's freshness. It understands rooms/spaces, panels/circuits/loops, levels, categories
  present in the graph, the selection and linked models, in English and Estonian. When the graph is
  missing or stale the plan starts with `revit_graph_build` and says that ids are hints to verify.
- **Hints on broad live queries.** `revit_get_elements_info`, `revit_find_elements_by_parameter`,
  `revit_group_by_parameter`, `revit_group_elements` and `revit_export_query_to_excel`, when called
  with a category scope (no `elementIds`, no selection) that matches 50 or more elements, return a
  `routingHint` in `data` naming the equivalent `revit_graph_query find` call. The live result is
  never changed or blocked. Opt out per call with `graphHint=false`, or for a user with
  `config_update scope=user updates={"$.graph.routingHints": false}`.

Examples:

```
revit_graph_route intent="which devices are in room 1.12?"
  → find kind=space nameContains=1.12 → neighbors rel=located_in direction=in → get_elements_info elementIds=[…]
revit_graph_route intent="everything fed by panel JK-1"
  → find kind=panel nameContains=JK-1 → subtree rel=fed_by depth=2 → get_circuit_info
revit_graph_route intent="fire alarm devices on Teine korrus"
  → find kind=element category="Fire Alarm Devices" level="Teine korrus" → get_elements_info elementIds=[…]
```

Measured on the Tarvastu EN model (`docs/benchmarks/`): inspecting a category costs about 145k result
tokens through broad live calls and about 5k graph-first; with the `query` profile (which now includes
the graph tools) the whole graph-first workflow, schema included, is about 13.5k tokens instead of
about 214k.

## Agent workflow

0. `revit_graph_route` with the intent gives the plan below in one call.
1. `revit_graph_status` at session start. If `exists` is false or `stale` is true, run
   `revit_graph_build` (or re-verify ids against the live model if a rebuild is not appropriate).
2. `revit_graph_summary` for orientation: which panels, levels, categories exist.
3. `revit_graph_query` to narrow down ids (`find`, `subtree`, `neighbors`, `path`).
4. Fetch live values by id with `revit_get_elements_info`, `revit_get_element_parameters`,
   `revit_get_circuit_info`, etc. Never quote graph names or `extra` hints as facts.

---

## Limitations in this version

- Incremental updates need a baseline built in the same Revit session (see above); changes made
  while the add-in was not loaded are invisible to them.
- Requests are capped at 30 s by the connector. On very large models the build can exceed that:
  the bridge reports `request_timeout` but the build keeps running on the Revit thread and still
  publishes the file — check `revit_graph_status` afterwards. Use `elementLimit` to bound the build.
- `tagged_in` links an element to the **view** its tag sits in, not to the tag element itself.
- Annotation elements other than tags are not indexed, and parameter values only when allowlisted
  (see [Routing parameters](#routing-parameters-63)); inside linked models
  only elements, rooms/spaces and types are (see [Linked models](#linked-models-62)).
- The add-in uses the SQLite that ships with Windows 10/11 (`winsqlite3.dll`) through
  `SQLitePCLRaw.bundle_winsqlite3`, so no native binary is deployed; the unit tests use the
  cross-platform `e_sqlite3` bundle instead.

---

## Rolling back

Delete `RevitMCP.Addin/Graph`, `RevitMCP.Addin/Tools/Graph`, the four `handler.RegisterTool(new Graph…Tool())`
lines in `App.cs`, the `Model Graph` block at the end of `RevitMCP.Bridge/RevitMcpTools.cs`, the
SQLite `PackageReference`/`PackageVersion` entries, the `Graph\*.cs` links and SQLite packages in
`RevitMCP.Tests.csproj`, and the `Graph*Tests.cs` files. No existing tool or file format depends on
the module.
