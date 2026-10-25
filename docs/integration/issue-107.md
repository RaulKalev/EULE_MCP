# Integration notes — #107 geometry queries

## Registration (App.cs)

Add one line to `App.OnStartup`, after the other `handler.RegisterTool(...)` calls:

```csharp
RevitMCP.Addin.Tools.Geometry.GeometryToolRegistration.Register(handler);
```

(or `using RevitMCP.Addin.Tools.Geometry;` and `GeometryToolRegistration.Register(handler);`).

## Approval summaries

None. All six tools are `ToolPermission.ReadOnly`; there is no `GeometryApprovalSummaries` class.

## Bridge

File: `RevitMCP.Bridge/RevitMcpTools.Geometry.cs` (partial `RevitMcpTools`). All `ReadOnly = true`:

| Tool | Add-in class | Permission |
|---|---|---|
| `revit_get_element_geometry` | `Tools/Geometry/GetElementGeometryTool.cs` | ReadOnly |
| `revit_find_elements_in_box` | `Tools/Geometry/FindElementsInBoxTool.cs` | ReadOnly |
| `revit_find_elements_near` | `Tools/Geometry/FindElementsNearTool.cs` | ReadOnly |
| `revit_find_nearest_surface` | `Tools/Geometry/FindNearestSurfaceTool.cs` | ReadOnly |
| `revit_check_line_of_sight` | `Tools/Geometry/CheckLineOfSightTool.cs` | ReadOnly |
| `revit_measure_clearance` | `Tools/Geometry/MeasureClearanceTool.cs` | ReadOnly |

Object arguments (`point`, `from`, `to`, `box`, `directions`) are `object?` parameters passed through the
existing `ToJToken` helper; the add-in accepts `{x,y,z}`, `[x,y,z]` or JSON text.

## BridgeToolGroups

- `query` exact table: `revit_get_element_geometry`, `revit_find_elements_in_box`, `revit_find_elements_near`,
  `revit_find_nearest_surface`, `revit_check_line_of_sight`, `revit_measure_clearance`.
- `Descriptions["query"]` gained `; geometry, boxes, ray casts`.
- Nothing added to `core`.

## Test project

One new `<ItemGroup>` at the end of `RevitMCP.Tests.csproj` linking `Geometry/GeometryDto.cs`, `BoxMath.cs`,
`ClearanceMath.cs`, `SurfaceClassifier.cs`, `LevelResolver.cs`, `GeometryArgs.cs`. New test file
`RevitMCP.Tests/GeometryQueryTests.cs` (BoxMathTests, ClearanceMathTests, GeometryDtoRoundingTests,
SurfaceClassifierTests, LevelResolverTests, GeometryArgsTests). Tests: 2601 → 2656, all green; both add-in
targets build.

## Follow-ups for the integrator

