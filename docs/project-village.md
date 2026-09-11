# Project Village (read-only activity visualizer)

> **The Project Village is a passive visualization. It does not make AI calls, add content to the
> agent context, execute MCP tools, or write to Revit. It displays deterministic summaries of
> connector activity and read-only graph snapshots.**

Each open Revit project becomes a small isometric village. Buildings stand for broad work areas
(sheets, views, schedules, families, tags, elements, systems, coordination, office work); one
character per connected MCP client walks between them as tools run. Below the village a
**warehouse yard** stores the model's inventory: one warehouse per Revit category, as big as that
category's element count. Nothing in the village shows model geometry or individual elements.

To see it without Revit, open the page with `?demo=1` (for example
`http://127.0.0.1:47800/?demo=1`, or the `village.html` file directly): a built-in deterministic
script plays a fire-alarm-themed project with one agent.

---

## Architecture and one-way data flow

```
AI agent ──MCP──▶ RevitMCP.Bridge ──pipe──▶ PipeServer ──▶ ExternalEvent ──▶ Revit   (unchanged)
                                               │                                    
                                               │ VillageHooks.ToolStarted   (1 line, before dispatch)
                                               │ VillageHooks.ToolCompleted (1 line, in ActivityLogger.WriteAsync)
                                               ▼
                                   VillageEventQueue (bounded, priority drop policy)
                                               ▼
                       VillageStateHub consumer (one background task, never the Revit thread)
                       ├─ VillageAggregator  → story steps + live state
                       ├─ VillageGraphReader → read-only graph snapshot + theme
                       └─ serialized "state" / "step" messages
                                               ▼
                       VillageSseServer  http://127.0.0.1:47800/  (GET only, loopback only)
                                               ▼
                       village.html (Canvas 2D, EventSource, no external assets)
```

The information flow is strictly one-way. The viewer has no endpoint that accepts a command, the
hub has no reference back into the pipe server or the tool registry, and the hooks never modify,
delay or retain the request or the result. If the village is disabled, not running, or failing,
the two hook calls return immediately.

Source layout. The feature is a separate project so the boundary is enforced by the compiler
rather than by convention: `RevitMCP.Village` cannot reference the add-in, so it can never reach
Revit, the pipe server or the tool registry. It also keeps the option of extracting the module
into its own repository later without untangling anything.

| Project / folder | Contents |
|---|---|
| `RevitMCP.Village/` | Class library (netstandard2.0 + net8.0). Event schema, tool classification, bounded queue, aggregation, theme scoring, the loopback SSE server and the state hub. No Revit API, no add-in reference. |
| `RevitMCP.Addin/Village/VillageGraphReader.cs` | The one piece that needs the add-in's graph classes: implements `IVillageGraphReader` from the library. |
| `RevitMCP.Addin/Village/Hosting/` | `VillageService` (wiring, config, instance registration), `VillageHooks`, the WPF status view model |
| `RevitMCP.Addin/Village/Viewer/village.html` | The viewer page, embedded as a resource |
| `RevitMCP.Tests/Village*Tests.cs` | Automated tests (no Revit needed) |

`VillageStateHub` takes an `IVillageGraphReader`, so the library depends on the graph only through
that interface: an implementation may read, must never hold the published file open, and must
never throw.

## Why it costs no AI credits

- The agent is never told the village exists: `CLAUDE.md`, tool descriptions and tool responses
  are untouched, so no tokens are added to any session.
- There is no visualization tool. Activity is observed from the connector's existing pipe and
  logging path.
- Every classification, label, aggregation decision, theme score and building size is computed by
  deterministic application code (`VillageToolClassifier`, `VillageAggregator`,
  `VillageThemeScorer`). No LLM or external service is called anywhere in the module, and the
  viewer page loads nothing from the network except its own `/events` stream and `/snapshot`.

## Enabling it

The village is **opt-in** because it starts a loopback listener. Either:

- open the Revit MCP Connector window → Status tab → **Project Village** → *Enable Village* (the
  choice is remembered in the user config), then *Open Village*; or
- set `village.enabled` in the user or company config:

```
config_update scope=user updates={"$.village.enabled": true}
```

The connector reads the setting at startup. The page is served at `http://127.0.0.1:47800/`
(the next nine ports are tried when 47800 is busy, e.g. a second Revit instance). Other running
villages appear in a drop-down in the page header.

## Event schema (version 1)

Events are produced by `VillageEventFactory` and never leave the process except as part of the
aggregated messages below; the schema is documented so the format can be replayed or extended.

