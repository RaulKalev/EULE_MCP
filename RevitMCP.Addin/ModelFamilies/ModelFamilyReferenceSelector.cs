using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>What a reference selector points at.</summary>
public enum ModelFamilyReferenceKind
{
    /// <summary>A reference plane, reference line or level by name or element id.</summary>
    Element,
    /// <summary>The family's reference level (<c>"level"</c>).</summary>
    Level,
    /// <summary>A planar face of a form, chosen by its outward normal.</summary>
    Face,
    /// <summary>A reference of a nested family instance, by reference type or by name.</summary>
    InstanceReference,
    /// <summary>A reference in Revit's stable string representation.</summary>
    Stable
}

/// <summary>
/// A reference selector (#170), the shared format every model-family tool uses to name a geometric reference for
/// dimensions, alignments and work planes. Pure: the Revit side (<c>ModelFamilyReferenceResolver</c>) turns it into a
/// <c>Reference</c>. Accepted forms:
/// <list type="bullet">
/// <item><c>"Center (Left/Right)"</c> or <c>1234</c>: a reference plane by name, or the element id of a reference plane, reference line or level; <c>"level"</c> is the reference level.</item>
/// <item><c>{"element": 1234, "face": "left|right|front|back|top|bottom"}</c> or <c>"face": [nx, ny, nz]</c>: the planar face of a form whose outward normal is closest to that direction.</item>
/// <item><c>{"element": 1234, "instanceReference": "left|…|strong|weak" | "&lt;reference name&gt;"}</c>: a reference of a nested family instance.</item>
/// <item><c>{"reference": "&lt;stable representation&gt;"}</c>: as returned by these tools.</item>
/// </list>
/// </summary>
public sealed class ModelFamilyReferenceSelector
{
    public const string ElementKey = "element";
    public const string FaceKey = "face";
    public const string InstanceReferenceKey = "instanceReference";
    public const string ReferenceKey = "reference";

