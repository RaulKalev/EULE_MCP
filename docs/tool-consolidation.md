# Tool Consolidation (token/credit optimization)

To reduce the per-request token cost of the MCP manifest (sent to the model on
every turn), several families of near-identical Bridge tools were merged into
single tools that take a discriminator parameter. This is a **clean break** —
the old tool names no longer exist. Update any saved prompts/scripts using the
mapping below.

The consolidation happens entirely in `RevitMCP.Bridge/RevitMcpTools.cs`. The
underlying Addin tools (real Revit logic, transactions, approval gating) are
unchanged — the merged Bridge tool routes to the existing Addin tool name based
on the discriminator. Read/preview vs. write/apply tools are **never** merged
together, so the `ReadOnly` hint and approval boundary are preserved.

## Old → new mapping

| Old tool(s) | New tool | Discriminator |
|---|---|---|
| `revit_export_view_list_to_excel`, `revit_export_sheet_list_to_excel`, `revit_export_schedule_list_to_excel` | `revit_export_list_to_excel` | `kind` = `views` \| `sheets` \| `schedules` |
| `revit_export_issues_to_json`, `revit_export_issues_to_excel`, `revit_export_issues_to_markdown`, `revit_export_issues_to_html_dashboard` | `revit_export_issues` | `format` = `json` \| `excel` \| `markdown` \| `html_dashboard` |
| `revit_detect_hard_clashes`, `revit_detect_clearance_clashes` | `revit_detect_clashes` | `mode` = `hard` \| `clearance` |
| `revit_get_next_clash`, `revit_get_previous_clash` | `revit_get_adjacent_clash` | `direction` = `next` \| `previous` |
| `revit_create_project_skill_override`, `revit_update_project_skill_override`, `revit_reset_project_skill_override` | `revit_manage_project_skill_override` | `action` = `create` \| `update` \| `reset` |
| `revit_preview_rename_views`, `revit_preview_rename_sheets` | `revit_preview_rename` | `entity` = `views` \| `sheets` |
| `revit_rename_views`, `revit_rename_sheets` | `revit_rename` | `entity` = `views` \| `sheets` |
| `revit_preview_delete_views`, `revit_preview_delete_sheets`, `revit_preview_delete_elements` | `revit_preview_delete` | `target` = `views` \| `sheets` \| `elements` |
| `revit_delete_views`, `revit_delete_sheets`, `revit_delete_elements` | `revit_delete` | `target` = `views` \| `sheets` \| `elements` |
| `revit_preview_duplicate_views`, `revit_preview_duplicate_sheets` | `revit_preview_duplicate` | `entity` = `views` \| `sheets` |
| `revit_duplicate_views`, `revit_duplicate_sheets` | `revit_duplicate` | `entity` = `views` \| `sheets` |
| `revit_set_view_parameters_bulk`, `revit_set_sheet_parameters_bulk` | `revit_set_parameters_bulk` | `entity` = `views` \| `sheets` |
| `revit_estimate_circuit_length`, `revit_estimate_circuit_lengths` | `revit_estimate_circuit_length` | single: pass `circuitId` (>0); batch: omit `circuitId` and filter by `panelName`/`systemType`/`circuitIds` |

## Notes

- For the rename/duplicate/set-parameters merges the discriminator is named
  `entity` (not `target`) because the sheet-rename tool already uses `target`
  for its field selector (`Name` \| `Number` \| `Both`).
- Per-target-only parameters are documented in each tool's parameter
  descriptions with a `views only:` / `sheets only:` prefix.
- `revit_merge_issue_reports` was **not** merged into `revit_export_issues` — it
  is a distinct read-only operation.
- The config tools (`config_read` / `config_get_project_config`,
  `config_write` / `config_update` / `config_set_project_config`) were **not**
  merged: they target semantically distinct files and use different argument
  shapes, so merging would lose detail.

## Other token reductions (same effort)

- Bridge `FormatResult` now serializes with `Formatting.None` and
  `NullValueHandling.Ignore`, and omits empty `warnings`/`errors` arrays.
- Several verbose payload defaults were flipped to opt-in:
  `revit_find_elements_by_parameter` and `revit_get_elements_info`
  (`includeTypeParameters` now defaults `false`), and
  `revit_get_electrical_circuits` (`includeElements` now defaults `false`).
- Response size in bytes is now logged per call (`ActivityLogger` /
  `LogEntry.ResponseSizeBytes`) so the worst offenders can be measured.

## Runtime tool profiles

The full 183-tool surface remains the default. Sessions that primarily inspect the
model can launch the bridge with `--tool-profile query`, which advertises 31 common
query/discovery tools, or use `--tool-names` with a comma-separated exact allow-list.
This reduces the MCP schema placed in model context without deleting or disabling any
add-in implementation.

See [`mcp-performance.md`](mcp-performance.md) for configuration, measured catalog
sizes, compact query responses, and the Revit-side parameter-read optimization.
</content>
</invoke>
