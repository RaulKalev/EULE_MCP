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

## Development notes

- Build: `dotnet build RevitMCP.slnx -c Release`; tests: `dotnet test RevitMCP.Tests/RevitMCP.Tests.csproj`.
- Token/round-trip benchmark: `RevitMCP.Benchmark` (see `docs/benchmarks/README.md`). Run it before and after
  changes to the tool surface or graph routing and commit the report.
- New Addin tools implement `IRevitMcpTool`, are registered in `App.OnStartup`, and get a matching
  `[McpServerTool]` method in `RevitMCP.Bridge/RevitMcpTools.cs`. Pure logic goes in a domain folder
  and is linked into `RevitMCP.Tests.csproj` so it can be unit-tested without Revit.
