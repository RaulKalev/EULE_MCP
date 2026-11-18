using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// Finds things in a family document by the identifiers the model-family tools hand out and accept (#168): an element
/// id, or a name for what has one (reference planes, parameters, views, levels, subcategories). Shared by every
/// model-family tool so the same reference means the same element everywhere.
/// </summary>
internal static class ModelFamilyLookup
{
    public static XYZ Xyz(double[] mm) =>
        new(ModelFamilyJson.ToFeet(mm[0]), ModelFamilyJson.ToFeet(mm[1]), ModelFamilyJson.ToFeet(mm[2]));

    public static JArray Mm(XYZ point) => ModelFamilyJson.PointMm(point.X, point.Y, point.Z);

    /// <summary>A direction as a unit-less array rounded for output.</summary>
    public static JArray Direction(XYZ vector) =>
        new(Math.Round(vector.X, 6), Math.Round(vector.Y, 6), Math.Round(vector.Z, 6));

    public static Element? Element(Document doc, JToken? idToken)
    {
        var id = ModelFamilyJson.AsId(idToken);
        return id == null ? null : doc.GetElement(new ElementId(id.Value));
    }

    /// <summary>An element of type <typeparamref name="T"/> by id; a problem when it is missing or something else.</summary>
    public static T? Element<T>(Document doc, JToken? idToken, string source, string what, List<string> problems) where T : Element
    {
        var id = ModelFamilyJson.AsId(idToken);
        if (id == null)
        {
            problems.Add($"{source}: give the element id of {what}.");
            return null;
        }
        var element = doc.GetElement(new ElementId(id.Value));
        if (element is T typed) return typed;
        problems.Add(element == null
            ? $"{source}: the family has no element {id}."
            : $"{source}: element {id} is a {element.GetType().Name}, not {what}.");
        return null;
    }

    public static List<ReferencePlane> ReferencePlanes(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();

    /// <summary>A reference plane by element id or by name (case-insensitive). Unnamed planes are found by id only.</summary>
    public static ReferencePlane? ReferencePlane(Document doc, JToken? token, string source, List<string> problems)
    {
        if (token == null)
        {
            problems.Add($"{source}: give a reference plane (its name or element id).");
            return null;
        }
        var planes = ReferencePlanes(doc);
        var id = token.Type == JTokenType.Integer ? ModelFamilyJson.AsId(token) : null;
        if (id != null)
        {
            var byId = planes.FirstOrDefault(p => p.Id.Value == id.Value);
            if (byId == null) problems.Add($"{source}: the family has no reference plane with id {id}.");
            return byId;
        }
        var name = (token.Type == JTokenType.String ? (string?)token : token.ToString())?.Trim() ?? string.Empty;
        var matches = planes.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1)
        {
            problems.Add($"{source}: {matches.Count} reference planes are called '{name}'; use an element id ({string.Join(", ", matches.Select(p => p.Id.Value))}).");
            return null;
        }
        if (ModelFamilyJson.AsId(token) is { } numeric && planes.FirstOrDefault(p => p.Id.Value == numeric) is { } plane) return plane;
        var close = ModelFamilyJson.Suggest(name, planes.Select(p => p.Name));
        problems.Add($"{source}: the family has no reference plane '{name}'" +
                     (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : ". revit_get_model_family lists them."));
        return null;
    }

    /// <summary>A family parameter by name (case-insensitive); a problem with close names when it is missing.</summary>
    public static FamilyParameter? Parameter(Document doc, string name, string source, List<string> problems)
    {
        var found = Parameter(doc, name);
        if (found != null) return found;
        var close = ModelFamilyJson.Suggest(name, Parameters(doc).Select(p => p.Definition.Name));
        problems.Add($"{source}: the family has no parameter '{name}'" +
                     (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : "."));
        return null;
    }

