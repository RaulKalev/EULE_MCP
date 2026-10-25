# Integration notes — #104 view creation

## Registration (App.OnStartup)

```csharp
RevitMCP.Addin.Tools.Views.ViewCreationToolRegistration.Register(handler);
```

## Approval summaries

`RevitMCP.Addin.Approval.ViewCreationApprovalSummaries.TryBuild(request)` — chain into `ApprovalSummaryBuilder`'s
default case. It covers `revit_create_views` and `revit_set_view_extent` from the request alone. Both tools also
implement `IApprovalSummaryTool` (live summary with the model: resolved level counts and names, the view's name and
type), which the handler prefers.

## Bridge

File: `RevitMCP.Bridge/RevitMcpTools.ViewCreation.cs` (partial class; uses `ToJToken`, `FormatResult`,
`DocumentArgDescription` from `RevitMcpTools.cs`).

| Tool | Add-in permission | Bridge ReadOnly |
|---|---|---|
| `revit_list_view_family_types` | ReadOnly | true |
| `revit_preview_create_views` | ReadOnly (rolled-back trial run) | true |
| `revit_create_views` | RequiresApproval | false |
| `revit_preview_set_view_extent` | ReadOnly | true |
| `revit_set_view_extent` | RequiresApproval | false |

## BridgeToolGroups

No entries added: every new name contains `view` and no earlier rule matches, so the `view` rule puts all five into
`views` (checked by `BridgeToolGroupsTests`). `PlanToolClassification`: `revit_create_` → creates,
`revit_set_` → modifies (existing prefixes).

## Tests csproj

One new `<ItemGroup>` at the end linking `Documentation/Views/ViewCreateSpec.cs`, `ViewCreatePlanner.cs`,
`SectionBoxMath.cs`, `ViewExtentMath.cs`. New tests: `SectionBoxMathTests`, `ViewCreatePlannerTests`,
`ViewCreateSpecParsingTests`, `ViewExtentServiceTests` (59 tests). Suite: 2601 → 2660, green.

## Files

- `RevitMCP.Addin/Documentation/Views/`: `ViewCreateSpec.cs` (DTOs, `Vec3`, `BoxMm`, kinds, codes, JSON parsing),
  `ViewCreatePlanner.cs` (fan-out, names, per-kind checks, `CalloutRules`, summaries), `SectionBoxMath.cs`,
  `ViewExtentMath.cs` (+ `ViewOrientationMath`, `FrameMm`) — pure; `ViewFamilyTypeResolver.cs`,
  `ViewReferenceResolver.cs` (`ResolvedView`, reference lookups), `ViewExtentService.cs`, `ViewCreator.cs` — Revit.
- `RevitMCP.Addin/Tools/Views/`: `ListViewFamilyTypesTool.cs`, `CreateViewsTools.cs`, `SetViewExtentTools.cs`,
  `ViewCreationToolRegistration.cs`.
- `RevitMCP.Addin/Approval/ViewCreationApprovalSummaries.cs`, `RevitMCP.Bridge/RevitMcpTools.ViewCreation.cs`.
- Refactored: `Coordination/Clash/Review/ClashReviewViewService.cs` (3D type via `ViewFamilyTypeResolver.ResolveDefault`,
  union/padding/section box via `ViewExtentService`), `Tools/IfcSpaceToRoom/Services/BoundaryViewResolver.cs` (2026
  only; floor plan type via `ViewFamilyTypeResolver.ResolveDefault`).
- `docs/view-creation.md`, README (one Capabilities row, one reference line).

## Deviations from the issue

1. **Section box Z range.** The issue gives `Min.z = 0, Max.z = depth`. With BasisZ = BasisX × BasisY as the section's
   `ViewDirection` (Revit: the direction towards the viewer), that volume would lie between the viewer and the line, so
   the cut plane would sit `depth` in front of the drawn line. Built instead: `Min = (−L/2, bottom, −depth)`,
   `Max = (+L/2, top, 0)` — the cut plane on the line, the far clip `depth` behind it (the Revit SDK CreateViewSection
   sample and the Building Coder samples also use `Max.Z = 0`). Documented convention: walking from `from` to `to` the
   section looks LEFT; `flip` looks right. Because the reference docs of `CreateSection` are ambiguous (they also call
   the computed frame left-handed), the tool checks Revit's actual `ViewDirection` and cut-plane offset after creation,
   returns them in `views[].actual` and warns if they disagree. **Please confirm in the manual test** (step 2); if Revit
   disagrees, the fix is one line in `SectionBoxMath.Compute` (swap the Z range or negate BasisZ).
