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
| `--var name=value` | — | Override a scenario variable (`category`, `roomNumber`, `deviceCategory`, `linkedCategory`, `reducedProfile`) |
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

### Memory on vs off (`memory-hints`)

The scenario the shared-memory design asks for before memory hints are switched on by default
([shared-memory.md](../shared-memory.md), "Credit usage"). On a workstation with Revit:

1. Open a real project model whose project memory holds an entry for the convention you measure,
   e.g. remember "Detector tags use <type>" once with `revit_memory_remember`.
2. Set `"memory": { "hints": true }` in `%AppData%\RKTools\MCP\user.config.json` (the `memory-off`
   variant still opts out per call with `memoryHint=false`).
3. Run, naming the query and the tag category of the model:

```bash
dotnet RevitMCP.Benchmark/bin/Release/net8.0/RevitMCP.Benchmark.dll --label memory-on-off-<model> --profiles full \
  --var memoryQuery="detector tag type" --var tagCategory="Fire Alarm Device Tags"
```

`memory-off` pays for the live read an agent without memory makes to learn the convention;
`memory-hints` gets it from the route's memory block; `memory-recall` asks memory explicitly. The
scenario is skipped, with the reason, when memory has no matching entry. Record the report below;
if `memory-hints` is clearly cheaper, `memory.hints` can default to on.

## Results

| Report | Model | Notes |
|---|---|---|
| [20261003-000623-baseline-73226_PP_EN](20261003-000623-baseline-73226_PP_EN.md) | Tarvastu EN (13.7k elements, 40 test devices, no circuits) | Baseline before #64/#65/#66: `full` = 228 tools ≈ 68k schema tokens; graph-first cuts the category scenario from ≈ 145k to ≈ 5k result tokens; the `query` profile has no graph tools, so graph-first fails there. |
| [20261003-001954-tool-profiles-66](20261003-001954-tool-profiles-66.md) | Tarvastu EN | After #64 + #66: `core` = 13 tools ≈ 3.5k schema tokens (−95 % vs `full`); graph-first through `core` ≈ 8.5k tokens total for the category scenario vs ≈ 214k live on `full`. |
| [20261003-002552-tool-discovery-65](20261003-002552-tool-discovery-65.md) | Tarvastu EN | After #65: `core` (default) = 17 tools ≈ 4.2k schema tokens; a specialist tool via `revit_tools_search` + `revit_tools_call` ≈ 4.6k tokens total vs ≈ 69k advertised on `full`. |
| [20261003-005245-linked-62](20261003-005245-linked-62.md) | Tarvastu EN + 5 IFC links | After #61 + #62: the graph also indexes ≈ 16k linked elements (17.2k nodes, full build ≈ 1.2 s, incremental ≈ 80 ms); `linked-target` (linked ducts in a host room) ≈ 10k tokens graph-first on `core` vs ≈ 92k live on `full`, which also cannot filter by host room. |
| [20261023-213314-shared-memory-phase1-before](20261023-213314-shared-memory-phase1-before.md) → [20261023-214349-shared-memory-phase1](20261023-214349-shared-memory-phase1.md) | none (`--schema-only`) | Shared memory phase 1: `revit_memory_recall` and `revit_memory_remember` join `core` (19 → 21 tools, ≈ 5.3k → 6.0k schema tokens, instructions ≈ 456 → 535); `revit_memory_list` is in the `memory` group. Recall results are capped at 20 short entries. The hit-rate run on a real project (memory on vs off) is still to be recorded. |
| [20261023-220217-shared-memory-phase2](20261023-220217-shared-memory-phase2.md) | none (`--schema-only`) | Shared memory phase 2: `memoryHint` on `revit_graph_route`, company scope and `basedOn` on `revit_memory_remember` — `core` ≈ 6.0k → 6.1k schema tokens, instructions ≈ 535 → 563. Route hints are off by default (`memory.hints`) until the memory-on/off run on a real project is recorded. |
| [20261023-221627-shared-memory-phase3-4](20261023-221627-shared-memory-phase3-4.md) | none (`--schema-only`) | Shared memory phases 3 and 4: `revit_memory_page`, `revit_memory_forget` and `revit_memory_consolidate` join the `memory` group — `core` unchanged (21 tools ≈ 6.1k tokens), `full` 271 → 274 tools. |
| [20261024-065913-usage-reporting](20261024-065913-usage-reporting.md) | none (`--schema-only`) | Usage reporting: `revit_usage_report` joins the `office` group — `core` unchanged (21 tools ≈ 6.1k tokens), `full` 274 → 275 tools. |
| [20261024-103001-memory-on-off-IPI2519_PP_EN](20261024-103001-memory-on-off-IPI2519_PP_EN.md) | Raemõisa põhikool EN PP (55.1k elements, 633 security device tags, no fire-alarm devices) | Memory on vs off at 1eb64c8, measured on camera tags (`memoryQuery="camera tag type"`, `tagCategory="Security Device Tags"`, one personal entry): result tokens `memory-off` ≈ 8.0k (2 calls, 100 tags read live) → `memory-hints` ≈ 0.6k (1 call) → `memory-recall` ≈ 0.6k (2 calls). The memory block adds ≈ 150 tokens to the route. `full` schema unchanged (275 tools ≈ 90.9k). `category-inspect` live fails here: the whole-model read exceeds the 1 MB response cap. Recall matching then turned out too loose (the entry also matched "detector tag type" and "renumber the circuits on the panel"), fixed in #113. |
| [20261024-142736-memory-on-off-IPI2519_PP_EN-recall-fix](20261024-142736-memory-on-off-IPI2519_PP_EN-recall-fix.md) | Raemõisa põhikool EN PP | Same run at 1d8ddaf, after #113 (word matching, filler words, minimum share, generic words): identical numbers (`memory-off` ≈ 8.0k → `memory-hints` ≈ 0.6k → `memory-recall` ≈ 0.6k result tokens). Checked live with hints on: the route carries the entry for "camera tag type" and returns `memory: null` for "detector tag type", "renumber the circuits on the panel" and "list sheets and their revisions"; recall now finds "kaamerate sildid". |

A large model run is still to be recorded: open one (e.g. a full EN model with circuits) and run
the benchmark with `--label <model>`; the panel and loop scenarios then also produce numbers.
