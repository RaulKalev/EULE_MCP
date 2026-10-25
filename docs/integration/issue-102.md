# Integration notes — #102 families and parameters

## Wiring

**App.cs** (`App.OnStartup`, next to the other registrations, after `handler` is created):

```csharp
RevitMCP.Addin.Tools.Families.FamiliesAndParametersToolRegistration.Register(handler);
```

**ApprovalSummaryBuilder.cs** — chain into the default case:

```csharp
_ => RevitMCP.Addin.Approval.FamiliesAndParametersApprovalSummaries.TryBuild(request) ?? $"Execute {request.ToolName}"
```

Class: `RevitMCP.Addin.Approval.FamiliesAndParametersApprovalSummaries` (`TryBuild(McpToolRequest)`). `revit_purge_unused`
and `revit_create_parameters` also implement `IApprovalSummaryTool` (live counts / the resolved shared file name); the
static class is their fallback.

## Bridge

File: `RevitMCP.Bridge/RevitMcpTools.FamiliesAndParameters.cs` (partial class, `RevitMcpTools.cs` untouched).

| Tool | Add-in permission | Bridge attribute |
|---|---|---|
| `revit_list_families` | ReadOnly | `ReadOnly = true` |
| `revit_preview_load_family` | ReadOnly | `ReadOnly = true` |
| `revit_load_family` | RequiresApproval | — |
| `revit_reload_family` | RequiresApproval | — |
| `revit_preview_purge_unused` | ReadOnly | `ReadOnly = true` |
| `revit_purge_unused` | DestructiveRequiresManualApproval | `Destructive = true` |
| `revit_list_project_parameters` | ReadOnly | `ReadOnly = true` |
| `revit_list_shared_parameters` | ReadOnly | `ReadOnly = true` |
| `revit_preview_create_parameters` | ReadOnly | `ReadOnly = true` |
| `revit_create_parameters` | RequiresApproval | — |
| `revit_bind_parameter_categories` | RequiresApproval | — |

All take `document` and resolve it through `ToolDocument.Resolve` (`revit_list_shared_parameters` works without an
open document when `document` is not passed).

## BridgeToolGroups.cs

- `edit` exact table: the eleven tool names above (block commented `// Families and parameters (#102)`).
- `Descriptions["edit"]`: appended "families (list, load, reload, purge unused), project and shared parameters (list,
  create, bind categories)".

## Other shared files touched (additive)

- `RevitMCP.Addin/Plans/PlanToolClassification.cs`: `PlanToolClassificationTests.EveryWriteToolTheBridgeAdvertisesIsClassified`
  fails for unclassified write tools, so `"revit_load_family"` was added to `CreatingPrefixes` and `"revit_purge_"`,
  `"revit_bind_parameter_"` to `ModifyingPrefixes` (`revit_create_parameters` and `revit_reload_family` were covered
  by `revit_create_` / `revit_reload_`). Other agents may add lines to the same arrays; keep all.
