# Line and fill patterns

A line pattern is the dash-space-dot sequence a line style draws with; a fill pattern is the hatch a filled region,
material or filter draws with. The connector lists both, creates new ones and replaces the definition of existing
ones, in a project or in a family that is open in the Family Editor. Once a pattern exists, use it with
[`revit_set_line_styles`](line-styles.md), `revit_set_filled_region_types` or `filledRegionTypes` of
`revit_create_detail_family`.

All six tools are in the `tags` group (`--tool-groups tags`, or find them with `revit_tools_search`).

| Tool | Permission | Purpose |
|---|---|---|
| `revit_list_line_patterns` | Read-only | id, name, segments; with `includeUsage=true` the line styles that use each pattern |
| `revit_preview_set_line_patterns` | Read-only | What `revit_set_line_patterns` would create or replace, and the problems to fix first |
| `revit_set_line_patterns` | Requires approval | Creates line patterns and replaces the segments of existing ones |
| `revit_list_fill_patterns` | Read-only | id, name, target, `isSolid`, orientation and grids; `target` filters, `includeGrids=false` leaves grids out |
| `revit_preview_set_fill_patterns` | Read-only | What `revit_set_fill_patterns` would create or replace, problems and warnings |
| `revit_set_fill_patterns` | Requires approval | Creates fill patterns and replaces the definition of existing non-solid ones |

`document` targets an open document other than the active one, a project or a family.

## Units

| Pattern | Lengths are |
|---|---|
| Line pattern | **Paper** millimetres: a 3 mm dash prints 3 mm long at any view scale |
| Drafting fill pattern (`target: drafting`) | **Paper** millimetres: a 2 mm spacing prints 2 mm apart at any scale |
| Model fill pattern (`target: model`) | **Model** millimetres: a 300 mm spacing is 300 mm on the building and scales with the view |

Angles are degrees, counter-clockwise from the x axis. The list tools report the same units.

## Line patterns

```json
{ "tool": "revit_set_line_patterns", "arguments": { "patterns": [
    { "name": "EL_Kaabel", "segments": [
        { "type": "dash", "length": 6 }, { "type": "space", "length": 1.5 },
        { "type": "dot" }, { "type": "space", "length": 1.5 } ] },
    { "name": "EL_Katkend", "segments": [3, -1.5] }
] } }
```

- `segments` is a list of `{type: dash | space | dot, length}`, or numbers the `.pat` way: positive a dash, negative
  a space, 0 a dot.
