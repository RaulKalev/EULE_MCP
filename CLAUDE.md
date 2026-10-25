# EULE MCP — guidance for AI agents

## Model graph: find ids first, then read live values

The connector ships a per-model routing graph (`revit_graph_*` tools, see
[`docs/model-graph.md`](docs/model-graph.md)). Use it to locate element ids cheaply before
querying Revit:

0. `revit_graph_route intent="<what you need>"` returns the cheapest call plan (graph steps, then live
   reads by id) together with the graph's freshness — the quickest way in.
1. Call `revit_graph_status` at the start of a session. If `exists` is false or `stale` is true,
   run `revit_graph_build` (a full rebuild takes seconds) or treat every graph result as a hint
   that must be re-verified against the live model.
2. Use `revit_graph_summary` to orient yourself (panels, levels, categories, orphan circuits).
3. Use `revit_graph_query` (`find`, `subtree`, `neighbors`, `path`) to narrow down the element ids
   you need — for example `subtree id=<panelId> rel=fed_by` for everything a panel feeds.
4. Fetch actual values by id with the live tools (`revit_get_elements_info`,
   `revit_get_element_parameters`, `revit_get_circuit_info`, …).

Rules:

- **Never quote graph values as facts.** Node names, levels, worksets and `extra` hints are routing
  data captured at build time. Every graph response says so in its `note` field.
- **Check `stale`.** When it is `true`, read `stale_reason`, rebuild if appropriate, and re-verify
  any id you act on against the live model before writing.
- The graph never replaces approval-gated write tools; it only tells you where to look.

## Tool surface

The bridge advertises the compact `core` profile by default (graph tools, live reads by id, selection,
connection). Find any other tool with `revit_tools_search`, read its schema with `revit_tools_describe`,
and run it with `revit_tools_call` (or `revit_tools_load` it into the session). `--tool-profile full`
advertises every tool. See `docs/mcp-performance.md`.

## Verify your writes

Before a batch of writes, `revit_checkpoint_create` with the ids or categories you will touch (`autonomy` group, see
[`docs/checkpoints.md`](docs/checkpoints.md)). After it, `revit_checkpoint_diff` lists what changed and who changed it
(`agent:<tool>`, `page`, `user`) with before/now values, and `revit_get_warnings sinceCheckpointId=…` shows what Revit
now complains about. Publish long lists with `revit_publish_page`. `revit_checkpoint_rollback` undoes the agent's changes
(manual approval; it never recreates deleted elements, and skips what the user changed last unless `force=true`).

## Several write steps: submit a plan

For more than a couple of write calls, `revit_plan_submit` once (`autonomy` group, see [`docs/plans.md`](docs/plans.md))
with the tools you will call and the element ids or categories you will touch, instead of one approval per call. After
the user approves it, the bridge attaches the `planId` to this session's writes and calls inside the envelope run without
prompts. A call it does not cover waits for its own approval (the reason is in `warnings`); one whose changes break it is
rolled back with `plan_envelope_violated` — narrow the call or submit a new plan, do not retry it unchanged. Deletes are
never covered. Review with `revit_checkpoint_diff checkpointId=<the plan's checkpoint>`; end with `revit_plan_close`.

## Unattended work: submit a job

When the user need not watch the steps, plan the work with the graph and live reads, then hand it over as one
`revit_job_submit` (see [`docs/jobs.md`](docs/jobs.md)): steps with `bind` paths and `{{var}}` placeholders, an optional
envelope (tools default to the steps' write tools) and stop conditions. One approval starts it; the connector runs the
steps without you. Follow it with `revit_job_status waitSeconds=25` (one call per change, not a loop of quick polls). A
paused or suspended job continues with `revit_job_resume` (approval). The report page it publishes is what the user
reviews — point them to it instead of repeating it.

## Several open projects

`revit_list_open_documents` lists the open project documents and their DWG/IFC/RVT links. The `documents` group
(`revit_reload_links_from`, `revit_remove_links`, `revit_set_link_visibility`, `revit_save_document`,
`revit_sync_with_central`, each with a preview where it changes the model) takes `document` (title or path) and works
on non-active documents; approvals bind to that target document. Other tools still act on the active document —
switch with `revit_activate_document` when needed.

## PanelSchematics plugin data

The `panelschematics` group (`revit_panelschematics_*`, see [`docs/panelschematics.md`](docs/panelschematics.md))
reads and writes what the PanelSchematics plugin stores: panel tables, empty rows, the parameters a row is written
to, and its Extensible Storage stores. Call `revit_panelschematics_guide` first. Every write has a preview, needs
approval and is refused while the plugin's table window is open. The storage formats live in
`RevitMCP.Addin/PanelSchematics/PanelSchematicsContract.cs` and must follow the plugin's source.

## Shared memory

`revit_memory_recall` and `revit_memory_remember` (see [`docs/shared-memory.md`](docs/shared-memory.md)) hold the
project's durable decisions and corrections next to the model (`.rktools\memory`) and the user's personal notes. Recall
before a write; remember at task end one user correction, decision with its reason, or exception — never a session
summary or anything readable from the model. Entries are hints like graph data: verify live. A project note needs
approval in Revit; a personal one does not; `scope=company` only writes a proposal for an administrator. Pass `basedOn`
with the ids of entries you relied on. `revit_graph_route` carries matching entries in a `memory` block when
`memory.hints` is on (the default), and the workflow observer learns project defaults, recipes and corrections from tool calls by itself.
When the user wants to review memory, publish `revit_memory_page`; `revit_memory_forget` retires wrong entries (approval).
The weekly consolidation pass is the `memory-consolidate` prompt / `revit_memory_consolidate` (no Revit needed): it writes
proposals only, never entries.

## Usage reporting

`revit_usage_report` (`office` group, see [`docs/usage-reporting.md`](docs/usage-reporting.md)) builds the monthly report
from the shared usage folder: deterministic, no credits. Present its figures as aggregates, and time saved always as an
estimate from the company baseline table — never as measured time. The shared copy is off until `usage.sharedFolder` is
set, which needs staff to be told first (INSTALL.md).

## Model pages

When the user gets a list or report of model elements to go through, publish it with `revit_publish_page`
(`pages` group, see [`docs/model-pages.md`](docs/model-pages.md)) instead of only printing it. Mark rows with
`data-revit-id` and value cells with `data-revit-param`; the served page then selects elements in Revit, shows live
values and lets the user edit them without the agent (a write needs approval in Revit, like an agent's). Write plain
semantic HTML and one plain table per element list: a default EULE-style theme with a black dark mode is applied, and
such a table gets search, filters, sorting and grouping by itself. The HTML must be self-contained (no CDN).

## Development notes

- Build: `dotnet build RevitMCP.slnx -c Release`; tests: `dotnet test RevitMCP.Tests/RevitMCP.Tests.csproj`.
- Token/round-trip benchmark: `RevitMCP.Benchmark` (see `docs/benchmarks/README.md`). Run it before and after
  changes to the tool surface or graph routing and commit the report.
- New Addin tools implement `IRevitMcpTool`, are registered in `App.OnStartup`, and get a matching
  `[McpServerTool]` method in `RevitMCP.Bridge/RevitMcpTools.cs`. Pure logic goes in a domain folder
  and is linked into `RevitMCP.Tests.csproj` so it can be unit-tested without Revit.
