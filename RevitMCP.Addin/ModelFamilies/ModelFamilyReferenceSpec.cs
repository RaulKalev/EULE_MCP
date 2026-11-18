using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>One item of <c>referencePlanes</c>: create (from origin and direction, or offset from a plane) or edit.</summary>
public sealed class ModelFamilyPlaneOperation
{
    public string Source { get; set; } = string.Empty;
    /// <summary>False for an edit of an existing plane (<see cref="Plane"/>).</summary>
    public bool IsCreate { get; set; }
    /// <summary>True for <c>{name, offset: {from, distance}}</c>.</summary>
    public bool IsOffset { get; set; }

    public string Name { get; set; } = string.Empty;
    public double[]? Origin { get; set; }
    public double[]? Direction { get; set; }
    public double[]? Normal { get; set; }
    public double[]? CutVector { get; set; }
    public double LengthMm { get; set; } = ModelFamilyReferenceGeometry.DefaultPlaneLengthMm;
    /// <summary>True when length was given (an offset plane otherwise keeps its source plane's length).</summary>
    public bool LengthGiven { get; set; }
    public JToken? View { get; set; }
    public JToken? OffsetFrom { get; set; }
    public double OffsetDistanceMm { get; set; }

    /// <summary>Edit: the plane (name or id).</summary>
    public JToken? Plane { get; set; }
    public string? Rename { get; set; }
    public double? MoveMm { get; set; }

    /// <summary>The canonical isReference name (<see cref="ModelFamilyIsReference.Entry.Name"/>), or null to leave it.</summary>
    public string? IsReference { get; set; }
    public bool? DefinesOrigin { get; set; }

    /// <summary>The plane's name after this operation (created name or rename), or null when it keeps its name.</summary>
    public string? ResultName => IsCreate ? Name : Rename;

    /// <summary>The edited plane as written, for duplicate checks.</summary>
    public string PlaneKey => Plane == null ? string.Empty : (Plane.Type == JTokenType.String ? ((string?)Plane ?? string.Empty).Trim() : Plane.ToString()).ToLowerInvariant();
}

/// <summary>One item of <c>referenceLines</c>: create from two points on a work plane, or edit by id.</summary>
public sealed class ModelFamilyLineOperation
{
    public string Source { get; set; } = string.Empty;
    public bool IsCreate { get; set; }
    public double[]? Start { get; set; }
    public double[]? End { get; set; }
    public JToken? WorkPlane { get; set; }
    public long? Line { get; set; }
    public string? IsReference { get; set; }
}

/// <summary>One item of <c>dimensions</c>: create from references, or edit by id.</summary>
public sealed class ModelFamilyDimensionOperation
{
    public string Source { get; set; } = string.Empty;
    public bool IsCreate { get; set; }
    /// <summary>linear (default) or angular.</summary>
    public string Kind { get; set; } = "linear";
    public JToken? View { get; set; }
    public List<ModelFamilyReferenceSelector> References { get; } = new();
    public double[]? LinePoint { get; set; }
    public double? RadiusMm { get; set; }
    /// <summary>Family parameter to label with; empty when not given.</summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>Edit: <c>label: null</c> (or "") removes the label.</summary>
    public bool Unlabel { get; set; }
    public bool? Lock { get; set; }
    public bool? Equal { get; set; }
    public long? Dimension { get; set; }
}

/// <summary>One item of <c>alignments</c>: align <see cref="Element"/> to <see cref="Reference"/> and lock it.</summary>
public sealed class ModelFamilyAlignmentOperation
{
    public string Source { get; set; } = string.Empty;
    public JToken? View { get; set; }
    public ModelFamilyReferenceSelector? Reference { get; set; }
    public ModelFamilyReferenceSelector? Element { get; set; }
    public bool Lock { get; set; } = true;
}

