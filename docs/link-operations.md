# Link operations: IFC-first model linking

Issue #163. Linking an IFC takes minutes: Revit opens it, converts it to a cached Revit file (`model.ifc.RVT`) and only
then links it. While that runs the Revit API thread is blocked. The failure to avoid is an agent that times out, links
again, and starts a second conversion on top of the first. This feature makes a link request a **durable operation**
with one id, one lock per source file, a status that does not need Revit, and no automatic retry anywhere.

IFC is the first-class path. Native RVT links share the same operation, identity, approval and status handling with a
simpler load. DWG links stay with `revit_link_dwg`.

| Tool | Permission | Where it runs | Purpose |
|---|---|---|---|
| `revit_preview_link_models` | read-only | Revit | What would happen per file: link, refresh, skip, invalid, already in progress; cache state |
| `revit_link_models` | approval | Revit | Admits the files as operations and returns their ids at once |
| `revit_link_operation_status` | read-only | **bridge, files only** | State, phase, measured elapsed time, `ready`, what to do next; `waitSeconds` (≤ 25) long-polls |
| `revit_list_link_operations` | read-only | **bridge, files only** | All operations, what is running, what waits, what needs review |
| `revit_request_link_cancel` | no approval | **bridge, files only** | Conservative cancel (see below) |
| `revit_reconcile_link_operation` | no approval | Revit | Settles an `outcome_unknown` operation from the live document |

All six are in the `documents` group. `revit_reload_links_from` is unchanged for callers; see "The reload path".
`revit_link_operation_run` is the connector's internal carrier (registered in the add-in, never advertised by the
bridge); it refuses anything that is not an operation the connector just handed to it.

## Lifecycle

```
queued -> starting -> converting -> verifying -> completed
   |         |            |            |-------> failed
   |         |            '--------------------> outcome_unknown --(verification)--> completed | failed
   |         '--> queued   (only while no conversion has begun: Revit was busy)
   '--> cancelled          (only before it started)
```

- **queued**: admitted and written to disk; waiting its turn.
- **starting**: handed to Revit; the run tool has not begun the conversion.
- **converting**: persisted *before* the blocking Revit call. From here on a lost answer can only be `outcome_unknown`.
  The record carries a `phase` in words ("converting the IFC to a Revit cache", "linking the converted IFC"). There is
  no percentage: Revit reports none, and the connector does not invent one. Elapsed time is measured.
- **verifying**: the call returned; the document is checked.
- **completed** / **failed**: final. `completed` means the link was verified, not that a file exists.
- **outcome_unknown**: neither active nor final. It blocks a new request for the same file and only a verification can
  settle it.
- **cancelled**: dropped before it started.

`waiting_approval` from the issue is not a state: `revit_link_models` needs approval, and until the user approves the
request is parked in Revit's Pending tab (`revit_get_approval_status`), before the connector has anything to record.

## Where the state lives and why status needs no Revit

