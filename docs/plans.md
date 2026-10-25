# Plans: one approval per job

Issue #99, part 2 of the autonomy series (#98 checkpoints, #100 jobs). A **plan** replaces one approval per write
call with one approval per job. The agent says what it will do, with which tools, on which elements and within which
limits (the *envelope*); the user approves it once in the Pending tab; every later write call that stays inside the
envelope runs without a prompt. The safety model is unchanged: destructive tools always need a manual approval,
approvals bind to the document, and everything the plan does is recorded against a [checkpoint](checkpoints.md).

| Tool | Permission | Purpose |
|---|---|---|
| `revit_plan_submit` | approval | Proposes a plan; the approval activates it and creates its checkpoint |
| `revit_plan_status` | read-only | State, counters, remaining budget, calls not covered, checkpoint id |
| `revit_plan_close` | no approval | Ends a plan early; returns the counters and the checkpoint diff summary |
| `revit_plan_use` | bridge only, no approval | Sets or clears the plan id the bridge attaches to this session's write calls |

All four are in the `autonomy` tool group.

## Flow

```text
revit_plan_submit title="Tag sockets on level 2"
                  steps=["Read sockets on level 2", "Set Mark", "Move the 4 that clash", "Tag all", "Verify"]
                  envelope={"tools":["revit_set_parameter","revit_move_elements","revit_place_tags"],
                            "elementIds":[…318 ids…], "categories":["Electrical Fixtures"], "viewIds":[4711]}
  → approval_required, planId plan-3fa9c1 (the bridge attaches it to every following write)
… user approves once in the Pending tab …
revit_get_approval_status requestId=…        → planId, checkpointId, expiresAt
revit_set_parameter elementIds=[…] …         → runs, no prompt
revit_move_elements moves=[…]                → runs, no prompt
revit_place_tags elementIds=[…] viewId=4711  → runs, no prompt
revit_checkpoint_diff checkpointId=cp-…      → review
revit_plan_close                              → counters + diff summary
```

1. `revit_plan_submit` is approval-gated like any write. The Pending tab shows a multi-line summary (tools, scope,
   limits, steps). In Direct Edit the plan activates at once — it still enforces its limits, which is the point of
   using one even then. An invalid plan (unknown or destructive tool, no title, too many ids) is refused at once,
   without an approval.
2. Approval activates it: a checkpoint is created with the envelope's ids and categories as its snapshot scope (an
   envelope too large to snapshot gets a checkpoint without a snapshot, with a warning), and the plan is registered.
3. The bridge chose the plan id when it sent the submit, so it attaches `planId` to every following request of the
   session whose tool is not read-only — also through `revit_tools_call`. `revit_plan_use` switches or clears it.
4. For an approval-gated tool with a `planId`, `ExternalEventHandler` asks `PlanService.TryRunUnderPlan`:
   - the plan exists, is active and not expired, and the call targets the plan's document;
   - **pre-check** (`PlanArgumentScope`) on the arguments: the tool is in `tools`; named element ids
     (`elementIds`, `circuitIds`, `tagIds`, `textNoteIds`, `sheetIds`, any `…ElementId(s)` key, and `elementId`/`id`
     inside object arrays such as `moves[]`, `changes[]`, `rows[]`, `rotations[]`, `elevations[]`) are in
     `elementIds`; view ids (`viewId`, `viewIds`, …) are in `viewIds`; a `category` argument is in `categories`;
     the id count and `limit` stay within `maxElementsPerCall`; `useSelection` resolves to the current selection; a
     tool that creates elements is refused when `allowCreate` is false.
   - A refusal does **not** fail the call: it takes the normal path (approval, or Direct Edit) with a warning
     `Plan <id>: <reason>; the plan does not cover this call…`. The agent is never blocked harder than without a plan.
