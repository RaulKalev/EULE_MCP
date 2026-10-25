# Integration notes — #109 model state (phases, design options, worksets, model health audit)

## Wiring

- **App.cs** (`App.OnStartup`, after the other registrations, `handler` is the `RevitMCP.Addin.Services.ExternalEventHandler`):

  ```csharp
  RevitMCP.Addin.Tools.ModelState.ModelStateToolRegistration.Register(handler);
  ```

- **Approval summaries**: `RevitMCP.Addin.Approval.ModelStateApprovalSummaries.TryBuild(request)` — chain it into the
  default case of `ApprovalSummaryBuilder.Build` (returns null for every tool other than `revit_set_phase` and
  `revit_set_view_phase`).
- **Bridge**: new file `RevitMCP.Bridge/RevitMcpTools.ModelState.cs` (also holds the `IncludeModelStateDescription` /
  `IncludeOwnershipDescription` constants used by the three query tools in `RevitMcpTools.cs`).

| Tool | Add-in permission | Bridge |
|---|---|---|
| `revit_list_phases` | ReadOnly | `ReadOnly = true` |
| `revit_list_design_options` | ReadOnly | `ReadOnly = true` |
| `revit_preview_set_phase` | ReadOnly | `ReadOnly = true` |
| `revit_set_phase` | RequiresApproval | not read-only |
| `revit_set_view_phase` | RequiresApproval | not read-only |
| `revit_audit_model` | ReadOnly (publishing the page is additive output) | `ReadOnly = true` |

- **BridgeToolGroups** exact table: `query` += `revit_list_phases`, `revit_list_design_options`, `revit_audit_model`;
  `edit` += `revit_preview_set_phase`, `revit_set_phase`, `revit_set_view_phase` (keeps `revit_set_view_phase` out of
  the `view` keyword rule). Group descriptions of `query` and `edit` extended (phases, design options, audit / element
  and view phases).
- **Tests csproj**: one new `<ItemGroup>` at the end linking `ModelState/PhaseSetPlanner.cs`, `AuditRules.cs`,
  `AuditModels.cs`, `AuditReportHtml.cs`.
- **Skill registry**: one line in `Skills/SkillTaskRegistry.cs` (`new Skills.Tasks.Audit.AuditModelTask()`), and the
  built-in `company.project.pre-delivery` skill in `Skills/SkillLoader.cs` gained an enabled `audit.model` task after
  the clash task (the clash task replaces `issueDtos`, so the audit must come after it) and before the merge.

## Shared / existing files touched

- `RevitMCP.Bridge/RevitMcpTools.cs`: `includeModelState` + `includeOwnership` on `revit_get_elements_info`,
  `revit_find_elements_by_parameter`, `revit_get_element_parameters` (as briefed) **and** `phase` / `option` on
  `revit_graph_query` (two parameters, two dictionary entries, the `find (...)` line of its description). The issue asks
  for graph `find` phase/option filters, and the argument-contract tests forbid an add-in-only argument; I am the only
  agent editing this file, so this cannot conflict. Revert those lines if you prefer to keep the method untouched —
  the add-in then simply never receives the filters.
- `Query/`: `ElementInfoDto.cs`, `ElementQueryOptions.cs`, `ElementQueryEngine.cs`, `ParameterFilterEvaluator.cs`
  (holds `ElementIdentityParameters`; there is no separate `ElementIdentityParameters.cs`), and
  `ElementQueryResponseFormatter.cs` (compact DTO carries the new fields, null-ignored).
- `Graph/`: `RevitGraphExtractor.cs` (workset cache moved to `ModelState/WorksetNameReader`, `phase`/`option` extra
  hints on elements and panels; `LevelOf` untouched), `GraphSchema.cs` (version 2 → 3, `ExtraPhase`/`ExtraOption`),
  `GraphDatabase.cs` (`Find(..., phase, option)` via `json_extract`, `Summarize().ElementsByPhase`), `GraphModels.cs`
  (`ElementsByPhase`), `SCHEMA.md`.
- `Tools/Graph/GraphQueryTool.cs` (reads `phase`, `option`, warns on a schema < 3 graph), `Tools/Graph/GraphSummaryTool.cs`
  (`elementsByPhase`).
- `Tools/GetElementsInfoTool.cs`, `Tools/FindElementsByParameterTool.cs`, `Tools/GetElementParametersTool.cs` (flags).
- Tests: `ParameterFilterEvaluatorTests.cs`, `GraphDatabaseTests.cs` (additions only).
- Docs: new `docs/model-state.md`; `docs/model-graph.md` (hints, filters), `docs/skills.md` (audit task), README (one
  Capabilities row, one Documentation line).

## Deviations from the issue

1. **Default payloads unchanged** — the five new DTO fields are `NullValueHandling.Ignore`; `revit_get_element_parameters`
   keeps its anonymous default shape and switches to a typed row only when a flag is set. Test: `ElementInfoDtoJsonTests`.
2. **Pseudo-parameter fallback** — the four model-state pseudo-parameters are answered from the model state only where the
   caller reads it (the query engine). Callers that pass only `ParameterReader.ReadIdentity` (uncircuited-element tools,
   `revit_set_parameter`) keep matching Revit's own `Workset` / `Phase Created` parameters, exactly as before. They are
   *not* identity names, so `revit_group_by_parameter parameterName=Workset` is unchanged.
