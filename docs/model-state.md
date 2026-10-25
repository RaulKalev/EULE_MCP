# Model state: phases, design options, worksets and the model health audit (#109)

Phases, design options and worksets decide whether an element exists in a view, belongs to a variant, or can be
edited from this local. The connector reads them into the query surface, writes the two things the Revit API lets
you write (element phases and view phases), and runs a deterministic **model health audit** an agent can run before
and after unattended work.

| Tool | Permission | Group | Purpose |
|---|---|---|---|
| `revit_list_phases` | read-only | query | Phases in sequence, the views using each, phase filters, optional element counts |
| `revit_list_design_options` | read-only | query | Option sets and options, primary flag, optional element counts |
| `revit_preview_set_phase` | read-only | edit | What `revit_set_phase` would change, per element |
| `revit_set_phase` | approval | edit | Set Phase Created / Phase Demolished on elements |
| `revit_set_view_phase` | approval | edit | Set a view's Phase and Phase Filter |
| `revit_audit_model` | read-only | query | Health report, published as a model page |

Plus `includeModelState` / `includeOwnership` on `revit_get_elements_info`, `revit_find_elements_by_parameter` and
`revit_get_element_parameters`, four filter pseudo-parameters, and `phase` / `option` filters in
`revit_graph_query find` ([model-graph.md](model-graph.md)).

Workset listing, ownership, checkout and moving elements to another workset (`revit_list_worksets`,
`revit_get_ownership`, `revit_set_workset`) are in the worker document tools ([worker-document.md](worker-document.md));
this page only reads worksets into queries and the audit.

## Model state in queries

`includeModelState=true` adds four fields to each returned element; `includeOwnership=true` adds a fifth. Both are
off by default and the fields are left out of the JSON when not requested, so default payloads are byte-for-byte what
they were before.

| Field | Source | Empty string means |
|---|---|---|
| `phaseCreated` | `PHASE_CREATED`, as the phase name | the element has no phase (annotations, views, types) |
| `phaseDemolished` | `PHASE_DEMOLISHED`, as the phase name | not demolished, or no phase |
| `designOption` | `Element.DesignOption.Name` | main model |
| `workset` | the element's workset name (cached per query) | not workshared |
| `ownedBy` | `WorksharingUtils.GetCheckoutStatus` — who has it checked out (you or someone else) | nobody, or not workshared |

