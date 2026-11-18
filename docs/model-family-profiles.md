# Model family forms from CAD profiles or family curves

Build editable solids and voids in a model family from the outline of a manufacturer's DWG, or from lines already
drawn in the family (#177). The curves are first analysed into closed loops; you then pick a loop (and its holes) and
a depth, and the form is made with the extrusion builder of [model-family-forms.md](model-family-forms.md) (#171). The
result is an ordinary extrusion: no link to the DWG remains, and it can be constrained like any other form.

| Tool | Permission | What it does |
|---|---|---|
| `revit_get_model_family_profiles` | read only | Lists the source's layers, units and placement, the work plane, the closed loops (with holes), open chains and issues. |
| `revit_preview_create_model_family_form_from_profile` | read only | Runs the creation and rolls it back. |
| `revit_create_model_family_form_from_profile` | approval | Creates solid or void extrusions from loops, optionally cuts a solid and removes the source curves. |

The section `imports` of `revit_get_model_family` lists the family's CAD imports and links. All tools take `target`
as described in [model-families.md](model-families.md). Lengths are millimetres, points `[x, y, z]` from the family
origin.

## Getting a DWG into the family

No tool imports or links a DWG into a **family** document (`revit_link_dwg` works on projects and refuses family
documents). Open the family, go to the plan view of the reference level and use **Insert > Import CAD** (units: the
DWG's own, or millimetres; positioning: origin to origin; current view only off). Then call
`revit_get_model_family_profiles`: `source.units` and `source.transform` show what Revit did with it.

## Arguments

Both tools share `source`, `workPlane` and `tolerance`; give the same values to both so a loop index means the same.

| Argument | Meaning |
|---|---|
| `source` | `{"import": <id> \| "<name>", "layers"?: ["…"]}`: a CAD import or link in the family (default: the only one; with several, a problem lists them). `layers` picks layers, case-insensitive; all layers otherwise. `{"curves": [ids]}`: model, symbolic or reference lines of the family. `{"view": "<name>" \| id, "subcategory"?: "<name>"}`: the model, symbolic and detail lines shown in a view, optionally only one line subcategory. |
| `workPlane` | The plane the curves are analysed on and the form is sketched on: a reference plane (name or id), `"level"`, or `{"origin": [x,y,z], "normal": [x,y,z]}`. Default: the plane the curves lie in (all points within 0.5 mm of it), matched to the reference level, else to a reference plane they all lie on, else used as `{origin, normal}`. Curves more than 0.5 mm off the plane are left out (issue `offPlane`). Without `workPlane`, curves that are not on one plane are a problem that reports the spread. |
| `tolerance` | Endpoint join tolerance in mm, default 0.05, at most 1. Ends closer than this are one vertex. |
| `limit` | (read tool) Most items per list, default 200. |
| `forms` | (create tools) The forms, in order (below). |
| `removeSourceCurves` | (create tools) `true` deletes the family curves the forms were made from (also their duplicates), after the forms exist. Family curve sources only; a CAD import is never deleted. |

### `forms[i]`

| Key | Meaning |
|---|---|
| `loop` | A loop index from the read tool, or `{"at": [x,y,z]}`: the innermost loop around that point. |
| `holes` | `"auto"` (default): the loops directly inside become holes. `"none"`. Or a list of indices, each a direct child of `loop`. An island inside a hole needs its own form. |
| `solid` | Default `true`; `false` makes a void. |
| `depth` or `start` + `end` | Offsets in mm along the work plane's normal (`workPlane.normal` in the read result), as in #171. |
| `subcategory`, `visible` | As in #171. |
| `cut` | A void only: the solid it cuts, an id or `{"created": "forms[0]"}`. The cut runs through #171's cut step (solid-solid cut, else a geometry combination). |
| `name` | A label echoed in the result. |
| `expected` | `{"width"?, "height"?, "centre"?: [x,y,z], "tolerance"? (0.5)}`: the loop must measure this, or nothing is created. Use the DWG's dimensions to catch a wrong layer, loop or import scale. |

## How loops are found

1. Lines and arcs keep their geometry; a full circle becomes two half arcs; polylines become lines. Splines, ellipses
   and elliptical arcs are replaced by straight segments from Revit's display tessellation (`Curve.Tessellate`, whose
   tolerance Revit sets for display; points closer than Revit's minimum curve length, 0.79 mm, are dropped). Loops through
   them are marked `approximated`, and an `approximated` issue lists the curves.
2. Curves are projected onto the work plane and put in a fixed order, so the result does not depend on the drawing order.
3. Endpoints within `tolerance` of each other become one vertex (transitively: three ends each within the tolerance of
   the next are one vertex).
4. The same curve drawn twice (also reversed) is used once and reported (`duplicate`). Collinear lines that overlap
   (`overlap`), curves that cross without a vertex (`crossing`) and a curve ending on the middle of another
   (`endpointOnSegment`) are reported and **not** merged or split.
5. Curves with a loose end are removed, repeatedly; they are the `open` chains, each with its loose ends and the gap to
   the nearest other loose end (so you see which tolerance would close it).
6. The rest is a planar graph. Every bounded region it encloses is a loop: **the minimal regions**. Two rectangles
   sharing an edge give two loops, both with `sharesEdges: true`, not the outline around both. Where more than two curves
   meet, a `branch` issue names the point and the loops through it. A curve that only connects one outline to another
   (a `bridge`) is removed and reported, and the two outlines remain loops.
7. Loops are numbered by area (largest first), then by their lowest corner. Each has `parent` (the smallest loop that
   contains it) and `depth` (0 outer, 1 hole, 2 island …). A loop that crosses or touches itself, or has a segment
   shorter than 0.79 mm, is listed with `usable: false` and `reasons`.

Width is measured along the work plane's `xAxis`, height along `yAxis`: on a horizontal plane family X and Y, on a
plane facing front or back X and Z, on a plane facing left or right Y and Z.

## Examples

Analyse the outline layer of the imported DWG:

```json
{
  "target": { "familyDocument": "active" },
  "source": { "import": "ENC-200x120.dwg", "layers": ["OUTLINE", "CUTOUT"] }
}
```

```json
{
  "source": { "kind": "import", "id": 5012, "name": "ENC-200x120.dwg", "linked": false, "pinned": false,
              "units": { "importUnits": "Millimeter", "scaleFactor": 1.0 },
              "transform": { "origin": [0, 0, 0], "rotation": 0, "scale": 1, "mirrored": false },
              "layers": [ { "name": "CUTOUT", "curves": 4, "included": true }, { "name": "DIM", "curves": 61, "included": false },
                          { "name": "OUTLINE", "curves": 4, "included": true } ] },
  "workPlane": { "coincidesWith": { "kind": "level", "id": 7, "name": "Ref. Level" }, "normal": [0, 0, 1],
                 "origin": [0, 0, 0], "xAxis": [1, 0, 0], "yAxis": [0, 1, 0], "argument": "level", "fitted": true, "spread": 0 },
  "counts": { "curves": 8, "segments": 8, "loops": 2, "usableLoops": 2, "open": 0, "issues": 0 },
  "loops": [
    { "loop": 0, "kind": "rectangle", "segments": 4, "width": 200, "height": 120, "area": 24000,
      "min": [-100, -60, 0], "max": [100, 60, 0], "centre": [0, 0, 0], "layers": ["OUTLINE"], "parent": null, "depth": 0,
      "usable": true, "children": [1], "points": [ … ] },
    { "loop": 1, "kind": "rectangle", "width": 60, "height": 30, "centre": [0, 20, 0], "layers": ["CUTOUT"], "parent": 0, "depth": 1, … }
  ],
  "open": [], "issues": []
}
```

Create the enclosure (without the cutout as a hole) and a void for the cutout that cuts it:

```json
{
  "target": { "path": "C:\\fam\\ENC-200x120.rfa", "overwrite": true },
  "source": { "import": "ENC-200x120.dwg", "layers": ["OUTLINE", "CUTOUT"] },
  "forms": [
    { "loop": 0, "holes": "none", "depth": 80, "name": "Enclosure", "expected": { "width": 200, "height": 120, "centre": [0, 0, 0] } },
    { "loop": 1, "solid": false, "start": -1, "end": 81, "cut": { "created": "forms[0]" }, "name": "Cutout",
      "expected": { "width": 60, "height": 30, "centre": [0, 20, 0] } }
  ]
}
```

The result lists each form under `created` (`id`, `loop`, `holes`, `measured` size and location from the curves,
`boundingBox`, `sizeCheck` from #171) with a `constrain` hint, `createdIds`, and `cuts` (#171's cut rows).

## Inspection

| Section | Contents |
|---|---|
| `imports` | CAD imports and links: `id`, `name`, `linked`, `pinned`, `viewSpecific` (and `ownerView`), `units` (`importUnits`, `scaleFactor`, `instanceScale`, where the parameters exist), `transform` (`origin` mm, `rotation` degrees about Z, `scale`, `mirrored`), `layers` with curve counts. |

## Limitations

- **Extrusions only.** Blends, revolves and sweeps from profiles are not built here; take the loop's `points` (or the
  curves) and use `revit_edit_model_family_forms`.
- **Importing the DWG** into the family is manual (see above); there is no family-document import tool.
- **Units and scale** are reported from the import's parameters (`IMPORT_DISPLAY_UNITS`, `IMPORT_SCALE`,
  `IMPORT_INSTANCE_SCALE`) only where Revit exposes them; the curves are always read in family coordinates after the
  import's transform, so the measured sizes are what Revit shows. Use `expected` to catch a wrong scale.
- **Splines and ellipses** are approximated by Revit's display tessellation; the form is made of straight segments.
- **No merging.** Overlapping or crossing curves are reported, not repaired; fix the drawing, hide a layer, or pick a
  different loop.
- **Minimal regions.** When curves divide an outline, the outline itself is not a loop (see step 6). Leave the dividing
  layer out to get the whole outline.
- **Pair checks** (overlap, crossing, end on curve) compare every pair of segments and are skipped above 4000 segments
  (issue `pairChecksSkipped`); at most 50,000 curves are read.
- **removeSourceCurves** deletes the curves of the loops used, including curves those loops share with other loops.
- **Revit 2024 and 2026** use the same API members (`ImportInstance`, `GeometryInstance.GetSymbolGeometry`,
  `GraphicsStyle.GraphicsStyleCategory`, `CurveElement.GeometryCurve`/`LineStyle`, `Curve.Tessellate`); no version branches.

## Live test

The issue's acceptance: "Use a dimensioned manufacturer DWG to create an editable enclosure extrusion and cutout at the
expected size and location. Repeat with native family curves. Verify geometry after save/reopen and verify an invalid
outline leaves the family unchanged." Run in Revit 2024 and in 2026.

1. Create a family from `Metric Generic Model.rft` (`revit_create_model_family`, `savePath` `C:\fam\ENC-200x120.rfa`)
   and open it. In the `Ref. Level` plan, **Insert > Import CAD** a manufacturer DWG with a dimensioned enclosure
   outline and cutout (origin to origin).
2. `revit_get_model_family include=["imports"]`: the import with its layers and transform.
3. `revit_get_model_family_profiles` with `{"source": {"import": "<name>", "layers": ["<outline>", "<cutout>"]}}`:
   check `workPlane.coincidesWith` is the level, the loop sizes match the DWG's dimensions, the cutout is a child of
   the outline, and `issues` is empty (or explain each one).
4. Preview the [create example](#examples) with the loop indices and the DWG's dimensions as `expected`: `ready` true;
   `forms[*].measured` match. The family is unchanged afterwards (`revit_get_model_family include=["forms"]`).
5. Run it. In 3D, the enclosure and the cutout sit where the DWG shows them; `created[0].sizeCheck` is `ok`;
   `cuts[0].volumeAfter` is smaller than `volumeBefore` by the cutout's volume within the enclosure.
6. The forms are editable: `revit_edit_model_family_forms` `edit [{"form": <id>, "end": 100}]` changes the depth;
   lock a face to a reference plane with `revit_edit_model_family_references`.
7. Native curves: draw the same outline with model lines (and a circle) on `Ref. Level`, then repeat steps 3 to 5 with
   `{"source": {"curves": [ids]}}` and `removeSourceCurves: true`; the lines are gone afterwards. Repeat once with
   `{"source": {"view": "Ref. Level"}}`.
8. Save (`target.overwrite`), close and reopen the family. `revit_get_model_family include=["forms","combinations"]`:
   the same ids, bounding boxes and the cut.
9. Invalid outline: delete one line of a native rectangle (or draw one with a 2 mm gap) and call the create tool on
   its loop index from before: the result has `problems` (no such loop, the open chain and its gap), and
   `revit_get_model_family include=["forms"]` shows the family unchanged. Repeat with a bow-tie outline (problem: crosses
   itself) and with `expected` set 10 mm off (problem: measures … expected …).
10. With a spline or ellipse in the DWG, its loop is `approximated: true` and still builds.

## For developers

- Pure (`RevitMCP.Addin/ModelFamilies`): `ModelFamilyProfileAnalysis.Run(curves, frame, options)` does the analysis
  above on `ModelFamilyProfileCurve`s (family mm); `ModelFamilyProfilePlane` fits and frames the plane;
  `ModelFamilyProfilePlan`/`ModelFamilyProfileRequest` parse the arguments; `ModelFamilyProfileSelection.Choose` picks and
  checks a form's loops (the decision that keeps an invalid outline from reaching a transaction) and `ToExtrusion`
  turns it into #171's `ModelFamilyFormExtrusionSpec`.
- Revit (`RevitMCP.Addin/Tools/ModelFamilies`): `ModelFamilyProfileSource` reads imports (`CadLayerResolver` for layers),
  family curves and view lines; `ModelFamilyProfileExecutor` runs both tools; the cut step calls
  `ModelFamilyFormExecutor.Run` with a `cuts` list in a sub-context, so it is #171's code.
