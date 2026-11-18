using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// The body of revit_preview_edit_model_family_references / revit_edit_model_family_references (#170). Runs the
/// operation lists in a fixed order, each as one step (one transaction): remove, referencePlanes, referenceLines,
/// dimensions, alignments. A step Revit refuses (an over-constrained family) rolls the whole edit back; the message then
/// names the request items (<c>dimensions[2]</c>) whose elements Revit blamed. Everything written is read back, so a
/// silent refusal is reported as a problem.
/// </summary>
internal static class ModelFamilyReferenceExecutor
{
    public const string RemoveArg = "remove";
    public const string ReferencePlanesArg = "referencePlanes";
    public const string ReferenceLinesArg = "referenceLines";
    public const string DimensionsArg = "dimensions";
    public const string AlignmentsArg = "alignments";

    /// <summary>References further apart than this cannot be aligned.</summary>
    public const double AlignmentToleranceMm = 0.1;
    /// <summary>Default distance of a dimension line from the references' centre, in the view.</summary>
    public const double DefaultLineOffsetMm = 300;
    public const double DefaultAngularRadiusMm = 500;
    /// <summary>How far a dimension line runs past the outermost references.</summary>
    private const double LinePaddingMm = 100;

    private static ModelFamilyReferenceEditSpec Parse(IReadOnlyDictionary<string, object?> args, List<string> problems) =>
        ModelFamilyReferenceEditSpec.Parse(
            ModelFamilyJson.Token(args, RemoveArg),
            ModelFamilyJson.Objects(args, ReferencePlanesArg, problems),
            ModelFamilyJson.Objects(args, ReferenceLinesArg, problems),
            ModelFamilyJson.Objects(args, DimensionsArg, problems),
            ModelFamilyJson.Objects(args, AlignmentsArg, problems),
            problems);

    /// <summary>What the request asks for in one line, for the approval summary.</summary>
    public static string Describe(RevitMCP.Core.Models.McpToolRequest request)
    {
        var problems = new List<string>();
        var spec = Parse(request.Arguments ?? new Dictionary<string, object?>(), problems);
        return spec.Describe() + (problems.Count > 0 ? $"; {problems.Count} input problem(s)" : string.Empty);
    }

    public static object? Run(ModelFamilyContext context)
    {
        var problems = context.Problems;
        var spec = Parse(context.Args, problems);
        if (spec.IsEmpty && problems.Count == 0)
            problems.Add("Nothing to do: give remove, referencePlanes, referenceLines, dimensions or alignments.");
        if (problems.Count > 0) return null;

        context.Summary = spec.Describe();
        var edit = new ReferenceEdit(context);
        var steps = new (string Name, bool Any, Action Run)[]
        {
            ("remove", spec.Remove.Count > 0, () => edit.Remove(spec.Remove)),
            (ReferencePlanesArg, spec.Planes.Count > 0, () => edit.Planes(spec.Planes)),
            (ReferenceLinesArg, spec.Lines.Count > 0, () => edit.Lines(spec.Lines)),
            (DimensionsArg, spec.Dimensions.Count > 0, () => edit.Dimensions(spec.Dimensions)),
            (AlignmentsArg, spec.Alignments.Count > 0, () => edit.Alignments(spec.Alignments))
        };
        for (var i = 0; i < steps.Length; i++)
        {
            if (!steps[i].Any) continue;
            steps[i].Run();
            if (problems.Count > 0)
            {
                var skipped = steps.Skip(i + 1).Where(s => s.Any).Select(s => s.Name).ToList();
                if (skipped.Count > 0)
                    context.Warnings.Add($"{string.Join(", ", skipped)} were not checked: they may use what {steps[i].Name} creates. Fix the problems above, then preview again.");
                break;
            }
        }
        return edit.Result();
    }

    /// <summary>The state of one edit: the result rows and which request item created or changed each element.</summary>
    private sealed class ReferenceEdit
    {
        private readonly ModelFamilyContext _context;
        private readonly Document _doc;
        private readonly Dictionary<long, string> _sources = new();
        private readonly JArray _removed = new();
        private readonly JArray _planes = new();
        private readonly JArray _lines = new();
        private readonly JArray _dimensions = new();
        private readonly JArray _alignments = new();
        private readonly JArray _originChanges = new();
        private bool _originTouched;

        public ReferenceEdit(ModelFamilyContext context)
        {
            _context = context;
            _doc = context.FamilyDoc;
        }

        private List<string> Problems => _context.Problems;

        public JObject Result()
        {
            var result = new JObject();
            if (_removed.Count > 0) result["removed"] = _removed;
            if (_planes.Count > 0) result[ReferencePlanesArg] = _planes;
            if (_lines.Count > 0) result[ReferenceLinesArg] = _lines;
            if (_dimensions.Count > 0) result[DimensionsArg] = _dimensions;
            if (_alignments.Count > 0) result[AlignmentsArg] = _alignments;
            if (_originTouched)
            {
                var origin = ModelFamilyReferenceInspect.Origin(_doc);
                origin["changes"] = _originChanges;
                result["origin"] = origin;
            }
            var blamed = _context.Failures.SelectMany(f => f.ElementIds).Distinct().Where(_sources.ContainsKey)
                .Select(id => new JObject { ["elementId"] = id, ["source"] = _sources[id] }).ToList();
            if (blamed.Count > 0) result["failureSources"] = new JArray(blamed);
            return result;
        }