```json
{
  "schema_version": 1,
  "event_id": "8b2f5c1e-…",
  "session_id": "connector-run-uuid",
  "model_id": "3f9c0a1b2c3d4e5f",
  "project_name": "1626_PP_EN",
  "timestamp": "2026-09-10T12:00:00.000Z",
  "event_type": "tool_completed",
  "tool_name": "revit_place_tags",
  "activity": "create",
  "area": "tags_annotations",
  "success": true,
  "duration_ms": 912,
  "affected_count": 24,
  "client_name": "Claude Code",
  "status": null,
  "sequence": 118
}
```

| Field | Notes |
|---|---|
| `event_type` | `session_started`, `project_changed`, `tool_started`, `tool_completed`, `tool_deferred` (approval required / rejected / expired), `tool_failed`, `graph_refreshed`, `bridge_connected`, `bridge_disconnected` |
| `area` | `project`, `graph`, `sheets`, `schedules`, `views`, `families_types`, `tags_annotations`, `elements`, `fire_alarm`, `security`, `lighting`, `it_av`, `electrical`, `coordination`, `office`, `unknown` |
| `activity` | `inspect`, `search`, `analyze`, `create`, `modify`, `delete`, `export`, `validate`, `build_graph`, `error`, `unknown` |
| `model_id` | 16 hex characters of a SHA-1 over the central (or local) path; never the path |
| `affected_count` | Read from an allow-list of top-level numeric result fields (`placedCount`, `updatedCount`, `returned`, `totalMatched`, …), one level deep |
| `status` | The connector's machine-readable status code (`approval_required`, `request_timeout`, …), never free text |

Unknown tools map to `area: unknown` / `activity: unknown` and are shown at the village square.
Unknown event types, unknown vocabulary and malformed fields are ignored by the parser and the
aggregator. Adding optional fields keeps the schema version; changing a field's meaning bumps it.

## Privacy and sanitization

Never published: prompts, AI responses, tool arguments, tool results, messages, warnings,
errors, parameter values, element ids, model geometry, file contents, file paths, user names,
credentials or tokens. The hooks read only the tool name, client name, success flag, status code,
duration, the document title and the model path (hashed), plus a shallow allow-listed numeric
count from the result. The graph snapshot carries counts, category and level names, health
counters and freshness — never node ids, element names or the `built_by` user.

The one thing read from the request itself is the Revit **category**, and only through
`VillageCategoryArgument`: a fixed allow-list of argument keys (`category`, `categoryName`,
`categories`, `categoryNames`), one level deep, capped at 100 characters. It is never published
as text. The hub matches it against the warehouses the graph snapshot already exposes and emits
only the resulting **warehouse id** — a value derived from the graph, not from the request. A
category with no warehouse, a misspelling, or a request naming several categories all resolve to
null and change nothing. `VillageWarehouseRoutingTests.TheEventStillCarriesNoArgumentsOrValues`
pins that a request carrying a parameter name, a value and a file path still serializes none of
them.

The tests `VillageEventTests.Serialize_NeverContainsPathsArgumentsOrResults`,
`VillageStateHubTests.GraphToolResponses_FeedTheFreshnessHintWithoutRetainingTheResult` and
`VillageGraphReaderTests.Snapshot_SerializesWithoutPathsUserNamesOrIds` pin this down.

## Local security model of the bridge

- Binds to a loopback address only. `village.host` accepts `127.0.0.1`, `localhost` or `::1`;
  anything else is coerced back to `127.0.0.1`.
- Accepts `GET`/`HEAD` for `/`, `/events`, `/snapshot`, `/instances`, `/health`. Every other
  method gets `405`, every other path `404`. Request bodies are never read.
- The `Host` header must name a loopback host (`421` otherwise) to block DNS-rebinding pages.
- No CORS headers are sent, so foreign origins cannot read responses or open the stream; a strict
  `Content-Security-Policy` is set on every response.
- The request head is capped at 8 KB and must arrive within 5 s. At most `village.maxViewers`
  streams (default 8) are served; each viewer has a bounded outbound queue (256 messages) and a
  slow viewer loses old state messages rather than growing memory.
- The server holds three read-only string providers and nothing else: there is no code path from
  a request to the hub, the pipe server, MCP or Revit.

## Configuration

All keys live under `village` in the user config (`%AppData%\RKTools\MCP\user.config.json`) or the
company config (`%ProgramData%\RKTools\MCP\Config\company.config.json`); the user value wins per
key. Values are clamped into the ranges shown.