/// <summary>
/// The arguments of <c>revit_edit_model_family_references</c> (#170), parsed and checked without Revit. Processed in
/// this order: <c>remove</c>, <c>referencePlanes</c>, <c>referenceLines</c>, <c>dimensions</c>, <c>alignments</c>.
/// </summary>
public sealed class ModelFamilyReferenceEditSpec
{
    public List<long> Remove { get; } = new();
    public List<ModelFamilyPlaneOperation> Planes { get; } = new();
    public List<ModelFamilyLineOperation> Lines { get; } = new();
    public List<ModelFamilyDimensionOperation> Dimensions { get; } = new();
    public List<ModelFamilyAlignmentOperation> Alignments { get; } = new();

    public bool IsEmpty => Remove.Count == 0 && Planes.Count == 0 && Lines.Count == 0 && Dimensions.Count == 0 && Alignments.Count == 0;

    public static readonly string[] PlaneCreateKeys = { "name", "origin", "direction", "normal", "cutVector", "length", "view", "isReference", "definesOrigin" };
    public static readonly string[] PlaneOffsetKeys = { "name", "offset", "length", "view", "isReference", "definesOrigin" };
    public static readonly string[] PlaneEditKeys = { "plane", "rename", "isReference", "definesOrigin", "move" };
    public static readonly string[] LineCreateKeys = { "start", "end", "workPlane", "isReference" };
    public static readonly string[] LineEditKeys = { "line", "isReference" };
    public static readonly string[] DimensionCreateKeys = { "kind", "view", "references", "linePoint", "radius", "label", "lock", "equal" };
    public static readonly string[] DimensionEditKeys = { "dimension", "label", "lock", "equal" };
    public static readonly string[] AlignmentKeys = { "view", "reference", "element", "lock" };

    /// <summary>
    /// Parses the operation lists (already read from the arguments) and checks each on its own and against the others:
    /// duplicate names, an element both removed and edited, the same element edited twice, contradictory flags.
    /// </summary>
    public static ModelFamilyReferenceEditSpec Parse(JToken? remove, List<JObject> planes, List<JObject> lines,
        List<JObject> dimensions, List<JObject> alignments, List<string> problems)
    {
        var spec = new ModelFamilyReferenceEditSpec();
        ParseRemove(spec, remove, problems);
        for (var i = 0; i < planes.Count; i++) spec.Planes.Add(ParsePlane(planes[i], $"referencePlanes[{i}]", problems));
        for (var i = 0; i < lines.Count; i++) spec.Lines.Add(ParseLine(lines[i], $"referenceLines[{i}]", problems));
        for (var i = 0; i < dimensions.Count; i++) spec.Dimensions.Add(ParseDimension(dimensions[i], $"dimensions[{i}]", problems));
        for (var i = 0; i < alignments.Count; i++) spec.Alignments.Add(ParseAlignment(alignments[i], $"alignments[{i}]", problems));
        CheckContradictions(spec, problems);
        return spec;
    }

    private static void ParseRemove(ModelFamilyReferenceEditSpec spec, JToken? remove, List<string> problems)
    {
        if (remove == null) return;
        var items = remove is JArray array ? array.ToList() : new List<JToken> { remove };
        for (var i = 0; i < items.Count; i++)
        {
            var id = ModelFamilyJson.AsId(items[i]);
            if (id == null || id <= 0)
            {
                problems.Add($"remove[{i}]: give the element id of a reference plane, reference line, dimension or alignment (revit_get_model_family lists them).");
                continue;
            }
            if (spec.Remove.Contains(id.Value)) problems.Add($"remove[{i}]: element {id} is listed twice.");
            else spec.Remove.Add(id.Value);
        }
    }

    private static string? IsReference(JObject obj, string source, List<string> problems)
    {
        if (!ModelFamilyJson.Has(obj, "isReference")) return null;
        var text = ModelFamilyJson.Str(obj, "isReference");
        var entry = ModelFamilyIsReference.Find(text);
        if (entry == null) problems.Add($"{source}.isReference: '{text}' is not a reference type. Use one of {ModelFamilyIsReference.Names}.");
        return entry?.Name;
    }