    /// <summary>The faces by name and the outward normal each stands for (the Family Editor's Front view looks at −Y).</summary>
    public static readonly IReadOnlyDictionary<string, double[]> FaceDirections = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["left"] = new[] { -1.0, 0, 0 },
        ["right"] = new[] { 1.0, 0, 0 },
        ["front"] = new[] { 0, -1.0, 0 },
        ["back"] = new[] { 0, 1.0, 0 },
        ["top"] = new[] { 0, 0, 1.0 },
        ["bottom"] = new[] { 0, 0, -1.0 }
    };

    public ModelFamilyReferenceKind Kind { get; private set; }
    /// <summary>The name of a reference plane (or level) for <see cref="ModelFamilyReferenceKind.Element"/> given by name.</summary>
    public string Name { get; private set; } = string.Empty;
    /// <summary>The element id: of the plane/line/level, of the form (face) or of the nested instance.</summary>
    public long? ElementId { get; private set; }
    /// <summary>The face name (left, right, …) or empty when the face was given as a normal.</summary>
    public string FaceName { get; private set; } = string.Empty;
    /// <summary>The wanted outward normal of a face, unit length.</summary>
    public double[]? FaceNormal { get; private set; }
    /// <summary>A nested instance reference: the canonical <c>FamilyInstanceReferenceType</c> name, when it is one.</summary>
    public string? InstanceReferenceType { get; private set; }
    /// <summary>A nested instance reference by its name in the nested family (when it is not a type).</summary>
    public string InstanceReferenceName { get; private set; } = string.Empty;
    public string StableRepresentation { get; private set; } = string.Empty;
    public string Source { get; private set; } = string.Empty;

    /// <summary>Reads a selector; a problem naming <paramref name="source"/> and returns null when it cannot.</summary>
    public static ModelFamilyReferenceSelector? Parse(JToken? token, string source, List<string> problems)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            problems.Add($"{source}: give a reference: a reference plane name or id, \"level\", {{element, face}}, {{element, instanceReference}} or {{reference}}.");
            return null;
        }
        if (token.Type == JTokenType.Integer)
        {
            var id = token.Value<long>();
            if (id <= 0)
            {
                problems.Add($"{source}: {id} is not an element id.");
                return null;
            }
            return new ModelFamilyReferenceSelector { Kind = ModelFamilyReferenceKind.Element, ElementId = id, Source = source };
        }
        if (token.Type == JTokenType.String)
        {
            var text = ((string?)token ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                problems.Add($"{source}: the reference is empty; give a reference plane name or id.");
                return null;
            }
            if (string.Equals(text, "level", StringComparison.OrdinalIgnoreCase))
                return new ModelFamilyReferenceSelector { Kind = ModelFamilyReferenceKind.Level, Source = source };
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) && numeric > 0)
                return new ModelFamilyReferenceSelector { Kind = ModelFamilyReferenceKind.Element, ElementId = numeric, Name = text, Source = source };
            return new ModelFamilyReferenceSelector { Kind = ModelFamilyReferenceKind.Element, Name = text, Source = source };
        }
        if (token is not JObject obj)
        {
            problems.Add($"{source}: a reference is a name, an element id or an object, not {token.Type.ToString().ToLowerInvariant()}.");
            return null;
        }

        var before = problems.Count;
        ModelFamilyJson.CheckKeys(obj, source, problems, ElementKey, FaceKey, InstanceReferenceKey, ReferenceKey);
        var stable = ModelFamilyJson.Str(obj, ReferenceKey);
        var hasFace = ModelFamilyJson.Has(obj, FaceKey);
        var hasInstance = ModelFamilyJson.Has(obj, InstanceReferenceKey);
        if (stable.Length > 0)
        {
            if (hasFace || hasInstance || ModelFamilyJson.Has(obj, ElementKey))
                problems.Add($"{source}: give either reference (a stable representation) or element with face/instanceReference, not both.");
            return problems.Count > before ? null : new ModelFamilyReferenceSelector { Kind = ModelFamilyReferenceKind.Stable, StableRepresentation = stable, Source = source };
        }

        var elementId = ModelFamilyJson.AsId(ModelFamilyJson.Get(obj, ElementKey));
        if (elementId == null || elementId <= 0)
        {
            problems.Add($"{source}.{ElementKey}: give the element id of the form or nested instance.");
            return null;
        }
        if (hasFace == hasInstance)
        {
            problems.Add(hasFace
                ? $"{source}: give face or instanceReference, not both."
                : $"{source}: with element, give face (left|right|front|back|top|bottom or [nx, ny, nz]) or instanceReference.");
            return null;
        }

        if (hasFace)
        {
            var face = ModelFamilyJson.Get(obj, FaceKey)!;
            if (face.Type == JTokenType.String)
            {
                var name = ((string?)face ?? string.Empty).Trim();
                if (!FaceDirections.TryGetValue(name, out var direction))
                {
                    problems.Add($"{source}.{FaceKey}: '{name}' is not a face name. Use left, right, front, back, top, bottom or a normal [nx, ny, nz].");
                    return null;
                }
                return new ModelFamilyReferenceSelector
                {
                    Kind = ModelFamilyReferenceKind.Face, ElementId = elementId, FaceName = name.ToLowerInvariant(),
                    FaceNormal = (double[])direction.Clone(), Source = source
                };
            }
            var normal = ModelFamilyJson.Point(face, $"{source}.{FaceKey}", problems, allow2D: false);
            if (normal == null) return null;
            var unit = ModelFamilyReferenceGeometry.Normalize(normal);
            if (unit == null)
            {
                problems.Add($"{source}.{FaceKey}: the normal must not be zero.");
                return null;
            }
            return problems.Count > before ? null : new ModelFamilyReferenceSelector
            {
                Kind = ModelFamilyReferenceKind.Face, ElementId = elementId, FaceNormal = unit, Source = source
            };
        }

        var reference = ModelFamilyJson.Str(obj, InstanceReferenceKey);
        if (reference.Length == 0)
        {
            problems.Add($"{source}.{InstanceReferenceKey}: give a reference type (left, centerLeftRight, right, front, centerFrontBack, back, bottom, centerElevation, top, strong, weak) or the name of a reference in the nested family.");
            return null;
        }
        var type = ModelFamilyIsReference.InstanceReferenceType(reference);
        if (problems.Count > before) return null;
        return new ModelFamilyReferenceSelector
        {
            Kind = ModelFamilyReferenceKind.InstanceReference, ElementId = elementId,
            InstanceReferenceType = type, InstanceReferenceName = type == null ? reference : string.Empty, Source = source
        };
    }

    /// <summary>A key that is equal for two selectors naming the same thing as written (case-insensitive).</summary>
    public string Key => Kind switch
    {
        ModelFamilyReferenceKind.Level => "level",
        ModelFamilyReferenceKind.Element => ElementId != null ? $"id:{ElementId}" : $"name:{Name.ToLowerInvariant()}",
        ModelFamilyReferenceKind.Face => $"face:{ElementId}:{string.Join(",", (FaceNormal ?? Array.Empty<double>()).Select(v => Math.Round(v, 6).ToString(CultureInfo.InvariantCulture)))}",
        ModelFamilyReferenceKind.InstanceReference => $"instance:{ElementId}:{(InstanceReferenceType ?? InstanceReferenceName).ToLowerInvariant()}",
        _ => $"stable:{StableRepresentation}"
    };

    /// <summary>The selector in words, for messages.</summary>
    public string Describe() => Kind switch
    {
        ModelFamilyReferenceKind.Level => "the reference level",
        ModelFamilyReferenceKind.Element => Name.Length > 0 && ElementId == null ? $"'{Name}'" : $"element {ElementId}",
        ModelFamilyReferenceKind.Face => FaceName.Length > 0
            ? $"the {FaceName} face of element {ElementId}"
            : $"the face of element {ElementId} facing [{string.Join(", ", FaceNormal!.Select(v => Math.Round(v, 4).ToString(CultureInfo.InvariantCulture)))}]",
        ModelFamilyReferenceKind.InstanceReference => $"reference '{InstanceReferenceType ?? InstanceReferenceName}' of nested instance {ElementId}",
        _ => $"stable reference '{StableRepresentation}'"
    };
}

