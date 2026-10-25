# Checkpoints, warnings, change diff and rollback

Issue #98, the first part of the autonomy series (#99 plans, #100 jobs). These tools let an agent — and the
user — see **what changed in the model and what Revit complains about** after a batch of writes, and undo it
as far as Revit allows. They are in the `autonomy` tool group (not in the `core` profile); `revit_tools_search
"what changed"` finds them.

| Tool | Permission | Purpose |
|---|---|---|
| `revit_get_warnings` | read-only | Revit's own warnings (`Document.GetWarnings()`), grouped by description or listed, filtered, paged, with element ids |
| `revit_checkpoint_create` | read-only | Remembers "now": the warning set and, for a scope, a snapshot of parameter values and locations |
| `revit_checkpoint_diff` | read-only | Everything changed since a checkpoint: added / modified / deleted, who did it, before/now values, moves, transactions, new and resolved warnings |
| `revit_checkpoint_list` | read-only | Checkpoints of the open documents |
| `revit_checkpoint_remove` | no approval | Deletes one checkpoint record (`checkpointId`); the model is not changed |
| `revit_preview_checkpoint_rollback` | read-only | What a rollback would do and what it cannot |
| `revit_checkpoint_rollback` | destructive, manual approval | Compensating transaction back to the checkpoint |

Every tool takes the optional `document` argument (title or path, #90).

## Workflow

```text
revit_checkpoint_create title="Tag sockets L2" categories=["Electrical Fixtures"] parameters=["Mark","Comments"]
  → checkpointId cp-3fa9c1
… writes (revit_set_parameter, revit_move_elements, revit_place_tags …)
revit_checkpoint_diff checkpointId=cp-3fa9c1                 # counts, transactions, warnings
revit_checkpoint_diff checkpointId=cp-3fa9c1 include=elements  # before/now per element, paged
revit_get_warnings sinceCheckpointId=cp-3fa9c1                # only warnings the batch caused
revit_preview_checkpoint_rollback checkpointId=cp-3fa9c1      # if it went wrong
revit_checkpoint_rollback checkpointId=cp-3fa9c1              # manual approval in Revit
```

## Scope and snapshot

Without a scope a checkpoint records only change ids and the warning set — fast and unbounded. With a scope
(`elementIds`, `categories`, `viewId`, `useSelection`; any combination, union) it also snapshots every element:
category, name, type, level, location (point in mm and rotation, or line endpoints in mm) and parameter values.

- `parameters[]` names the instance parameters to snapshot. Without it every instance parameter with a value
  is taken, writable ones first, up to `QueryLimits.MaxParametersPerElement` (40) per element.
- Each value is stored twice: as the display string (what the diff shows, the same formatting as
  `revit_get_element_parameters`) and as the raw stored value in internal units (what a rollback writes back,
  so `1200 mm` is restored exactly, never re-parsed). Read-only parameters store no raw value.
- A scope that resolves to more than `QueryLimits.MaxScanElements` (20 000) elements is refused with status
  `snapshot_too_large` and the count; nothing is snapshotted silently in part.
- `useSelection` needs the document to be active. Selection and view scopes are resolved to ids at creation.

At most **20 checkpoints per document**; creating a 21st removes the oldest and says so in `warnings`.

## How changes are recorded

`App.OnDocumentChanged` feeds `CheckpointChangeTracker`, which appends one batch per Revit `DocumentChanged`
event to every checkpoint of that document: added / modified / deleted ids, Revit's transaction names (its Undo
menu names) and the operation (`TransactionCommitted`, `TransactionUndone`, …).

Attribution comes from `ActiveToolProbe`, which `ExternalEventHandler` sets around every tool execution:

| `by` | When |
|---|---|
| `agent:<tool> (<client>)` | a connector tool was running |
| `page` | a model page write (client "Model page") |
| `user` | anything else — the user working in Revit, including Undo/Redo |

The diff nets the batches: an element added and later deleted (also an undone addition) disappears; added then
modified stays *added*; a deletion that is undone leaves the element *modified*; the latest batch's attribution
wins. For elements that are not snapshotted the tracker keeps an identity cache (category, name, type, level;
at most 50 000 per checkpoint) so a deletion can still be described.

The snapshot is authoritative for what it covers: the diff re-reads the live element with the same reader, so a
snapshotted element that is gone is *deleted* and one that differs is *modified* even when no batch saw the
change. A snapshotted element that was touched but is back to its checkpoint values is reported as
`touchedButUnchanged`, not as modified.

## Persistence and the restart caveat

`%LOCALAPPDATA%\RevitMCP\Checkpoints\<modelId>\<id>.json` (meta, warnings, batches, identities) and
`<id>.snapshot.json` (written once). Batches are saved debounced (2 s) on a background thread, never on the
Revit API thread. After a Revit restart a checkpoint is loaded with `trackingComplete=false` and every result
says: *changes made while the connector was not running are not listed; snapshot comparison is still exact.*
Recording stops at 500 000 ids per checkpoint with the same flag.

## Rollback

`revit_checkpoint_rollback` runs one transaction named `Revit MCP - Rollback to <title>` — one Revit undo
reverses it. `what[]` picks any of:

1. `restoreValues` — writes the raw checkpoint value back to each differing snapshotted parameter.
2. `restoreLocations` — moves the element by the inverse delta and rotates it back about its checkpoint point.
   A line that changed length or direction is skipped (only a move can be undone).
3. `deleteAdded` — deletes elements added since the checkpoint.

It never does:

- recreate deleted elements (counted in `cannotRestore.deleted`);
- restore elements outside the snapshot (`cannotRestore.notSnapshotted`), read-only parameters, type
  parameters, or element-reference parameters (set those with `revit_set_parameter`);
- touch an element whose latest change was not an agent's (the user's, a page's, or seen only by the snapshot
  after a restart) unless `force=true`;
- delete or move pinned elements.

The approval in the Pending tab is built from the live model, e.g. *"Rollback to checkpoint 'Tag sockets L2':
delete 12 added elements, restore 340 parameter values on 170 elements, move back 20 elements. Deleted elements
are NOT restored. Reversible with one Revit undo."* It always needs a manual approval, also in Direct Edit.
The result lists every skipped element with its reason and the diff counts after the rollback.

## Status codes

`checkpoint_not_found`, `checkpoint_other_document` (the id belongs to another open model — pass `document`),
`snapshot_too_large`.

## Code

Pure logic (no Revit API, linked into `RevitMCP.Tests`): `RevitMCP.Addin/Checkpoints/` — `CheckpointModels`,
`CheckpointIds`, `CheckpointStore`, `ChangeNetting`, `SnapshotDiff`, `WarningDiff`, `CheckpointDiff`,
`CheckpointDiffReport`, `RollbackPlanner`. Revit side: `ActiveToolProbe`, `CheckpointChangeTracker`,
`CheckpointSnapshotReader`, `RevitWarningReader`, and the tools in `RevitMCP.Addin/Tools/Checkpoints/`. A tool
whose approval text needs the live model implements `IApprovalSummaryTool`.

## Out of scope

Keeping a `TransactionGroup` open across tool calls for a true undo (it blocks the Revit UI and breaks on sync),
recreating deleted elements, tracking changes in linked documents.
