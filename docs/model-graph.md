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
| `revit_graph_build` | Full rebuild from the open document (Revit API thread, one collector pass). Reports node/edge counts and timings. `incremental=true` is accepted but **not implemented yet** — it is reported in `warnings` and falls back to a full rebuild. |
| `revit_graph_status` | Does a graph exist for the open model, where, `built_at`, `central_version`, the current document version, and `stale` with a human-readable reason. |
| `revit_graph_query` | Structured queries only (no SQL): `neighbors`, `find`, `path`, `subtree`. |
| `revit_graph_summary` | Counts per kind/category/level/workset/panel, orphan circuits, elements without a room/space. Cheap orientation at session start. |

All four are read-only tools for Revit (the build writes only the graph file). They run on the
Revit API thread like the other `revit_*` query tools; reads are indexed lookups and take
milliseconds.

### `revit_graph_query` operations

| `operation` | Arguments | Returns |
|---|---|---|
| `neighbors` | `id`, `rel?`, `direction?` (`in` \| `out` \| `both`, default `both`), `limit?` (1–500, default 100) | The node plus its neighbours with `rel` and `direction` |
| `find` | `kind?`, `category?`, `level?`, `workset?`, `nameContains?`, `page?`, `pageSize?` (default 100, max 500) | Paginated ids + names (`itemsReturned`, `totalAvailable`, `hasMore`, `nextPage`) |
| `path` | `fromId`, `toId`, `maxHops?` (1–10, default 6) | Shortest undirected path; each step has `rel` and `direction` |
| `subtree` | `id`, `rel`, `depth?` (1–10, default 3), `direction?` (`in` \| `out`, default `in`), `maxNodes?` (1–2000, default 500) | Everything reachable via one relationship, with `depth` and `parentId` |

Examples:

```
revit_graph_query operation=find kind=panel nameContains=JK
revit_graph_query operation=subtree id=123456 rel=fed_by depth=2        # everything fed by panel 123456
revit_graph_query operation=neighbors id=234567 rel=located_in direction=out
revit_graph_query operation=path fromId=234567 toId=123456 maxHops=4
revit_graph_query operation=find kind=element category="Lighting Fixtures" level="2. korrus" page=0 pageSize=200
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

## Agent workflow

1. `revit_graph_status` at session start. If `exists` is false or `stale` is true, run
   `revit_graph_build` (or re-verify ids against the live model if a rebuild is not appropriate).
2. `revit_graph_summary` for orientation: which panels, levels, categories exist.
3. `revit_graph_query` to narrow down ids (`find`, `subtree`, `neighbors`, `path`).
4. Fetch live values by id with `revit_get_elements_info`, `revit_get_element_parameters`,
   `revit_get_circuit_info`, etc. Never quote graph names or `extra` hints as facts.

---

## Limitations in this version

- `incremental` builds are not implemented; every build is a full rebuild.
- Requests are capped at 30 s by the connector. On very large models the build can exceed that:
  the bridge reports `request_timeout` but the build keeps running on the Revit thread and still
  publishes the file — check `revit_graph_status` afterwards. Use `elementLimit` to bound the build.
- `tagged_in` links an element to the **view** its tag sits in, not to the tag element itself.
- Linked-model elements, annotation elements other than tags, and parameter values are not indexed.
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