/// <summary>
/// The "Is Reference" values of reference planes and reference lines (#170): the names the tools accept, the integer
/// Revit stores in <c>ELEM_REFERENCE_NAME</c>, and the <c>FamilyInstanceReferenceType</c> a placed instance exposes it
/// as. The integers 0–8 follow <c>FamilyInstanceReferenceType</c> (Left = 0 … Top = 8); 12–14 for Not a Reference,
/// Strong and Weak are Revit's stored values and are verified by reading them back after every write.
/// </summary>
public static class ModelFamilyIsReference
{
    public sealed class Entry
    {
        public Entry(string name, int parameterValue, string instanceType, string label)
        {
            Name = name;
            ParameterValue = parameterValue;
            InstanceType = instanceType;
            Label = label;
        }

        /// <summary>The name on the wire (camelCase).</summary>
        public string Name { get; }
        /// <summary>The integer stored in ELEM_REFERENCE_NAME.</summary>
        public int ParameterValue { get; }
        /// <summary>The FamilyInstanceReferenceType member name.</summary>
        public string InstanceType { get; }
        /// <summary>The English label the Family Editor shows.</summary>
        public string Label { get; }
    }

    public static readonly IReadOnlyList<Entry> Entries = new[]
    {
        new Entry("notAReference", 12, "NotAReference", "Not a Reference"),
        new Entry("strong", 13, "StrongReference", "Strong Reference"),
        new Entry("weak", 14, "WeakReference", "Weak Reference"),
        new Entry("left", 0, "Left", "Left"),
        new Entry("centerLeftRight", 1, "CenterLeftRight", "Center (Left/Right)"),
        new Entry("right", 2, "Right", "Right"),
        new Entry("front", 3, "Front", "Front"),
        new Entry("centerFrontBack", 4, "CenterFrontBack", "Center (Front/Back)"),
        new Entry("back", 5, "Back", "Back"),
        new Entry("bottom", 6, "Bottom", "Bottom"),
        new Entry("centerElevation", 7, "CenterElevation", "Center (Elevation)"),
        new Entry("top", 8, "Top", "Top")
    };

    public static string Names => string.Join(", ", Entries.Select(e => e.Name));

    /// <summary>An entry by its wire name, its FamilyInstanceReferenceType name or its Family Editor label (case and spaces ignored).</summary>
    public static Entry? Find(string? text)
    {
        var key = Squash(text);
        if (key.Length == 0) return null;
        return Entries.FirstOrDefault(e => Squash(e.Name) == key || Squash(e.InstanceType) == key || Squash(e.Label) == key ||
                                           key == "strongreference" && e.Name == "strong" || key == "weakreference" && e.Name == "weak");
    }

    public static Entry? FromParameterValue(int value) => Entries.FirstOrDefault(e => e.ParameterValue == value);

    /// <summary>
    /// The FamilyInstanceReferenceType member a nested-instance selector names (left … top, strong, weak), or null when
    /// the text is a reference name instead. "notAReference" is never a usable instance reference.
    /// </summary>
    public static string? InstanceReferenceType(string text)
    {
        var entry = Find(text);
        return entry == null || entry.Name == "notAReference" ? null : entry.InstanceType;
    }

    private static string Squash(string? text) =>
        new string((text ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
