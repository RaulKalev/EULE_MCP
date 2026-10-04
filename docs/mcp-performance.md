# MCP performance and credit usage

MCP cost and latency come from three different places:

1. The tool catalog supplied to the model. A large catalog consumes input context even
   before a tool is called.
2. Tool results supplied back to the model. Verbose element and parameter DTOs consume
   output/context tokens.
3. Work performed inside Revit. Parameter reads must run on Revit's API thread and can
   dominate elapsed time on large categories.

The connector now addresses all three without changing the default tool surface or
response contracts.

## Reduced tool profiles

**The default profile is `core`** (since #65/#66): 17 tools — connection and instance routing,
selection, live reads by id, the five graph tools and tool discovery — about 4.2k schema tokens
instead of about 69k for all 233 tools. Every other tool stays reachable through discovery (below),
so nothing was removed. To advertise every tool, as before, start the bridge with
`--tool-profile full` (or set `RevitMCP:ToolProfile` to `full` in `appsettings.json`).

### Tool discovery (#65)

| Tool | Purpose |
|---|---|
| `revit_tools_search` | Search all tools by intent (English or Estonian, e.g. "circuit info", "ahela kaabel"); compact results: name, group, read-only, one-line summary, whether advertised. Without a query it lists the groups. |
| `revit_tools_describe` | Full description and JSON input schema of the named tools only. |
| `revit_tools_call` | Run any tool by name with its arguments, advertised or not. Behaves exactly like a direct call: write tools still go through the approval prompt in Revit. Works with every MCP client because it is an ordinary tool. |
| `revit_tools_load` | Add tools or whole groups to the session's advertised list. The server sends `notifications/tools/list_changed`; clients that honour it (e.g. Claude Code) then show the tools natively. Clients that do not keep using `revit_tools_call`. |

Typical flow: `revit_graph_route` / `revit_graph_query` to narrow ids → `revit_tools_search
"electrical circuit info"` → `revit_tools_describe ["revit_get_circuit_info"]` →
`revit_tools_call name="revit_get_circuit_info" arguments={"circuitId": 123}`.

Measured on the Tarvastu EN model: reaching a specialist tool through discovery on `core` costs about
4.6k tokens in total (schema + 2 calls) versus about 69k when every tool is advertised on `full`.

For query-oriented sessions, start the bridge with:

```toml
[mcp_servers.revit-mcp]
command = "C:\\path\\to\\RevitMCP.Bridge.exe"
args = ["--client", "Codex", "--tool-profile", "query"]
```

Available profiles:

| Profile | Purpose |
|---|---|
| `core` | **Default.** The graph-first core: connection/instance routing, selection, live reads by id, the five graph tools and tool discovery — 17 tools, ≈ 4.2k schema tokens |
| `full` | All tools; the compatibility profile for automations that call tools by name |
| `query` | Common connection, graph, model-query, selection, view/sheet, family-type, electrical and coordination discovery tools |
| `read-only` | Every tool marked read-only or preview-only |

### Tool groups

Every tool belongs to exactly one group (`RevitMCP.Bridge/BridgeToolGroups.cs`; a unit test fails when a
new tool has no group). `--tool-groups` adds whole groups to a profile:

```toml
args = ["--client", "Codex", "--tool-profile", "core", "--tool-groups", "electrical,devices"]
```

| Group | Contents |
|---|---|
| `core` | Connection, instance routing, selection and live reads by element id |
| `discovery` | Search, describe, load and call any connector tool |
| `graph` | Model graph: build, status, summary, query, route |
| `query` | Broad element queries, parameter filters, grouping, query presets, parameter QA |
| `edit` | Parameters, move/align/rotate/elevation, delete/duplicate/rename, family types, placement |
| `devices` | Room geometry, device codes, room-based device placement, device and fire-alarm audits |
| `electrical` | Circuits, panels, wires/cables, voltage drop, fire alarm circuits, patch panels |
| `ifc` | IFC links, IFC spaces to rooms, linked-model element queries |
| `views` | Views, sheets, schedules, title blocks, revisions, view templates |
| `tags` | Tags, dimensions, text notes, detail lines, view alignment |
| `coordination` | Clash detection, clash review, issue reports |
| `cad` | DWG/CAD imports, overrides and placement from CAD |
| `skills` | Skill builder and skill runs |
| `office` | Configuration, files, Excel, standards lookup, delivery checks |

`core` always includes `core`, `discovery` and `graph`; `all` adds every group (same tools as `full`).
The same values can be set in `appsettings.json` as `RevitMCP:ToolGroups`.

An exact allow-list gives the smallest possible catalog:

```toml
args = [
  "--client", "Codex",
  "--tool-names", "revit_get_connection_status,revit_count_elements,revit_get_elements_info"
]
```

The same values can be set as `RevitMCP:ToolProfile` and `RevitMCP:ToolNames` in
`appsettings.json`. Restart the MCP client after changing a profile because tool
discovery happens when the MCP process starts.

In a local MCP handshake against an earlier revision (re-measure with `RevitMCP.Benchmark --schema-only`):

| Catalog | Tools | `tools/list` JSON |
|---|---:|---:|
| `full` | 191 | 213,770 bytes |
| `query` | 32 | 26,247 bytes |
| two-tool exact allow-list | 2 | 1,144 bytes |

The query profile reduces the advertised schema payload by about 87%. Actual credit
savings depend on whether the MCP client caches tool definitions and how its model
provider bills cached input.

Reduced profiles only change what the bridge advertises. They do not remove or disable
add-in functionality; switching back to `full` restores the complete catalog.

## Compact element results

`revit_find_elements_by_parameter` and `revit_get_elements_info` accept
`compact=true`.

Full mode (the default) returns parameter metadata such as storage type, scope,
read-only state, shared-parameter GUID, parameter ID, and raw value. Compact mode keeps
element identity fields and returns parameters as simple name/value pairs. Use compact
mode for discovery, counting, and agent reasoning; use full mode when parameter metadata
is needed for a write or audit.

Also prefer:

- explicit `parameterNames` / `returnParameters`;
- `includeTypeParameters=false` unless type data is required;
- smaller `pageSize` values;
- `summaryOnly=true` before a broad detailed query.

## Filtering by type or family name

Revit's built-in `Type`, `Family` and `Family and Type` instance parameters are
ElementId parameters, so their raw value is a numeric id, and a type's own name is
not one of its readable parameters. Every tool built on the shared query engine
(`revit_find_elements_by_parameter`, `revit_get_elements_info`,
`revit_select_elements_by_query`, `revit_group_elements`, `revit_export_query_to_excel`,
the circuit and uncircuited-element tools) therefore resolves these filter names
from the element's type instead:

| `parameterName` | Compared value |
| --- | --- |
| `Type`, `Type Name` | type name (`ElementType.Name`) |
| `Family`, `Family Name` | family name (`ElementType.FamilyName`) |
| `Family and Type` | `Family: Type` |

For example `{"parameterName":"Type","operator":"contains","value":"WiFi"}`. The names
are matched as whole names (case and spacing ignored); `Type Mark`, `Type Comments` and
`Type Id` remain ordinary parameters, so use `Type Id` to match a numeric type id.
Identity-only filters need no parameter read at all.

## Grouping covers every match

`revit_group_by_parameter` and `revit_group_elements` collect every matching element
before grouping (`ElementQueryOptions.CollectAll`); page size never truncates the counts.
Only `limit` (group_elements, default 5000) and the hard scan safety cap
(`QueryLimits.MaxScanElements`) bound them, with a warning when hit. Both read only the
grouped parameters. `revit_group_by_parameter` returns `totalMatched`, `elementsGrouped`,
`matchedElements`, `notFoundElements` and the groups; with `includeElementIds=true` each
group lists at most `maxElementIdsPerGroup` ids (default 100) and flags
`elementIdsTruncated` — that id list is the only paged part.

## Automatic Revit-side optimization

The shared element query engine now separates filter parameters from response
parameters:

- elements outside the requested page are no longer materialized with all response
  parameters merely to calculate `totalMatched`;
- filtered scans materialize values only for parameters named by filters;
- full response parameters are read only for elements included in the returned page;
- type parameters remain materialized and cached once per type within each query.

Exact totals, paging metadata, filter semantics, safety limits, and the default full DTO
shape are unchanged.
