# Integration notes — #108 transforms and groups

## Wiring

**App.cs** (`App.OnStartup`, next to the move/copy registrations):

```csharp
TransformsAndGroupsToolRegistration.Register(handler);
```

The class is `RevitMCP.Addin.Tools.TransformsAndGroupsToolRegistration` (file
`RevitMCP.Addin/Tools/TransformsAndGroupsToolRegistration.cs`); `handler` is the
`RevitMCP.Addin.Services.ExternalEventHandler` already created there.

**Approval summaries**: `RevitMCP.Addin.Approval.TransformsAndGroupsApprovalSummaries.TryBuild(request)` — chain it into
`ApprovalSummaryBuilder.Build`'s default case. `MirrorElementsTool`, `UngroupTool` and `UngroupAndDeleteTypeTool` also
implement `IApprovalSummaryTool` (live summary naming the plane/view, and the instance and member counts).

**Bridge file**: `RevitMCP.Bridge/RevitMcpTools.TransformsAndGroups.cs`.

| Tool | Add-in permission | Bridge flag |
|---|---|---|
| `revit_preview_mirror_elements` | ReadOnly | `ReadOnly = true` |
| `revit_mirror_elements` | RequiresApproval | — |
| `revit_preview_array_elements` | ReadOnly | `ReadOnly = true` |
| `revit_array_elements` | RequiresApproval | — |
| `revit_list_groups` | ReadOnly | `ReadOnly = true` |
| `revit_preview_create_group` | ReadOnly | `ReadOnly = true` |
| `revit_create_group` | RequiresApproval | — |
| `revit_ungroup` | RequiresApproval | — |
| `revit_ungroup_and_delete_type` | DestructiveRequiresManualApproval | `Destructive = true` |
| `revit_preview_place_group_instances` | ReadOnly | `ReadOnly = true` |
| `revit_place_group_instances` | RequiresApproval | — |

**BridgeToolGroups.cs**: all eleven names above added to the exact `edit` table (after `revit_place_on_face`), and
`Descriptions["edit"]` now mentions "mirror/array, groups (list, create, ungroup, place)".

## Other shared files touched (additive)

- `RevitMCP.Addin/Plans/PlanToolClassification.cs`: `revit_mirror_elements`, `revit_array_elements` added to the
  creating prefixes and `revit_ungroup` (covers `revit_ungroup_and_delete_type`) to the modifying prefixes —
  `PlanToolClassificationTests.EveryWriteToolTheBridgeAdvertisesIsClassified` requires every bridge write tool to be
  classified. `revit_create_group` / `revit_place_group_instances` were already covered by `revit_create_` / `revit_place_`.
- `RevitMCP.Tests/RevitMCP.Tests.csproj`: one new ItemGroup at the end linking `MirrorMath.cs`, `ArrayMath.cs`,
  `GroupSupport.cs`.
- `README.md`: one Documentation line and one Capabilities row.
- Owned files changed as the brief assigns: `Placement/MoveElementsService.cs`, `Placement/MoveElementsMath.cs`
  (`MoveStatus.GroupMoved`, `MovePlan.IsGroupInstance`, `GroupMemberReason`, `BlockedAsGroupMember`, `MarkGroupMoves`),
  `Placement/CopyElementsService.cs` (public `CopySet(doc, batch, ids, Transform)` overload, group notes),
  `Tools/DeviceAdjustTools.cs` (`RotatePlanner`, `AdjustPlanEntry`), `RevitMCP.Tests/MoveElementsMathTests.cs`.
- Small additive edits in the move/copy area that no other issue owns: `Placement/MoveElementsPayload.cs`
  (`isGroupInstance`), `Placement/CopyElementsMath.cs` (`CopyItem.IsGroupInstance`), `Tools/CopyElementsTools.cs`
  (`isGroupInstance` in the element payload), `Tools/MoveElementsTool.cs` (calls `MarkGroupMoves` after commit; the
  description mentions group instances).

## Not edited (for the integrator to consider)

`RevitMCP.Bridge/RevitMcpTools.cs` still describes `revit_move_elements` as "group members and pinned elements are
reported, not moved" (true) but does not say that a group instance id moves the whole group, and the rotate/copy bridge
descriptions do not mention groups either. Suggested additions: move — "A group instance id moves the whole group
(status GroupMoved)."; rotate — "A group instance id rotates the whole group about its origin."; copy — "A group
instance id copies the whole group."

## Deviations from the issue

- **Group logic split**: the issue names `GroupSupport.cs` for kind detection, unique names and member listing.
  `GroupSupport.cs` is the pure part (linked into the tests); the Revit reads (group types, members, ungroup targets)
  are in `Placement/GroupService.cs`.
- **Preview for placement**: the issue lists `revit_place_group_instances` only but describes a preview;
  `revit_preview_place_group_instances` (ReadOnly) was added. There is no separate preview for ungroup; its approval
  summary is built live (instances, members, type names, whether types are deleted).
- **`revit_ungroup`** takes no `deleteTypeWhenEmpty`; deleting emptied types is only the destructive sibling
  `revit_ungroup_and_delete_type`, a separate bridge tool (no route table entry needed).