Every operation is `%LOCALAPPDATA%\RevitMCP\LinkOperations\<id>.json`, replaced atomically on each change and written
**before** Revit is involved. The bridge process reads the same folder, so `revit_link_operation_status`,
`revit_list_link_operations` and `revit_request_link_cancel` never use an ExternalEvent or the add-in's pipe: they answer
while the Revit API thread is blocked converting, and even while Revit is closed. (A background add-in tool would not do:
every add-in request, background ones included, is started from the Revit API thread's ExternalEvent.)

The add-in also writes a **heartbeat** into active records every 15 s from a timer thread that does not touch the Revit
API. The bridge combines it with the process list: a running operation whose Revit process no longer exists is reported
`outcome_unknown` (`effectiveState`); a silent heartbeat only produces a warning, since Revit may be hung and could
still finish.

A cancel from the bridge is a separate `<id>.cancel` marker file, so the bridge never rewrites a record the add-in is
changing; the add-in applies markers on its timer.

## Exactly-once admission

The lock key is **canonical source path + target document**. Canonical means case, slashes, quotes and the long-path
prefix do not matter, and `model.ifc.RVT` is the same source as `model.ifc`. The target document is its file path, else
its title.

- An open operation (`queued`, `starting`, `converting`, `verifying`) for the same key answers `already_in_progress` with
  **its** operation id, whatever the type: two clients racing, a reconnecting agent, a refresh next to a link.
- An `outcome_unknown` one answers `needs_review`: reconcile first.
- A finished one (`completed`, `failed`, `cancelled`) does not block; a deliberate new request is a new operation.
- At most 25 operations are open per Revit process.

Admission is under one lock and re-checks the files, so it also covers a second Revit instance sharing the folder.

## Never retried

The connector never starts a conversion twice. Concretely:

- `revit_link_models` returns at once; nothing waits on the conversion, so a client timeout cannot happen there. If the
  caller still disconnects, the operation carries on and is found again by id (or `requestId`).
- The connector runs operations **one at a time** by dispatching the internal run tool through the normal dispatcher
  (as the job runner does). A dispatch that Revit never started (`revit_busy`, `queue_full`) is offered again after 5, 10,
  20, 40, then 60 s, at most 12 times, then fails with "nothing was converted". That is not a retry of a conversion: nothing
  ran. Once the run tool has begun converting, **every** other outcome (timeout after 120 min, cancelled wait, dropped
  pipe, an exception) is `outcome_unknown`.
- The run tool claims its operation with a one-time token, so a second delivery cannot start it again, and it never
  reads the caller's cancellation token once it is converting: the Revit call cannot be interrupted safely, so it is always
  followed by its verification.
- If the conversion finishes after the connector stopped waiting, the run tool still updates the record
  (`outcome_unknown` -> `completed`/`failed`, `resolvedBy: late_completion`).
- After a Revit restart: a queued operation becomes `cancelled`, one that never reached the conversion `failed`, one that
  did `outcome_unknown`. Nothing is queued again and no cache file is deleted.

## Verification

After the Revit call returns, on the API thread, the document must show (and the result records as `checks`):

1. a link type for the requested file (its path, or its `.ifc.RVT`, matches the canonical source),
2. Revit's linked-file status is `Loaded`,
3. at least one instance in the model,
4. the linked document holds elements (an empty conversion fails; an unreadable count is noted, not assumed),
5. for IFC, a cache file that exists is not empty (a partial conversion fails).

A non-empty cache file alone is never success. If the link type was created but could not be placed, the type this
operation created is removed again. Cache files are never deleted.

`revit_reconcile_link_operation` runs the same verification for an `outcome_unknown` operation once Revit answers: link
found and sound -> `completed`; no link in the open document -> `failed`; document not open or unclear -> stays
`outcome_unknown` for a person. It links, retries and deletes nothing.

## Positioning, paths and formats

- `positioning`: `origin` (origin to origin, default) or `shared` (shared coordinates). The Revit API has no other mode
  for links; `center` and friends are refused with `unsupported_positioning`.
- `pin`: applied after the instance exists.
- `ifExists`: `skip` (default) or `refresh`.
- `regenerateIfcCache`: convert again even when the cache is newer than the IFC.
- Only absolute `.ifc` and `.rvt` paths. `.ifczip`, `.ifcxml`, the `.ifc.RVT` cache and CAD files are refused with the
  reason. The path must pass the connector's file policy (`FileAccessPolicy.json`); network paths are refused unless
  that policy allows them, and a note warns that converting over the network is slow.
- Up to 25 files per call, run in request order.

## The reload path (#90)

`ifExists=refresh` does not add a second reload implementation: the operation calls `LinkReloadService.Reload`, the same
code behind `revit_reload_links_from`. Conversely `revit_reload_links_from` now records every IFC / RVT pair as a link
operation (type `refresh`), run inline on its own thread: the same lock (a second reload or link of the same file is
refused as `already_in_progress` before anything changes), the same durable record and bridge-side status while it is
blocked, a verification afterwards, and `outcome_unknown` instead of a plain timeout. Its arguments and results are
unchanged.

## What other agents see while Revit converts

- Busy answers from the add-in (`revit_busy`) and the bridge's timeout answer name the running operation, its elapsed
  time and the queue, and say not to retry or start other Revit work (`busy.linkOperation` in the structured data).
- `revit_list_link_operations` shows the running operation (`runningOperationId`).
- Job steps back off on `revit_busy` as before; a job step that calls `revit_link_models` returns immediately with ids.

## Cancel

`revit_request_link_cancel` is conservative by design. A queued (or just handed over) operation is cancelled and Revit is
never involved. One that is converting is only marked `cancelRequested`: the Revit call is not interrupted, no thread is
aborted, Revit is not killed or restarted, and the operation finishes and is verified. Nothing already done is undone
(`revit_remove_links` removes a link that was created).

## Limits and API-thread facts

- A blocking Revit call (`OpenIFCDocument` + `SaveAs`, `CreateFromIFC`) occupies the API thread; ExternalEvent requests
  wait, which is why they come back `revit_busy` after the busy timeout. The model graph, other tools and
  `revit_get_connection_status` behave as for any long Revit operation.
- The 120 minute limit is how long the connector waits on one operation before recording `outcome_unknown`; it does not
  stop Revit. Larger IFCs still finish and update their record.
- Revit 2024 (net48) and 2026 (net8) share the code path; the API used (`RevitLinkType.CreateFromIFC`,
  `RevitLinkType.Create`, `RevitLinkInstance.Create`, `OpenIFCDocument`) exists in both.

## Smoke tests (live Revit, with realistically large IFCs)

1. Preview two IFCs (one with a fresh `.ifc.RVT`, one without) and a bad path: expect link / link / invalid with reasons.
2. `revit_link_models` with the large IFC: the call returns in a second with an id. Poll `revit_link_operation_status`
   every 20 s while Revit is unresponsive: it keeps answering with `converting`, a phase and growing elapsed time.
3. During the conversion call `revit_link_models` again for the same file from a second client: `already_in_progress`,
   same id, no second conversion. Call any ordinary tool: `revit_busy` naming the operation.
4. After completion: `ready: true`, `result.checks` complete, `revit_list_open_documents` shows the link loaded.
5. Disconnect the agent mid-conversion, reconnect and look the operation up by `requestId`.
6. Kill Revit mid-conversion (a test machine only), restart, open the model: the record is `outcome_unknown`; run
   `revit_reconcile_link_operation` -> `failed` (no link) and the cache file is still on disk.
7. `ifExists=refresh` on an existing IFC link, and `revit_reload_links_from` on it: both appear as `refresh` operations.
8. `revit_request_link_cancel` on a queued file (cancelled) and on the converting one (`cancel_requested`, finishes).
9. A native RVT link, `positioning: shared`, `pin: true`.
