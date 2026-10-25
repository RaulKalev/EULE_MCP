# Jobs: unattended work

Issue #100, part 3 of the autonomy series (#98 [checkpoints](checkpoints.md), #99 [plans](plans.md)). A **job** is a
whole task — an ordered list of tool calls with simple result binding — that the connector runs on its own after one
approval, while the user keeps working in Revit. The agent spends its tokens on planning (reading the model through
the graph, choosing ids, writing the steps); the connector spends none on executing. The user comes back to a report
page that says what happened.

| Tool | Permission | Purpose |
|---|---|---|
| `revit_job_submit` | approval | Submits steps, envelope and stop conditions; the approval activates the plan and starts the job |
| `revit_job_status` | read-only | Progress, per-step results, counters; `waitSeconds` (≤ 25) long-polls until something changes |
| `revit_job_list` | read-only | Jobs of this session and journaled earlier ones; `removeJobId` |
| `revit_job_cancel` | read-only | Stops after the running step; nothing is undone |
| `revit_job_resume` | approval | Resumes a paused, suspended or failed job from its first unfinished step |
| `revit_job_report` | read-only | Publishes (or re-publishes) the handoff page |

All six are in the `autonomy` tool group.

## Example

```json
{
  "title": "Mark and tag the sockets on level 2",
  "steps": [
    { "tool": "revit_graph_query", "arguments": { "op": "find", "category": "Electrical Fixtures", "level": "L2" },
      "bind": { "ids": "$.data.items[*].id" } },
    { "tool": "revit_set_parameter", "title": "Set Mark",
      "arguments": { "elementIds": "{{ids}}", "parameterName": "Mark", "value": "S" } },
    { "tool": "revit_place_tags", "arguments": { "elementIds": "{{ids}}", "viewId": 4711 }, "continueOnError": true },
    { "tool": "revit_get_elements_info", "arguments": { "elementIds": "{{ids}}", "compact": true } }
  ],
  "envelope": { "categories": ["Electrical Fixtures"], "viewIds": [4711], "maxElementsTotal": 1000 },
  "stop": { "stopOnNewWarnings": "error" }
}
```

Then `revit_job_status jobId=job-… waitSeconds=25` until it is `completed` (or stops), and point the user at the
report url.

## Steps and binding

A step is `{tool, arguments, bind, title, continueOnError, optional}`.

- `tool` — any registered tool except `revit_job_*` and `revit_plan_*`. A destructive tool needs
  `allowDestructiveSteps=true` on the submit and still waits for a manual approval when it runs.
- `bind` — variable → JSON path into the step's result `{success, status, message, data, warnings}`. Supported:
  `.name` (case-insensitive), `[n]`, `[*]`, `[:n]` and the equality filter `[?(@.key=='value')]` (also numbers and
  `true`/`false`). A path through `[*]`, `[:n]` or a filter yields an array; string ids become numbers.
- `"{{var}}"` as a whole string value is replaced by the bound value (an array stays an array); inside a longer
  string it is written as text. A placeholder must be bound by an earlier step (checked at submit). A binding that
  finds nothing leaves the variable unbound, and the step using it fails with `job_step_invalid` before it is sent.
- `continueOnError` — a failure does not stop the job (the step shows "continued" and is not repeated on resume).
- `optional` — a refusal (`validation_failed`) skips the step.

At most 200 steps.

## Flow

1. **Submit.** Validated at once (unknown tools, destructive steps, unbound placeholders, bad paths) — an invalid job
   is refused without an approval. The Pending tab shows the job line, its steps and stop conditions, then the plan
   summary. The envelope's `tools` default to the job's approval-gated write tools; its `expiresMinutes` to the job's
   `maxDurationMinutes`. In Direct Edit the job starts immediately.
