# Extensible Storage Queries

Revit add-ins store private structured data on elements using
[Extensible Storage](https://www.revitapidocs.com/2024/ce6c0b46-0b6d-e59a-c9d1-fbd85ff5f7d9.htm).
The data is invisible in the Revit UI: it does not appear in parameters,
schedules, or filters. These tools make that data readable from an MCP client.

Both tools are **read-only**. The connector never creates, changes, or deletes a
schema or an entity — there is no `SchemaBuilder`, `Entity.Set`, `SetEntity`, or
`DeleteEntity` call behind them.

## Tools

| Tool | Permission | Purpose |
|---|---|---|
| `revit_list_extensible_storage_schemas` | Read-only | Discover schemas, their access levels, and their field layout |
| `revit_read_extensible_storage` | Read-only | Read decoded values stored on elements |

## Typical flow

1. Call `revit_list_extensible_storage_schemas` to see what exists. By default it
   also counts how many elements in the open model carry each schema, so schemas
   that are merely loaded but unused are easy to skip.
2. Take the `schemaGuid` of the interesting schema.
3. Call `revit_read_extensible_storage` with that GUID and either `scanDocument=true`
   (every element carrying it) or `useSelection=true` / `elementIds`.

```text
List the extensible storage schemas that are actually used in this model,
then read the values for the one from vendor ACME.
```

## What "visible to this session" means

`Schema.ListSchemas()` returns schemas **loaded into the running Revit session**:
those registered by add-ins that have run, plus those Revit read from open
documents. A schema whose owning add-in has not been loaded yet may not appear
until the document containing it is opened.

`elementCount` is the reliable signal for "this model actually uses the schema".
`onlyUsedInDocument=true` filters the list down to those.

## Access levels

A schema declares a read access level, and it is enforced by Revit, not by this
connector:

| Read access level | Who can read the values |
|---|---|
| `Public` | Any add-in, including this connector |
| `Vendor` | Only add-ins whose `.addin` manifest carries the schema's vendor ID |
| `Application` | Only the specific application that created the schema |

Schema **metadata** (name, vendor, field names and types) is readable regardless.
Only the stored **values** are gated. When access is denied, the response reports
`readAccessGranted: false` with an explanatory error instead of failing the call,
so a mixed query still returns everything it is allowed to return.

## Where the data lives

Entities are attached to elements, so the target may be:

- A regular element (wall, device, view, sheet — anything).
- An **element type**, when the add-in stores data per family type.
- A `DataStorage` element, the usual home for project-wide settings. These have
  no category and no name, and are invisible in the Revit UI. `scanDocument=true`
  finds them; browsing the model by category never will.

The response reports `elementClass` and `isElementType` so these cases are
distinguishable.

## Value decoding

All Extensible Storage shapes are decoded:

| Field shape | Returned as |
|---|---|
| Simple | The value |
| Array | A JSON array |
| Map | An array of `{ key, value }` objects |
| Sub-entity | A nested `{ subSchemaGuid, subSchemaName, fields }` object |

Revit types are converted to readable JSON: `ElementId` becomes
`{ elementId, name }`, `XYZ` becomes `{ x, y, z }`, `UV` becomes `{ u, v }`, and
`Guid` becomes a string.

Nested sub-entities are followed to `maxDepth` (default 3). Beyond that the
value is replaced with `{ truncated: true }` rather than recursing without limit.

### Units

Fields declared with a measurable spec (length, area, angle, …) must be read in
a specific unit. The tools use the **document's display unit** for that spec, so
numbers match what the user sees in Revit. The unit that was applied is reported
per field in `fieldUnits`, keyed by field name:

```json
{
  "fields": { "CableLength": 12.5 },
  "fieldUnits": { "CableLength": "autodesk.unit.unit:meters-1.0.1" }
}
```

Fields without a spec are unitless and absent from `fieldUnits`.

## Limits and failure handling

- `maxElements` defaults to 25 and is capped at 500. The response reports
  `totalElementsMatched` so a cap is visible rather than silent.
- A field that fails to decode is reported in that entity's `errors` array; the
  remaining fields are still returned.
- An element carrying a schema that is not loaded in the session is reported with
  an explanatory error, since its fields cannot be decoded without the definition.
- `scanDocument=true` requires `schemaGuid` or `schemaName`. Scanning the whole
  model for every schema at once is deliberately not supported.

## Writing data

Out of scope by design. Writing to another vendor's schema risks corrupting data
whose invariants are owned by that add-in, and the write access level usually
forbids it anyway.
