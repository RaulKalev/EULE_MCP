# Integration notes: #106 MEP routing (cable tray and conduit runs)

## Wiring

**App.cs** (`OnStartup`, next to the other registrations):

```csharp
RevitMCP.Addin.Tools.Mep.MepToolRegistration.Register(handler);
```

**Approval summaries**: chain `RevitMCP.Addin.Approval.MepApprovalSummaries.TryBuild(request)` into the default case of
`ApprovalSummaryBuilder.Build`, e.g. `_ => MepApprovalSummaries.TryBuild(request) ?? $"Execute {request.ToolName}"`.
`CreateMepRunTool` also implements `IApprovalSummaryTool` (live type name, level, plan from the model); the static class is
the fallback and the only summary for `revit_connect_mep_curves`.

**Bridge file**: `RevitMCP.Bridge/RevitMcpTools.Mep.cs` (partial class, no edits to `RevitMcpTools.cs`).

| Tool | Add-in permission | Bridge |
|---|---|---|
| `revit_list_mep_curve_types` | ReadOnly | `ReadOnly = true` |
| `revit_preview_create_mep_run` | ReadOnly | `ReadOnly = true` |
| `revit_create_mep_run` | RequiresApproval | write |
| `revit_connect_mep_curves` | RequiresApproval | write |
| `revit_get_mep_run_summary` | ReadOnly | `ReadOnly = true` |

**BridgeToolGroups.cs**: new exact-table block `("electrical", new[] { "revit_list_mep_curve_types",
"revit_preview_create_mep_run", "revit_create_mep_run", "revit_connect_mep_curves", "revit_get_mep_run_summary" })`
(placed after the `memory` block; "preview" contains "view", and conduit/mep names match no electrical keyword).
`Descriptions["electrical"]` gained ", cable tray and conduit runs (containment)".

**Other shared files touched (additive)**:
- `RevitMCP.Addin/Plans/PlanToolClassification.cs`: `"revit_connect_mep_"` added to `CreatingPrefixes` (it places
  fittings). Needed by `PlanToolClassificationTests.EveryWriteToolTheBridgeAdvertisesIsClassified`;
  `revit_create_mep_run` is already covered by `revit_create_`.
- `RevitMCP.Tests/RevitMCP.Tests.csproj`: one new `<ItemGroup>` at the end linking `Mep/MepPoint.cs`, `MepRunSpec.cs`,
  `MepRunPlanner.cs`, `MepConnectMath.cs`, `MepRunSummaryMath.cs`, `MepApprovalText.cs`.
- `README.md`: one Documentation entry, one Capabilities row (tool count 5).

## Files

- Pure (linked into tests): `RevitMCP.Addin/Mep/MepPoint.cs` (mm point + `MepMath`), `MepRunSpec.cs` (arguments,
  `MepKind`, `MepStatus`, `MepArgs`), `MepRunPlanner.cs` (+ `MepSizeMath`), `MepConnectMath.cs`, `MepRunSummaryMath.cs`,
  `MepApprovalText.cs`.
- Revit: `Mep/MepRunSchema.cs` (ES), `MepTypeCatalog.cs`, `MepConnectorFinder.cs`, `MepRunBuilder.cs`,
  `MepRunSummaryService.cs`; tools in `Tools/Mep/`: `ListMepCurveTypesTool.cs`, `CreateMepRunTools.cs` (preview + create +
  executor), `ConnectMepCurvesTool.cs`, `MepRunSummaryTool.cs`, `MepToolRegistration.cs`; `Approval/MepApprovalSummaries.cs`.
- Tests: `MepRunPlannerTests`, `MepConnectMathTests`, `MepRunSpecParsingTests` (incl. approval text),
  `MepRunSummaryMathTests`. Suite: 2601 → 2659 passing.
- Docs: `docs/mep-routing.md`.

## Deviations from the issue

1. **Routing preferences**: tray and conduit types keep their fittings as type parameters (`RBS_CURVETYPE_DEFAULT_*`:
   horizontal bend/bend/elbow, elbow up/down for vertical bends, tee, cross, transition, union), not routing
   preferences. A fitting kind is `available` when such a parameter names a family **or**
   `RoutingPreferenceManager.GetNumberOfRules(...) > 0`. An extra kind `verticalElbow` is reported because tray bends into
   risers use separate families. Types with `IsWithFitting == false` count elbows as available (Revit draws the bend
   itself); to be confirmed in the manual test.
2. **`routing_preference_missing`** does not block the create: the preview returns it as its status with the missing kinds,
   the create warns and goes on, so the segments exist and each bend reports `fitting_failed` with Revit's message (as the
   manual test and acceptance criteria require).