2. **Approve.** One approval covers plan and job: the plan (#99) is activated with the envelope, its checkpoint (#98)
   created, the journal written, and the first step dispatched.
3. **Run loop** (`JobService`, off the Revit API thread):
   - each step is an internal request through `ExternalEventService.DispatchAsync` with client `Job <id>` and the
     job's `planId` on write steps — the same path as any client, so plan authorization, approvals, the activity log,
     graph freshness and `revit_busy` all apply. The runner never calls a tool directly or touches the Revit API;
   - **one step per dispatch**: between steps the queue is empty, so the user's own commands and other MCP clients
     interleave;
   - `approval_required` (a step outside the envelope, or destructive): the job shows `waitingApproval`, the Pending
     tab shows the step labelled with the job's title, and the job continues with the user's decision (a rejection
     fails the step);
   - `revit_busy`: retried after 5 s, 10 s, 20 s, 40 s, then every 60 s, up to `maxConsecutiveBusy`, then
     `suspended`;
   - `plan_envelope_violated`: the call was already rolled back by the plan; the step fails;
   - after every write step: `revit_get_warnings sinceCheckpointId=<job checkpoint>`; a new warning at or above
     `stopOnNewWarnings` (beyond `maxNewWarnings`) pauses the job with the warning text as the reason;
   - `maxDurationMinutes` of running time → `paused`; the plan expired or was revoked → `suspended`;
   - last step done → `completed`; the plan is closed.
4. **Status.** `revit_job_status waitSeconds=25` blocks until the job changes, then answers: state, `stopReason`,
   progress, compact steps, new warnings, elapsed time, report url; `include=results` adds the stored results of the
   last steps. It does not take a background slot while it waits.
5. **Report.** With `report=auto` (default) the handoff page is published when the job completes, fails, pauses,
   suspends or is cancelled — and opened the first time when `openReport` (default). See below.
6. **Resume.** A paused job whose plan is still active continues at the next step with the same plan and checkpoint.
   A suspended or failed one (plan gone, Revit restarted) is re-bound to its model (open it first), gets a new plan
   with the stored envelope and a fresh checkpoint, and continues from the first step that is not done; variables
   bound by finished steps come from the journal. Either way the warnings that made it pause are accepted.
7. **Cancel.** After the running step (a step waiting for approval stops at once, its approval rejected). Nothing is
   undone; the plan is revoked; the report published. **Panic Stop** cancels every job.

## Stop conditions

| Field | Default | |
|---|---|---|
| `stopOnFailure` | true | Overridden per step by `continueOnError` |
| `stopOnNewWarnings` | `error` | `none` \| `error` (errors only) \| `any` |
| `maxNewWarnings` | 0 | Tolerated new warnings, counted from the last resume |
| `maxDurationMinutes` | 120 | Running time (pauses do not count), 1–480 |
| `maxConsecutiveBusy` | 20 | `revit_busy` retries of one step, 1–100 |

## Journal

`%LOCALAPPDATA%\RevitMCP\Jobs\<modelId>\<jobId>.json`: the whole job with per-step results (each result's `data`
trimmed to 64 KB) and the bound variables, rewritten after every change off the API thread. On startup every job that
was running, waiting or paused is marked `suspended` with "Revit was restarted"; `revit_job_list` shows it, and
nothing resumes without `revit_job_resume`. At most 50 journals per model; the oldest finished ones go first.

## The report page

`JobReportHtml` builds one self-contained model page ([model-pages.md](model-pages.md)) — deterministic, no AI, no
external URLs, `<html lang="et">` like the team's other pages — under the page id `job-<id>`, so re-publishing keeps
the link:

- **Kokkuvõte**: steps ok / failed / skipped, elements added / modified / deleted, new warnings, duration, plan usage;
- **Sammud**: #, title, tool, state, ms, message;
- **Muudetud elemendid**: one row per changed parameter (or move) of a modified element — id, category, name, who,
  what, before, and *now* as a live `data-revit-param` cell, so drift since the job shows highlighted;
- **Lisatud** and **Kustutatud** elements;
- **Uued hoiatused** with their element ids;
- **Tagasivõtmine**: Revit's transaction names in order, the checkpoint id and how to roll back (text, no button —
  rollback stays approval-gated through the agent).

Rows select their elements in Revit. Tables are capped at 2 000 rows with a note.

## In the MCP window

A **JOBS** tab lists the session's and journaled jobs with state, progress `7 / 14 · 3.2 min`, stop reason, and
**Pause**, **Resume** (the user's click is the approval), **Cancel** and **Open report**. The header reads `JOBS (1)`
while a job runs.

## Activity log

Every step is logged as usual with client `Job <id>` and its `planId`/`planCheck`. The runner adds entries for start,
pause, suspend, resume, cancel, completion and report publication under `revit_job_*`.

## Status codes

`job_not_found`, `job_not_resumable`, `job_step_invalid`.

## Limits and what a job does not do

- No branching or loops — the agent re-plans between jobs.
- No scheduling for later.
- Steps act on the active document, as the tools do; the plan falls back to approvals when it changes, and resume
  checks the model.
- No second Revit instance as a worker (#101).

## Code

Pure (`RevitMCP.Addin/Jobs/`, linked into `RevitMCP.Tests`): `JobModels`, `JobDefinitionParser`, `JobJsonPath`,
`JobStateMachine`, `JobJournalStore`, `JobReportHtml`, `JobSummary`, and `JobService` (the runner; its Revit-facing
parts are delegates, so the tests drive it with a fake dispatcher). Tools: `RevitMCP.Addin/Tools/Jobs/JobTools.cs`.
