# Family Types

Tools for the everyday type-authoring loop inside a project: find a type, duplicate
it, name the copy, and set the copy's type parameters. The same edit tool also
renames and re-parameterises types that already exist.

Both loadable family types (`FamilySymbol`, e.g. a door type) and system types
(`WallType`, `DuctType`, `TextNoteType`, …) are supported — anything Revit exposes
as an `ElementType` with a category.

## Tools

| Tool | Permission | Purpose |
|---|---|---|
| `revit_list_family_types` | Read-only | Find types and their `typeId`, optionally with writable parameters and instance counts |
| `revit_preview_duplicate` (`entity=familyTypes`) | Read-only | Review the planned copies, their names, and their parameter writes |
| `revit_duplicate` (`entity=familyTypes`) | Requires approval | Create the copies and set their parameters |
| `revit_preview_edit_family_types` | Read-only | Review renames and parameter writes on existing types |
| `revit_edit_family_types` | Requires approval | Rename types and set their parameter values |

The duplication tools are reached through the shared `revit_duplicate` /
`revit_preview_duplicate` bridge tools with `entity=familyTypes`; see
[`tool-consolidation.md`](tool-consolidation.md). The underlying add-in tools are
`revit_duplicate_family_types` and `revit_preview_duplicate_family_types`.

## Typical flow

1. Find the source type:

   ```json
   { "category": "Lighting Devices", "familyName": "Detector", "includeParameters": true }
   ```

   `includeParameters` returns only the writable type parameters with their current
   display values — the exact names and formats the write tools accept.

2. Preview the copies (`revit_preview_duplicate`, `entity=familyTypes`).
3. Apply with `revit_duplicate`, `entity=familyTypes`.

## Naming the copies

Pick one of three shapes:

| Shape | Use |
|---|---|
| `nameSuffix` / `namePrefix` | `"Standard"` → `"Standard - Copy"`. Both affixes support `{index}`, which is replaced with the copy number. Without a placeholder, the copy number is appended only when `numberOfCopies > 1`. |
| `newTypeNames` | One explicit name per source type. Cannot be combined with `numberOfCopies > 1`. |
| `variants` | `[{ "name": "...", "parameters": { ... } }]` — one copy per entry, each with its own name and values. Requires exactly one source type. |

Type names must be unique inside their family. A name that is already taken gets
` 2`, ` 3`, … appended, and the change is reported as a warning. Names Revit
rejects outright (empty, surrounding whitespace, or containing `` \ : { } [ ] | ; < > ? ` ~ ``)
are refused with a reason instead of failing inside the API.

`revit_edit_family_types` does **not** auto-rename: a rename onto an existing name is
reported as blocked, because silently renaming to something else is not what the
caller asked for.

## Parameter values

Values are passed as strings, keyed by parameter name:

```json
{
  "typeIds": [123456],
  "variants": [
    { "name": "DN100", "parameters": { "Diameter": "100 mm", "Comments": "Riser" } },
    { "name": "DN150", "parameters": { "Diameter": "150 mm" } }
  ]
}
```

- **Numbers** are written through Revit's own display-unit parser, so `"200"`,
  `"200 mm"`, and `1/2"` all mean what they say in the project's units. If Revit
  cannot parse the string, the value is written as a raw internal (feet-based)
  value and the response says so.
- **Yes/No** parameters accept `true`/`false`, `yes`/`no`, `1`/`0`.
- **ElementId** parameters accept a numeric element id or the exact name of an
  existing type or element.
- Parameter names match exactly first, then fall back to a single normalized
  partial match. An ambiguous partial match is reported rather than guessed.

Every parameter write comes back with its own `status` (`set`, `notFound`,
`readOnly`, `invalidValue`, `failed`) plus the previous and new display values.

By default a copy is kept even if some of its parameters could not be set, and the
failures are returned as warnings. Pass `requireAllParameters=true` to roll a copy
back instead, so the model never ends up with a half-configured type.

## Safety

- Every write runs inside a single Revit transaction, one sub-transaction per type,
  so a failure on one type does not abandon the others.
- Both write tools are approval-gated.
- Renaming a type changes it for every placed instance of that type — preview first.

## Cable types

Electrical cable types are not family types, so they have their own pair of tools:

| Tool | Permission | Purpose |
|---|---|---|
| `revit_preview_create_cable_type` | Read-only | Resolve the source cable type and report, per new name, `create`, `skipExisting` or `blocked`, plus a check of every parameter |
| `revit_create_cable_type` | Requires approval | Duplicate the source cable type under each new name and set its parameters |

- Source: `sourceTypeName` or `sourceTypeId` (see `revit_get_available_cable_types`).
  When the model has exactly one cable type it is used by default.
- New types: `newName` (+ `parameters`), `newNames` (batch, shared `parameters`), or
  `items=[{newName, parameters}]`.
- `ifExists`: `skip` (default) returns the existing type's id; `error` blocks that name.
- The result returns each new type's `id`, ready for `revit_change_circuit_cable_or_wire_type`
  or `revit_set_circuit_parameter` ("Cable Type").
- Revit 2026 has `Autodesk.Revit.DB.Electrical.CableType`; its `Duplicate()` takes no
  name, so the copy is renamed afterwards. Where the class does not exist (Revit 2024),
  the tool duplicates `WireType` instead and says so in a warning.
- One transaction, one sub-transaction per new type; Undo removes them all.