- **Mirror tracking point**: `fromMm/toMm` use the copy snapshot anchor (insertion point, curve midpoint or bounding-box
  centre) rather than `MoveElementsService.TrackingPoint` (bounding-box minimum), because a reflected box minimum is not
  the copy's box minimum; for point-located elements both are the insertion point.
- **Mirror `axis` form**: `{axis: x|y|z, throughMm}` is read as the plane *perpendicular* to the axis (axis x flips X).
  Documented.
- **Mirror line in a non-plan view**: the plane contains the line and the view direction (Revit's Draw Axis); points
  are model coordinates (z optional), projected onto the view plane.
- **Pinned / group members with copy=true** (mirror) are mirror-copied with a note, as the copy tool does; with
  copy=false they are `Pinned` / `InGroup`.
- **Copy of a group member** keeps today's behaviour (only the member is copied) and the note now starts `InGroup:`
  with the group id and the hint; a group instance is copied whole with a note and `isGroupInstance`.
- **Array `totalMm` / `totalAngleDeg`** run from the original to the last copy; a full circle (±360°) spreads members
  evenly (step = 360 / members). `asGroup=true` requires `includeOriginal=true` (Revit's count includes the original),
  one view or the model only, and a radial array group always rotates its members (`rotateElements=false` refused).
- **Radial copies** are copied in place and then rotated with `RotateElements` about the axis (as the issue says), not
  copied with a rotation transform. `rotateElements=false` translates the set so its anchor centroid follows the arc.
- **Array preview** runs static checks only (no trial run): a trial of up to 200 × 2000 copies would be heavy.
- **Rotate of a group**: current facing is `LocationPoint.Rotation`; if Revit does not report it, only `rotateByDeg`
  is accepted (angleDeg/faceToward blocked with the reason).
- **Model group level**: `PlaceGroup` decides the group's reference level; the preview reports the nearest level per
  point (or `levelOverride`) and uses its elevation when the point has no z; the result reads back `GROUP_LEVEL`.

## API notes per target

- `ElementTransformUtils.MirrorElements(Document, ICollection<ElementId>, Plane, bool)` — same signature in 2024 and
  2026 reference assemblies; the result is a collection of new ids (empty when `mirrorCopies=false`), consumed with
  `?.ToList()` so `IList`/`ICollection` both work. `CanMirrorElement` / `CanMirrorElements` exist in both.
- `LinearArray.Create(Document, View, ICollection<ElementId>, int, XYZ, ArrayAnchorMember)` and
  `RadialArray.Create(Document, View, ICollection<ElementId>, int, Line, double, ArrayAnchorMember)` — identical in
  both. Count validation is done in `ArrayMath` (2–200, radial array 3–200) because `IsValidArraySize(int)` (2024) was
  replaced by `IsValidNumberOfMembers(int, Document)` (2026); neither is called. `LinearArray.IsElementArrayable`
  exists in both.
- `ItemFactoryBase.NewGroup(ICollection<ElementId>)`, `PlaceGroup(XYZ, GroupType)`, `Group.UngroupMembers()`,
  `Group.GetMemberIds()`, `GroupType.Groups`, `BuiltInParameter.GROUP_LEVEL`, `Category.BuiltInCategory` — same in both.
  No `#if` was needed. Element ids go through `MoveElementsService.ToElementId(long)` / `ElementId.Value`.

## Manual Revit test (2026 and 2024)

1. Mirror-copy a classroom's EN devices across the corridor axis drawn in the plan view:
   `revit_preview_mirror_elements` then `revit_mirror_elements` with
   `plane {viewId: <plan>, fromMm {x, y}, toMm {x, y}}`; compare with Revit's Mirror – Pick Axis; check facing by eye.
   Try `plane {axis: "z", throughMm …}` without `allowHorizontalPlane` (refused) and an element Revit cannot mirror
   (listed, `mirror_not_allowed`).
2. Linear array of 6 luminaires at 1 200 mm: `kind=linear, count=6, spacingMm {x: 1200, y: 0, z: 0}` → 5 copies.
   Repeat with `asGroup=true` and check the array's count parameter is 6.
3. Radial array of 8 detectors: `kind=radial, count=8, centerMm …, totalAngleDeg=360` → 45° apart.
4. Create a model group from the classroom's devices (`revit_create_group name="Classroom EN"`), place it in 3 rooms
   with rotation (`revit_place_group_instances`), move one placed group with `revit_move_elements` (status
   `GroupMoved`), try moving a member (`InGroup` with the group id), rotate one with `revit_rotate_elements`, ungroup
   one (`revit_ungroup`), then `revit_ungroup_and_delete_type` on a type with one instance (manual approval).
5. Detail group of tags: create it in a plan, place a copy with `revit_place_group_instances` (path
   `copiedFromInstance`), and into another plan with `viewId`.
6. Undo after every call is one step.

## Not done

- The benchmark (`RevitMCP.Benchmark`) was not run (needs Revit), so no benchmark report is committed.
- Live Revit testing has not been done; everything above is compiled for both targets and unit-tested for the pure
  logic only.

## Tests

2601 → 2664 passing (`dotnet test RevitMCP.Tests/RevitMCP.Tests.csproj -c Release`): `MirrorMathTests`,
`ArrayMathTests`, `GroupSupportTests` and five group cases in `MoveElementsMathTests`.