        /// <summary>
        /// Runs one step. When Revit refuses it, the exception is rethrown naming the request items whose elements
        /// Revit blamed, so the caller knows which item conflicts.
        /// </summary>
        private void Step(string step, Action action)
        {
            try
            {
                _context.Transact(step, action);
            }
            catch (ModelFamilyStepException ex)
            {
                var named = ex.Failures.SelectMany(f => f.ElementIds).Distinct().Where(_sources.ContainsKey)
                    .Select(id => $"{_sources[id]} (element {id})").Distinct().ToList();
                if (named.Count == 0) throw;
                throw new ModelFamilyStepException(ex.Step,
                    $"{ex.Message.TrimEnd('.')}. Caused by {string.Join(", ", named)}: change or leave out {(named.Count == 1 ? "that item" : "those items")}.",
                    ex.Failures);
            }
        }

        private void Blame(Element element, string source)
        {
            if (!_sources.ContainsKey(element.Id.Value)) _sources[element.Id.Value] = source;
        }

        // ── 1. Remove ────────────────────────────────────────────────────────

        public void Remove(List<long> ids)
        {
            var dimensions = ModelFamilyReferenceInspect.AllDimensions(_doc);
            var rows = new List<JObject>();
            for (var i = 0; i < ids.Count; i++)
            {
                var source = $"remove[{i}]";
                var element = _doc.GetElement(new ElementId(ids[i]));
                switch (element)
                {
                    case null:
                        Problems.Add($"{source}: the family has no element {ids[i]}.");
                        continue;
                    case ReferencePlane plane:
                    {
                        rows.Add(new JObject { ["id"] = ids[i], ["kind"] = "referencePlane", ["name"] = plane.Name });
                        if (ModelFamilyReferenceInspect.DefinesOrigin(plane))
                            _context.Warnings.Add($"{source}: {ModelFamilyReferenceResolver.PlaneName(plane)} defines the family origin; after removing it no plane fixes the insertion point in that direction (set definesOrigin on another plane).");
                        var carried = dimensions.Where(d => ModelFamilyReferenceInspect.References(d).Any(r => r.ElementId == plane.Id)).Select(d => d.Id.Value).ToList();
                        if (carried.Count > 0)
                            _context.Warnings.Add($"{source}: {ModelFamilyReferenceResolver.PlaneName(plane)} carries dimensions or alignments {string.Join(", ", carried)}; Revit deletes them with it, and geometry it drove is no longer constrained.");
                        break;
                    }
                    case ModelCurve curve when ModelFamilyReferenceInspect.SafeIsReferenceLine(curve):
                        rows.Add(new JObject { ["id"] = ids[i], ["kind"] = "referenceLine" });
                        break;
                    case Dimension dimension:
                        rows.Add(new JObject
                        {
                            ["id"] = ids[i],
                            ["kind"] = ModelFamilyReferenceInspect.IsAlignment(dimension) ? "alignment" : "dimension",
                            ["label"] = SafeLabel(dimension)
                        });
                        break;
                    default:
                        Problems.Add($"{source}: element {ids[i]} is {ModelFamilyLookup.Describe(element)}, not a reference plane, reference line, dimension or alignment; this tool removes only those.");
                        continue;
                }
            }
            if (Problems.Count > 0) return;

            ICollection<ElementId> deleted = new List<ElementId>();
            Step("Remove", () => deleted = _doc.Delete(ids.Select(id => new ElementId(id)).ToList()));
            foreach (var row in rows) _removed.Add(row);
            var also = deleted.Select(id => id.Value).Where(id => !ids.Contains(id)).ToList();
            if (also.Count > 0)
                _context.Warnings.Add($"Revit also deleted {also.Count} dependent element(s) with them: {string.Join(", ", also.Take(20))}.");
        }

        // ── 2. Reference planes ──────────────────────────────────────────────

        public void Planes(List<ModelFamilyPlaneOperation> operations)
        {
            var touched = new List<(ModelFamilyPlaneOperation Op, ReferencePlane Plane)>();
            var originSet = new List<(XYZ Normal, string Source)>();
            Step("Reference planes", () =>
            {
                foreach (var op in operations)
                {
                    var before = Problems.Count;
                    var plane = op.IsCreate ? (op.IsOffset ? CreateOffset(op) : Create(op)) : EditPlane(op);
                    if (plane == null || Problems.Count > before) continue;
                    ApplyCommon(plane, op, originSet);
                    touched.Add((op, plane));
                }
            });
            if (Problems.Count > 0) return;

            // Read back: a property Revit silently ignored is a problem, not a success.
            foreach (var (op, plane) in touched)
            {
                if (op.ResultName != null && !string.Equals(plane.Name, op.ResultName.Trim(), StringComparison.Ordinal))
                    Problems.Add($"{op.Source}: the plane's name reads back as '{plane.Name}', not '{op.ResultName}'.");
                VerifyIsReference(plane, op.IsReference, op.Source);
                if (op.DefinesOrigin != null && ModelFamilyReferenceInspect.DefinesOrigin(plane) != op.DefinesOrigin)
                    Problems.Add($"{op.Source}.definesOrigin: reads back as {ModelFamilyReferenceInspect.DefinesOrigin(plane).ToString().ToLowerInvariant()}; Revit did not take it.");
                var row = ModelFamilyReferenceInspect.PlaneRow(plane);
                row.AddFirst(new JProperty("action", op.IsCreate ? "created" : "edited"));
                row.AddFirst(new JProperty("source", op.Source));
                _planes.Add(row);
            }
        }

        private ReferencePlane? Create(ModelFamilyPlaneOperation op)
        {
            var ends = ModelFamilyReferenceGeometry.Ends(op.Origin!, op.Direction!, op.Normal, op.CutVector, op.LengthMm, op.Source, Problems);
            if (ends == null || !NameFree(op.Name, null, $"{op.Source}.name")) return null;
            return NewPlane(op, ends, $"{op.Source}");
        }

