# Change the type of existing instances

`revit_preview_change_element_type` and `revit_change_element_type` (group `edit`, #159) switch existing instances to
another type with `Element.ChangeTypeId`, without deleting and recreating them. They do not edit or duplicate type
definitions; use `revit_edit_family_types` / `revit_duplicate` for that.

## Arguments

| Argument | Meaning |
| --- | --- |
| `elementIds`, `useSelection` | The instances. Both may be combined; `useSelection` needs the active document. Up to 5000 per call. |
| `typeId` | Target type (preferred, stable). |
| `typeName` (+ `familyName`, `categoryName`) | Target by name, case-insensitive and exact. A name that matches more than one type is refused with the candidates listed, never guessed. |
| `requireAll` | `true`: write nothing when any element is blocked, and undo the batch if any switch fails. Default `false`: switch what can switch, report the rest. |
| `sampleSize` | Example rows per status (default 20, max 200). |
| `document` | Target document (title or path), as for the other model tools. |

## Per-element status

The preview, and the apply, report each element as `switchable`, `unchanged` or `blocked`:

* `unchanged` (`sameType`): the element already has the target type.
* `blocked` reasons: `notFound`, `isElementType` (a type id was passed), `linkedDocument`, `noType`, `inGroup` (group
  members change through the group), `pinned`, `notEditable` (owned by another user, or newer/deleted in central),
  `categoryMismatch` (target is in another category), `invalidForElement` (`Element.IsValidType` is false: wrong family,
  host or kind).

The result is aggregated: `counts`, `blockedByReason` and up to `sampleSize` examples per status.

## Apply

* One transaction (`Revit MCP - Change Element Type`), one undo step; each element in its own sub-transaction so one
  refusal costs only that element (unless `requireAll`).
* An inactive family symbol is activated first (the preview says `needsActivation`).
* After each switch the element is re-read; if it did not take the type it is reported `failed`.
* `Element.ChangeTypeId` may return a different element id. Every case is reported in `changes` (`oldId`, `newId`,
  `oldTypeId`, `newTypeId`, `status` = `changed` | `replaced` | `failed`) and `idMappings` lists `oldId → newId` for the
  replaced elements. Use the new ids afterwards: tags, dimensions, saved lists and schedules by id may refer to the old one.
* Revit keeps instance parameter values where the new type has the same parameter; a replaced element may lose
  values or constraints that the new type cannot hold. Check with `revit_checkpoint_diff` when it matters.

## Safe use

1. `revit_preview_change_element_type` with the same arguments.
2. `revit_checkpoint_create` with the element ids, then `revit_change_element_type` (approval shows counts and the
   most common old type names).
3. `revit_checkpoint_diff` / `revit_get_warnings sinceCheckpointId=…`.

## Smoke test (needs Revit)

1. Place three lighting fixtures of type A; pick type B of the same family.
2. Preview: three `switchable`. Apply: `changed` = 3; the idMappings are empty or listed.
3. Preview again: three `unchanged`.
4. Pin one, put one in a group, add a wall id: preview shows `pinned`, `inGroup`, `categoryMismatch`.
5. With `requireAll=true` and the blocked ones present, nothing changes. Revit undo reverts a successful run in one step.
6. Pass a `typeName` shared by two families: the call is refused with both listed.