| Key | Default | Range | Meaning |
|---|---|---|---|
| `enabled` | `false` | | Start the loopback listener |
| `host` | `127.0.0.1` | loopback only | Listener address |
| `port` | `47800` | 1024–65535 | First port to try |
| `portFallback` | `true` | | Try the next 9 ports when busy |
| `queueSize` | `2000` | 100–20000 | Bounded event queue |
| `maxEventsPerSecond` | `200` | 10–5000 | Rate limit for low-priority events |
| `aggregationWindowMs` | `1500` | 200–10000 | Same-area events inside this window merge |
| `recentActivityLimit` | `200` | 20–2000 | Steps kept for the feed / late viewers |
| `historyLimit` | `500` | 50–5000 | Replay-ready step history (memory only) |
| `graphRefreshSeconds` | `60` | 10–3600 | Graph file check interval |
| `agentIdleSeconds` | `300` | 30–3600 | Silent agents become "disconnected" |
| `animationSpeed` | `1.0` | 0.1–5 | Viewer default (the page has a slider) |
| `maxViewers` | `8` | 1–32 | Simultaneous streams |
| `maxBuildings` | `12` | 6–12 | Landmarks drawn |
| `maxWarehouses` | `10` | 0–20 | Category warehouses drawn (`0` hides the yard) |
| `maxEffects` | `6` | 1–24 | Simultaneous visual effects |
| `reconnectBackoffMs` / `reconnectBackoffMaxMs` | `1000` / `15000` | | Viewer reconnect backoff |
| `diagnosticLogging` | `false` | | Village diagnostics in `%LOCALAPPDATA%\RevitMCP_startup.log` |
| `toolAreas` | `{}` | | Per-tool area overrides, e.g. `{"revit_list_sheets": "office"}` |
| `toolActivities` | `{}` | | Per-tool activity overrides |
| `themes` | built-in | | System-theme mapping, see below |

Example:

```json
{
  "village": {
    "enabled": true,
    "port": 47800,
    "aggregationWindowMs": 2000,
    "toolAreas": { "revit_run_skill": "sheets" },
    "themes": {
      "minSampleCount": 20,
      "systems": {
        "fire_alarm": { "categories": ["Fire Alarm Devices", "Tulekahjusignalisatsiooni seadmed"], "keywords": ["ATS", "suitsuandur"] }
      }
    }
  }
}
```

## Village layout (tool → area → building)

| Building | Areas | Sized by (graph) |
|---|---|---|
| Town hall | `project`, `graph` | node count |
| Archive | `sheets` | sheets |
| Lookout tower | `views` | views |
| Market hall | `schedules` | schedules |
| Workshop | `families_types` | types |
| Sign workshop | `tags_annotations` | `tagged_in` edges |
| Houses | `elements` | elements |
| Utility district | `electrical`, `fire_alarm`, `security`, `lighting`, `it_av` | panels + circuits; ELV districts decorate it |
| Survey post | `coordination` | fixed |
| Records office | `office` (files, Excel, reports, delivery, config, standards, skills) | fixed |
| Warning area | — | recent failures, stale graph, orphan circuits |

## Warehouse yard (category → warehouse)

The landmarks above are fixed: the village always has an archive, whether or not the project has
sheets. The yard is the opposite — it is built from the model. `VillageWarehouseYard.Plan` takes
the graph snapshot's `elementsByCategory` counts and gives **one warehouse to every Revit category
that has at least one element**. A category with no elements gets no warehouse, so a project
without security devices simply has no security warehouse.

| Property | Derived from |
|---|---|
| Which warehouses exist | Categories with `count > 0`, largest first, capped at `maxWarehouses` |
| Footprint (1.05–2.10 tiles) | `log10(1 + count) / log10(1 + 5000)`, clamped |
| Height | Follows the footprint |
| Size bucket 1–6 and loading bays 2–6 | `< 10`, `< 50`, `< 200`, `< 1000`, `< 5000`, above |
| Pallets stacked outside | Size bucket above 2 |
| Roof and sign colour | The theme system that lists the category (`village.themes`), else a hue hashed from the category name |
| Position | Row-major, five per row, starting at tile (1.0, 13.4) below the village |

The scale is **absolute, not relative**: a warehouse is sized by its own count alone, so it never
changes when a different category grows, and 5 000 elements or more is always the largest building.
It is logarithmic because category counts span several orders of magnitude — 4 mechanical
equipment next to 40 000 conduit fittings would otherwise make everything but one building
invisible.

The yard grows the isometric grid downwards, and the viewer refits the tile size to whatever the
grid needs, so more categories mean a slightly smaller village rather than a clipped one. Set
`village.maxWarehouses` to `0` to get the village exactly as it was before the yard existed.

### Walking to a warehouse