        private ReferencePlane? CreateOffset(ModelFamilyPlaneOperation op)
        {
            var from = ModelFamilyLookup.ReferencePlane(_doc, op.OffsetFrom, $"{op.Source}.offset.from", Problems);
            if (from == null || !NameFree(op.Name, null, $"{op.Source}.name")) return null;
            var normal = Vector(from.Normal.Normalize());
            var ends = ModelFamilyReferenceGeometry.Offset(Mm(from.BubbleEnd), Mm(from.FreeEnd), normal, op.OffsetDistanceMm);
            if (op.LengthGiven)
            {
                var middle = ModelFamilyReferenceGeometry.Scale(ModelFamilyReferenceGeometry.Add(ends.BubbleEnd, ends.FreeEnd), 0.5);
                var along = ModelFamilyReferenceGeometry.Normalize(ModelFamilyReferenceGeometry.Subtract(ends.FreeEnd, ends.BubbleEnd));
                if (along != null)
                {
                    ends.BubbleEnd = ModelFamilyReferenceGeometry.Subtract(middle, ModelFamilyReferenceGeometry.Scale(along, op.LengthMm / 2));
                    ends.FreeEnd = ModelFamilyReferenceGeometry.Add(middle, ModelFamilyReferenceGeometry.Scale(along, op.LengthMm / 2));
                }
            }
            return NewPlane(op, ends, op.Source);
        }

        private ReferencePlane? NewPlane(ModelFamilyPlaneOperation op, ModelFamilyReferenceGeometry.PlaneEnds ends, string source)
        {
            var view = op.View != null
                ? ModelFamilyLookup.View(_doc, op.View, $"{source}.view", Problems)
                : AutoView(ModelFamilyReferenceGeometry.PlaneViewKinds(ends.Normal), null) ?? ModelFamilyLookup.Views(_doc).FirstOrDefault();
            if (view == null)
            {
                if (op.View == null) Problems.Add($"{source}: the family has no view to create the plane in; name one with view.");
                return null;
            }
            ReferencePlane plane;
            try
            {
                plane = _doc.FamilyCreate.NewReferencePlane(ModelFamilyLookup.Xyz(ends.BubbleEnd), ModelFamilyLookup.Xyz(ends.FreeEnd), Xyz(ends.CutVector), view);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{source}: Revit refused the reference plane: {ex.Message.TrimEnd('.')}");
            }
            if (plane == null) throw new InvalidOperationException($"{source}: Revit did not create the reference plane.");
            Blame(plane, source);
            plane.Name = op.Name.Trim();

            // Keep the normal the caller asked for, so "move" and "offset" go the way they expect.
            var wanted = Xyz(ends.Normal);
            if (plane.Normal.DotProduct(wanted) < 0)
            {
                try { plane.Flip(); }
                catch { /* reported below */ }
                if (plane.Normal.DotProduct(wanted) < 0)
                    _context.Warnings.Add($"{source}: Revit made the plane's normal {Format(plane.Normal)}, opposite to the one asked for; move distances on it follow Revit's normal.");
            }
            return plane;
        }

        private ReferencePlane? EditPlane(ModelFamilyPlaneOperation op)
        {
            var plane = ModelFamilyLookup.ReferencePlane(_doc, op.Plane, $"{op.Source}.plane", Problems);
            if (plane == null) return null;
            Blame(plane, op.Source);
            if (op.Rename != null)
            {
                if (!NameFree(op.Rename, plane.Id.Value, $"{op.Source}.rename")) return null;
                try { plane.Name = op.Rename.Trim(); }
                catch (Exception ex)
                {
                    Problems.Add($"{op.Source}.rename: Revit refused the name '{op.Rename}': {ex.Message.TrimEnd('.')}.");
                    return null;
                }
            }
            if (op.MoveMm is { } move)
            {
                var vector = plane.Normal.Normalize().Multiply(ModelFamilyJson.ToFeet(move));
                try { ElementTransformUtils.MoveElement(_doc, plane.Id, vector); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"{op.Source}.move: Revit refused to move {ModelFamilyReferenceResolver.PlaneName(plane)}: {ex.Message.TrimEnd('.')}");
                }
            }
            return plane;
        }