    public static ModelFamilyPlaneOperation ParsePlane(JObject obj, string source, List<string> problems)
    {
        var op = new ModelFamilyPlaneOperation { Source = source };
        op.IsReference = IsReference(obj, source, problems);
        op.DefinesOrigin = ModelFamilyJson.Bool(obj, "definesOrigin", source, problems);

        if (ModelFamilyJson.Has(obj, "plane"))
        {
            ModelFamilyJson.CheckKeys(obj, source, problems, PlaneEditKeys);
            op.Plane = ModelFamilyJson.Get(obj, "plane");
            if (ModelFamilyJson.Has(obj, "rename"))
            {
                op.Rename = ModelFamilyJson.Str(obj, "rename");
                CheckName(op.Rename, $"{source}.rename", problems);
            }
            op.MoveMm = ModelFamilyJson.Number(obj, "move", source, problems);
            if (op.Rename == null && op.MoveMm == null && op.IsReference == null && op.DefinesOrigin == null)
                problems.Add($"{source}: nothing to change; give rename, isReference, definesOrigin or move.");
            if (op.MoveMm is 0) problems.Add($"{source}.move: 0 mm moves nothing; give a signed distance along the plane's normal.");
            return op;
        }

        op.IsCreate = true;
        op.Name = ModelFamilyJson.Str(obj, "name");
        if (op.Name.Length == 0) problems.Add($"{source}.name is required: every new reference plane gets a unique name (an unnamed plane cannot be found again by name).");
        else CheckName(op.Name, $"{source}.name", problems);
        op.View = ModelFamilyJson.Get(obj, "view");
        var length = ModelFamilyJson.Number(obj, "length", source, problems);
        if (length != null)
        {
            op.LengthGiven = true;
            op.LengthMm = length.Value;
            if (!(length > 0)) problems.Add($"{source}.length: must be more than 0 mm.");
        }

        if (ModelFamilyJson.Has(obj, "offset"))
        {
            op.IsOffset = true;
            ModelFamilyJson.CheckKeys(obj, source, problems, PlaneOffsetKeys);
            if (ModelFamilyJson.Get(obj, "offset") is not JObject offset)
            {
                problems.Add($"{source}.offset: must be {{from: \"<plane name or id>\", distance: <mm along its normal>}}.");
                return op;
            }
            ModelFamilyJson.CheckKeys(offset, $"{source}.offset", problems, "from", "distance");
            op.OffsetFrom = ModelFamilyJson.Get(offset, "from");
            if (op.OffsetFrom == null) problems.Add($"{source}.offset.from is required: the plane (name or id) the new plane is parallel to.");
            var distance = ModelFamilyJson.Number(offset, "distance", $"{source}.offset", problems);
            if (distance == null) problems.Add($"{source}.offset.distance is required: millimetres along the from plane's normal (negative: the other side).");
            else if (distance == 0) problems.Add($"{source}.offset.distance: 0 mm would put the new plane on top of the from plane.");
            else op.OffsetDistanceMm = distance.Value;
            return op;
        }

        ModelFamilyJson.CheckKeys(obj, source, problems, PlaneCreateKeys);
        op.Origin = ModelFamilyJson.Point(obj, "origin", source, problems);
        op.Direction = ModelFamilyJson.Point(obj, "direction", source, problems);
        op.Normal = ModelFamilyJson.Point(obj, "normal", source, problems, required: false);
        op.CutVector = ModelFamilyJson.Point(obj, "cutVector", source, problems, required: false);
        if (op.Origin != null && op.Direction != null && op.LengthMm > 0)
            ModelFamilyReferenceGeometry.Ends(op.Origin, op.Direction, op.Normal, op.CutVector, op.LengthMm, source, problems);
        return op;
    }