- `RevitMCP.Tests/RevitMCP.Tests.csproj`: one new `<ItemGroup>` at the end (#102 pure files).
- `README.md`: one Documentation line, one Capabilities row. `docs/family-types.md`: one cross-link paragraph.

## Files

- Pure (linked into tests): `RevitMCP.Addin/Families/FamilyLoadPolicy.cs`, `FamilyPathStore.cs`, `PurgePlanner.cs`;
  `RevitMCP.Addin/Parameters/ParameterSpecMap.cs`, `ParameterCreatePlanner.cs`.
- Revit side: `RevitMCP.Addin/Families/FamilyLoadOptions.cs`, `FamilyInventory.cs`, `ItemTransactions.cs`;
  `RevitMCP.Addin/Parameters/ParameterRevitTypes.cs`, `SharedParameterFileReader.cs`, `ParameterBindingReader.cs`.
- Tools: `RevitMCP.Addin/Tools/Families/ListFamiliesTool.cs`, `LoadFamilyTools.cs`, `PurgeUnusedTools.cs`,
  `FamiliesAndParametersToolRegistration.cs`; `RevitMCP.Addin/Tools/Parameters/ListProjectParametersTool.cs`,
  `ListSharedParametersTool.cs`, `CreateParametersTools.cs`, `BindParameterCategoriesTool.cs`.
- Tests: `FamilyLoadPolicyTests`, `FamilyPathStoreTests`, `PurgePlannerTests`, `ParameterSpecMapTests`,
  `ParameterCreatePlannerTests`, `FamiliesAndParametersSurfaceTests` (113 tests; suite 2601 → 2714).
- Docs: `docs/families-and-parameters.md`.

## Deviations from the issue

1. **Transactions.** The issue asks for one `Transaction` with a `SubTransaction` per file / parameter / batch. Errors
   Revit posts on commit are processed for the whole transaction, so one bad file could still undo the good ones. The
   writes therefore use a `TransactionGroup` with one `Transaction` per item (each with `RollBackOnErrorPreprocessor`),
   assimilated into one undo step (`ItemTransactions`). Same single undo, real per-item isolation. Needs an idle
   document (no open outer transaction); otherwise the tools refuse with `validation_failed`.
2. **ifExists values.** Added `overwrite` (the file's version, the project's parameter values kept — Revit's
   "Overwrite the existing version") besides `skip | overwriteParameters | overwriteAll`; it is the default of
   `revit_reload_family`, which refuses `skip`.
3. **Shared nested families.** The issue says "FamilySource.Family unless overwriteAll"; Revit's `FamilySource.Family`
   means *the version in the file being loaded*, so that reading would overwrite nested families on every policy.
   Implemented the other way round: `FamilySource.Project` (keep the project's nested families) unless `overwriteAll`,
   which takes them from the file. Documented in the table in docs.
4. **Purge cross-check.** `Document.GetUnusedElements(ISet<ElementId>)` exists in both reference assemblies (2024 and
   2026, `<since>2024</since>`), so it is used on both targets: a zero-instance type is purged only when Revit also
   lists it unused (`usedPerRevit` protection otherwise). The instance-count-only fallback runs when the call throws at
   run time, and the result says so.
5. **Purge apply restriction.** Added `elementIds` on `revit_purge_unused`: only ids the preview listed may be deleted.
   The preview returns them.
6. **Reload name guard.** `revit_reload_family` refuses a file whose name differs from the family name (Revit would
   add a second family instead of reloading). A family identical to its file reports "already matches" (success).
7. **Parameter group default** `Data` when `parameterGroup` is omitted; `Other` maps to `Insert(definition, binding)`
   without a group.
8. **Binding kind change** (instance ↔ type) on an existing parameter is refused as `invalid` (would drop values).
9. **Shared file path policy.** Only the explicit `sharedParameterFile` argument is checked against `FilePathPolicy`;
   the config key and Revit's own setting are trusted (admin/user configured, often on a network share).
10. **Benchmark report** not produced (needs Revit; the brief assigns it to the integrator).

## API notes per target

- Both targets: `ForgeTypeId` everywhere (`Definition.GetDataType/GetGroupTypeId`, `BindingMap.Insert/ReInsert(def,
  binding[, ForgeTypeId])`, `SpecTypeId.*`, `GroupTypeId.*`, `LabelUtils.GetLabelForSpec/GetLabelForGroup`,
  `SpecUtils.GetAllSpecs/IsValidDataType`, `ParameterUtils.GetAllBuiltInGroups/IsBuiltInGroup`). Every member used was
  checked in both `RevitAPI.xml` files; no `#if` was needed.
- `Document.GetUnusedElements`, `Document.LoadFamily(string, IFamilyLoadOptions, out Family)`,
  `SharedParameterElement.Lookup`, `InternalDefinition.SetAllowVaryBetweenGroups`, `Category.AllowsBoundParameters`:
  present in 2024 and 2026.
- `source=project` is a shared definition in `%LOCALAPPDATA%\RevitMCP\Parameters\RevitMCP-project-parameters.txt`
  (group `RevitMCP`): neither API can create a non-shared project parameter.

## Manual Revit test (2026, then 2024)

1. `revit_list_families category="Electrical Fixtures" includeTypes=true` — counts match Revit's browser.
2. Put three `.rfa` files in a folder, one of them already loaded. `revit_preview_load_family folder=<it>` → 2
   `willLoad`, 1 `willSkip`. `revit_load_family folder=<it>` (approve) → 2 `loaded`, 1 `skipped`; one Undo removes both.
   Add a renamed `.txt` as `.rfa` (damaged): it is `failed`, the others still load.
3. `revit_place_family_instances` with a type of a newly loaded family — places without a manual Activate.
4. `revit_reload_family family=<loaded one>` (no path) → reloads from the recorded file; `ifExists=overwriteParameters`
   writes the file's type values.
5. Set `parameters.sharedParameterFile` in the project config (or pass `sharedParameterFile`).
   `revit_list_shared_parameters` → groups and GUIDs; Manage > Shared Parameters still shows the user's previous file.
6. `revit_preview_create_parameters` with two shared definitions bound to Electrical Fixtures and Data Devices →
   `willCreate`; a missing name → `missingDefinition`; check the file's timestamp is unchanged.
   `revit_create_parameters` (approve) → both in Manage > Project Parameters with the right type, group, binding and
   categories; `revit_get_element_parameters` on a fixture shows them. Repeat with `ifExists=rebind` and one more
   category → `willRebind`, the old categories stay.
7. `revit_bind_parameter_categories name=<one> removeCategories=[...all]` → refused (last category).
8. `revit_create_parameters` with `source=project` → result says it is a shared definition in the scratch file.
9. On a model with known unused types: `revit_preview_purge_unused passes=2` (protected list shows in-place, device map
   types, the default tag type as `usedPerRevit`), then `revit_purge_unused elementIds=<preview ids>` (manual
   approval) → counts match; one Undo in Revit restores them.

## Left out

- Benchmark report (integrator runs it).
- No live Revit run was possible here: everything above the pure layer is compiled on both targets but untested
  against a model.