        /// <summary>isReference and definesOrigin, shared by created and edited planes.</summary>
        private void ApplyCommon(ReferencePlane plane, ModelFamilyPlaneOperation op, List<(XYZ Normal, string Source)> originSet)
        {
            if (op.IsReference != null) SetIsReference(plane, op.IsReference, op.Source);
            if (op.DefinesOrigin == null) return;

            var parameter = ModelFamilyReferenceInspect.DefinesOriginParameter(plane);
            if (parameter == null || parameter.IsReadOnly)
            {
                Problems.Add($"{op.Source}.definesOrigin: {ModelFamilyReferenceResolver.PlaneName(plane)} has no settable Defines Origin parameter.");
                return;
            }
            _originTouched = true;
            var normal = plane.Normal.Normalize();
            if (op.DefinesOrigin == true)
            {
                var clash = originSet.FirstOrDefault(o => Parallel(o.Normal, normal));
                if (clash.Source != null)
                {
                    Problems.Add($"{op.Source}.definesOrigin: {clash.Source} also defines the origin in this direction; only one plane per direction can.");
                    return;
                }
                originSet.Add((normal, op.Source));
                var previous = new JArray();
                foreach (var other in ModelFamilyLookup.ReferencePlanes(_doc))
                {
                    if (other.Id == plane.Id || !ModelFamilyReferenceInspect.DefinesOrigin(other) || !Parallel(other.Normal.Normalize(), normal)) continue;
                    // Revit keeps one origin plane per direction: clear the old one explicitly rather than rely on it.
                    var otherParameter = ModelFamilyReferenceInspect.DefinesOriginParameter(other);
                    try { otherParameter?.Set(0); }
                    catch { /* Revit may clear it itself when the new one is set */ }
                    previous.Add(new JObject { ["id"] = other.Id.Value, ["name"] = other.Name });
                }
                var wasAlready = ModelFamilyReferenceInspect.DefinesOrigin(plane);
                parameter.Set(1);
                _originChanges.Add(new JObject
                {
                    ["source"] = op.Source,
                    ["direction"] = ModelFamilyLookup.Direction(normal),
                    ["previous"] = wasAlready ? new JArray(new JObject { ["id"] = plane.Id.Value, ["name"] = plane.Name }) : previous,
                    ["now"] = new JObject { ["id"] = plane.Id.Value, ["name"] = plane.Name }
                });
            }
            else
            {
                var wasDefining = ModelFamilyReferenceInspect.DefinesOrigin(plane);
                parameter.Set(0);
                if (wasDefining)
                    _context.Warnings.Add($"{op.Source}.definesOrigin: no plane defines the origin in direction {Format(normal)} any more; set definesOrigin on another plane facing that way.");
                _originChanges.Add(new JObject
                {
                    ["source"] = op.Source,
                    ["direction"] = ModelFamilyLookup.Direction(normal),
                    ["previous"] = wasDefining ? new JArray(new JObject { ["id"] = plane.Id.Value, ["name"] = plane.Name }) : new JArray(),
                    ["now"] = null
                });
            }
        }

        private bool NameFree(string name, long? self, string source)
        {
            var clash = ModelFamilyReferenceEditSpec.NameClash(name, ModelFamilyLookup.ReferencePlanes(_doc).Select(p => (p.Id.Value, p.Name ?? string.Empty)), self);
            if (clash == null) return true;
            Problems.Add($"{source}: '{name.Trim()}' is already the name of reference plane {clash.Value.Id}; plane names must be unique. Choose another name, or edit that plane with {{plane: {clash.Value.Id}}}.");
            return false;
        }

        // ── 3. Reference lines ───────────────────────────────────────────────

        public void Lines(List<ModelFamilyLineOperation> operations)
        {
            var touched = new List<(ModelFamilyLineOperation Op, ModelCurve Line)>();
            Step("Reference lines", () =>
            {
                foreach (var op in operations)
                {
                    var before = Problems.Count;
                    var line = op.IsCreate ? CreateLine(op) : EditLine(op);
                    if (line == null || Problems.Count > before) continue;
                    if (op.IsReference != null) SetIsReference(line, op.IsReference, op.Source);
                    touched.Add((op, line));
                }
            });
            if (Problems.Count > 0) return;
            foreach (var (op, line) in touched)
            {
                if (!ModelFamilyReferenceInspect.SafeIsReferenceLine(line))
                    Problems.Add($"{op.Source}: element {line.Id.Value} reads back as a model line, not a reference line.");
                VerifyIsReference(line, op.IsReference, op.Source);
                var row = ModelFamilyReferenceInspect.LineRow(line);
                row.AddFirst(new JProperty("action", op.IsCreate ? "created" : "edited"));
                row.AddFirst(new JProperty("source", op.Source));
                _lines.Add(row);
            }
        }

        private ModelCurve? CreateLine(ModelFamilyLineOperation op)
        {
            var sketch = ModelFamilyLookup.SketchPlane(_doc, op.WorkPlane, $"{op.Source}.workPlane", Problems);
            if (sketch == null) return null;
            var plane = sketch.GetPlane();
            foreach (var (point, key) in new[] { (op.Start!, "start"), (op.End!, "end") })
            {
                var off = Math.Abs(plane.Normal.DotProduct(ModelFamilyLookup.Xyz(point) - plane.Origin));
                if (ModelFamilyJson.ToMm(off) > 0.01)
                {
                    Problems.Add($"{op.Source}.{key}: is {ModelFamilyJson.ToMm(off):0.###} mm off the work plane; a reference line lies in its work plane (give points on it, or another workPlane).");
                    return null;
                }
            }
            ModelCurve curve;
            try
            {
                curve = _doc.FamilyCreate.NewModelCurve(Line.CreateBound(ModelFamilyLookup.Xyz(op.Start!), ModelFamilyLookup.Xyz(op.End!)), sketch);
                Blame(curve, op.Source);
                curve.ChangeToReferenceLine();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{op.Source}: Revit refused the reference line: {ex.Message.TrimEnd('.')}");
            }
            return curve;
        }

        private ModelCurve? EditLine(ModelFamilyLineOperation op)
        {
            var element = _doc.GetElement(new ElementId(op.Line!.Value));
            if (element is ModelCurve curve && ModelFamilyReferenceInspect.SafeIsReferenceLine(curve))
            {
                Blame(curve, op.Source);
                return curve;
            }
            Problems.Add(element == null
                ? $"{op.Source}.line: the family has no element {op.Line}."
                : $"{op.Source}.line: element {op.Line} is {ModelFamilyLookup.Describe(element)}, not a reference line.");
            return null;
        }

        // ── isReference ──────────────────────────────────────────────────────

        private void SetIsReference(Element element, string name, string source)
        {
            var entry = ModelFamilyIsReference.Find(name)!;
            var parameter = ModelFamilyReferenceInspect.IsReferenceParameter(element);
            if (parameter == null || parameter.IsReadOnly)
            {
                Problems.Add($"{source}.isReference: {ModelFamilyLookup.Describe(element)} has no settable Is Reference parameter.");
                return;
            }
            try { parameter.Set(entry.ParameterValue); }
            catch (Exception ex) { Problems.Add($"{source}.isReference: Revit refused '{entry.Name}': {ex.Message.TrimEnd('.')}."); }
        }

