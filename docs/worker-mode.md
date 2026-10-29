# Worker mode: unattended open, sync and close

Part 5 of the autonomy series (#98 [checkpoints](checkpoints.md), #99 [plans](plans.md), #100 [jobs](jobs.md), #101
[worker document](worker-document.md)). With the worker document an agent can open its own local, work in it, sync and
close it — but open waits for an approval, sync and close always wait for a manual click, and the first Revit dialog
(missing links, a missing updater) stops everything. Worker mode removes those two stops for a machine that runs work
unattended:

1. **Pre-approved document operations** (`approval.worker`): open, sync, close, relinquish, reload latest and
   checkout run without a prompt for models in folders you list.
2. **Dialog auto-answer** (`dialogs`): dialogs Revit raises *while the connector runs a tool* are answered by rules —
   by dialog id or by a piece of their text. With `scope=always` the same rules also click away those dialogs when you
   open a model yourself.

Both are read from the **per-user config only** (`%APPDATA%\RKTools\MCP\user.config.json`), like
`approval.directEditByDefault`: a company or project config cannot switch approvals off. Both are off by default.
Changes to the file apply on the next call; no Revit restart.

## Pre-approved document operations

```json
{
  "approval": {
    "worker": {
      "enabled": true,
      "operations": ["open", "sync", "close", "relinquish", "reloadLatest", "checkout"],
      "folders": ["P:\\Projektid", "\\\\server\\central"]
    }
  }
}
```

| Operation | Tool | Normally |
|---|---|---|
| `open` | `revit_open_document` | approval |
| `sync` | `revit_sync_with_central` | manual approval (destructive) |
| `close` | `revit_close_document` | manual approval (destructive) |
| `relinquish` | `revit_relinquish` | approval |
| `reloadLatest` | `revit_reload_latest` | approval |
| `checkout` | `revit_checkout_elements` | approval |

`revit_save_document` needs no approval already.

A call is pre-approved only when **all** of these hold; otherwise it takes the normal approval path and the reason is
in its `warnings`:

- `enabled` is `true`, the operation is listed, and at least one absolute folder is listed (`P:\…` or `\\server\…`).
- The model's file — or, for a workshared local, its central — lies inside a listed folder (`..` is refused, and
  `P:\ProjektidVana` does not match `P:\Projektid`).
- `open`: with `activate=false` (the default), so the user's window is never taken over.
- Every other operation: the document was **opened by the connector in this Revit session**. The user's own
  documents are never synced or closed without a click, even inside a worker folder. The list is in memory and is
  cleared when Revit restarts.
- No `all=true`: name the document.
- `close`: never with unsaved changes and `save=false`. Pass `save=true` (or save first).

Nothing else is ever pre-approved: deletes, purge, rollback, link removal and every other destructive tool keep their
manual approval. Inside a job, a destructive step still needs `allowDestructiveSteps=true` on `revit_job_submit`;
worker mode then lets a sync or close step run instead of waiting.

A pre-approved call runs as if approved: it is written to the activity log, and its result's `warnings` names the rule
("Pre-approved by worker mode ('sync' in P:\PROJEKTID)").

## Dialog auto-answer

```json
{
  "dialogs": {
    "autoAnswer": true,
    "unknown": "show",
    "scope": "connector",
    "rules": {
      "TaskDialog_Unresolved_References": 1002,
      "Some_Other_Dialog_Id": "show"
    },
    "messageRules": {
      "Some entities were lost during import": 8
    }
  }
}
```

- `scope`: `connector` (default) touches only dialogs raised **while the connector runs a tool or a job step**.
  `always` also answers dialogs in your own work — but only by an explicit rule (id or message); `unknown=cancel`
  never applies to your own dialogs. Those answers are logged with `duringConnectorWork=false`.
- A rule (`rules`) maps a Revit dialog id to the result Revit gets: `1` OK, `2` Cancel, `6` Yes, `7` No, `8` Close, `1001`–`1004`
  the first to fourth command link of a task dialog. `"show"` leaves that dialog to the user.
- A message rule (`messageRules`) matches a piece of the dialog's text, case-insensitive — for dialogs whose id you
  do not know yet. An id rule wins over a message rule; a configured message rule wins over a built-in one; among
  several matches the longest text wins.
- When Revit refuses Close (8) the handler tries OK, then Cancel; when it refuses OK it tries Close. The log says
  which answer was used.
- Built-in defaults (overridable):

  | Dialog | Match | Answer |
  |---|---|---|
  | Missing links on open | id `TaskDialog_Unresolved_References` | `1002` — Ignore and continue opening the project |
  | Missing add-in updater | id `TaskDialog_Missing_Third_Party_Updater(s)` | `1001` — Continue working with the file |
  | "Some entities were lost during import." | message | `8` — Close |
  | "Some numerical data within the imported file was out of range. …" | message | `8` — Close |

  The last two come from DWG links and can appear many times when a model opens; each one is closed and logged.

- `unknown`: `show` (default) leaves a dialog without a rule to the user — safe, but an unattended run then waits.
  `cancel` dismisses it with Cancel, so the call fails with an error instead of hanging. Use `cancel` on a dedicated
  worker machine once the rules cover what you expect.
- Every dialog the connector meets is listed in the call's `warnings` ("Revit dialog '…' answered 1002 (command
  link 2): …"), kept in memory (last 200) and appended to `%LOCALAPPDATA%\RevitMCP\dialogs.jsonl`.

**Verify the defaults on your first run.** Dialog ids and the order of command links can differ between Revit
versions and languages. Run an open with worker mode on a model with a missing link, then read the log with
`revit_get_worker_status` and adjust the rule if the answer was wrong.

Not every window can be answered: licensing, crash reports, add-in error windows and Revit's own save reminder do
not raise `DialogBoxShowing`. On a worker machine, turn Revit's save reminder off (Options → General).

## `revit_get_worker_status`

Read-only (`documents` group). Shows whether worker mode is active, its operations and folders, the documents the
connector opened this session, the effective dialog rules, problems in the config (unknown operations, relative
folders, bad rule values) and the last dialogs (`dialogs`, default 20) with id, message, action, answer, whether Revit
accepted it, and the tool and request that raised it. Call it before unattended work and after a call reports a
dialog.

## An unattended run

1. Revit is started on the worker machine (a Windows scheduled task or a script); the connector starts with it.
2. The agent calls `revit_get_worker_status`, then `revit_open_document path=<central>` — pre-approved, missing-link
   dialogs answered.
3. It plans and submits the work as a job (`revit_job_submit`); the job's own approval is the one click left, unless
   Direct Edit is on.
4. When the job completes: `revit_sync_with_central document=…`, `revit_relinquish`, `revit_close_document
   document=… save=true` — all pre-approved.
5. The report page and the dialog log are what the user reviews.

Quitting Revit is still outside the connector.

## Code

- `RevitMCP.Addin/Approval/WorkerPolicy.cs` — the policy and the connector-opened registry (pure, unit-tested).
- `RevitMCP.Addin/Approval/WorkerPreapproval.cs` — reads the user config, gathers facts from Revit, asks the policy.
- `RevitMCP.Addin/Dialogs/DialogRules.cs` — rules, decision and the dialog journal (pure, unit-tested).
- `RevitMCP.Addin/Dialogs/DialogAutoAnswer.cs` — the `DialogBoxShowing` handler.
- `ExternalEventHandler` checks worker mode before plans and before the approval queue, and runs every tool inside a
  dialog journal scope.
- Tests: `RevitMCP.Tests/WorkerModeTests.cs`.