3. **ES schema name** is `RevitMCP_MepRun` (GUID `b8f0c3a2-6d4e-4c71-9e25-3f1a7d6b9c04`): Revit schema names do not
   allow dots. Fields `RunId`, `Kind`, `Role` (segment|fitting), `CreatedUtc`; public read/write like the circuit-arcs marker.
4. **z from level**: only points without `z` get `level elevation + offsetMm`; points with their own `z` keep it (risers).
   Elevation = `Level.ProjectElevation`; the default level is the one whose ProjectElevation is nearest the first point.
5. **Sizes optional**: without widthMm/heightMm/diameterMm Revit's default size is used (warning). Tray width/height are
   free values; `CableTraySizes` is listed as the usual choices. Conduit diameter is checked against the type's standard
   (`CONDUIT_STANDARD_TYPE_PARAM` display value matched to `ConduitSizeSettings` keys; with one standard in the project it
   is used; when the standard cannot be told the check is skipped).
6. **`revit_connect_mep_curves`** gained `maxGapMm` (default 5000) for `too_far`; it also returns `no_open_end`.
   Parallel side-by-side ends are reported as `skew_lines` (no single fitting joins them).
7. **Auto end connection** searches tray/conduit curves only (not fittings), as the issue says.
8. **Preview** does no rolled-back trial run (static checks only); the snap candidates and fitting availability come
   from the live model.
9. `revit_get_mep_run_summary` lengths use `CURVE_ELEM_LENGTH` (what schedules sum), falling back to the location curve.
   `types` filters segments only; fittings are counted by their own family type.

## API notes (both targets)

Verified in both reference assemblies (2024.1 net48, 2026 net8): `CableTray.Create(doc, typeId, p1, p2, levelId)`,
`Conduit.Create(...)`, `Document.Create.NewElbowFitting/NewUnionFitting/NewTransitionFitting(Connector, Connector)`,
`MEPCurve.ConnectorManager`, `MEPCurveType.RoutingPreferenceManager`, `RoutingPreferenceManager.GetNumberOfRules`,
`CableTraySizes.GetCableTraySizes(doc)` (enumerates `MEPSize`), `ConduitSizeSettings.GetConduitSizeSettings(doc)`
(`KeyValuePair<string, ConduitSizes>`), `CableTrayType/ConduitType.IsWithFitting`, BuiltInParameters
`RBS_CABLETRAY_WIDTH_PARAM`, `RBS_CABLETRAY_HEIGHT_PARAM`, `RBS_CONDUIT_DIAMETER_PARAM`, `CONDUIT_STANDARD_TYPE_PARAM`,
`CURVE_ELEM_LENGTH`, `RBS_CURVETYPE_DEFAULT_*`. No `#if` was needed. Both targets build with no new warnings.

## Manual Revit test (2026 and 2024)

1. `revit_list_mep_curve_types` — check a tray type with fittings shows `elbow`/`verticalElbow: available` and the family
   names match the type properties; a conduit type lists its standard's diameters.
2. Tray run on level 1, 5 points, `fittings=auto`, `widthMm=300`, `heightMm=60`, `offsetMm=2800` (no z): preview, then
   create. Expect 4 segments, 3 elbows `created`. `revit_get_mep_run_summary runId=<runId>`: 2 open ends (the run ends)
   only, 0 disconnected segments.
3. Conduit run with a 45° bend using a conduit type whose *Bend* is empty: segments created, the bend reports
   `fitting_failed` with Revit's message, nothing else lost (`atomic=true` still commits).
4. Connect the conduit end to the tray with `connectEnd=auto` (expect `no_candidate`: kinds differ) and with
   `revit_connect_mep_curves` between two conduits: coincident in line → `connected` (`connect_to`), gap in line →
   `extend`, crossing → `elbow`, parallel offset → `skew_lines`, already joined → `already_connected`.
5. A second tray run starting within 50 mm of the first run's open end with `connectStart=auto` → `connected`; a run start
   near a connected end → `no_candidate` (never joins a connected connector).
6. Summary totals vs. a Revit cable tray schedule (Length sum per type): equal within 1 mm per segment.
7. Undo once: the whole run (segments, elbows, end joins) disappears.
8. Verify for `IsWithFitting == false` tray types that elbows are created (deviation 1) and that
   `CONDUIT_STANDARD_TYPE_PARAM` matches the standard names (deviation 5).

## Not done

- Benchmark (needs Revit; the integrator runs it).
- Tee/cross fittings, cable fill, slope, obstacle-aware routing: out of scope per the issue.