`revit_get_elements_info` and `revit_find_elements_by_parameter` return them as `PhaseCreated`, `PhaseDemolished`,
`DesignOption`, `Workset`, `OwnedBy` (the element DTO's casing, also in `compact=true`);
`revit_get_element_parameters` as `phaseCreated` … `ownedBy`. Ownership is a separate flag because it asks the
worksharing layer per element and is slower.

```
revit_get_elements_info elementIds=[2123366,2123367] includeModelState=true compact=true
→ { "ElementId": 2123366, "Category": "Data Devices", …, "PhaseCreated": "Uus", "PhaseDemolished": "",
    "DesignOption": "", "Workset": "EN_Nõrkvool" }
```

### Filter pseudo-parameters

The filters of every engine-based query tool (`revit_find_elements_by_parameter`, `revit_get_elements_info`,
`revit_select_elements_by_query`, grouping and Excel exports) accept four pseudo-parameters, answered from the same
reader as `includeModelState` (whole-name match, case- and whitespace-insensitive):

| Name | Compares |
|---|---|
| `Phase Created` | the created phase name |
| `Phase Demolished` | the demolished phase name; empty when not demolished |
| `Design Option` | the option name; empty in the main model |
| `Workset` | the workset name |

```
revit_find_elements_by_parameter category="Electrical Fixtures"
  filters='[{"parameterName":"Phase Created","operator":"equals","value":"Uus"}]'
revit_find_elements_by_parameter category="Data Devices"
  filters='[{"parameterName":"Design Option","operator":"notEquals","value":""}]'        # in any option
revit_find_elements_by_parameter category="Lighting Fixtures"
  filters='[{"parameterName":"Phase Demolished","operator":"isEmpty"}]'                    # not demolished
```

Revit's own parameters of these names are element ids or integers whose display text varies ("Main Model", "None");
the pseudo-parameters compare the plain names. Tools that do not use the query engine (the uncircuited-element
tools, `revit_set_parameter`'s filters) keep matching Revit's own parameters, as before.

## Phases

`revit_list_phases` returns `phases[] {id, name, sequenceNumber, viewCount, viewsUsingAsPhase[], elementCounts?}`
and `phaseFilters[] {id, name, newStatus, existingStatus, demolishedStatus, temporaryStatus, viewCount}` (the status
values are Revit's `PhaseStatusPresentation`: `DontShow`, `ShowByCategory`, `ShowOverriden`). `countElements=true`
scans the non-type elements once and adds `{created, demolished}` per phase; above the query scan cap
(`QueryLimits.MaxScanElements`, 20 000) the counts are left out with a note — use `revit_graph_summary`
(`elementsByPhase`) or the audit for model-wide figures. Views are listed up to 200 per phase.

### Setting element phases

```
revit_preview_set_phase elementIds=[…10 ids…] phaseDemolished="Uus"
revit_set_phase elementIds=[…10 ids…] phaseDemolished="Uus"
revit_set_phase useSelection=true phaseCreated="Olemasolev" phaseDemolished=none
```

- `elementIds` (max 2 000) or `useSelection=true`; `phaseCreated` and/or `phaseDemolished` as a phase **name or id**.
  `phaseDemolished=none` clears it (not demolished). An empty argument keeps that parameter.
- Order: the demolished phase may not come before the created one (`phase_order_invalid`), checked against the
  request and against each element's current other phase. Created and demolished in the same phase is allowed — Revit
  shows it as *Temporary*. When both move later, the demolished phase is written first so no intermediate state is
  out of order.
- Elements without phase parameters (annotations, views, types) are `skipped` with a reason and never block.
  Read-only parameters (group members, elements that follow their host) are `read_only`; missing ids `not_found`.
- `atomic=true` (default): a read-only, out-of-order or missing element rejects the whole request before a
  transaction opens. `atomic=false`: each element in its own sub-transaction; failures are reported, the rest kept.
- One transaction, one undo step. The preview and the result list
  `{id, category, name, phaseCreated {before, after}, phaseDemolished {before, after}, status, reason}`.
- An unknown phase fails with `phase_not_found` and `data.availablePhases`.

### Setting view phases

```
revit_set_view_phase viewIds=[312004,312009] phase="Uus" phaseFilter="Show New"
revit_set_view_phase viewIds=[312004] phaseFilter=none
```

`VIEW_PHASE` and `VIEW_PHASE_FILTER`, by name or id. A view whose template controls the setting is
`controlled_by_template` (change the template or remove it from the view); sheets and legends are `skipped`. Same
`atomic` rule and one undo step. A phase filter that does not exist fails with `phase_filter_not_found`.

## Design options

`revit_list_design_options` returns `sets[] {setId, setName, options[] {id, name, isPrimary, elementCount?}}`
(`countElements=true` counts with `ElementDesignOptionFilter`). Its result always carries
`status: design_option_set_only` with the reason:

- **Membership cannot be written.** The Revit API has no call that moves an element into or out of a design option;
  an element is created in the active option in the Revit UI. There is deliberately no `revit_set_design_option`.
- **Views per option are not reported.** A view's design option visibility has no reliable API read, so the tool
  lists sets, options, the primary flag and counts only.

Use the `Design Option` pseudo-parameter to find elements in an option (`notEquals ""`) or in the main model
(`isEmpty`), and `revit_graph_query find option=<name>` / `option=none` to route by it.

## Model health audit

`revit_audit_model` builds a deterministic report and publishes it as a model page ([model-pages.md](model-pages.md)):
one page per model (re-running replaces it and keeps the link), Estonian page language, one plain table per list,
element rows with `data-revit-id` (they select the element in Revit; link rows select the link instance). The JSON
result mirrors the page: `sections[] {name, title, status, counts, flagged, notes, items (first 200), itemsTotal}`,
plus `status`, `pageUrl` and the threshold sources.

```
revit_audit_model                                   # every section, publish the page
revit_audit_model sections=["warnings","views","worksets"] open=true
revit_audit_model publish=false document="Kool_EN"
```

Run it before handing unattended work to a job and again afterwards: the two pages (or JSON results) show what moved.

Each section is timed and wrapped: if one collector fails, the section shows `error` with the message and the rest of
the report is still produced. Lists are capped at 500 rows per table on the page (the figures stay complete), and the
page is shortened further when it would exceed the model page size cap (2 MB).

| Section | Figures (keys) | Source |
|---|---|---|
| `summary` | `summary.elements`, `summary.categories`, `file.sizeMb`; model info, elements per category (top 30) | one `FilteredElementCollector` pass over non-type elements (shared with types, phases and worksets); `doc.PathName` on disk |
| `warnings` | `warnings.total`, `warnings.kinds`, `warnings.errors`; groups by description | `Document.GetWarnings()` grouped like `revit_get_warnings` |
| `types` | `types.unused`, `types.total`, `types.familiesWithUnused`; per family | family symbols with no instance (`GetTypeId` of every element) |
| `views` | `views.total`, `views.unplaced`, `views.unplacedPercent`, `views.withoutTemplate`, `views.templates`; per view type; unplaced views | printable views, no templates, sheets or schedules; placed = `GetAllPlacedViews` (as `revit_find_unplaced_views`) |
| `sheets` | `sheets.total`, `sheets.withoutViews`, `sheets.placeholders`, `sheets.withRevisions`, `sheets.revisions`, `sheets.revisionsNotIssued` | sheets, schedule instances, revisions |
| `rooms` | `rooms.total`, `rooms.unplaced`, `rooms.notEnclosed`, `rooms.redundant` | `Room.Location == null`; area 0 split by Revit's "multiple rooms in one region" warning |
| `imports` | `imports.instances`, `imports.cadImports`, `imports.cadLinks`, `imports.dwg`, `imports.viewSpecific`, `imports.importedLinePatterns` | `ImportInstance.IsLinked`; line patterns named `IMPORT-*` |
| `families` | `families.inPlace`, `families.inPlaceInstances` | `Family.IsInPlace` |
| `groups` | `groups.modelTypes`, `groups.detailTypes`, `groups.attachedDetailTypes`, `groups.modelInstances`, `groups.detailInstances`, `groups.unusedTypes` | group types and instances |
| `phases` | `phases.count`, `phases.filters`, `phases.demolishedElements`; created/demolished per phase | the shared scan and `revit_list_phases`' reader |
| `designOptions` | `designOptions.sets`, `designOptions.options`, `designOptions.elements` | `revit_list_design_options`' reader |
| `worksets` | `worksets.user`, `worksets.ownedByOthers`, `worksets.elementsOwnedByOthers`, `worksets.closed`; elements per workset | user worksets; ownership **sampled**: the first 2 000 elements (the section says so) |
| `links` | `links.total`, `links.rvt`, `links.ifc`, `links.dwg`, `links.notLoaded`, `links.absolutePath`, `links.unpinned` | `LinkInventory` (as `revit_list_open_documents`): load status, `PathType`, pinning |
| `graph` | `graph.stale`, `graph.nodes`, `agent.checkpoints`, `agent.activePlans` | the graph status service, checkpoints and plans of this model |

Notes on scope: the whole-model scan stops at 400 000 elements (the affected sections then say they are sampled);
symbols referenced only by type parameters (nested, profile and panel types) can appear as unused although Revit's
Purge keeps them; imported line *styles* are not counted (Revit does not mark them); "mixed" groups are reported as
attached detail group types (model groups with detail annotation attached); not-workshared models show the worksets
section as `skipped`, models without rooms the rooms section.

### Thresholds

Every figure is "lower is better": a value strictly **above** `warn` gets a warn badge, strictly above `bad` a bad
badge; a section takes the worst badge of its figures (a failed collector is `error`). Figures without a rule (totals,
counts by type) carry no badge. Company defaults:

| Key | warn above | bad above |
|---|---:|---:|
| `warnings.total` | 100 | 500 |
| `types.unused` | 100 | 500 |
| `views.unplacedPercent` | 20 | 50 |
| `sheets.withoutViews` | 0 | — |
| `rooms.unplaced` | 0 | 50 |
| `rooms.notEnclosed` | 0 | 20 |
| `rooms.redundant` | 0 | — |
| `imports.cadImports` | 0 | 10 |
| `imports.importedLinePatterns` | 20 | — |
| `families.inPlace` | 10 | 50 |
| `groups.unusedTypes` | 10 | — |
| `worksets.ownedByOthers` | 0 | — |
| `worksets.elementsOwnedByOthers` | 0 | — |
| `links.notLoaded` | 0 | 5 |
| `links.absolutePath` | 0 | — |
| `links.unpinned` | 0 | — |
| `file.sizeMb` | 300 | 600 |
| `graph.stale` | 0 | — |

Override them per key under `audit.thresholds` in the company, user or project config (`config_update`, or the
project's `.rktools` config); scopes apply in that order, so a project wins. An object sets `warn` and/or `bad`
(`null` clears one), a number sets `warn`, `false` turns the badge off:

```json
{
  "audit": {
    "thresholds": {
      "warnings.total": { "warn": 200, "bad": 1000 },
      "families.inPlace": 5,
      "imports.cadImports": false,
      "rooms.unplaced": { "bad": null }
    }
  }
}
```

Unknown keys and invalid values are reported in the result's warnings and on the page; the rest still applies.

### Skill task

`audit.model` runs the same audit inside a skill and turns every warn/bad figure into an issue for the skill's
Excel / HTML report; `company.project.pre-delivery` includes it. See [skills.md](skills.md#task-auditmodel-model-health-audit-109).

## Status codes

| Code | Where | Meaning |
|---|---|---|
| `phase_not_found` | set phase, set view phase | the named phase does not exist; `data.availablePhases` lists them |
| `phase_order_invalid` | set phase | demolished before created (request or element) |
| `controlled_by_template` | set view phase | the view template controls the setting |
| `phase_filter_not_found` | set view phase | the named phase filter does not exist |
| `design_option_set_only` | list design options | informational: options can be read, not assigned |