        private void VerifyIsReference(Element element, string? name, string source)
        {
            if (name == null) return;
            var entry = ModelFamilyIsReference.Find(name)!;
            var read = ModelFamilyReferenceInspect.IsReferenceOf(element);
            var value = read?["value"]?.Value<int>();
            if (value != entry.ParameterValue)
                Problems.Add($"{source}.isReference: reads back as {value?.ToString() ?? "nothing"} ('{read?["label"]}'), not {entry.ParameterValue} ({entry.Label}); Revit did not take it.");
        }

        // ── 4. Dimensions ────────────────────────────────────────────────────

        public void Dimensions(List<ModelFamilyDimensionOperation> operations)
        {
            var touched = new List<(ModelFamilyDimensionOperation Op, Dimension Dimension)>();
            Step("Dimensions", () =>
            {
                foreach (var op in operations)
                {
                    var before = Problems.Count;
                    var dimension = op.IsCreate
                        ? op.Kind == "angular" ? CreateAngular(op) : CreateLinear(op)
                        : EditDimension(op);
                    if (dimension == null || Problems.Count > before) continue;
                    Configure(dimension, op);
                    touched.Add((op, dimension));
                }
            });
            if (Problems.Count > 0) return;

            foreach (var (op, dimension) in touched)
            {
                if (op.Label.Length > 0 && !string.Equals(SafeLabel(dimension), op.Label, StringComparison.OrdinalIgnoreCase))
                    Problems.Add($"{op.Source}.label: the dimension's label reads back as '{SafeLabel(dimension) ?? "none"}', not '{op.Label}'; Revit did not take it.");
                if (op.Unlabel && SafeLabel(dimension) != null)
                    Problems.Add($"{op.Source}.label: the dimension is still labelled '{SafeLabel(dimension)}'.");
                if (op.Lock != null && ModelFamilyReferenceInspect.Locked(dimension) != op.Lock)
                    Problems.Add($"{op.Source}.lock: reads back as {ModelFamilyReferenceInspect.Locked(dimension)?.ToString().ToLowerInvariant() ?? "unknown"}; Revit did not take it.");
                if (op.Equal != null && SafeEqual(dimension) != op.Equal)
                    Problems.Add($"{op.Source}.equal: reads back as {SafeEqual(dimension)?.ToString().ToLowerInvariant() ?? "unknown"}; Revit did not take it.");
                var row = ModelFamilyReferenceInspect.DimensionRow(_doc, dimension);
                row.AddFirst(new JProperty("action", op.IsCreate ? "created" : "edited"));
                row.AddFirst(new JProperty("source", op.Source));
                _dimensions.Add(row);
            }
        }

        private List<ModelFamilyResolvedReference>? ResolveAll(IEnumerable<ModelFamilyReferenceSelector> selectors)
        {
            var before = Problems.Count;
            var resolved = selectors.Select(s => ModelFamilyReferenceResolver.Resolve(_doc, s, Problems)).ToList();
            return Problems.Count > before || resolved.Any(r => r == null) ? null : resolved.Select(r => r!).ToList();
        }

        private Dimension? CreateLinear(ModelFamilyDimensionOperation op)
        {
            var refs = ResolveAll(op.References);
            if (refs == null) return null;
            var view = op.View != null ? ModelFamilyLookup.View(_doc, op.View, $"{op.Source}.view", Problems) : null;
            if (op.View != null && view == null) return null;

            // The direction the dimension measures along: the references' common normal.
            var normal = refs.FirstOrDefault(r => r.Normal != null)?.Normal;
            if (normal == null)
            {
                if (view == null)
                {
                    Problems.Add($"{op.Source}: none of the references is a plane or face, so the view cannot be chosen; give view.");
                    return null;
                }
                normal = view.ViewDirection.CrossProduct(refs[0].LineDirection!).Normalize();
            }
            foreach (var reference in refs)
            {
                if (reference.Normal != null && !Parallel(reference.Normal, normal))
                {
                    Problems.Add($"{reference.Selector.Source}: {reference.Description} is not parallel to {refs.First(r => r.Normal != null).Description} ({Angle(reference.Normal, normal):0.##}° apart); a linear dimension measures between parallel references.");
                    return null;
                }
                if (reference.LineDirection != null && Math.Abs(reference.LineDirection.DotProduct(normal)) > 1e-6)
                {
                    Problems.Add($"{reference.Selector.Source}: {reference.Description} is not at right angles to the dimension direction {Format(normal)}.");
                    return null;
                }
            }
            view ??= AutoView(ModelFamilyReferenceGeometry.DimensionViewKinds(Vector(normal)), normal);
            if (view == null)
            {
                Problems.Add($"{op.Source}: no plan or elevation view contains the dimension direction {Format(normal)}; give view.");
                return null;
            }
            if (Math.Abs(view.ViewDirection.Normalize().DotProduct(normal)) > 1e-6)
            {
                Problems.Add($"{op.Source}.view: '{view.Name}' looks along the dimension direction {Format(normal)}, so the dimension cannot be drawn there. Use {DescribeKinds(ModelFamilyReferenceGeometry.DimensionViewKinds(Vector(normal)))}.");
                return null;
            }

            var line = DimensionLine(refs, normal, view, op.LinePoint);
            var array = new ReferenceArray();
            foreach (var reference in refs) array.Append(reference.Reference);
            Dimension? dimension;
            try
            {
                dimension = _doc.FamilyCreate.NewLinearDimension(view, line, array);
            }
            catch (Exception first)
            {
                try { dimension = _doc.FamilyCreate.NewDimension(view, line, array); }
                catch { throw new InvalidOperationException($"{op.Source}: Revit refused the dimension in '{view.Name}': {first.Message.TrimEnd('.')}"); }
            }
            if (dimension == null) throw new InvalidOperationException($"{op.Source}: Revit did not create the dimension in '{view.Name}'.");
            Blame(dimension, op.Source);
            return dimension;
        }