2. **Box union and padding** live in `ViewExtentMath` / `ViewExtentService` (as asked); `ClashBoundingBoxHelper` /
   `ClashBoundingBoxMath` (#107) are not touched and no longer used by `ClashReviewViewService`.
3. **Type selection change for the two refactored callers:** they now prefer the document's default 3D / floor plan
   type over the first one by collector order (the issue's resolver rule). Same result in models whose default is the
   first type.
4. **Name uniqueness** is checked against every view and template name, case-insensitively (stricter than Revit's
   per-type rule), so the planner never hands Revit a clashing name.
5. **Fan-out without `{level}`/`{n}`** appends the level name to the name (otherwise all levels would be suffixed 1, 2, …).
   `placeOnSheet` takes `{level}`/`{n}` placeholders or a `sheets[]` list aligned with the levels.
6. **Reference callouts** (`referenceView`) create no view (`CreateReferenceCallout` returns nothing), so name, template
   and sheet placement do not apply; placing one on a sheet is invalid.
7. **`cropTo` is plans-only** in `revit_create_views` (sections/callouts/3D have their own extent fields); other kinds
   note it as ignored. `cropTo.scopeBox` assigns the scope box instead of copying its box.
8. **`revit_set_view_extent` with `scopeBox`** assigns the scope box parameter (padding ignored) wherever the view takes
   one (3D too); a scope box that controlled a plan's crop is cleared, with a warning, before a box crop is set.
9. **Area plans** need `areaScheme` when the document has more than one (code `area_scheme_required`, extra code).
   Extra status codes beyond the four in the issue: `view_family_type_not_found`, `template_not_found`,
   `scope_box_not_found`, `sheet_not_found`, `parent_view_not_found`, `phase_not_found`, `extent_not_found`,
   `invalid_spec`, `revit_refused`.
10. **Preview trial run** (`dryRun`, default true): the preview also asks Revit to create everything in a rolled-back
    transaction (pattern of `revit_preview_copy_elements`), so Revit refusals appear before approval.
11. **Max 100 views** is enforced after the per-level fan-out.

## API notes (both targets)

Verified in the 2024 (net48) and 2026 (net8.0) reference assemblies: `ViewPlan.Create`, `ViewPlan.CreateAreaPlan`,
`ViewSection.CreateSection`, `ViewSection.CreateCallout`, `ViewSection.CreateReferenceCallout`, `View3D.CreateIsometric`,
`View3D.CreatePerspective`, `View3D.SetSectionBox`, `View3D.SetOrientation`, `ViewOrientation3D(eye, up, forward)`,
`ViewDrafting.Create`, `View.Duplicate(ViewDuplicateOption)`, `View.CanViewBeDuplicated`, `PlanViewRange.SetOffset/GetOffset/GetLevelId`,
`PlanViewRange.Current/LevelAbove/Unlimited`, `ViewFamilyType.DefaultTemplateId`, `Document.GetDefaultElementTypeId`,
`ElementTypeGroup.ViewType{FloorPlan, CeilingPlan, StructuralPlan, Section, DetailView, 3D, Drafting, Elevation, Legend, …}`
(there is no area plan group: area plans resolve through the area scheme), `View.GetTemplateParameterIds`,
`View.GetNonControlledTemplateParameterIds`, `View.HasViewDiscipline`, `SubTransaction.HasStarted/HasEnded`,
`BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP`, `VIEW_PHASE`. No `#if` was needed; both targets build without new
warnings.

## Manual Revit test (2026 and 2024)

1. `revit_preview_create_views` then `revit_create_views` with the per-level example of `docs/view-creation.md`
   (`levels: "all"`, template, scope box, `placeOnSheet` with `EN-10{n}`): names `EN <level>`, each on its level, template
   applied, scope box set, view range as given, one viewport per sheet. Re-run with `ifNameExists: "fail"`: every view is
   `invalid` / `view_name_exists`, nothing created.
2. A section through a corridor from two plan points (west → east): it must look **north** (left of the line), cut on
   the line, far clip `depthMm` behind; check `views[].actual.cutPlaneOffsetFromLineMm ≈ 0` and that no direction
   warning appears. Repeat with `flip: true` (looks south).
3. A 3D view around a panel: `{"kind":"threeD","aroundElements":[<panel>],"paddingMm":500}` — tight section box with
   500 mm padding, iso-se orientation. Then `revit_create_clash_review_view` with a clash id: the review view still frames
   the clash (shared service).
4. A callout of a plan around a room (`aroundRoom`, default Detail type); try a CeilingPlan type in a floor plan and
   expect `view_family_type_incompatible` with the compatible types in the reason.
5. Legend: `{"kind":"legend","name":"Copy","sourceLegend":"<legend>"}` duplicates with detailing; without
   `sourceLegend` → `legend_source_required`.
6. `revit_set_view_extent` on a plan with `room` + `roomLinkInstanceId` of an AR link room: crop matches the linked
   room + 500 mm. On a 3D view with `elements`: section box. With `scopeBox`: assigned.
7. Undo once after each `revit_create_views` call: every view (and viewport) of the call disappears.
8. `atomic: true` with one bad spec: nothing is created; `atomic: false`: the rest are created.

## Left out

- Benchmark report (needs Revit; integrator runs it).
- Elevations (out of scope per the issue).
- The 2024 target does not compile `BoundaryViewResolver` (IFC space-to-room is 2026-only, unchanged).
