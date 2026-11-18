# Model family forms

Solid and void forms in a loadable 3D family (#171, part of [model-families.md](model-families.md)): extrusions,
blends, revolves, sweeps and swept blends on work planes, edits of their offsets and angles, void cuts and joins, and
the face references other tools place and constrain against.

| Tool | Permission | What it does |
|---|---|---|
| `revit_preview_edit_model_family_forms` | read-only | Runs the edit in a transaction group and rolls it back: `ready`, `problems`, measured profile sizes, what Revit would build or refuse. |
| `revit_edit_model_family_forms` | approval | Runs the edit and saves/loads the family as `target` says ([model-families.md](model-families.md#the-target-argument)). |

`revit_get_model_family include=["forms","combinations"]` reads the result (see [Inspection](#inspection)).

## Arguments

`target` and `document` as for every model-family tool. The operation lists run in this order, one transaction per
item; a step Revit refuses rolls the whole edit back:

| # | List | Item |
|---|---|---|
| 1 | `remove` | Form ids (or `{form: id}`) to delete. Runs first, so it cannot name a form created in the same call. |
| 2 | `extrusions` | `{profile, start?, end \| depth, …common}` |
| 3 | `blends` | `{bottomProfile, topProfile, start?, end \| depth, …common}` |
| 4 | `revolves` | `{profile, axis: {start, end}, startAngle?, endAngle?, …common}` |
| 5 | `sweeps` | `{path, profile, profilePlane?, profileCurve?, …common}` |
| 6 | `sweptBlends` | `{path, bottomProfile, topProfile, …common}` |
| 7 | `edit` | `{form, start?, end?, startAngle?, endAngle?, visible?, subcategory?}` |
| 8 | `cuts` | `{void, solid, remove?}` |
| 9 | `joins` | `{elements: [ids]}` or `{combination: id, unjoin: true}` |

Common keys of every form:

| Key | Default | Meaning |
|---|---|---|
| `solid` | `true` | `false` makes a void. |
| `workPlane` | `"level"` | A reference plane (name or id), `"level"` (the reference level), or `{origin: [x,y,z], normal: [x,y,z]}`. |
| `subcategory` | none | A subcategory of the family's category, by name. It must exist (#175 creates them). |
| `visible` | Revit's | Show or hide the form (its Visible parameter). |

A form references another one made in the same call with `{"created": "extrusions[0]"}` (any form list and index);
`edit`, `cuts` and `joins` accept it wherever they take a form id. The result lists the new ids in `createdIds`.

### Profiles

A profile is a list of loops (a single loop object is a list of one). The first loop is the outer boundary; further
loops are holes, which must lie inside it without touching it or each other (Revit decides solid and hole by
containment). A loop is one of:

| Loop | Example | Notes |
|---|---|---|
| Polygon | `{"points": [[0,0,0],[100,0,0],[100,50,0]]}` | Closed; do not repeat the first point (a repeat is dropped). At least 3 points. |
| Rectangle | `{"rectangle": {"min": [-100,-50,0], "max": [100,50,0]}}` | Opposite corners, both on the work plane; edges follow the plane's axes. |
| Circle | `{"circle": {"center": [0,0,0], "radius": 15}}` | Made as two half arcs: blends and swept blends refuse a single closed curve in a loop, so the tools never send one. |
| Segments | `{"segments": [{"line": {"start": p, "end": p}}, {"arc": {"start": p, "mid": p, "end": p}}]}` | Each segment must start where the previous ends and the last must end at the first start (gaps up to 0.01 mm are snapped shut). |

Points are family coordinates in millimetres (`[x, y]` means z = 0). They are projected onto the work plane: a point
more than 0.5 mm off the plane is a problem (it usually means the wrong `workPlane`), closer ones are projected
silently. The checks, all before anything is changed: every segment at least Revit's short-curve tolerance
(about 0.79 mm, read from Revit), no zero area, no loop crossing itself, holes inside the outer loop, loops not crossing,
arcs not straight. The preview reports each profile's measured `width` and `height` (along the work plane's x and y
axes), `area` (net of holes) and loop count.

### Work planes

`workPlane` is resolved to a sketch plane inside the transaction. A form sketched on a **reference plane follows that
plane**: when a dimension or a parameter moves the plane, the form's sketch moves with it. This is what makes the
constraints of #170 work: sketch on the reference planes, then lock the form's faces to them. `"level"` sketches on
the reference level; `{origin, normal}` makes a free plane that follows nothing.

Offsets (`start`, `end`) are measured along the sketch plane's normal, which the result reports in
`created[i].sketchPlane.normal`. A reference plane's normal depends on how it was drawn; check it before relying on the
sign of an offset.

### Forms

| Form | Revit call | Notes |
|---|---|---|
| Extrusion | `FamilyCreate.NewExtrusion`, then `StartOffset`/`EndOffset` | `depth` means `start: 0, end: depth`. `start` and `end` must differ. |
| Blend | `FamilyCreate.NewBlend(solid, bottom, top, sketchPlane)`, then `BottomOffset`/`TopOffset` | One loop per profile, both given on the work plane; the tool lifts the top loop by `end − start` along the normal, then sets the offsets. Revit pairs the loops' vertices itself. |
| Revolve | `FamilyCreate.NewRevolution` | `axis` lies on the work plane. The profile may touch the axis but not cross it. Angles in degrees, default 0 to 360, at most a full turn. |
| Sweep | `FamilyCreate.NewSweep(solid, path, sketchPlane, NewCurveLoopsProfile(…), profileCurve, profilePlane)` | `path`: segments on the work plane, open or closed. `profile`: loops in the profile plane's own coordinates `[u, v]` mm (z ignored); Revit maps this XY plane onto the plane perpendicular to the path at `profilePlane` (`start` default, `middle`, `end`) of path segment `profileCurve` (default 0). |
| Swept blend | `FamilyCreate.NewSweptBlend(solid, curve, sketchPlane, bottom, top)` | `path`: one line or arc. Both profiles one loop each in `[u, v]`, mapped like a sweep's. |

After each form the tool reads back what Revit built: id, sketch plane (and the reference plane it follows), offsets or
angles, bounding box (mm). For an extrusion on a plane square to the family axes it compares the bounding box with the
profile's width and height and with `|end − start|`; a difference over 0.1 mm is a warning with both values. A void
that cuts nothing at the end of the call is a warning.

### Edit

`edit` changes existing forms (or ones made earlier in the call): `start`/`end` (extrusions and blends), `startAngle`/
`endAngle` (revolves), `visible`, `subcategory` (`""` puts it back on the family category). Values are read back and
differences reported. A property driven by a family parameter is refused: change the parameter's value instead.

**Profile replacement is not supported.** Revit's sketch editing (`SketchEditScope`, Revit 2022+) works only in project
documents; its constructor refuses a family document. The workaround is `remove` plus a new form, which loses the
dimensions, labels and associations on the old form; add them again afterwards.

### Cuts and joins

- `cuts: [{void, solid}]` makes the void cut the solid. `SolidSolidCutUtils.CanElementCutElement` /
  `AddCutBetweenSolids` is tried first; where it does not apply (the API documents it for projects and conceptual
  families, and reports `CutFailureReason`) the two are combined with `Document.CombineElements`, the Family Editor's
  own way of cutting a solid with a void. The result says which (`method`) and the solid's volume before and after; an
  unchanged volume is a warning (the void may not overlap the solid).
- `cuts: [{void, solid, remove: true}]` removes a solid-solid cut, or deletes the two-member geometry combination that
  made the cut. A combination with more members is refused: unjoin it and cut again.
- `joins: [{elements: [a, b, …]}]` joins forms into a geometry combination (`Document.CombineElements`); the result
  lists the combination id and its members.
- `joins: [{combination: id, unjoin: true}]` deletes the combination. The API has no "uncombine" member, so the tool
  deletes the `GeomCombination` element and checks that every member still exists; if one was deleted with it, the
  step is rolled back.

### Face references

`revit_get_model_family include=["forms"]` lists each form's planar faces: `face` (`left|right|front|back|top|bottom`
when the outward normal is within 1° of −X/+X/−Y/+Y/+Z/−Z, else null; front is −Y, the side the Front elevation looks
at), `normal`, a `point` on it (mm), `area` (mm²) and `reference` (a stable representation from geometry computed with
references). Tools that place or constrain against a face accept `{"element": id, "face": "top"}` (when several faces
share a label, the outermost wins) or `{"reference": "<stable representation>"}`.

## Examples

Preview an enclosure with a round cable entry in its top (same arguments for the edit):

```json
{
  "target": { "path": "C:\\fam\\ELV_Device.rfa", "overwrite": true },
  "extrusions": [
    { "profile": { "rectangle": { "min": [-100, -50, 0], "max": [100, 50, 0] } }, "depth": 80, "subcategory": "Housing" },
    { "profile": { "circle": { "center": [0, 0, 0], "radius": 10 } }, "start": 60, "end": 90, "solid": false }
  ],
  "cuts": [ { "void": { "created": "extrusions[1]" }, "solid": { "created": "extrusions[0]" } } ]
}
```

A revolve, a sweep and a later edit:

```json
{
  "target": { "familyDocument": "active" },
  "revolves": [ { "profile": { "rectangle": { "min": [0, 0, 0], "max": [20, 0, 40] } }, "workPlane": "Center (Front/Back)",
                  "axis": { "start": [0, 0, 0], "end": [0, 0, 100] } } ],
  "sweeps": [ { "path": [ { "line": { "start": [0, 0, 0], "end": [0, 300, 0] } },
                          { "arc": { "start": [0, 300, 0], "mid": [29.3, 370.7, 0], "end": [100, 400, 0] } } ],
                "profile": { "circle": { "center": [0, 0], "radius": 8 } } } ],
  "edit": [ { "form": 3141, "end": 120 } ]
}
```

## Issue verification: an enclosure keeps its cutout

"An enclosure with a cutout retains the cut after a type size change and save/reopen."

1. With #170's tool, make reference planes `Left` (x = −100), `Right` (x = 100), `Front` (y = −50), `Back` (y = 50)
   and dimension them to the centre planes, labelled with type parameters `Width` = 200 and `Depth` = 100.
2. Create the enclosure on the reference level with its edges on those planes, a cutout void sketched on `Front`
   (it follows that plane when `Depth` changes), and the cut:

   ```json
   {
     "target": { "path": "C:\\fam\\ELV_Device.rfa", "overwrite": true },
     "extrusions": [
       { "profile": { "rectangle": { "min": [-100, -50, 0], "max": [100, 50, 0] } }, "depth": 80 },
       { "profile": { "rectangle": { "min": [-30, -50, 20], "max": [30, -50, 60] } }, "workPlane": "Front",
         "start": -15, "end": 15, "solid": false }
     ],
     "cuts": [ { "void": { "created": "extrusions[1]" }, "solid": { "created": "extrusions[0]" } } ]
   }
   ```

3. `revit_get_model_family include=["forms"]` lists the solid's faces; lock its `left`, `right`, `front` and `back`
   faces to the four planes with #170's tool (`{"element": <solid id>, "face": "left"}` and so on).
4. Flex: change `Width` to 300 and `Depth` to 160 with #176's flex tool (or set the type values), then back.
5. Save the family (`target.save`/`overwrite`), close it, open it again.
6. `revit_get_model_family include=["forms","combinations"]`: the solid's `cutBy` still lists the void (and the
   combination, when the cut was made by combining), the void's `cuts` lists the solid, the solid's bounding box follows
   the type size, and the void's sketch plane still names `Front`.

## Inspection

| Section | Contents |
|---|---|
| `forms` | `id`, `kind` (`extrusion|blend|revolve|sweep|sweptBlend|other`), `solid`, `sketchPlane` (`id`, `name`, `origin`, `normal`, `referencePlane` it follows, or `level`), `start`/`end` or `startAngle`/`endAngle`, `profile` loops in mm (`line`, `arc`, `circle`), `axis` (revolve), `path` (sweeps), `boundingBox`, `visible`, `subcategory`, `material` (read only; #173 assigns), `drivenBy` (family parameters driving offsets, angles, visibility, material), `cutBy`, `cuts`, `combination`, `faces`. |
| `combinations` | Geometry combinations: `id` and `members` (`id`, `kind`, `solid`). |

## Limitations

- **Template and category.** Forms need a 3D model family (for example `Metric Generic Model.rft`). Annotation and
  Detail Item families are refused. Conceptual mass and adaptive families use other API members (`NewExtrusionForm` …)
  and are not covered.
- **Profile replacement** is not supported in a family document (see [Edit](#edit)).
- **Blends.** Revit chooses how the two loops' vertices map onto each other; the API notes that `TopOffset` may end up
  below `BottomOffset` depending on loop and plane orientation. The tool sets both offsets and reports any difference.
- **Sweeps and swept blends** take sketched profiles only (no profile families) and a path on one work plane; there is
  no `ReferenceArray` (3D edge) path.
- **Size checks** use the axis-aligned bounding box, so they are skipped for extrusions on tilted planes.
- **Cuts.** The API's solid-solid cut utilities are documented for projects and conceptual families; in other families
  the tool cuts by combining the void and the solid. There is no API to uncombine, so `unjoin` deletes the combination
  (verified, see [Cuts and joins](#cuts-and-joins)).
- **Revit 2024 and 2026.** The members used are the same in both versions' API (`FamilyItemFactory.New*`,
  `SolidSolidCutUtils`, `Document.CombineElements`, `GenericForm`, `SketchPlane.GetPlaneReference`); no version branches.
  `GenericForm.Visible` is read-only in both, so visibility is set through the `IS_VISIBLE_PARAM` parameter.

## Live test

1. In Revit 2024 and in 2026, create a family from `Metric Generic Model.rft` (#169) and save it.
2. Preview the first example: `ready` is true, `forms[0].profile` is 200 × 100, `forms[1].profile` is 20 × 20. The family
   is unchanged afterwards.
3. Run it: `created[0].sizeCheck` is `ok`; `cuts[0].method` is `solidSolidCut` or `combination` and `volumeAfter` is
   smaller than `volumeBefore` by about π·10²·20 mm³. Note which method each version used.
4. Create one form of each kind: a blend (rectangle to circle), a revolve on `Center (Front/Back)`, a sweep with a line
   and an arc path and a circle profile, a swept blend on one arc. Check each in 3D: the sweep's profile sits at the
   path start, perpendicular to it, centred on the path (record how `[u, v]` maps on the profile plane), and the blend's
   offsets match `start`/`end`.
5. Edit: change the extrusion's `end`, the revolve's `endAngle` to 180, `visible: false` on the sweep; read back with
   `revit_get_model_family include=["forms"]`.
6. Give a profile with a 0.4 mm edge, a point 5 mm off the plane, a hole outside the outer loop, and a revolve
   profile crossing its axis: the preview names each problem; nothing changes.
7. Run the [issue verification](#issue-verification-an-enclosure-keeps-its-cutout) steps, including save, close and
   reopen.
8. Join two solids, then unjoin the combination: both forms remain.
9. `{"element": <id>, "face": "top"}` resolves in a tool that takes face references (#170 or #174).

## For developers (#177 and other features)

The code is meant to be reused:

- **Pure profile model** (`RevitMCP.Addin/ModelFamilies/ModelFamilyFormProfile.cs`, no Revit API):
  `ModelFamilyFormProfile.Parse(token, source, problems)` reads loops; `profile.OnPlane(frame, check, problems,
  singleLoop)` projects them onto a `ModelFamilyFormFrame` (origin and axes as doubles, mm) and checks them, returning a
  `ModelFamilyFormPlanarProfile` (loops with `Uv` and `World` segments, `Width`, `Height`, `Area`). A CAD importer can
  also build `ModelFamilyFormLoop { Kind = Segments, Segments = … }` directly from `ModelFamilyFormSegment.Line/Arc`.
  `ModelFamilyFormPath` does the same for open or closed paths.
- **Revit builder** (`RevitMCP.Addin/Tools/ModelFamilies/ModelFamilyFormBuilder.cs`): `Frame(Plane)`, `Check(context)`,
  `Prepare(context, spec)` (no transaction; problems to `context.Problems`), then inside `context.Transact(...)` one of
  `CreateExtrusion`, `CreateBlend`, `CreateRevolve`, `CreateSweep`, `CreateSweptBlend` (or `Create`), then
  `Verify(context, form, prepared)` after the transaction.
- **Faces** (`ModelFamilyFormFaces`): `PlanarFaces(element)`, `Find(element, label)`, `Resolve(doc, token, source,
  problems)` for `{element, face}` or `{reference}`; the label rule is the pure `ModelFamilyFormFaceLabels.LabelOf`.
