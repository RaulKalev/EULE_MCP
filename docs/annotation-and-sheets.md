# Annotation lines, overlaps, image coordinates and title blocks

Tools that came out of one drawing session (VBH 5475, issues #139-#143): measuring on an exported image, resizing lines
in place, finding overlapping text without looking at a picture, reading title blocks, and finding equipment sizes in a DWG.

## Pixels to coordinates: `revit_export_view_image`

The response has `imageTransform`:

| Field | Meaning |
|---|---|
| `widthPx`, `heightPx` | The PNG's real size, read from the file |
| `mmPerPx` | Scale of the image |
| `coordinateSystem` | `model`, or `sheet` for a sheet (sheet mm) |
| `originMm` | `{x, y, z}` under the top-left pixel |
| `rightDirection`, `upDirection` | Unit vectors along the image's right and up (a rotated plan or a section is not model X and Y) |
| `contentBoxPx` | `{x, y, width, height}`: where the crop is in the image |

`model point = originMm + rightDirection * px * mmPerPx - upDirection * py * mmPerPx`

The PNG does not show just the crop. Grids, levels and other annotation reaching past it make the image larger on any
side, so the mapping cannot be worked out from the crop and the image size: on a test plan that guess was 11 % off. So the
tool measures it: after the export it draws a temporary magenta line across the crop, exports again, compares the two images
to find where the line landed, and rolls the line back. The second export costs about the time of the first. Images over
4000 px are compared at 4000 px.

`imageTransform` is null, with `imageTransformNote`, for a view without an active crop (pass `bboxMm` or `cropToRoom` for a
plan, or crop the view), for a 3D view, and when the line does not show, for example under a template that hides Lines.

`highlightIds` is the same as `highlightElementIds`: the elements are outlined in red for this export only.

## Lines in place: `revit_set_line_geometry`, `revit_create_rectangle`

`revit_set_line_geometry` moves the endpoints of straight detail and model lines. Ids stay, so tags and dimensions stay,
and a size fix needs no delete. A missing `z1`/`z2` keeps the point on the line's sketch plane. Each line succeeds or is
skipped with its reason (`not a straight line`, `pinned`, off the plane, too short).

`revit_create_rectangle` draws 4 detail lines from `{minX, minY, maxX, maxY}` or `{center, width, height, rotationDeg}`,
with optional centred text and `asGroup=true`. In a plan, drafting view, legend or sheet the coordinates are model (sheet)
x and y. In a section or elevation they are measured along the view's right and up directions from the model origin; the
response's `coordinateFrame` says which (`modelXY`, `viewRightUp`).

Both need approval, and Direct Edit allows them.

## Text notes: `revit_set_text_notes`, `revit_get_text_notes`, `revit_check_annotation_overlaps`

`formatting` on `revit_set_text_notes` and its preview:

| Value | The edited range |
|---|---|
| `keep` (default) | Takes the neighbouring run's formatting, as Revit does |
| `plain` | No bold, italic or underline |
| `matchFirstRun` | The formatting of the note's first character |

Text outside the edited range keeps its formatting in all three.

### Box width

A text note's width is the width of its box **on paper** (printed mm), not in the model. At 1:100 a `widthMm` of 2000
gives a 2 m box on the sheet, which is the usual reason a box is far longer than its text.
`revit_create_text_notes` warns when `widthMm` looks like a model value. Leave `widthMm` out there and the box fits the
text.

For notes that already exist, `revit_set_text_notes` has three width options:

| Option | Effect |
|---|---|
| `widthMm` | Sets the paper width |
| `autoWidth=true` | Widens a note so its longest line fits, using an estimate from text size and width factor. It never narrows |
| `fitWidth=true` | Fits the box to the text: the narrowest width at which Revit lays the note out in as few lines as at its maximum width. An over-wide box shrinks and a wrapped one widens |

`fitWidth` asks Revit itself: it sets a width, regenerates and reads the note's height, narrowing in on the width where
a line would start to wrap (to 0.25 mm). Fonts, bold runs and width factors are therefore exact, at the cost of a
dozen or so regenerations per note. The preview reports an estimate and says the width is measured when applied. Run
it without `mode` to fix widths only:

```json
{ "tool": "revit_set_text_notes", "arguments": { "viewId": -1, "fitWidth": true } }
```

A fitted note still has a fixed width afterwards. If its text grows later, fit it again.

### Rotation

`rotationDeg` on `revit_set_text_notes` sets each note's absolute angle in its view: degrees counter-clockwise from the
view's right, so 0 is horizontal and 90 reads bottom to top. The note turns about its insertion point, around its
view's direction. Leaders turn with it. The preview shows `oldRotationDeg` → `newRotationDeg`, and a note already at
that angle is unchanged. Rotation alone needs no `mode`:

```json
{ "tool": "revit_set_text_notes", "arguments": { "elementIds": [123456], "rotationDeg": 90 } }
```

`revit_rotate_elements` also turns text notes (`angleDeg` is the same absolute angle, or use `rotateByDeg`), together
with any other elements.

`revit_get_text_notes` also returns `boundingBox` (`minX, minY, maxX, maxY` in view or sheet mm), `lineCount` and
`paragraphCount`. `lineCount` is an estimate with wrapping, the same one `autoWidth` uses: the API does not report rows.

`revit_check_annotation_overlaps` takes `viewId` or `sheetNumber` and reports each text note overlapping another text
note, a tag or a detail line, or crossing a viewport's edge on a sheet. A note wholly inside a viewport is not reported.
A sheet also checks each view placed on it, on its own, since a legend's notes belong to the legend; `checkedViews` lists
them and each finding names its view. A note's box is its text, not its leaders.
Overlaps are in view mm and paper mm; `minOverlapMm` (paper, default 0.5) ignores touching. Boxes come from Revit's
bounding boxes in that view.

**Not checked:** title block label areas. The API exposes neither a label's position in the instance nor which parameter
it shows.

## Title blocks: `revit_get_titleblock_values`

For `sheetIds` or `sheetNumbers`: family and type, the sheet's and the instance's values, the real size (`SHEET_WIDTH`,
`SHEET_HEIGHT`, else the bounding box) and the ISO size (`A4`...`A0`, extended formats like `A3x3`). `findings`:

| Code | Finding |
|---|---|
| `format_mismatch` | The written format does not match the real size |
| `format_unreadable` | The value in the format parameter states no size |
| `dates_differ` | The sheets do not share one date |
| `versions_differ` | The sheets do not share one version |

The format, date and version parameters are found by common English and Estonian names; `formatParameter`, `dateParameter`
and `versionParameter` name them explicitly. A sheet with no value is left out of the comparison.

**Not checked:** a value longer than its label, because the label's width and parameter cannot be read. All values are
returned rather than only those used in labels for the same reason.

To write: `revit_set_parameters_bulk entity=sheets includeTitleBlockInstance=true`. A parameter the sheet does not have
goes to the title block instance on that sheet; `results[].titleBlockParameters` lists which.

## Sizes from a DWG: `revit_get_cad_placement_points`, `revit_get_cad_shapes`

`includeExtents=true` on `revit_get_cad_placement_points` adds `extents` to each block: `minX, minY, maxX, maxY` (the
axis-aligned box in host mm), `centerX`, `centerY`, `widthMm`, `depthMm` (along the block's own axes, so a rotated unit
keeps its size) and `rotationDegrees`. The box covers the block's curves and points, not its text.

A DWG exported from Revit can insert every block at one point, so `x`, `y` are then that point for all of them and the
centre says where each block is. Blocks at one point with different extents are not merged.

`revit_get_cad_shapes` answers the same for loose line work. Both return host model mm with the link transform applied:
there is no need to open the DWG. `revit_tools_search` finds them for bbox, outline, kontuur, piirjoon, mõõdud, suurus and dwg.