- What Revit requires, checked before anything changes: a dash or dot first, then alternating with spaces, an even
  number of segments (so it ends with a space). A dot has no length. Dashes and spaces are 0.01 to 10000 mm; at most
  40 segments and 50 patterns per call (the connector's limits).
- A name the document does not have is **created**; a name it has gets its segments **replaced**. Names match
  without regard to case. Replacing a pattern changes every line style that uses it; `revit_list_line_patterns
  includeUsage=true` shows which.
- *Solid* is Revit's built-in line, not a pattern element: it is not listed and cannot be created or edited.

## Fill patterns

The simple form is a hatch or a crosshatch:

```json
{ "tool": "revit_set_fill_patterns", "arguments": { "patterns": [
    { "name": "EL_Viirutus 45", "angle": 45, "spacing": 2 },
    { "name": "EL_Ruudustik", "target": "model", "spacing": 300, "crosshatch": true },
    { "name": "EL_Rist", "angle": 30, "spacing": 2, "crosshatch": { "angle": 120, "spacing": 4 } }
] } }
```

`crosshatch: true` adds a second set of lines at `angle + 90` with the same spacing; `{angle?, spacing?}` sets
either.

The full form is the grids of a `.pat` definition, one per set of parallel lines:

```json
{ "name": "EL_Tellis", "target": "model", "grids": [
    { "angle": 0,  "origin": [0, 0], "offset": 75,  "shift": 0 },
    { "angle": 90, "origin": [0, 0], "offset": 125, "shift": 75,
      "segments": [ { "type": "dash", "length": 75 }, { "type": "space", "length": 75 } ] }
] }
```

| Field | Notes |
|---|---|
| `name` | Required. No `\ : { } [ ] ; < > ? ~`, pipe or backtick |
| `target` | `drafting` (default) or `model`. The same name may exist once per target |
| `orientation` | `toView` (orient to view, the drafting default), `toHost` (align with element, the model default), `asText` (keep readable). An existing pattern keeps its orientation unless given |
| `angle`, `spacing`, `crosshatch` | The simple form |
| `grids[].angle` | Degrees, default 0 |
| `grids[].origin` | `[x, y]` mm, default `[0, 0]` |
| `grids[].offset` | Distance between the lines, mm, never 0 (`spacing` is accepted too) |
| `grids[].shift` | Moves the dashes along each next line, mm; ignored for solid lines |
| `grids[].segments` | As for line patterns; left out, the lines are solid |

- Give either the simple form or `grids`, not both.
- A name the target lacks is **created**; an existing non-solid pattern gets its whole definition **replaced**.
  Replacing a pattern changes every region, material and filter that uses it.
- **The solid fill cannot be created or edited.** A name such as `Solid fill` or `<Solid fill>` is refused, and so is
  an existing pattern Revit marks as solid.
- A drafting spacing below 0.3 mm draws nearly solid; the preview warns, and Revit may refuse it as too dense.
- Dots are stored as zero-length dashes and widened by Revit (`ExpandDots`), so a pattern with dots may read back
  with short dashes and show as `replace` on a second identical call.

Both write tools run in one transaction with one undo step. Any problem (an invalid definition, the solid fill, a
definition Revit refuses) leaves the document unchanged. Neither tool renames or deletes patterns.

## Not yet run in Revit

These tools were written when no Revit was reachable. The parsing, validation, comparison and summaries are unit
tested (`PatternDefinitionTests`); the Revit calls compile against the Revit 2024 and 2026 APIs but have not run.
To test, in a project:

1. `revit_list_line_patterns includeUsage=true`. Expect the patterns of Manage > Additional Settings > Line Patterns
   with segments in mm (a pattern drawn as 1/8" dashes reads about 3.175), and the styles of Line Styles that use them.
2. `revit_preview_set_line_patterns` with the `EL_Kaabel` example above. Expect `ready: true`, action `create`.
3. `revit_set_line_patterns` with the same arguments, approve. Open Line Patterns: `EL_Kaabel` shows Dash 6 mm,
   Space 1.5 mm, Dot, Space 1.5 mm. Ctrl+Z removes it in one step.
4. Run it again with the dash at 8. Expect action `replace`, and a line style using it redraws.
5. `revit_set_line_patterns` with `segments: [3]`. Expect `invalid_arguments` saying to end with a space; nothing changes.
6. `revit_list_fill_patterns target=drafting`. Expect the drafting patterns with `isSolid: true` on the solid fill
   and grids for the rest; compare one (e.g. *Diagonal crosshatch*) with Edit Pattern.
7. `revit_set_fill_patterns` with the three simple examples above, approve. Open Fill Patterns: the two drafting
   patterns are under Drafting with the given angle and spacing, `EL_Ruudustik` under Model with 300 mm lines.
8. `revit_set_fill_patterns` with the `EL_Tellis` grids example. Edit it in Fill Patterns (custom): a brick
   pattern with 75 mm courses. Apply it to a filled region type with `revit_set_filled_region_types` and check the
   region in a view at 1:20 and 1:100: the bricks keep their model size.
9. `revit_set_fill_patterns` with `{name: "Solid fill", spacing: 2}`. Expect `invalid_arguments`.
10. Repeat steps 1, 3 and 7 in a detail family open in the Family Editor with `document=<family title>`.

Report especially: whether lengths and angles read back as given (feet and radians conversions), whether replacing an
existing fill pattern under its own name succeeds, and whether dotted fill grids draw.