When a tool names a Revit category that has a warehouse, the character works **at that warehouse**
instead of the area landmark: "how many fire alarm devices have Ahela nr 2" walks it to the Fire
Alarm Devices warehouse, not to Houses. The step is worded after the category too — "Searched Fire
Alarm Devices (2 tools)".

The rules, all of them deterministic:

- Only areas that hold model elements redirect: `elements`, `fire_alarm`, `security`,
  `lighting`, `it_av`, `electrical` (`VillageLayout.StorableAreas`). A sheet or graph tool that
  happens to take a category keeps its landmark.
- Only a category that currently has a warehouse redirects. Everything else — an unknown category,
  a request spanning several categories, a category whose warehouse fell outside `maxWarehouses` —
  leaves the character at the landmark.
- A follow-up tool that names **no** category leaves the character where it is, so fetching
  parameters for ids you just found does not walk it back to Houses every call.
- Work at two different warehouses never merges into one story step.
- Rebuilding the yard walks characters off warehouses that no longer exist.

Clicking a warehouse still shows its element count, its share of all counted elements, its system
and its rank.

Without a graph the yard is empty — like the rest of the graph-derived display, it runs in limited
mode. The counts come from the graph file and are as old as the last build; the inspector says so.

Classification (`VillageToolClassifier`): explicit overrides → namespace prefixes (`config_`,
`file_`, `excel_`, `standards_`, `delivery_` → office) → ordered keyword rules matched at token
boundaries (`_tag` matches `_tags` but not `_voltage`). Activity comes from the verb after the
namespace (`preview` → analyze, `set`/`apply`/`rename`… → modify, `find`/`query` → search, …) with
a small override table for `revit_run_*` and graph tools. `VillageToolClassifierTests` checks
every tool name that existed when the feature was added; a new tool that maps to `unknown` fails
that test so it gets a deliberate mapping.

## System themes

`VillageThemeScorer` decides how a village looks from the graph snapshot:

| Evidence | Weight | Notes |
|---|---|---|
| Elements per Revit category (`Fire Alarm Devices`, `Security Devices`, `Lighting Fixtures`, `Data Devices`, `Electrical Fixtures`, …) | 1.0 | Strong evidence |
| Instances of family/type names matching keywords (`ATS`, `LPS`, `VVS`, `DALI`, `RJ45`, `kaamera`, `valgusti`, …) | 0.5, capped | Keywords of four characters or fewer must match a whole token (tokens split on non-alphanumerics and camelCase); longer keywords match as substrings |

Rules:

- name evidence for a system is capped at `category evidence + minSystemCount`, so a few
  misnamed families can never turn a project red;
- below `minSampleCount` (20) category-classified elements the village stays **neutral**;
- a system is **visible** (its district is drawn) at `visibleShare` (10 %) and at least
  `minSystemCount` (10) points;
- the top system is **dominant** at `dominantShare` (45 %) *and* at least `minSystemCount`
  category-classified elements; otherwise two or more visible systems make a **mixed** village.

Themes: fire alarm (red roofs, beacon tower), security (grey, fence and gate), lighting (amber,
lanterns along the roads), IT/AV (blue, antenna mast), general electrical (brown, poles and
cables), mixed (green, blended districts), neutral. Ground colour and tile pattern come from a
hash of the model id, so the same model looks the same in every session while different models
differ. Warning effects (pulsing warning area, red badge) are reserved for actual failures, stale
graph data and reported orphan circuits — never for "many fire-alarm devices".

The **Diagnostics** panel in the page shows the full breakdown: category and name evidence per
system, the capped contribution, share, thresholds and whether the mapping came from the default
table or the config.

## Aggregation behaviour

Raw events are folded by `VillageAggregator` into *story steps*:

- events from the same agent, in the same area and the same group (reads = inspect/search/
  analyze/validate; each write activity, export and graph build separately) within
  `aggregationWindowMs` merge into one step with a tool count, distinct tool names (max 5) and an
  affected total ("24 tags updated (24 tools)");
- an area change closes the step and emits a **move** step; the character walks only then;
- failures, approval waits, project switches, graph refreshes and agent connects/disconnects are
  always their own steps;
- a step closes when the window elapses, when an incompatible event arrives, or on shutdown.

Catch-up: when the queue holds more than `max(50, queueSize / 10)` events the hub enters
**catch-up** mode (header badge), the merge window widens fourfold and the mode is left once the
queue is drained. The viewer additionally speeds up a character that has several pending moves and
teleports it when more than eight are pending ("Catching up: N moves skipped"). Real tool
execution never waits for any of this: the hooks enqueue and return.