1. **`legacy` on two bridge methods (RevitMcpTools.cs, which #107 must not edit).** The add-in now reads
   `legacy` (default true) in `revit_inspect_selected_elements` and `revit_get_linked_elements_in_room` through
   `GeometryArgs.Legacy(args)`. Add `[Description("Keep the pre-#107 fields (boundingBoxMm / flat bbox.minX…maxZ) next to the new ones. Default true, for one release.")] bool legacy = true`
   to `InspectSelectedElements` and `GetLinkedElementsInRoom` and send `args["legacy"] = legacy`. Until then
   the old fields are always present (the default), and job steps can already pass `legacy=false`.
2. **`Graph/RevitGraphExtractor.LevelOf` (owned by #109)** can switch to the shared rule:
   `var level = RevitLevelReader.Resolve(element);` → `(level.Name, level.Id is { } id ? new ElementId(id) : null)`.
   The extractor keeps its per-id name cache; `LevelResolver` has the same fallback order
   (LevelId → FAMILY_LEVEL_PARAM → RBS_START_LEVEL_PARAM → SCHEDULE_LEVEL_PARAM → LEVEL_PARAM) and the same
   "id without a level name = no level" rule, unit-tested with a fake reader. The extractor was left untouched.
3. `McpToolResult.Status` doc comment (Core) could list the new statuses `geometry_query_too_broad`,
   `no_3d_view_for_raycast`, `link_not_found` — left out to avoid a conflict in a shared file.
4. Benchmark: not run (needs Revit). The tool surface grew by six `query`-group tools; `core` is unchanged.
5. `MoveElementsMath.MmPerFoot` (owned by #108) is referenced by `GeometryUnits.MmPerFoot`; keep the constant.

## Files

New: `RevitMCP.Addin/Geometry/{GeometryDto, GeometryConvert, BoxMath, RayCaster, SurfaceClassifier,
LevelResolver, ClearanceMath, GeometryArgs}.cs`, `RevitMCP.Addin/Tools/Geometry/{the six tools,
GeometryToolSupport, GeometryToolRegistration}.cs`, `RevitMCP.Bridge/RevitMcpTools.Geometry.cs`,
`docs/geometry-queries.md`, `RevitMCP.Tests/GeometryQueryTests.cs`.

Changed: `Placement/AlignmentService.cs`, `Placement/FacePlacementService.cs` (onto `RayCaster`),
`Tools/InspectSelectedElementsTool.cs`, `Tools/DeviceAuditTools.cs` (`GetLinkedElementsInRoomTool`),
`RoomDevices/DeviceAudit.cs` (`DeviceRecord.PointPayload` returns `PointMmDto`, same `{x,y,z}`),
`Coordination/Clash/Geometry/ClashBoundingBoxMath.cs` (additive `DistancePointToBox`), `BridgeToolGroups.cs`,
`RevitMCP.Tests.csproj`, `README.md`.

## Deviations from the issue

- **Status `geometry_query_too_broad`** is the result `Status`; `Data` carries `QueryGuard.ValidateGeometryQuery`'s
  shape (`error = "GeometryQueryTooBroad"` — the existing `ToolErrorCodes` constant —, `message`,
  `suggestedActions`, plus `limits`). More than 100 ids, more than 200 rows without categories and more than 32
  directions are refused the same way (instead of being silently truncated).
- **`revit_get_linked_elements_in_room` payload:** `location {x,y,z}` was already the point DTO's shape; `bbox`
  is now `BoxMmDto` and keeps the flat `minX … maxZ` as aliases while `legacy=true`. For
  `revit_inspect_selected_elements` the new box is a separate `bbox` field; the old `boundingBoxMm` (whose inner
  names `size`/`center` differ from the DTO's `sizeMm`/`centerMm`) is the legacy alias. Values are now rounded
  to 0.1 mm instead of 0.01 mm.
- **`revit_find_elements_in_box roomId`** uses the room element's bounding box (transformed from the link when
  `roomLinkInstanceId` is given), not the polygon from `RoomSourceService`: the polygon's bounding box is the
  same in plan, and the room element's box also carries the room's height. Added `roomLinkInstanceId` because
  rooms usually live in the architecture link.
- **Extra arguments** beyond the issue: `fromElementId`/`toElementId` on line of sight (bounding box centres, both
  ignored); `ignoreElementIds`, `angleToleranceDegrees`, `horizontalSamples`, `searchViewId` on nearest
  surface; `searchViewId` on all ray tools; `directions="local"` (front/back/left/right from
  FacingOrientation/HandOrientation) — useful for the "front ≥ 1000 mm" rule.
- **`revit_find_nearest_surface` distance:** per surface kind the best three candidates are refined to the
  perpendicular distance (face normal from the face geometry, else three-ray plane measurement, confirmed by one
  ray straight at the face). `perpendicular=false` falls back to the distance along the search ray. Default
  `surfaces=["any"]`, 16 horizontal directions.
- **Surface classification:** orientation first (as `AlignmentMath.ClassifySurface`), then the category: the Roofs
  category always gives `roof`; a sloped face takes its category's kind. Category keys use `BuiltInCategory`
  names (`OST_Walls`) so localized Revit UIs classify the same; display names are the fallback.
- **`ignoreElementIds`** match host elements only (linked ids are not unique across documents).
- **Solids summary** is the first solid of `ClashSolidExtractor.TryExtract` (volume, face count), as the issue
  says; the result notes it.
- **Line of sight / clearance distances** are along the ray; clearance rays start 15 mm behind the box face
  (subtracted again), so a face lying on the box is still met.

## API notes per target

- Same code for Revit 2024 (net48) and 2026 (net8.0-windows); no `#if` needed. Uses `ElementId.Value` and
  `new ElementId(long)` like the rest of the code base, `ReferenceIntersector` (Face, `FindReferencesInRevitLinks`),
  `BoundingBoxIntersectsFilter` / `BoundingBoxIsInsideFilter`, `ElementMulticategoryFilter`,
  `RevitLinkInstance.GetTotalTransform`, `Face.Project/ComputeNormal`, `Reference.CreateReferenceInLink` /
  `CreateLinkReference`, `FamilyInstance.get_Room(Phase)`, `Document.GetRoomAtPoint`, `Element.GetDependentElements`.
- Avoided net48 gaps (`Math.Clamp`, `string.Contains(string, StringComparison)`).

## Manual Revit test (2026 and 2024, EN model with a linked ARH model)

Regression first:
1. `revit_preview_align_elements` / `revit_align_elements` and `revit_preview_place_on_face` /
   `revit_place_on_face` with the steps in `docs/alignment.md` and `docs/place-on-face.md` (linked walls and
   ceilings): same surfaces, distances and placements as before the refactor.
2. `revit_inspect_selected_elements` and `revit_get_linked_elements_in_room`: old fields still present
   (`boundingBoxMm`, flat `bbox.minX…`), new `bbox` present.

New tools:
3. `revit_get_element_geometry elementIds=[<ceiling detector>]`: facing/hand, `host` = the linked ceiling
   (`linkInstanceId` set), level, room; compare `location.pointMm` with Revit's coordinates (internal origin).
   Call without ids → `geometry_query_too_broad`.
4. `revit_find_elements_in_box roomId=<room> roomLinkInstanceId=<ARH link> categories=["Lighting Fixtures"]`:
   the room's luminaires; `mode=inside` drops those straddling the room box.
5. `revit_find_elements_near elementId=<panel> radiusMm=3000 categories=["Lighting Fixtures"]`: distances vs
   Revit's Measure tool (bounding box to box centre) within 5 mm.
6. `revit_find_nearest_surface elementId=<socket> surfaces=["wall"] horizontalSamples=16`: `best.wall` with
   `perpendicular=true`, distance vs Measure within 5 mm; with the active view a plan, the result warns which 3D
   view was used; in a model without 3D views → `no_3d_view_for_raycast`; with a section box → warning.
7. `revit_check_line_of_sight fromElementId=<camera> toElementId=<door>`: blocked by the linked wall when the
   door is behind it, clear when seen through the doorway.
8. `revit_measure_clearance elementId=<panel> directions="local"`: `front` freeMm vs Measure (≥ 1000 mm rule),
   `back` ≈ 0 against its wall.

## Left out

- Benchmark report (needs Revit; the integrator runs it).
- The extractor switch-over and the bridge `legacy` parameter (follow-ups 1 and 2).
