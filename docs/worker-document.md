# Worker document: the agent's own local

Issue #101, part 4 of the autonomy series (#98 [checkpoints](checkpoints.md), #99 [plans](plans.md), #100
[jobs](jobs.md)). Most tools act on the active document, so the agent and the user share one document, one active view
and one selection. With its **own local copy** of the central, the agent owns its elements through worksharing, its
writes never hit "element owned by another user", the user's selection and view are never disturbed, and syncing is an
explicit, reviewable step.

## Tools

All in the `documents` group; every one takes `document` (title or path) except `revit_open_document`.

| Tool | Permission | Purpose |
|---|---|---|
| `revit_open_document` | approval | Opens a central as a new local (or a detached copy, or a plain file) **without activating it** |
| `revit_close_document` | destructive | Closes a non-active document, with or without saving |
| `revit_list_worksets` | read-only | User worksets: id, name, owner, open, editable, default, active; `countElements` |
| `revit_preview_set_workset` | read-only | What `revit_set_workset` would move or skip |
| `revit_set_workset` | approval | Sets the active workset and/or moves elements to a workset |
| `revit_get_ownership` | read-only | Checkout status of elements and worksets, creator, last changed by, newer in central |
| `revit_checkout_elements` | approval | Borrows elements; reports what others own and who |
| `revit_relinquish` | approval | Gives elements / worksets back without syncing |
| `revit_reload_latest` | approval | Pulls central's changes without syncing |

Open, close, checkout, relinquish and reload latest talk to central or the file system, so they get the long-running
timeout (15 min) instead of 30 s.

## The workflow

1. The user opens Revit and their own local as usual.
2. The agent: `revit_open_document path=<central> worksets=editable` → a new local
   `<central>_<user>_mcp.rvt` in `%LOCALAPPDATA%\RevitMCP\Locals` (or `localFolder`), opened in the background; the
   result gives its title.
3. Every following call passes `document="<that title>"` (a job sets it in each step's arguments).
4. `revit_get_ownership elementIds=[…]` → `revit_checkout_elements elementIds=[…]` for what it will edit.
5. It works; `revit_checkpoint_create` before and `revit_checkpoint_diff` after verify it (#98).
6. `revit_sync_with_central document=… comment=…` (destructive, manual approval), then `revit_relinquish
   document=… what=all`, then `revit_close_document document=…`.
7. The user does Reload Latest (or `revit_reload_latest`) and sees the agent's work.

The agent's local is found by its title; its central path is shared with the user's own local, so a selector by
central path is ambiguous and refused.

**The limit**: Revit's API thread is shared, so heavy work in the agent's document still competes with the user's own
commands for UI time. A second Revit instance (`revit_list_instances`, `revit_select_instance`) removes that too.

## `revit_open_document`

- `path` — the central, or a plain `.rvt`. It must pass the FileAccessPolicy
  (`%ProgramData%\RKTools\MCP\Config\FileAccessPolicy.json`); centrals on a share need `"AllowNetworkPaths": true`
  there, and the error says so. Revit Server and cloud paths are not supported yet.
- `mode` — `newLocal` (default for a workshared central), `detach` (`DetachAndPreserveWorksets`), `plain` (default
  for anything else). A local copy of someone else is refused: pass its central.
- `worksets` — `all` (default), `editable` (not borrowed by anyone else), `lastViewed`, `closeAll`, or a list of names.
- `audit`, `activate` (default false: the user's view does not change).
- Refused when the same file, or the agent's own `_mcp` local of the same central, is already open (status
  `document_already_open`, with its title to use instead), or when the active document has an open transaction.
  The user's own local of the same central being open is the normal case and is not refused.

## `revit_close_document`

`document` is required and may not be the active document. `save=true` saves it first (a local saves locally — sync
before). Its approval says whether there are unsaved changes that will be lost. Relinquishing is not automatic.

## Document resolution for every tool

`RevitMCP.Addin/Documents/ToolDocument.cs` is the one helper: `Resolve(uiapp, request, out failure)` resolves the
`document` argument (or the active document), `UiDocumentOf` gives the UI document only when the target is active,
and `ActiveRequired` answers `active_document_required` for UI-bound work (selection, active view) on a non-active
document instead of acting on the wrong one. The model graph describes the active document only, so its gate, its
automatic refresh and its routing hints step aside for a call that targets another document.

Tools are migrated in waves; `ToolDocumentMigrationTests` lists the migrated ones (they may not read the active
document directly) and caps the direct uses left in `RevitMCP.Addin/Tools`, a number that may only go down.

| Wave | Tools | State |
|---|---|---|
| 1 | `core` and `query` reads: `revit_get_elements_info`, `revit_get_element_parameters`, `revit_count_elements`, `revit_find_elements_by_parameter`, `revit_group_by_parameter`, `revit_group_elements`, `revit_get_available_parameters`, `revit_run_query_preset`, `revit_check_parameter_completeness`, `revit_run_parameter_qa_rule_set`, `revit_export_query_to_excel` | done |
| 2 | `edit` and `electrical` writes and their previews: `revit_(preview_)move_elements`, `revit_(preview_)copy_elements`, `revit_(preview_)align_elements`, `revit_(preview_)place_on_face`, `revit_(preview_)rotate_elements`, `revit_(preview_)set_elevation`, `revit_(preview_)assign_room_to_elements`, `revit_(preview_)edit_family_types`, `revit_set_parameter`, `revit_create_lines`, `revit_place_family_instances`, `revit_add_elements_to_circuit`, `revit_apply_circuit_load_names`, `revit_apply_circuit_numbering`, `revit_(preview_)assign_data_devices_to_patch_panels`, `revit_change_circuit_cable_or_wire_type`, `revit_create_cable_type`, `revit_create_electrical_circuit`, `revit_(preview_)draw_circuit_arcs`, `revit_move_elements_between_circuits`, `revit_reassign_circuit_panel`, `revit_set_circuit_parameter`, `revit_set_circuit_parameters_bulk`, `revit_set_circuit_path_mode` | done |
| 3 | `views` (views, sheets, schedules, title blocks, revisions, view templates, list exports), `tags` (tags, dimensions, text notes, detail lines, view alignment), `devices` (room geometry, device codes and placement, audits, element rooms, view images), the `electrical` reads (circuit info, panels, health, voltage drop, fire alarm, length estimates, exports), the combined `revit_delete` / `revit_duplicate` / `revit_rename` / `revit_set_parameters_bulk`, and the previews of wave-2 writes that were left out | done |

On a non-active document a view-based tool needs `viewId` (or `searchViewId` for ray casting): there is no active view
to fall back on. Deleting views of a non-active document closes their open tabs but cannot switch its active view.

Still active-document only, on purpose: the selection tools (`revit_select_*`, `revit_get_selected_elements`,
`revit_inspect_selected_elements`, the selected-tag-template tools, `revit_focus_clash`), the model graph, model pages,
memory and plans (they bind to the active model by design). Not migrated yet: linked-model queries, CAD, clash, IFC
Space-to-Room, delivery checks, Extensible Storage and PanelSchematics. `ToolDocumentMigrationTests` lists them; any
other tool reading the active document directly fails it.

A bridge tool offers `document` only when every add-in tool it sends to resolves it — a `document` argument a tool
ignored would bind the approval to one document while the tool acts on another. `ToolDocumentMigrationTests` checks this
for every bridge method.

Approvals already bind to the target document, so a write with `document=` is approved for that document.

## Status codes

`document_already_open`, `not_workshared`, `owned_by_other` (checkout: some elements belong to others; data names
them), `active_document_required`.