        /// <summary>
        /// The dimension line: along the normal through linePoint, or by default offset 300 mm (in the view, at right
        /// angles to the dimension) from the references' centre, running 100 mm past the outermost references.
        /// </summary>
        private static Line DimensionLine(List<ModelFamilyResolvedReference> refs, XYZ normal, View view, double[]? linePoint)
        {
            var points = refs.Where(r => r.Point != null).Select(r => r.Point!).ToList();
            var centre = points.Count == 0 ? XYZ.Zero : points.Aggregate(XYZ.Zero, (sum, p) => sum + p) / points.Count;
            var across = view.ViewDirection.CrossProduct(normal);
            across = across.GetLength() < 1e-9 ? XYZ.BasisZ : across.Normalize();
            var through = linePoint != null ? ModelFamilyLookup.Xyz(linePoint) : centre + across.Multiply(ModelFamilyJson.ToFeet(DefaultLineOffsetMm));
            var along = points.Count == 0 ? new List<double> { 0 } : points.Select(p => p.DotProduct(normal)).ToList();
            var pad = ModelFamilyJson.ToFeet(LinePaddingMm);
            var at = through.DotProduct(normal);
            var start = through + normal.Multiply(along.Min() - at - pad);
            var end = through + normal.Multiply(along.Max() - at + pad);
            return Line.CreateBound(start, end);
        }

        private Dimension? CreateAngular(ModelFamilyDimensionOperation op)
        {
            var refs = ResolveAll(op.References);
            if (refs == null) return null;
            if (refs.Any(r => r.Normal == null || r.Point == null || r.PointApproximate))
            {
                Problems.Add($"{op.Source}: an angular dimension needs two reference planes or planar faces whose position is known; {string.Join(", ", refs.Where(r => r.Normal == null || r.Point == null || r.PointApproximate).Select(r => r.Description))} is not one.");
                return null;
            }
            var n1 = Vector(refs[0].Normal!);
            var n2 = Vector(refs[1].Normal!);
            var axis = ModelFamilyReferenceGeometry.Normalize(ModelFamilyReferenceGeometry.Cross(n1, n2));
            if (axis == null)
            {
                Problems.Add($"{op.Source}: {refs[0].Description} and {refs[1].Description} are parallel; an angular dimension needs two planes at an angle (use a linear dimension).");
                return null;
            }
            var view = op.View != null
                ? ModelFamilyLookup.View(_doc, op.View, $"{op.Source}.view", Problems)
                : AutoView(ModelFamilyReferenceGeometry.AngularViewKinds(axis), null);
            if (view == null)
            {
                if (op.View == null) Problems.Add($"{op.Source}: no plan or elevation view looks along the planes' intersection {Format(Xyz(axis))}; give view.");
                return null;
            }
            var viewDirection = Vector(view.ViewDirection.Normalize());
            var arcAxes = ModelFamilyReferenceGeometry.Arc(n1, n2, viewDirection);
            var centre = ModelFamilyReferenceGeometry.Intersection(n1, Mm(refs[0].Point!), n2, Mm(refs[1].Point!));
            if (arcAxes == null || centre == null)
            {
                Problems.Add($"{op.Source}.view: '{view.Name}' does not see both planes edge-on; use {DescribeKinds(ModelFamilyReferenceGeometry.AngularViewKinds(axis))}.");
                return null;
            }
            var arc = Arc.Create(ModelFamilyLookup.Xyz(centre), ModelFamilyJson.ToFeet(op.RadiusMm ?? DefaultAngularRadiusMm), 0, arcAxes.EndAngle,
                Xyz(arcAxes.XAxis), Xyz(arcAxes.YAxis));
            Dimension? dimension;
            try { dimension = _doc.FamilyCreate.NewAngularDimension(view, arc, refs[0].Reference, refs[1].Reference); }
            catch (Exception ex) { throw new InvalidOperationException($"{op.Source}: Revit refused the angular dimension in '{view.Name}': {ex.Message.TrimEnd('.')}"); }
            if (dimension == null) throw new InvalidOperationException($"{op.Source}: Revit did not create the angular dimension.");
            Blame(dimension, op.Source);
            return dimension;
        }

        private Dimension? EditDimension(ModelFamilyDimensionOperation op)
        {
            var element = _doc.GetElement(new ElementId(op.Dimension!.Value));
            if (element is not Dimension dimension)
            {
                Problems.Add(element == null
                    ? $"{op.Source}.dimension: the family has no element {op.Dimension}."
                    : $"{op.Source}.dimension: element {op.Dimension} is {ModelFamilyLookup.Describe(element)}, not a dimension.");
                return null;
            }
            if (ModelFamilyReferenceInspect.IsAlignment(dimension) && (op.Label.Length > 0 || op.Equal != null))
            {
                Problems.Add($"{op.Source}: element {op.Dimension} is an alignment; it can only be locked or unlocked (or removed).");
                return null;
            }
            Blame(dimension, op.Source);
            return dimension;
        }