3. **Same created and demolished phase is allowed** (Revit's Temporary status). The issue says "must be after"; only
   demolished *before* created is `phase_order_invalid`.
4. **Group members**: a read-only phase parameter on a group member is reported as `read_only` with the group id; there
   is no attempt to edit the group.
5. **`revit_set_view_phase`** has no preview tool (the issue lists none); `phaseFilter=none` clears the filter; an unknown
   filter fails with an extra status `phase_filter_not_found`. It has the same `atomic` rule as `revit_set_phase`.
6. **Design options**: views per option are not reported and membership cannot be set — the list result always carries
   `status: design_option_set_only` with that note (also in docs).
7. **Audit summary counts** come from the audit's own single element pass (needed anyway for unused types, phases and
   worksets) rather than from the graph; the graph section reports freshness via the graph status service.
8. **Unused types** are counted inline (no #102 `PurgePlanner` dependency): symbols with no element whose `GetTypeId`
   points at them; the page says type-parameter-only references may show as unused.
9. **Warnings** reuse the merged `Checkpoints/RevitWarningReader` + `WarningDiff.Group` (pure, from #98).
10. **Rooms**: redundant vs not enclosed is split by Revit's `BuiltInFailures.RoomFailures.RoomsInSameRegionRooms`
    warning; placed rooms with area 0 without it are "not enclosed".
11. **Groups "mixed instances"** is interpreted as attached detail group types (model + detail); no other API notion fits.
12. **Imported line styles** are not counted (Revit does not mark them); imported line patterns (`IMPORT-*`) are.
13. **Thresholds** are "strictly above" (warnings > 500 bad, in-place > 10 warn, imports > 0 warn, unplaced views > 20 %
    warn …) and override per key from `audit.thresholds`, applied company → user → project.
14. **Page id**: one page per model (`audit-<modelId>`), so re-running replaces the page and keeps the link.
15. Both list tools live in `Tools/ModelState/ListPhasesTool.cs` (`ListPhasesTool` and `ListDesignOptionsTool`).

## API notes per target

No `#if` was needed: everything used exists in both the 2024 and 2026 APIs (`ElementId(long)`, `ElementId.Value`,
`Element.DesignOption`, `ElementDesignOptionFilter`, `PhaseFilter.GetPhaseStatusPresentation`,
`View.GetNonControlledTemplateParameterIds`, `WorksharingUtils.GetCheckoutStatus`, `BuiltInFailures.RoomFailures`,
`ExternalFileReference.PathType`). `WorksetId.IntegerValue` is used for the workset cache as before. Both
`net48` and `net8.0-windows` build with 0 errors. SQLite `json_extract` (JSON1, built into e_sqlite3) is used for
the graph filters and is covered by `GraphDatabaseTests`.

## Tests

`dotnet test RevitMCP.Tests/RevitMCP.Tests.csproj -c Release`: 2601 → 2660 pass. New: `PhaseSetPlannerTests`,
`AuditRulesTests` (thresholds, overrides, section status, issues), `AuditReportHtmlTests` (tables, `data-revit-id`,
escaping, no external URLs, size cap, page id, JSON mirror), `ElementInfoDtoJsonTests`; additions in
`ParameterFilterEvaluatorTests` (pseudo-parameters, fallback) and `GraphDatabaseTests` (phase/option filters,
`elementsByPhase`, schema bump). Benchmark not run (needs Revit).

## Manual Revit test (2026 and 2024)

Model with two phases (e.g. Olemasolev, Uus), one design option set with two options, worksharing on.

1. `revit_graph_status` → stale (schema 2 → 3); `revit_graph_build`; then
   `revit_graph_query operation=find kind=element phase=Uus` and `option=none` return ids; `revit_graph_summary`
   shows `elementsByPhase`.
2. `revit_find_elements_by_parameter category="Electrical Fixtures" filters='[{"parameterName":"Phase Created","operator":"equals","value":"Uus"}]' includeModelState=true`
   → only new sockets, each with `PhaseCreated`, `DesignOption`, `Workset`. Same call without `includeModelState`:
   payload identical to before.
3. `Design Option notEquals ""` finds the option elements; `revit_list_design_options countElements=true` matches the
   Design Options dialog.
4. `revit_list_phases countElements=true` matches Phasing dialog order and the view properties.
5. `revit_preview_set_phase elementIds=[10 ids] phaseDemolished=Uus`, then `revit_set_phase` (approve) → one undo step;
   the 10 elements disappear in a "Show New" view of phase Uus. `phaseCreated=Uus phaseDemolished=Olemasolev` →
   `phase_order_invalid`; an annotation id → `skipped`; a group member → `read_only`.
6. `revit_set_view_phase viewIds=[a view with a template that controls Phase]` → `controlled_by_template`.
7. `revit_audit_model open=true` → page opens; compare warnings count (Manage › Warnings), unplaced views (browser
   / `revit_find_unplaced_views`) and worksets (Worksets dialog); clicking a row selects the element. Time a 100 k
   element model (target < 30 s); the worksets section says it sampled.
8. Run `company.project.pre-delivery`: its Excel report lists `ModelAudit.*` issues.

## Left out

- No preview for `revit_set_view_phase`; no design option writes (API limit); no purge, warning details or phase
  filter creation (out of scope).
- README tool count (195) not updated — the integrator knows the final number.
- `ReadOnlyFlagTests.AdditiveOutput` not edited (no test requires it); add `revit_audit_model` there if you want the
  list to document every page-publishing read-only tool.
