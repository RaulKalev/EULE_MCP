# Usage reporting (design)

> **Status: design proposal, nothing implemented yet.** This document plans company-wide usage
> reporting on top of the activity log the connector already writes. It is a companion to
> [shared-memory.md](shared-memory.md); the two share the same collection path and the same
> no-credits principle.

The goal is to answer, per user, project and month: who uses the connector, how much, which tools
the most, with what outcome, what it costs in credits, and — clearly labelled as an estimate — how
much manual Revit time it theoretically saved. All of it is produced by code, so it costs no credits.

---

## What is logged today

`RevitMCP.Addin/Logging/ActivityLogger.cs` appends one JSON line per completed tool call to
`%AppData%\RKTools\RevitMCP\Logs\<yyyy-MM-dd>.jsonl` on the user's machine:

| Field | Content |
|---|---|
| `timestamp`, `user`, `machine`, `client` | When, Windows user, machine, MCP client (Claude Code, Codex, …) |
| `revitVersion`, `model`, `centralPath`, `activeView`, `isWorkshared`, `revitUsername` | Document context |
| `tool`, `permission`, `status`, `durationMs`, `responseSizeBytes` | What ran, read-only or write, success or failure, how long, how big the response |
| `message`, `warnings`, `errors` | Outcome text |
| `querySafety` | Candidate and returned counts, truncation, summary mode for large reads |

The Project Village additionally computes an **affected count** for writes from an allow-list of
result fields (`placedCount`, `updatedCount`, `createdCount`, `deletedCount`, …) in
`VillageEventFactory`, but it is not written to the log.

So "who used which tool, how often, on which project, with what outcome" already sits on every
workstation. It is only not collected anywhere and not aggregated.

---

## Additions

1. **Shared copy.** With `usage.sharedFolder` set (company config, user override allowed), the
   logger appends the same line a second time to
   `<sharedFolder>\<user>\<yyyy-MM-dd>.jsonl`. One file per user per day, append-only: no write
   conflicts, no sync step. A log line is a few hundred bytes; a busy user stays well under a
   megabyte a month. Writes to the share are best-effort — a failure never affects the tool call
   and is retried from the local file by a catch-up on the next start.
2. **Affected count on every entry.** The count the village already computes is written to the
   log as `affectedCount`, so a report can say "placed 1,240 tags" rather than "called
   `revit_place_tags` 51 times".
3. **Approval outcome.** `status` already carries `approval_required`, rejected and expired codes;
   the report counts them so rejected writes per session become a trend line.
4. **Project id.** Resolved the same way skills resolve it (project config, then the model name
   pattern), written as `projectId` so the report groups by project without parsing paths.

Nothing new is read from arguments or results beyond the existing allow-lists; the privacy
guarantees of the village apply unchanged.

---

## Monthly report

`revit_usage_report` `month= sharedFolder?= format=html|xlsx` — a deterministic aggregation over the
shared files, run by whoever holds the report role, once a month. No agent is needed to produce it;
the tool is exposed so it can be run from any client, and the same code runs from the connector
window.

Groupings: user, project, client, tool, permission (read / write), outcome.

Metrics per group:

| Metric | From |
|---|---|
| Calls, success rate, failures | `status` |
| Writes, approved, rejected, expired | `permission`, `status` |
| Elements affected | `affectedCount` |
| Active days, sessions, first and last use | `timestamp`, session gaps |
| Response volume | `responseSizeBytes` |
| Truncated or summarised reads | `querySafety` |
| Time saved (estimate) | see below |

Output: the HTML dashboard uses the model-page styling ([model-pages.md](model-pages.md)) — one
plain table per grouping, so search, filters, sorting and grouping come for free — and the existing
Excel exporter writes the same tables as a workbook. Reports are written to
`<sharedFolder>\reports\<yyyy-MM>.html|xlsx`.

---

## Credit costs

The connector never sees token counts or money; it sees tool calls and response sizes. Actual spend
lives on the client side:

- Claude Code: the provider's usage and cost reports per user or API key.
- Codex and Antigravity: their own usage exports.

*Decision:* the report keeps two columns joined by user and month — activity from the connector,
spend from the provider export placed in `<sharedFolder>\costs\<yyyy-MM>.csv` (columns `user`,
`client`, `cost`, `inputTokens`, `outputTokens`). When no export is present, `responseSizeBytes` is
shown as a proxy for the output half of the token cost, labelled as such.

---

## Theoretical time saved

Feasible, and labelled an estimate everywhere it appears.

A baseline table in the company config gives each tool a manual-time figure per affected element
(or per call for tools without a count):

```json
{
  "usage": {
    "timeSavedBaselines": {
      "revit_place_tags":            { "secondsPerElement": 20 },
      "revit_apply_sheet_numbers":   { "secondsPerElement": 45 },
      "revit_set_parameter_values":  { "secondsPerElement": 15 },
      "revit_run_clash_preset":      { "secondsPerCall": 900 },
      "revit_export_issues_html_dashboard": { "secondsPerCall": 1200 }
    }
  }
}
```

Per call: `saved = baseline × affectedCount (or 1) − durationMs/1000`, floored at zero and counted
only for successful calls. The report sums it per user and project and prints the baseline table
beside the totals, so nobody mistakes the figure for measured time. The office argues about the
numbers in one place.

---

## Privacy and consent

The log carries Windows user names and Revit user names, and a shared copy makes individual usage
visible to the company. That is an ordinary employee-monitoring question under Estonian and EU
rules: staff are told what is collected, where it is stored, who can see it and why, before
`usage.sharedFolder` is set. Technically it is one config key; the policy is the real step.

The shared copy contains no prompts, AI responses, tool arguments, tool results, parameter values
or file contents — only what the local log already holds. Reports show aggregates; the raw files are
readable only by the report role.

---

## Credit usage

| Item | Credits |
|---|---|
| Logging, shared copy, catch-up | None |
| Monthly report | None (deterministic code) |
| Time-saved estimate | None |
| Asking an agent to comment on a report | One session reading the aggregated HTML, not the raw log |

---

## Components

| Component | Location | Responsibility |
|---|---|---|
| `LogEntry` additions | `RevitMCP.Core/Logging/` | `affectedCount`, `projectId` |
| `ActivityLogger` | `RevitMCP.Addin/Logging/` | Second append to the shared folder, best-effort, catch-up on start |
| `UsageAggregator` | `RevitMCP.Addin/Usage/` | Pure aggregation over `.jsonl` files; linked into the tests |
| `TimeSavedEstimator` | `RevitMCP.Addin/Usage/` | Baseline table, per-call formula |
| `UsageReportWriter` | `RevitMCP.Addin/Usage/` | HTML (model-page styling) and Excel via the existing exporters |
| `revit_usage_report` | `RevitMCP.Addin/Tools/Usage/` + `RevitMCP.Bridge/RevitMcpTools.cs` | The tool |

Configuration keys (`usage.*`): `sharedFolder`, `reportFolder`, `timeSavedBaselines`,
`costExportFolder`.

---

## Phases

1. `affectedCount` and `projectId` in the log; `usage.sharedFolder` with the second append and the
   catch-up. Consent note in the deployment guide.
2. `UsageAggregator`, `UsageReportWriter`, `revit_usage_report` with HTML and Excel output.
3. Cost export join and the time-saved estimate with the baseline table.
