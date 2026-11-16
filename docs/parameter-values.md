# One value per element (#165)

`revit_set_parameter` writes **one** value to many elements. `revit_set_parameter_values` writes a
**different** value to each element — Object_IDs, device numbers, room codes prepared in Excel — in one
transaction and one approval.

| Tool | Permission | What it does |
|---|---|---|
| `revit_preview_set_parameter_values` | Read-only | Counts what the write would do, with examples |
| `revit_set_parameter_values` | Approval | Writes the values, one transaction, one undo step |

Both are in the `edit` group (`revit_tools_search "set parameter values"`).

## Input

Rows come from the call or from a file, not both.

```json
{
  "parameterName": "RBR-Object_ID",
  "values": [ { "elementId": 1037257, "value": "ED-SEN-B01-0001" }, { "uniqueId": "…", "value": "…" } ],
  "unique": true
}
```

```json
{
  "parameterName": "RBR-Object_ID",
  "sourceFile": "C:\\…\\RB_LowVoltage_ObjectID_eelvaade.xlsx",
  "sourceSheet": "Object_ID eelvaade",
  "idColumn": "ElementId",
  "valueColumn": "Object_ID uus",
  "unique": true
}
```

- `values`: `[{elementId | uniqueId, value, parameterName?}]`, or an object `{"<elementId>": "value"}`.
- `sourceFile`: `.xlsx`/`.xlsm` (`sourceSheet`, default the first visible sheet), `.csv` (`;`, `,` or tab) or `.json`
  (the same rows, or `{"values": [...]}`). The file may be open in Excel. Columns are found by header name:
  `idColumn` (default `ElementId`/`Id`), `uniqueIdColumn` (default `UniqueId`), `valueColumn` (default `Value`),
  `parameterColumn` (optional, per-row parameter). Use a file for thousands of rows so they never travel through
  the MCP message.
- `uniqueId` is the safer key when the preview and the write happen in different sessions.
- `parameterName` (or `builtInParameter` / `parameterGuid`) is the default parameter; a row may name its own.
  The parameter is resolved like `revit_set_parameter`, but `exactMatch` defaults to **true**.
- `scope`: `Instance` (default), `Type` or `Any`. String, Integer, Double (internal units, `.` decimal) and ElementId
  (an id or an element/type name) parameters.
- `onlyIfDifferent` (default true) skips elements that already hold the value.

## Checks

| Count | Meaning |
|---|---|
| `willChange` / `written` | Elements whose value changes |
| `unchanged` | Already hold the value |
| `notFound` | No element with that id / UniqueId |
| `noParameter` | The element has no such parameter (or the name is ambiguous) |
| `readOnly` | The parameter cannot be written |
| `invalidValue` | Not an integer / number / known element for that storage type |
| `invalidElementId` | The row has no usable element id |
| `conflict` | Two rows give different values to the same element and parameter (also a type parameter reached through two instances) — neither is written |
| `collapsedRepeats` | Identical repeated rows, written once |

`unique: true` finds values that would be held by more than one element after the write: among the rows, and
against every other element of the targets' categories (instances, or types for a type parameter). If there is
one, `revit_set_parameter_values` writes **nothing** and returns `validation_failed` with the duplicates.

Results are aggregated: counts, `problemsByReason`, and up to `sampleSize` (default 20, max 200) example changes and
problems. A 6000-row write returns a few kilobytes.

## Rolling back

The write is attributed to `agent:revit_set_parameter_values` like any agent write. To review and undo it:

1. `revit_checkpoint_create categories=[…] parameters=["RBR-Object_ID"]` (or `elementIds`) before the write.
2. `revit_set_parameter_values …`
3. `revit_checkpoint_diff` shows before/now values; `revit_checkpoint_rollback` restores them.

Within a plan (`revit_plan_submit`), inline `values` carry their element ids, so the envelope's element list and
`maxElementsPerCall` are checked before the call. A `sourceFile` call has no ids in its arguments: only the plan's
check after the write sees which elements changed (and rolls the call back if they fall outside the envelope). For
one large write, a single approval without a plan is the simpler route.