        /// <summary>equal, label and lock, in that order (a label has to be on before an equality is checked against it).</summary>
        private void Configure(Dimension dimension, ModelFamilyDimensionOperation op)
        {
            var segments = ModelFamilyReferenceInspect.SafeSegments(dimension);
            if (op.Equal != null)
            {
                if (segments < 2)
                    Problems.Add($"{op.Source}.equal: the dimension has one segment; an equality constraint needs three or more references.");
                else
                {
                    try { dimension.AreSegmentsEqual = op.Equal.Value; }
                    catch (Exception ex) { Problems.Add($"{op.Source}.equal: Revit refused it: {ex.Message.TrimEnd('.')}."); }
                }
            }

            if (op.Label.Length > 0) Label(dimension, op, segments);
            else if (op.Unlabel)
            {
                try { dimension.FamilyLabel = null; }
                catch (Exception ex) { Problems.Add($"{op.Source}.label: Revit refused to remove the label: {ex.Message.TrimEnd('.')}."); }
            }

            if (op.Lock != null && ModelFamilyReferenceInspect.Locked(dimension) != op.Lock)
            {
                if (op.Lock == true && SafeLabel(dimension) != null)
                {
                    Problems.Add($"{op.Source}.lock: the dimension is labelled '{SafeLabel(dimension)}' and its parameter drives it; a labelled dimension cannot be locked. Remove the label first (label: null).");
                    return;
                }
                try
                {
                    if (segments > 1)
                        foreach (DimensionSegment segment in dimension.Segments) segment.IsLocked = op.Lock.Value;
                    else dimension.IsLocked = op.Lock.Value;
                }
                catch (Exception ex) { Problems.Add($"{op.Source}.lock: Revit refused it: {ex.Message.TrimEnd('.')}."); }
            }
        }

        private void Label(Dimension dimension, ModelFamilyDimensionOperation op, int segments)
        {
            var parameter = ModelFamilyLookup.Parameter(_doc, op.Label, $"{op.Source}.label", Problems);
            if (parameter == null) return;
            var angular = ModelFamilyReferenceInspect.SafeShape(dimension) == DimensionShape.Angular;
            var expected = angular ? SpecTypeId.Angle : SpecTypeId.Length;
            ForgeTypeId? actual = null;
            try { actual = parameter.Definition.GetDataType(); }
            catch { /* checked by Revit below */ }
            if (actual != null && !SameSpec(actual, expected))
            {
                Problems.Add($"{op.Source}.label: '{parameter.Definition.Name}' is a {SpecLabel(actual)} parameter; {(angular ? "an angular" : "a linear")} dimension needs {(angular ? "an Angle" : "a Length")} parameter.");
                return;
            }
            if (parameter.IsReporting)
            {
                if (segments > 1)
                {
                    Problems.Add($"{op.Source}.label: '{parameter.Definition.Name}' is a reporting parameter; Revit does not label a multi-segment dimension with one.");
                    return;
                }
                var other = ModelFamilyReferenceInspect.AllDimensions(_doc)
                    .FirstOrDefault(d => d.Id != dimension.Id && string.Equals(SafeLabel(d), parameter.Definition.Name, StringComparison.Ordinal));
                if (other != null)
                {
                    Problems.Add($"{op.Source}.label: the reporting parameter '{parameter.Definition.Name}' already reports dimension {other.Id.Value}; a reporting parameter reports one dimension.");
                    return;
                }
            }
            else if (parameter.IsDeterminedByFormula)
                _context.Warnings.Add($"{op.Source}.label: '{parameter.Definition.Name}' has a formula ({parameter.Formula}); the formula drives this dimension.");
            try { dimension.FamilyLabel = parameter; }
            catch (Exception ex)
            {
                Problems.Add($"{op.Source}.label: Revit refused to label the dimension with '{parameter.Definition.Name}': {ex.Message.TrimEnd('.')}.");
            }
        }

        // ── 5. Alignments ────────────────────────────────────────────────────

        public void Alignments(List<ModelFamilyAlignmentOperation> operations)
        {
            var touched = new List<(ModelFamilyAlignmentOperation Op, Dimension Alignment)>();
            Step("Alignments", () =>
            {
                foreach (var op in operations)
                {
                    var before = Problems.Count;
                    var alignment = CreateAlignment(op);
                    if (alignment == null || Problems.Count > before) continue;
                    touched.Add((op, alignment));
                }
            });
            if (Problems.Count > 0) return;
            foreach (var (op, alignment) in touched)
            {
                if (!alignment.IsValidObject)
                {
                    Problems.Add($"{op.Source}: Revit removed the alignment again when the step was committed.");
                    continue;
                }
                if (ModelFamilyReferenceInspect.Locked(alignment) is { } locked && locked != op.Lock)
                    Problems.Add($"{op.Source}.lock: reads back as {locked.ToString().ToLowerInvariant()}; Revit did not take it.");
                var row = ModelFamilyReferenceInspect.DimensionRow(_doc, alignment);
                row["kind"] = "alignment";
                row.AddFirst(new JProperty("action", "created"));
                row.AddFirst(new JProperty("source", op.Source));
                _alignments.Add(row);
            }
        }