    private static void CheckName(string name, string source, List<string> problems)
    {
        if (name.Trim().Length == 0) problems.Add($"{source}: must not be empty.");
        else if (name.IndexOfAny(new[] { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', '\\', ':' }) >= 0)
            problems.Add($"{source}: '{name}' contains a character Revit does not allow in names ({{}}[]|;<>?`~\\:).");
    }

    public static ModelFamilyLineOperation ParseLine(JObject obj, string source, List<string> problems)
    {
        var op = new ModelFamilyLineOperation { Source = source };
        op.IsReference = IsReference(obj, source, problems);
        if (ModelFamilyJson.Has(obj, "line"))
        {
            ModelFamilyJson.CheckKeys(obj, source, problems, LineEditKeys);
            op.Line = ModelFamilyJson.AsId(ModelFamilyJson.Get(obj, "line"));
            if (op.Line == null || op.Line <= 0) problems.Add($"{source}.line: give the element id of a reference line.");
            if (op.IsReference == null) problems.Add($"{source}: nothing to change; give isReference.");
            return op;
        }
        op.IsCreate = true;
        ModelFamilyJson.CheckKeys(obj, source, problems, LineCreateKeys);
        op.Start = ModelFamilyJson.Point(obj, "start", source, problems);
        op.End = ModelFamilyJson.Point(obj, "end", source, problems);
        op.WorkPlane = ModelFamilyJson.Get(obj, "workPlane");
        if (op.Start != null && op.End != null && ModelFamilyReferenceGeometry.Length(ModelFamilyReferenceGeometry.Subtract(op.End, op.Start)) < 1)
            problems.Add($"{source}: start and end are less than 1 mm apart.");
        return op;
    }

    public static ModelFamilyDimensionOperation ParseDimension(JObject obj, string source, List<string> problems)
    {
        var op = new ModelFamilyDimensionOperation { Source = source };
        if (ModelFamilyJson.Has(obj, "value"))
            problems.Add($"{source}.value: a dimension does not set values. Label it with a family parameter (label) and set the parameter's value per type with revit_edit_model_family_parameters.");
        var labelProperty = obj.Properties().FirstOrDefault(p => string.Equals(p.Name, "label", StringComparison.OrdinalIgnoreCase));
        if (labelProperty != null)
        {
            if (labelProperty.Value.Type == JTokenType.Null) op.Unlabel = true;
            else
            {
                op.Label = ModelFamilyJson.Str(obj, "label");
                if (op.Label.Length == 0) op.Unlabel = true;
            }
        }
        op.Lock = ModelFamilyJson.Bool(obj, "lock", source, problems);
        op.Equal = ModelFamilyJson.Bool(obj, "equal", source, problems);

        if (ModelFamilyJson.Has(obj, "dimension"))
        {
            ModelFamilyJson.CheckKeys(obj, source, problems, DimensionEditKeys.Append("value").ToArray());
            op.Dimension = ModelFamilyJson.AsId(ModelFamilyJson.Get(obj, "dimension"));
            if (op.Dimension == null || op.Dimension <= 0) problems.Add($"{source}.dimension: give the element id of a dimension.");
            if (labelProperty == null && op.Lock == null && op.Equal == null)
                problems.Add($"{source}: nothing to change; give label (a parameter name, or null to remove it), lock or equal.");
            if (op.Label.Length > 0 && op.Lock == true)
                problems.Add($"{source}: a labelled dimension cannot be locked (its parameter drives it); give label or lock=true, not both.");
            return op;
        }

        op.IsCreate = true;
        ModelFamilyJson.CheckKeys(obj, source, problems, DimensionCreateKeys.Append("value").ToArray());
        if (op.Unlabel) problems.Add($"{source}.label: a new dimension has no label to remove; leave label out.");
        var kind = ModelFamilyJson.Str(obj, "kind", "linear").ToLowerInvariant();
        if (kind is not ("linear" or "angular"))
        {
            problems.Add($"{source}.kind: '{kind}' is not supported; use linear (default) or angular. Radial and diameter dimensions are not offered (see the documentation).");
            kind = "linear";
        }
        op.Kind = kind;
        op.View = ModelFamilyJson.Get(obj, "view");
        op.LinePoint = ModelFamilyJson.Point(obj, "linePoint", source, problems, required: false);
        op.RadiusMm = ModelFamilyJson.Number(obj, "radius", source, problems);
        if (op.RadiusMm != null && !(op.RadiusMm > 0)) problems.Add($"{source}.radius: must be more than 0 mm.");
        if (op.RadiusMm != null && kind != "angular") problems.Add($"{source}.radius: only an angular dimension has a radius; use linePoint to place a linear one.");
        if (op.LinePoint != null && kind == "angular") problems.Add($"{source}.linePoint: an angular dimension is placed by radius, not linePoint.");

        if (ModelFamilyJson.Get(obj, "references") is not JArray references)
        {
            problems.Add($"{source}.references is required: a list of two or more references (plane names or ids, \"level\", {{element, face}}, …).");
            return op;
        }
        for (var i = 0; i < references.Count; i++)
        {
            var selector = ModelFamilyReferenceSelector.Parse(references[i], $"{source}.references[{i}]", problems);
            if (selector == null) continue;
            if (op.References.Any(r => r.Key == selector.Key))
                problems.Add($"{source}.references[{i}]: {selector.Describe()} is listed twice.");
            else op.References.Add(selector);
        }
        if (references.Count < 2) problems.Add($"{source}.references: give at least two references.");
        if (kind == "angular")
        {
            if (references.Count != 2) problems.Add($"{source}.references: an angular dimension takes exactly two references.");
            if (op.Equal == true) problems.Add($"{source}.equal: an angular dimension has one segment; equal needs a linear dimension with three or more references.");
        }
        else if (op.Equal == true && references.Count < 3)
            problems.Add($"{source}.equal: an equality constraint needs three or more references (two or more segments).");
        if (op.Label.Length > 0 && op.Lock == true)
            problems.Add($"{source}: a labelled dimension cannot be locked (its parameter drives it); give label or lock=true, not both.");
        return op;
    }

    public static ModelFamilyAlignmentOperation ParseAlignment(JObject obj, string source, List<string> problems)
    {
        ModelFamilyJson.CheckKeys(obj, source, problems, AlignmentKeys);
        var op = new ModelFamilyAlignmentOperation { Source = source, View = ModelFamilyJson.Get(obj, "view") };
        op.Reference = ModelFamilyReferenceSelector.Parse(ModelFamilyJson.Get(obj, "reference"), $"{source}.reference", problems);
        op.Element = ModelFamilyReferenceSelector.Parse(ModelFamilyJson.Get(obj, "element"), $"{source}.element", problems);
        op.Lock = ModelFamilyJson.Bool(obj, "lock", source, problems) ?? true;
        if (op.Reference != null && op.Element != null && op.Reference.Key == op.Element.Key)
            problems.Add($"{source}: reference and element are both {op.Reference.Describe()}; an alignment needs two different references.");
        return op;
    }

    /// <summary>Checks the operations against each other.</summary>
    public static void CheckContradictions(ModelFamilyReferenceEditSpec spec, List<string> problems)
    {
        // New and renamed plane names must differ from each other.
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in spec.Planes)
        {
            var name = op.ResultName?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (names.TryGetValue(name!, out var first))
                problems.Add($"{op.Source}: the name '{name}' is also given by {first}; reference plane names must be unique.");
            else names[name!] = op.Source;
        }

        // The same plane edited twice.
        var edited = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var op in spec.Planes.Where(p => !p.IsCreate && p.Plane != null))
        {
            if (edited.TryGetValue(op.PlaneKey, out var first))
                problems.Add($"{op.Source}: the plane '{op.PlaneKey}' is also edited by {first}; put both changes in one item.");
            else edited[op.PlaneKey] = op.Source;
        }
        foreach (var op in spec.Lines.Where(l => !l.IsCreate && l.Line != null).GroupBy(l => l.Line).Where(g => g.Count() > 1))
            problems.Add($"{op.Skip(1).First().Source}: reference line {op.Key} is also edited by {op.First().Source}; put both changes in one item.");
        foreach (var op in spec.Dimensions.Where(d => !d.IsCreate && d.Dimension != null).GroupBy(d => d.Dimension).Where(g => g.Count() > 1))
            problems.Add($"{op.Skip(1).First().Source}: dimension {op.Key} is also edited by {op.First().Source}; put both changes in one item.");

        // Removed and then used.
        var removed = new HashSet<long>(spec.Remove);
        foreach (var op in spec.Planes)
        {
            if (!op.IsCreate && ModelFamilyJson.AsId(op.Plane) is { } id && removed.Contains(id))
                problems.Add($"{op.Source}: plane {id} is removed by remove; it cannot also be edited.");
            if (op.IsOffset && ModelFamilyJson.AsId(op.OffsetFrom) is { } from && removed.Contains(from))
                problems.Add($"{op.Source}.offset.from: plane {from} is removed by remove.");
        }
        foreach (var op in spec.Lines.Where(l => l.Line != null && removed.Contains(l.Line.Value)))
            problems.Add($"{op.Source}: reference line {op.Line} is removed by remove; it cannot also be edited.");
        foreach (var op in spec.Dimensions)
        {
            if (op.Dimension != null && removed.Contains(op.Dimension.Value))
                problems.Add($"{op.Source}: dimension {op.Dimension} is removed by remove; it cannot also be edited.");
            foreach (var reference in op.References.Where(r => r.ElementId != null && removed.Contains(r.ElementId.Value)))
                problems.Add($"{reference.Source}: element {reference.ElementId} is removed by remove.");
        }
        foreach (var op in spec.Alignments)
            foreach (var reference in new[] { op.Reference, op.Element }.Where(r => r?.ElementId != null && removed.Contains(r.ElementId!.Value)))
                problems.Add($"{reference!.Source}: element {reference.ElementId} is removed by remove.");

        // Two new planes defining the origin in the same direction.
        var origin = spec.Planes.Where(p => p.IsCreate && !p.IsOffset && p.DefinesOrigin == true && p.Origin != null && p.Direction != null)
            .Select(p => (Op: p, Ends: ModelFamilyReferenceGeometry.Ends(p.Origin!, p.Direction!, p.Normal, p.CutVector, p.LengthMm, p.Source, new List<string>())))
            .Where(x => x.Ends != null).ToList();
        for (var i = 0; i < origin.Count; i++)
            for (var j = 0; j < i; j++)
                if (ModelFamilyReferenceGeometry.Parallel(origin[i].Ends!.Normal, origin[j].Ends!.Normal))
                    problems.Add($"{origin[i].Op.Source}.definesOrigin: {origin[j].Op.Source} also defines the origin in this direction; only one plane per direction can.");
    }

    /// <summary>
    /// The plane a name would clash with (case-insensitive) among <paramref name="planes"/>, ignoring
    /// <paramref name="self"/> (the plane being renamed), or null when the name is free.
    /// </summary>
    public static (long Id, string Name)? NameClash(string name, IEnumerable<(long Id, string Name)> planes, long? self = null)
    {
        var wanted = name.Trim();
        foreach (var plane in planes)
            if (plane.Id != self && string.Equals(plane.Name?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                return plane;
        return null;
    }

    /// <summary>One line of what the edit does, for the summary.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        void Add(int count, string one, string many)
        {
            if (count > 0) parts.Add($"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? one : many)}");
        }
        Add(Remove.Count, "element removed", "elements removed");
        Add(Planes.Count(p => p.IsCreate), "reference plane created", "reference planes created");
        Add(Planes.Count(p => !p.IsCreate), "reference plane edited", "reference planes edited");
        Add(Lines.Count(l => l.IsCreate), "reference line created", "reference lines created");
        Add(Lines.Count(l => !l.IsCreate), "reference line edited", "reference lines edited");
        Add(Dimensions.Count(d => d.IsCreate), "dimension created", "dimensions created");
        Add(Dimensions.Count(d => !d.IsCreate), "dimension edited", "dimensions edited");
        Add(Alignments.Count, "alignment", "alignments");
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }
}
