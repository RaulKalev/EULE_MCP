# MCP benchmark

`RevitMCP.Benchmark` measures what an agent actually pays for when it uses the connector
(issue #67), so graph-first routing and tool-surface changes can be compared and regressions
caught.

It starts the real bridge (`RevitMCP.Bridge.exe`) over stdio with the official MCP client, the
same way Claude Code or Codex do — one bridge process per tool profile — and measures:

| Metric | Meaning |
|---|---|
| Tool-schema bytes / ≈ tokens | The `tools/list` payload of a profile (name, description, input schema of every tool). Paid once per session, or per request when the client does not cache tool definitions. |
| Result bytes / ≈ tokens | The text of every tool result in a workflow. Paid per call. |
| Calls | Tool calls in the workflow. |
| Live elements | Element records (`ElementId`) returned by live Revit tools — graph results are routing data and count as zero. |
| Elapsed | Wall time of the workflow's calls (bridge + Revit). |
| Graph build | Full and incremental `revit_graph_build` time, nodes, edges, model element count. |

Token figures are `characters / 4` estimates; byte counts are exact. Schema and result costs are
reported separately and summed per variant as *Total*.

## Running it

Revit must be running with the connector started and a model open.

```bash
dotnet build RevitMCP.slnx -c Release
dotnet RevitMCP.Benchmark/bin/Release/net8.0/RevitMCP.Benchmark.dll --label <name>
```

| Option | Default | |
|---|---|---|
| `--bridge <exe>` | `RevitMCP.Bridge/bin/Release/net8.0/RevitMCP.Bridge.exe` | Bridge build to measure |
| `--profiles a,b` | `full,core,query,read-only` | Profiles whose schema is measured and whose variants run |
| `--scenarios <file>` | `scenarios.json` next to the exe | Scenario definitions |
| `--var name=value` | — | Override a scenario variable (`category`, `roomNumber`, `deviceCategory`, `reducedProfile`) |
| `--schema-only` | off | Only measure tool-schema sizes (no Revit needed) |
| `--no-graph-build` | off | Skip the graph build measurement |
| `--out <dir>` | `docs/benchmarks` | Where `<timestamp>-<label>.md` and `.json` are written |
| `--dump-tools <file>` | — | Write every advertised tool (name, description, input schema) of `--profile` (default `full`) to a JSON file and exit |
| `--calls <file> --results <file>` | — | Smoke-suite mode: run `[{"id","tool","args"}]` in order through the `full` profile, print ok/FAIL per call and write the results (status, error, result excerpt) as JSON |

The process exits with 1 when any measured call failed, so it can gate a CI or pre-merge check.

## Scenarios

Defined in [`RevitMCP.Benchmark/scenarios.json`](../../RevitMCP.Benchmark/scenarios.json). Each
scenario has unmeasured *setup* steps that pick ids from the model (a room, a panel, a level) and
measured *variants*:

- **live** — broad live Revit queries, as an agent without the graph would do;
- **graph** — graph-first discovery, then a narrow live read by id;
- **graph-reduced** — the same graph-first calls through a reduced tool profile (only the schema
  cost differs; a missing tool shows up as a failure).

A scenario whose setup cannot resolve (no electrical panels, no devices of the category) is
reported as *skipped* with the reason instead of producing misleading numbers. Steps use
`{{variable}}` placeholders and bind values from results with a small JSON path
(`$.data.items[:10].id`, `[*]` for all).

## Results

| Report | Model | Notes |
|---|---|---|
| [20261003-000623-baseline-73226_PP_EN](20261003-000623-baseline-73226_PP_EN.md) | Tarvastu EN (13.7k elements, 40 test devices, no circuits) | Baseline before #64/#65/#66: `full` = 228 tools ≈ 68k schema tokens; graph-first cuts the category scenario from ≈ 145k to ≈ 5k result tokens; the `query` profile has no graph tools, so graph-first fails there. |
| [20261003-001954-tool-profiles-66](20261003-001954-tool-profiles-66.md) | Tarvastu EN | After #64 + #66: `core` = 13 tools ≈ 3.5k schema tokens (−95 % vs `full`); graph-first through `core` ≈ 8.5k tokens total for the category scenario vs ≈ 214k live on `full`. |
| [20261003-002552-tool-discovery-65](20261003-002552-tool-discovery-65.md) | Tarvastu EN | After #65: `core` (default) = 17 tools ≈ 4.2k schema tokens; a specialist tool via `revit_tools_search` + `revit_tools_call` ≈ 4.6k tokens total vs ≈ 69k advertised on `full`. |

A large model run is still to be recorded: open one (e.g. a full EN model with circuits) and run
the benchmark with `--label <model>`; the panel and loop scenarios then also produce numbers.