        private Dimension? CreateAlignment(ModelFamilyAlignmentOperation op)
        {
            var refs = ResolveAll(new[] { op.Reference!, op.Element! });
            if (refs == null) return null;
            var (fixedRef, moving) = (refs[0], refs[1]);

            if (fixedRef.Normal != null && moving.Normal != null)
            {
                if (!Parallel(fixedRef.Normal, moving.Normal))
                {
                    Problems.Add($"{op.Source}: {moving.Description} is not parallel to {fixedRef.Description} ({Angle(fixedRef.Normal, moving.Normal):0.##}° apart); only coincident references can be aligned.");
                    return null;
                }
                if (fixedRef.Point != null && moving.Point != null)
                {
                    var gap = Math.Abs(ModelFamilyJson.ToMm((moving.Point - fixedRef.Point).DotProduct(fixedRef.Normal)));
                    if (fixedRef.PointApproximate || moving.PointApproximate)
                        _context.Warnings.Add($"{op.Source}: the position of {(fixedRef.PointApproximate ? fixedRef.Description : moving.Description)} is not known exactly; Revit checks that the two coincide.");
                    else if (gap > AlignmentToleranceMm)
                    {
                        Problems.Add($"{op.Source}: {moving.Description} is {gap:0.###} mm from {fixedRef.Description}. An alignment only locks references that already coincide; put the geometry on the plane first (create the form on that plane, or move it), then align.");
                        return null;
                    }
                }
            }

            View? view;
            if (op.View != null) view = ModelFamilyLookup.View(_doc, op.View, $"{op.Source}.view", Problems);
            else
            {
                var normal = fixedRef.Normal ?? moving.Normal;
                view = normal != null ? AutoView(ModelFamilyReferenceGeometry.DimensionViewKinds(Vector(normal)), normal) : ModelFamilyLookup.PlanView(_doc);
                if (view == null) Problems.Add($"{op.Source}: no plan or elevation view shows these references edge-on; give view.");
            }
            if (view == null) return null;

            Dimension? alignment;
            try { alignment = _doc.FamilyCreate.NewAlignment(view, fixedRef.Reference, moving.Reference); }
            catch (Exception ex) { throw new InvalidOperationException($"{op.Source}: Revit refused to align {moving.Description} to {fixedRef.Description} in '{view.Name}': {ex.Message.TrimEnd('.')}"); }
            if (alignment == null) throw new InvalidOperationException($"{op.Source}: Revit did not create the alignment.");
            Blame(alignment, op.Source);
            if (!op.Lock)
            {
                try { alignment.IsLocked = false; }
                catch (Exception ex) { Problems.Add($"{op.Source}.lock: Revit refused to unlock the alignment: {ex.Message.TrimEnd('.')}."); }
            }
            return alignment;
        }

        // ── Views ────────────────────────────────────────────────────────────

        /// <summary>
        /// The first existing view of <paramref name="kinds"/> (plan, front, back, left, right): the plan of the reference
        /// level; an elevation by its name, else by the way it faces. With <paramref name="contains"/>, only a view whose
        /// plane contains that direction.
        /// </summary>
        private View? AutoView(List<string> kinds, XYZ? contains)
        {
            var views = ModelFamilyLookup.Views(_doc);
            foreach (var kind in kinds)
            {
                View? view;
                if (kind == "plan") view = ModelFamilyLookup.PlanView(_doc);
                else
                {
                    var facing = ModelFamilyReferenceGeometry.ViewDirections[kind];
                    var elevations = views.Where(v => v.ViewType == ViewType.Elevation).ToList();
                    view = elevations.FirstOrDefault(v => string.Equals(v.Name, kind, StringComparison.OrdinalIgnoreCase) && Facing(v, facing))
                           ?? elevations.FirstOrDefault(v => Facing(v, facing));
                }
                if (view == null) continue;
                if (contains != null && Math.Abs(view.ViewDirection.Normalize().DotProduct(contains)) > 1e-6) continue;
                return view;
            }
            return null;
        }

        private static bool Facing(View view, double[] direction)
        {
            try { return view.ViewDirection.Normalize().DotProduct(Xyz(direction)) > 1 - 1e-6; }
            catch { return false; }
        }

        private static string DescribeKinds(List<string> kinds) => kinds.Count == 0
            ? "a view that contains it (none of the standard plan and elevations does)"
            : $"view {string.Join(" or ", kinds.Select(k => $"\"{k}\""))}";

        // ── Helpers ──────────────────────────────────────────────────────────

        private static string? SafeLabel(Dimension dimension)
        {
            try { return dimension.FamilyLabel?.Definition.Name; }
            catch { return null; }
        }

        private static bool? SafeEqual(Dimension dimension)
        {
            try { return ModelFamilyReferenceInspect.SafeSegments(dimension) > 1 ? dimension.AreSegmentsEqual : false; }
            catch { return null; }
        }

        private static bool SameSpec(ForgeTypeId a, ForgeTypeId b)
        {
            static string Base(string id)
            {
                var dash = id.LastIndexOf('-');
                return dash > 0 ? id.Substring(0, dash) : id;
            }
            return string.Equals(Base(a.TypeId ?? string.Empty), Base(b.TypeId ?? string.Empty), StringComparison.OrdinalIgnoreCase);
        }

        private static string SpecLabel(ForgeTypeId type)
        {
            try { return LabelUtils.GetLabelForSpec(type); }
            catch { return type.TypeId; }
        }

        private static bool Parallel(XYZ a, XYZ b) => ModelFamilyReferenceGeometry.Parallel(Vector(a.Normalize()), Vector(b.Normalize()));
        private static double Angle(XYZ a, XYZ b) => ModelFamilyReferenceGeometry.AngleDegrees(Vector(a.Normalize()), Vector(b.Normalize()));
        private static double[] Vector(XYZ v) => new[] { v.X, v.Y, v.Z };
        private static double[] Mm(XYZ point) => new[] { point.X * ModelFamilyJson.MmPerFoot, point.Y * ModelFamilyJson.MmPerFoot, point.Z * ModelFamilyJson.MmPerFoot };
        private static XYZ Xyz(double[] direction) => new(direction[0], direction[1], direction[2]);
        private static string Format(XYZ v) => $"[{Math.Round(v.X, 4)}, {Math.Round(v.Y, 4)}, {Math.Round(v.Z, 4)}]";
    }
}
