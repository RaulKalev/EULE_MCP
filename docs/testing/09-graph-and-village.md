# Smoke tests — Model graph and Project Village

Part of the [smoke test checklist](../../TESTING.md).

## 51. Model Graph (routing layer)

Requires any project model, ideally workshared with a few panels, circuits, rooms and tagged
elements. No approval is involved: the tools are read-only for Revit and only write the graph file.
See `docs/model-graph.md` and `RevitMCP.Addin/Graph/SCHEMA.md`.

### 51.1 Status before a build

**Prompt:**
```
Check the model graph status.
```

**Verify:**
1. `revit_graph_status` returns `success: true`, `exists: false`, `stale: true` and a `stale_reason` telling you to run `revit_graph_build`.
2. `databasePath` is `<root>\<project number>\<model name>.graph.db`; `rootSource` names the local fallback when `graph.sharedFolder` is unset.
3. `currentVersion` and `versionSource` are populated (`central:…` for file-based worksharing, `saves:…` otherwise); `currentElementCount` > 0.
4. `note` reads "Graph values are for routing only; fetch live data by ID for actual values."

### 51.2 Full build

**Prompt:**
```
Build the model graph.
```

**Verify:**
1. `revit_graph_build` reports `nodeCount`, `edgeCount`, `nodesByKind`, `edgesByRel` and `timing` (`extractMs`, `writeMs`, `publishMs`).
2. The file exists at `databasePath`; no `.tmp-*`, `-journal` or `-wal` file remains next to it.
3. `incremental.applied` is `false`; calling again with `incremental=true` adds a warning saying it fell back to a full rebuild.
4. Revit stays responsive afterwards and the model is unchanged (no transaction, undo stack untouched).
5. `revit_graph_status` now reports `exists: true`, `stale: false`.

### 51.3 Shared folder

**Prompt:**
```
Set graph.sharedFolder in my user config to \\server\bim\graphs, then rebuild the graph and check its status.
```

**Verify:**
1. `config_update scope=user updates={"$.graph.sharedFolder": "..."}` is approval-gated as usual.
2. `revit_graph_build` writes to `<shared>\<project>\<model>.graph.db` and `rootSource` says "user config".
3. `revit_graph_status` reports `usedLocalCache: true` and `readFrom` under `%LOCALAPPDATA%\RKTools\RevitMCP\Graph\cache`.
4. Deleting the cache folder and re-running status re-copies the file; the shared file is never locked (it can be renamed while Revit is open).
5. `sharedFolder="C:\Temp\graphs"` as a tool argument overrides the config for that call.

### 51.4 Queries

**Prompts:**
```
Find panels in the graph whose name contains "JK".
Show everything fed by panel <id> (subtree, fed_by, depth 2).
Show the neighbours of element <id>.
Find the path from element <id> to panel <id>.
Find lighting fixtures on level "2. korrus", page 0, page size 50.
```

**Verify:**
1. `find` returns ids + names only, paginated (`itemsReturned`, `totalAvailable`, `hasMore`, `nextPage`); `pageSize` above 500 is clamped.
2. `subtree` from a panel lists its circuits at depth 1 and their elements at depth 2 with `parentId`; `direction=out` from an element walks element → circuit → panel.
3. `neighbors` reports `rel` and `direction` for every edge; `rel=located_in` returns the room/space.
4. `path` returns `hops` and steps with `rel`/`direction`; `maxHops=1` between element and panel reports `found: false`.
5. An unknown `operation`, `rel` or `kind` returns `status: validation_failed` with the valid values listed.
6. Every response carries `built_at`, `central_version`, `stale`, `stale_reason`, `note`.
7. Spot-check three returned ids with `revit_get_elements_info`; the live category/level match.

### 51.5 Summary

**Prompt:**
```
Summarise the model graph.
```

**Verify:**
1. `nodesByKind`, `edgesByRel`, `elementsByCategory`, `elementsByLevel`, `elementsByWorkset` and `panels` (circuit and fed-element counts) are present.
2. `orphanCircuits.withoutPanel` lists circuits with no panel; `withoutElements` lists empty circuits; counts match `revit_check_circuit_health`.
3. `elementsWithoutLocation.count` is plausible (elements outside any room/space).

### 51.6 Staleness

**Steps:**
1. Build the graph, then place or delete one element (do not save). `revit_graph_status` → `stale: true`, reason mentions the element count.
2. Undo, then Save / Sync with Central. `revit_graph_status` → `stale: true`, reason mentions the version signal (`saves:` or `central:`).
3. Rebuild → `stale: false`.
4. `revit_graph_query`/`revit_graph_summary` on a stale graph still answer, add a warning, and set `stale: true`.

