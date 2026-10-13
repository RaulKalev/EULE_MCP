# Circuit arcs

`revit_preview_draw_circuit_arcs` and `revit_draw_circuit_arcs` draw arc **detail lines** between the devices of
an electrical circuit in a plan view. They stand in for Revit wires, which are awkward to place and edit: the
arcs are plain detail lines that can be moved, restyled or deleted like any other.

## What gets drawn

- Circuits come from `circuitIds`, from `panel` (every circuit of that panel), or both.
- Only devices **visible in the view** are joined, so a circuit that spans levels gets the arcs of this level
  only, and hidden categories are left out. The preview reports devices on the circuit against devices in the view.
- `mode`
  - `chain` (default): start at the device nearest the panel, then always go to the nearest device not joined
    yet — like a daisy-chained cable.
  - `tree`: the shortest total length, with branches (a minimum spanning tree).
- `bulge` is the arc's height as a fraction of the distance between the two devices (default 0.2); `side` is
  `left`, `right` or `alternate`, looking from one device to the next.
- Devices closer together than `minDistanceMm` (stacked symbols) get no arc between them.
- `connectToPanel` also joins the panel to the first device when the panel is visible in the view.
- `lineStyle` names a line style; an unknown name is refused with the list of styles.
- `curve` is `spline` (default) or `arc`. A spline has the fewest points there can be — its two ends and one
  control point between them — so it is reshaped in Revit by dragging a single point. It is created as a
  three-point NURBS spline; the control point sits twice as far from the chord as the bulge, because such a
  spline only goes halfway to its control point. A Hermite spline is never passed to `NewDetailCurve`: in Revit
  2026 that call creates the line and then throws, leaving a duplicate behind.
- `homeRun` adds a short open-ended line from the device nearest the panel, pointing at the panel: the drafting
  sign for "this circuit goes to its panel". `homeRunLengthMm` (default 900, never more than 80 % of the way to
  the panel) and `homeRunSide` (default `right`) shape it. A circuit with a single device in the view gets only
  the home run. With `connectToPanel` and the panel visible, the full link replaces the home run.

## Redrawing

Every arc carries an Extensible Storage marker (`RevitMcpCircuitArc`) with its circuit's UniqueId. Drawing a
circuit again in the same view deletes that circuit's earlier arcs first (`replaceExisting`, default true), so
the tool can be rerun after devices move. Arcs of other circuits and hand-drawn lines are never touched.

The write runs in one transaction and is reversible with Undo. The route logic is in
`RevitMCP.Addin/Electrical/CircuitArcPlanner.cs` and covered by `CircuitArcPlannerTests.cs`.