    public static FamilyParameter? Parameter(Document doc, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        var all = Parameters(doc);
        return all.FirstOrDefault(p => string.Equals(p.Definition.Name, name, StringComparison.Ordinal))
               ?? all.FirstOrDefault(p => string.Equals(p.Definition.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static List<FamilyParameter> Parameters(Document doc) => doc.FamilyManager.Parameters.Cast<FamilyParameter>().ToList();

    /// <summary>The family's reference level: the level called "Ref. Level" where there is one, else the lowest.</summary>
    public static Level? ReferenceLevel(Document doc)
    {
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
        return levels.FirstOrDefault(l => string.Equals(l.Name, "Ref. Level", StringComparison.OrdinalIgnoreCase)) ?? levels.FirstOrDefault();
    }

    public static List<View> Views(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();

    /// <summary>
    /// A view by element id or name, or by kind: <c>plan</c> (the reference level's floor plan), <c>front</c>,
    /// <c>back</c>, <c>left</c>, <c>right</c> (elevations by name), <c>3d</c>.
    /// </summary>
    public static View? View(Document doc, JToken? token, string source, List<string> problems)
    {
        var views = Views(doc);
        if (token == null)
        {
            var plan = PlanView(doc);
            if (plan == null) problems.Add($"{source}: the family has no plan view; name a view.");
            return plan;
        }
        if (token.Type == JTokenType.Integer && ModelFamilyJson.AsId(token) is { } id)
        {
            var byId = views.FirstOrDefault(v => v.Id.Value == id);
            if (byId == null) problems.Add($"{source}: the family has no view with id {id}.");
            return byId;
        }
        var name = (token.Type == JTokenType.String ? (string?)token : token.ToString())?.Trim() ?? string.Empty;
        var byName = views.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName;
        View? byKind = name.ToLowerInvariant() switch
        {
            "plan" => PlanView(doc),
            "3d" => views.OfType<View3D>().FirstOrDefault(),
            // Translated templates name their elevations differently: find the one that faces that way.
            "front" => ElevationFacing(views, -XYZ.BasisY),
            "back" => ElevationFacing(views, XYZ.BasisY),
            "left" => ElevationFacing(views, -XYZ.BasisX),
            "right" => ElevationFacing(views, XYZ.BasisX),
            _ => null
        };
        if (byKind != null) return byKind;
        problems.Add($"{source}: the family has no view '{name}'. Views: {string.Join(", ", views.Where(v => v.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.Elevation or ViewType.ThreeD or ViewType.Section).Select(v => $"'{v.Name}'").Take(12))}.");
        return null;
    }

    /// <summary>The elevation whose view direction (towards the viewer) is <paramref name="towardsViewer"/>.</summary>
    private static View? ElevationFacing(List<View> views, XYZ towardsViewer)
    {
        foreach (var view in views.Where(v => v.ViewType == ViewType.Elevation))
        {
            try { if (view.ViewDirection.IsAlmostEqualTo(towardsViewer, 1e-6)) return view; }
            catch { /* a view without a direction */ }
        }
        return null;
    }

    /// <summary>The floor plan of the reference level, else any floor plan.</summary>
    public static ViewPlan? PlanView(Document doc)
    {
        var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan).ToList();
        var level = ReferenceLevel(doc);
        return plans.FirstOrDefault(v => level != null && v.GenLevel != null && v.GenLevel.Id == level.Id) ?? plans.FirstOrDefault();
    }

    /// <summary>
    /// The plane a <c>workPlane</c> argument names, without creating anything: a reference plane (name or id),
    /// <c>"level"</c> (the reference level, the default), or <c>{origin:[x,y,z], normal:[x,y,z]}</c> in millimetres.
    /// </summary>
    public static Plane? WorkPlane(Document doc, JToken? token, string source, List<string> problems, out ReferencePlane? referencePlane)
    {
        referencePlane = null;
        if (token == null || token.Type == JTokenType.String && string.Equals(((string?)token)?.Trim(), "level", StringComparison.OrdinalIgnoreCase))
        {
            var level = ReferenceLevel(doc);
            if (level == null)
            {
                problems.Add($"{source}: the family has no level; name a reference plane.");
                return null;
            }
            // By basis, not by normal: CreateByNormalAndOrigin leaves the in-plane axes to Revit.
            return Plane.CreateByOriginAndBasis(new XYZ(0, 0, level.Elevation), XYZ.BasisX, XYZ.BasisY);
        }
        if (token is JObject obj)
        {
            var origin = ModelFamilyJson.Point(obj, "origin", source, problems);
            var normal = ModelFamilyJson.Point(obj, "normal", source, problems);
            if (origin == null || normal == null) return null;
            var n = new XYZ(normal[0], normal[1], normal[2]);
            if (n.GetLength() < 1e-9)
            {
                problems.Add($"{source}.normal: must not be zero.");
                return null;
            }
            return Plane.CreateByNormalAndOrigin(n.Normalize(), Xyz(origin));
        }
        referencePlane = ReferencePlane(doc, token, source, problems);
        return referencePlane?.GetPlane();
    }

    /// <summary>
    /// A sketch plane for <see cref="WorkPlane"/>'s argument. Creates an element, so call it inside a transaction. A
    /// reference plane gives a sketch plane that follows it, which is what keeps a form attached when the plane moves.
    /// </summary>
    public static SketchPlane? SketchPlane(Document doc, JToken? token, string source, List<string> problems)
    {
        var plane = WorkPlane(doc, token, source, problems, out var referencePlane);
        if (plane == null) return null;
        if (referencePlane != null)
        {
            // No fallback to a free plane here: a form that silently does not follow its reference plane defeats
            // every constraint built on it, and nothing would say so.
            try { return Autodesk.Revit.DB.SketchPlane.Create(doc, referencePlane.Id); }
            catch (Exception ex)
            {
                problems.Add($"{source}: Revit could not make a work plane on reference plane '{referencePlane.Name}' ({referencePlane.Id.Value}): {ex.Message.TrimEnd('.')}.");
                return null;
            }
        }
        var onLevel = token == null || token.Type == JTokenType.String && string.Equals(((string?)token)?.Trim(), "level", StringComparison.OrdinalIgnoreCase);
        if (onLevel && ReferenceLevel(doc) is { } level)
        {
            // Hosted on the level, as a sketch drawn in the Ref. Level plan is; a free plane is the fallback.
            try { return Autodesk.Revit.DB.SketchPlane.Create(doc, level.Id); }
            catch { /* below */ }
        }
        return Autodesk.Revit.DB.SketchPlane.Create(doc, plane);
    }

    /// <summary>A subcategory of the family's category by name; null when it has none of that name.</summary>
    public static Category? Subcategory(Document doc, string name)
    {
        var parent = doc.OwnerFamily?.FamilyCategory;
        if (parent == null || name.Trim().Length == 0) return null;
        foreach (Category sub in parent.SubCategories)
            if (string.Equals(sub.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) return sub;
        return null;
    }

    /// <summary>A short description of an element for messages and result rows.</summary>
    public static string Describe(Element element)
    {
        var name = string.Empty;
        try { name = element.Name ?? string.Empty; }
        catch { /* some elements have no name */ }
        return $"{element.GetType().Name} {element.Id.Value}{(name.Length > 0 ? $" '{name}'" : string.Empty)}";
    }
}

/// <summary>
/// Element parameter ↔ family parameter associations (#172), shared by the tools that drive an element property from a
/// family parameter: dimensions and visibility (#170, #175), materials (#173), connector properties (#174), nested
/// family parameters (#175).
/// </summary>
internal static class ModelFamilyAssociations
{
    /// <summary>
    /// An element's parameter by name, or by BuiltInParameter name (for example <c>MATERIAL_ID_PARAM</c>,
    /// <c>IS_VISIBLE_PARAM</c>, <c>RBS_ELEC_VOLTAGE</c>), which does not depend on Revit's language.
    /// </summary>
    public static Parameter? ElementParameter(Element element, string nameOrBuiltIn)
    {
        nameOrBuiltIn = nameOrBuiltIn.Trim();
        if (nameOrBuiltIn.Length == 0) return null;
        if (Enum.TryParse<BuiltInParameter>(nameOrBuiltIn, ignoreCase: true, out var builtIn) && builtIn != BuiltInParameter.INVALID)
        {
            try { if (element.get_Parameter(builtIn) is { } p) return p; }
            catch { /* not on this element */ }
        }
        foreach (Parameter parameter in element.Parameters)
        {
            try { if (string.Equals(parameter.Definition?.Name, nameOrBuiltIn, StringComparison.OrdinalIgnoreCase)) return parameter; }
            catch { /* a parameter without a definition */ }
        }
        return null;
    }

    public static bool CanAssociate(Document familyDoc, Parameter elementParameter)
    {
        try { return familyDoc.FamilyManager.CanElementParameterBeAssociated(elementParameter); }
        catch { return false; }
    }

    /// <summary>The family parameter an element parameter is associated with, or null.</summary>
    public static FamilyParameter? Associated(Document familyDoc, Parameter elementParameter)
    {
        try { return familyDoc.FamilyManager.GetAssociatedFamilyParameter(elementParameter); }
        catch { return null; }
    }

    /// <summary>
    /// Associates <paramref name="elementParameter"/> with <paramref name="familyParameter"/>, or dissociates it when
    /// that is null. Call inside a transaction. Returns an error instead of throwing when Revit refuses (wrong data
    /// type, a property that cannot be driven), and verifies the association by reading it back.
    /// </summary>
    public static string? Associate(Document familyDoc, Parameter elementParameter, FamilyParameter? familyParameter)
    {
        var name = SafeName(elementParameter);
        if (!CanAssociate(familyDoc, elementParameter))
            return $"'{name}' cannot be associated with a family parameter on this element.";
        var typeNote = string.Empty;
        if (familyParameter != null)
        {
            ForgeTypeId? elementType = null, familyType = null;
            try { elementType = elementParameter.Definition.GetDataType(); familyType = familyParameter.Definition.GetDataType(); }
            catch { /* compared by Revit below */ }
            // Version-free, as everywhere else: a parameter from an older shared parameter file carries an older spec version.
            if (elementType != null && familyType != null && elementType.TypeId.Length > 0 && familyType.TypeId.Length > 0 &&
                !RevitMCP.Addin.Parameters.ParameterSpecMap.SameType(elementType.TypeId, familyType.TypeId))
            {
                typeNote = $" '{name}' is {LabelOf(elementType)}, the family parameter '{familyParameter.Definition.Name}' is {LabelOf(familyType)}.";
                // Different kinds of value (a number against a material) can never be associated. Two numeric specs are
                // Revit's call: it accepts some pairs (a connector's size against a length), and refuses the rest below.
                StorageType elementStorage, familyStorage;
                try { elementStorage = elementParameter.StorageType; familyStorage = familyParameter.StorageType; }
                catch { elementStorage = familyStorage = StorageType.None; }
                if (elementStorage != familyStorage)
                    return typeNote.TrimStart();
            }
        }
        try
        {
            familyDoc.FamilyManager.AssociateElementParameterToFamilyParameter(elementParameter, familyParameter);
        }
        catch (Exception ex)
        {
            return $"Revit refused to {(familyParameter == null ? "dissociate" : "associate")} '{name}': {ex.Message.TrimEnd('.')}.{typeNote}";
        }
        var now = Associated(familyDoc, elementParameter);
        if (familyParameter == null) return now == null ? null : $"'{name}' is still associated with '{now.Definition.Name}'.";
        return now != null && now.Id == familyParameter.Id ? null : $"'{name}' did not take the association with '{familyParameter.Definition.Name}'.";
    }

    /// <summary>Every parameter of an element that can be driven by a family parameter, with its current association.</summary>
    public static JArray Associable(Document familyDoc, Element element)
    {
        var rows = new JArray();
        foreach (Parameter parameter in element.Parameters)
        {
            try
            {
                if (!CanAssociate(familyDoc, parameter)) continue;
                var builtIn = (parameter.Definition as InternalDefinition)?.BuiltInParameter ?? BuiltInParameter.INVALID;
                rows.Add(new JObject
                {
                    ["name"] = SafeName(parameter),
                    ["builtIn"] = builtIn == BuiltInParameter.INVALID ? null : builtIn.ToString(),
                    ["dataType"] = LabelOf(parameter.Definition.GetDataType()),
                    ["associatedWith"] = Associated(familyDoc, parameter)?.Definition.Name
                });
            }
            catch
            {
                // A parameter Revit cannot describe is left out.
            }
        }
        return rows;
    }

    private static string SafeName(Parameter parameter)
    {
        try { return parameter.Definition?.Name ?? "(unnamed)"; }
        catch { return "(unnamed)"; }
    }

    private static string LabelOf(ForgeTypeId? type)
    {
        if (type == null || string.IsNullOrEmpty(type.TypeId)) return "(no type)";
        try { return LabelUtils.GetLabelForSpec(type); }
        catch { return type.TypeId; }
    }
}

/// <summary>
/// The sections <c>revit_get_model_family</c> returns (#169). Each model-family feature adds the sections it owns when
/// its tools are registered, so the inspection grows with the features without one file knowing all of them.
/// </summary>
internal static class ModelFamilyInspect
{
    public sealed class Section
    {
        public string Name { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        /// <summary>Reads the section; <c>limit</c> caps a list. Returns the list (or object) for the result.</summary>
        public Func<ModelFamilyContext, int, JToken> Read { get; init; } = null!;
    }

    private static readonly List<Section> SectionList = new();

    public static IReadOnlyList<Section> Sections => SectionList;

    /// <summary>Adds (or replaces) a section. Call from a feature's Register method.</summary>
    public static void AddSection(string name, string summary, Func<ModelFamilyContext, int, JToken> read)
    {
        SectionList.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        SectionList.Add(new Section { Name = name, Summary = summary, Read = read });
    }
}