### 51.7 Multiple Revit instances / other model

1. Open a different model and run `revit_graph_status`: it resolves a different file and reports `exists: false` (or its own graph).
2. Rename the model file, open it, run status: `stale_reason` says the graph was built for a different model.

### Matrix rows (Section 30)

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Graph | `revit_graph_build` | RO | "Build the model graph." | `{databasePath, nodeCount, edgeCount, nodesByKind, edgesByRel, timing}` | N/A | N/A | Writes only the graph file; `incremental` reported as not implemented |
| Graph | `revit_graph_status` | RO | "Check the model graph status." | `{exists, databasePath, built_at, central_version, stale, stale_reason, note}` | N/A | N/A | Reports which config source chose the root |
| Graph | `revit_graph_query` | RO | "Show everything fed by panel 123456." | `{operation, …, built_at, central_version, stale, note}` | N/A | N/A | neighbors / find / path / subtree; hard caps on result size |
| Graph | `revit_graph_summary` | RO | "Summarise the model graph." | `{nodesByKind, edgesByRel, panels, orphanCircuits, elementsWithoutLocation, …}` | N/A | N/A | Cheap orientation at session start |

## 52. Project Village (read-only activity visualizer)

Opt-in localhost page that shows connector activity as a village. It never sends commands to
Revit or MCP. See `docs/project-village.md`. Enable it in the connector window (Status tab →
Project Village → Enable Village) or with `config_update scope=user updates={"$.village.enabled": true}`.

### 52.1 Viewer absent

1. Keep the village disabled. Run any ten tools. **Verify:** responses and timings are unchanged; `%LOCALAPPDATA%\RevitMCP_startup.log` has no `[VILLAGE` errors.
2. Enable the village but open no browser. Run tools. **Verify:** no change in tool behaviour; the Status tab shows "0 viewers connected".

### 52.2 Viewer connected

1. Click *Open Village*. **Verify:** header shows the document title, `read-only`, `connected`, `live`; one agent per MCP client name.
2. Run `revit_list_sheets`. **Verify:** the agent walks to the Archive and the feed shows "Inspected sheets".
3. Click the Archive. **Verify:** counters (reads 1) and, with a graph, "Graph: sheets".

### 52.3 Viewer disconnects during a tool call

Start a long tool (e.g. `revit_graph_build`), close the browser tab mid-run. **Verify:** the tool completes normally; reopening the page shows the completed step in the feed.

### 52.4 Rapid tool calls

Ask the agent to read parameters of 100+ elements one by one. **Verify:** the feed shows one merged step ("Inspected model elements (N tools)"); the agent stays in place; a `catch-up` badge may appear and clears; Revit stays responsive.

### 52.5 Failed MCP tool

Call a write tool with an invalid id. **Verify:** red "… failed in …" step, agent shows `!`, warning area pulses, Diagnostics counts the failure; nothing else changes.

### 52.6 Approval flow

Run `revit_place_tags` with approval required, approve later. **Verify:** "Awaiting approval" (amber) then "24 tags created" on approval.

### 52.7 Project switch

Open a second model. **Verify:** "Switched to <title>" step, counters reset, the graph badge changes to the new model's status, colours/pattern differ from the first model.

### 52.8 Graph rebuild, stale and no graph

1. `revit_graph_build` → badge `fresh`, sizes update, "Graph rebuilt (N nodes)".
2. Edit an element unsaved, `revit_graph_status` → badge `stale` with the reason in the tooltip.
3. Model without a graph → badge `graph: none (limited mode)`, neutral theme, default sizes.
4. Write tools after a fresh report → `possibly stale`.

### 52.9 Workshared and non-workshared models

Repeat 52.2 on both. **Verify:** the graph is found in the shared folder for the workshared model (Diagnostics: "via local cache copy"); the shared file can be renamed while the page is open.

### 52.10 Connector and Revit shutdown

1. Stop the connector → the page shows `reconnecting` and retries with backoff; start again → `connected`.
2. Close Revit → the page keeps animating idle and shows `reconnecting`; no error dialogs in Revit.

### 52.11 No reverse channel

From a browser console on the page run `fetch('/snapshot', {method: 'POST', body: 'revit_delete'})`. **Verify:** HTTP 405; nothing happens in Revit.

### Matrix rows (Section 30)

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Village | (none — passive observer) | RO | "List sheets." with the page open | Agent walks to the Archive; feed "Inspected sheets" | N/A | N/A | No tool, no tokens; page is GET-only on 127.0.0.1 |