5. An authorized call runs inside a `TransactionGroup` named `Plan <title>: <tool>`. Tools open their own
   transactions inside it as usual. Afterwards the **post-check** (`PlanPostCheck`) looks at the ids Revit reported:
   - any deletion violates — plans never approve deletes;
   - a modified element outside `elementIds`, or any changed element outside `categories`, `levelIds`, `viewIds`
     (owner view of view-specific elements) or `worksets` violates; creating violates when `allowCreate` is false,
     modifying when `allowModify` is false;
   - the distinct changed ids must stay within `maxElementsPerCall` and, with the plan's earlier ones, within
     `maxElementsTotal`.
   - **Incidental categories** — every tag category, dimensions, spot dimensions, view markers, viewports, revision
     clouds, wires and electrical circuits, and elements without a category — are exempt from the scope rules unless
     `strict=true`: moving a socket drags its circuit and tag along. Elements *added* in them still count against
     the limits, so a tagging plan cannot place tags without bound.
   - Violation → the group is rolled back (the model is unchanged, the checkpoint forgets the call's batches) and the
     call fails with `plan_envelope_violated`, naming the reason, the offending ids and categories and the limits.
     There is no approval fallback here: the call already ran and was undone; narrow it or submit a new plan.
   - Otherwise the group is assimilated — **one Revit undo per authorized call** — and the counters update.
6. At `maxCalls` or `maxElementsTotal` the plan becomes **exhausted**: the call that used it up carries a warning,
   later calls take the normal path. A rolled-back call still uses one call, so a looping agent runs out.
7. `revit_plan_close` (agent), **Revoke** (user, Pending tab), expiry (`expiresMinutes`, checked on every call and
   every 30 s by the window) or **Panic Stop** (revokes all) end a plan.

## Envelope

| Field | Default | |
|---|---|---|
| `tools` | — (required) | Approval-gated write tools; destructive and non-model tools are refused, read-only ones ignored |
| `elementIds` | none | Allow list (max 20 000); empty = not restricted by id. New elements are never on it |
| `categories` | none | Display name (`Electrical Fixtures`) or `OST_` name |
| `levelIds`, `viewIds`, `worksets` | none | Optional further scope |
| `allowCreate`, `allowModify` | true, true | |
| `maxElementsPerCall` | 500 | 1–5 000, never above `maxElementsTotal` |
| `maxElementsTotal` | 2 000 | 1–20 000 |
| `maxCalls` | 100 | 1–1 000 |
| `expiresMinutes` | 120 | 1–480 |
| `strict` | false | true: side effects on incidental categories count as well |

Out-of-range numbers are clamped with a warning.

## What a plan can never do

- Auto-approve a `DestructiveRequiresManualApproval` tool (delete, remove links, rollback, sync).
- Act on another document than the one it was approved for.
- Exceed its counters: a call that would is rolled back whole.
- Survive a Revit restart: plans live in memory; the checkpoint survives.

## In the Pending tab

A **Plans** section lists the session's plans with state, calls and elements used, calls not covered and time left,
and two buttons: **Show changes** (selects the elements the plan changed) and **Revoke**. The tab header counts
active plans: `PENDING (2) · PLANS (1)`.

## Activity log

Each entry of a call that carried a plan has `planId` and `planCheck` — `authorized`, `fallback:<reason>` or
`violated`. Activation is the approved `revit_plan_submit` entry; close, revoke, expiry, exhaustion and each
violation are logged as `revit_plan_*` entries, so an unattended session can be audited from the log alone.

## Status codes

`plan_not_found`, `plan_not_active` (with the state) — both as fallback reasons in `warnings` —, and
`plan_envelope_violated`. `plan_exhausted` is a warning only.

## Code

Pure (`RevitMCP.Addin/Plans/`, linked into `RevitMCP.Tests`): `PlanModels`, `PlanArgs`, `PlanEnvelopeParser`,
`PlanArgumentScope`, `PlanPostCheck`, `PlanToolClassification` (which tools create; the incidental categories; a
test fails when a new write tool is not classified), `PlanRegistry`, `PlanSummary`. Revit side: `PlanService`,
`PlanChangeRecorder` (fed by `App.OnDocumentChanged`), `Tools/Plans/PlanTools.cs`, and the branch in
`ExternalEventHandler.ExecuteItem` before the approval check.