Queue policy (`VillageEventQueue`): low-priority events (routine reads) are dropped when the queue
is full or the per-second budget is spent; high-priority events (errors, approvals, writes, graph
builds, transitions) evict the oldest low-priority event instead. Counters are visible in
Diagnostics.

## Graph freshness

The village reads the model graph strictly for visualization, following the shared-folder rules:
the published file is copied into a village-owned cache (`%LOCALAPPDATA%\RKTools\RevitMCP\Village\graph-cache`)
by the same `GraphStore` logic the tools use, the copy is opened read-only, connections are closed
immediately, and the result is cached until the file's size or timestamp changes. Atomic
replacement by a build is tolerated. The village never writes the graph and never locks it.

The graph is located from the `databasePath` of the last graph tool response, otherwise by a
one-level search for `<model name>.graph.db` under the configured `graph.sharedFolder` and the
local fallback root. Freshness needs a Revit-thread signal the village must not read itself, so
it shows:

| Status | Meaning |
|---|---|
| `missing` | No graph file — limited mode (default sizes, neutral theme) |
| `unknown` | A graph exists but no graph tool has reported freshness this session |
| `fresh` | The last `revit_graph_*` response said the graph matches the document |
| `stale` | The last response said it is stale (reason shown) |
| `possibly_stale` | Reported fresh, but write tools succeeded since |

Graph values are routing and visualization metadata captured at build time, not live Revit values.

## Performance limits

| Limit | Value |
|---|---|
| Hook cost (no viewer) | ≈ 4.6 µs per completed tool on the test machine (`VillageStateHubTests.HookOverhead_WithNoViewer_IsMicroseconds`), never blocking |
| Event queue | `queueSize` (2000), `maxEventsPerSecond` (200) |
| Consumer | one background task, 500 events per pass, state pushed at most every 250 ms |
| Recent feed / history | 200 / 500 steps |
| Buildings / warehouses / effects | ≤ 12 / ≤ 20 / ≤ 6 |
| Graph reads | on graph tools, after writes (cache hit), and every `graphRefreshSeconds`; ≤ 2000 types and ≤ 5000 views examined |
| Viewers | ≤ 8, 256 queued messages each, 15 s heartbeat |
| Reconnect | browser retry, then exponential backoff 1 s → 15 s |

## Troubleshooting

| Symptom | Check |
|---|---|
| Status tab says "Enabled but not listening" | Ports 47800–47809 are busy; set `village.port` or free the port. Details in `%LOCALAPPDATA%\RevitMCP_startup.log` (`[VILLAGE …]`). |
| Page shows "reconnecting" | The connector was stopped or Revit closed; the page retries with backoff. |
| Badge says "graph: none" | No graph for this model; run `revit_graph_build`, or check `graph.sharedFolder`. |
| Badge says "graph: unknown" | Run `revit_graph_status` once; freshness is only known from graph tool responses. |
| A tool lands on the village square | It classified as `unknown`; add a `village.toolAreas` entry or extend the rule table. |
| Theme looks wrong | Open Diagnostics; adjust `village.themes` categories/keywords (localised category names can be added). |
| "Catching up" stays on | A large burst is being folded; it clears when the queue drains. |

## Known limitations

- One village per Revit process shows the active document only; switching documents resets the
  counters (the story feed is kept).
- Freshness after unsaved edits depends on graph tool responses; write activity only flags
  "possibly stale".
- The character starts events only when a tool is dispatched through the pipe; approved writes
  that execute later show as a completion without a start (still counted).
- No persistent history: the replay-ready buffer lives in memory for the connector's lifetime.
- The viewer is a single page with procedural sprites; it is not a 3D or geometry viewer by design.
- Revit's UI language changes category names; add localised names to `village.themes`.

## Rolling back

Delete the `RevitMCP.Village` project (and its entry in `RevitMCP.slnx`) and `RevitMCP.Addin/Village`,
then remove: the village block in `App.OnStartup` and the `_village?.Dispose()` line in
`OnShutdown`; the one-line hooks in `PipeServer.HandleClientAsync` and `ActivityLogger.WriteAsync`;
the `Village` property in `McpWindowViewModel`; the **PROJECT VILLAGE** section (and the
`ScrollViewer`) in `McpWindow.xaml`; the `EmbeddedResource` and the `RevitMCP.Village`
`ProjectReference` in `RevitMCP.Addin.csproj`; the `RevitMCP.Village` `ProjectReference`, the
`VillageGraphReader.cs` link and `Village*Tests.cs` in the test project; and this document.
No tool, response format or configuration file format depends on the module.
