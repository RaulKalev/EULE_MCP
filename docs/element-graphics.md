# Per-element view graphics (#160)

Hide, unhide and override the graphics of **individual elements in one view**. This is a different scope from
`revit_apply_view_filters` (rules over categories and parameters, #134) and `revit_set_link_visibility` (linked models);
the three do not overlap and none calls another.

| Tool | Permission | What it does |
| --- | --- | --- |
| `revit_get_element_graphics` | Read-only | Hide state, overrides and every known reason an element is not drawn |
| `revit_preview_set_element_graphics` | Read-only | What an operation would do per element, before any write |
| `revit_set_element_graphics` | Approval | Applies it in one transaction ("Revit MCP - Set Element Graphics"), one undo step |

All three take `viewId` (or `useActiveView=true` for the active view of the active document), `elementIds` (or
`useSelection=true`, active document only) and `document`. `elementIds` is capped at 5000 per write; the read lists 500.
Revit calls used: `View.HideElements`, `UnhideElements`, `GetElementOverrides`, `SetElementOverrides`,
`Element.IsHidden`, `Element.CanBeHidden`.

## Operations

`operation` is one of:

- `hide` — Hide in View > Elements. Elements Revit refuses to hide (`cannotBeHidden`) or that are not in the view are reported, not forced.
- `unhide` — undoes **only** that permanent hide. It never switches on a category, a filter, a workset or ends temporary isolate.
- `override` — `graphics` changes only the fields it names; other existing overrides stay.
- `resetOverrides` — clears every element-level override (`new OverrideGraphicSettings()`).

### `graphics` (same shape as `revit_apply_view_filters`)

```json
{
  "halftone": true,
  "transparency": 40,
  "projectionLines": { "color": "#FF0000", "weight": 5, "pattern": "Dash" },
  "cutLines": { "color": [0, 0, 0], "weight": 3 },
  "surfaceForeground": { "color": "#0080FF", "pattern": "solid", "visible": true },
  "surfaceBackground": {}, "cutForeground": {}, "cutBackground": {}
}
```

Units: colors are `#RRGGBB`, `[r,g,b]` or `{r,g,b}` with 0-255 (no alpha: transparency is a separate 0-100 percent);
line weight is Revit's pen number 1-16; patterns are a name, `solid`, `none` (back to no override) or an element id.
A pattern the document does not have makes the call fail before anything is written.

## Why an element may still not be drawn

The read and the result report these codes; an empty list is not a promise that the element is drawn.

| Code | Meaning |
| --- | --- |
| `hiddenInView` | Permanently hidden (the thing `unhide` removes) |
| `categoryHidden` | Category switched off in Visibility/Graphics |
| `filterHidden` | A view filter with visibility off that the element passes |
| `worksetHidden` | Its workset is hidden in the view |
| `temporaryHideIsolate` | View is in temporary hide/isolate and the element is not part of it |
| `notInView` | The view does not return the element (other level, outside the crop or view range, phase, design option) |

After a write, the changed elements are re-checked against the model and listed under `afterwards.examples` when something
still keeps them out.

## Limits and safety

- A view **template** cannot be targeted. A view that *uses* a template is allowed: element hides and overrides live on the view, not the template; the template is never changed, and category/filter visibility it controls can still suppress elements (a warning says so).
- Sheets, schedules and similar views have no element graphics (`view_not_supported`).
- A write is refused (`view_not_writable`, nothing changed) while the view is in temporary hide/isolate mode or checked out by another user. The preview warns instead.
- Preview classifies each element as `willChange`, `unchanged` (`alreadyHidden`, `notHidden`, `noOverrides`, `sameOverrides`) or `ineligible` (`notFound`, `notInView`, `cannotBeHidden`); mixed batches apply what is eligible and report the rest.
- Results are aggregated: counts, `byReason`, and `sampleSize` examples (default 20), ineligible rows first.
- Checkpoints record the elements, not view settings; use Revit's undo (one step) to revert.
