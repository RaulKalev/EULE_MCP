using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>
/// What a caller asks of an element's visibility (#175): the Visible flag, a yes/no family parameter that drives it,
/// the detail levels and the view-specific display. Every field is optional; unset means "leave as it is".
/// </summary>
public sealed class ComponentVisibilityRequest
{
    public bool? Visible { get; set; }
    /// <summary>True when visibleParameter was written, including as null (which dissociates).</summary>
    public bool VisibleParameterGiven { get; set; }
    public string? VisibleParameter { get; set; }
    public bool? Coarse { get; set; }
    public bool? Medium { get; set; }
    public bool? Fine { get; set; }
    public bool? PlanRcp { get; set; }
    public bool? FrontBack { get; set; }
    public bool? LeftRight { get; set; }
    public bool? WhenCut { get; set; }

    public bool HasDetailLevels => Coarse != null || Medium != null || Fine != null;
    public bool HasViews => PlanRcp != null || FrontBack != null || LeftRight != null || WhenCut != null;
    public bool HasFlags => HasDetailLevels || HasViews;
    public bool IsEmpty => Visible == null && !VisibleParameterGiven && !HasFlags;

    public static readonly string[] Keys = { "visible", "visibleParameter", "detailLevels", "views" };

    /// <summary>
    /// Reads the keys of a visibility request from <paramref name="obj"/> (the entry itself for the visibility list, the
    /// nested <c>visibility</c> object for an instance or a line group). Returns null when there is none.
    /// </summary>
    public static ComponentVisibilityRequest? Parse(JObject obj, string source, List<string> problems)
    {
        var request = new ComponentVisibilityRequest();
        request.Visible = ModelFamilyJson.Bool(obj, "visible", source, problems);
        var property = obj.Properties().FirstOrDefault(p => string.Equals(p.Name, "visibleParameter", StringComparison.OrdinalIgnoreCase));
        if (property != null)
        {
            request.VisibleParameterGiven = true;
            if (property.Value.Type == JTokenType.Null) request.VisibleParameter = null;
            else
            {
                var name = ModelFamilyJson.Str(obj, "visibleParameter");
                if (name.Length == 0) problems.Add($"{source}.visibleParameter: give the name of a yes/no family parameter, or null to dissociate.");
                else request.VisibleParameter = name;
            }
        }
        if (ModelFamilyJson.Get(obj, "detailLevels") is { } levels)
        {
            if (levels is not JObject levelObject) problems.Add($"{source}.detailLevels: must be {{coarse?, medium?, fine?}} with true or false.");
            else
            {
                ModelFamilyJson.CheckKeys(levelObject, $"{source}.detailLevels", problems, "coarse", "medium", "fine");
                request.Coarse = ModelFamilyJson.Bool(levelObject, "coarse", $"{source}.detailLevels", problems);
                request.Medium = ModelFamilyJson.Bool(levelObject, "medium", $"{source}.detailLevels", problems);
                request.Fine = ModelFamilyJson.Bool(levelObject, "fine", $"{source}.detailLevels", problems);
            }
        }
        if (ModelFamilyJson.Get(obj, "views") is { } views)
        {
            if (views is not JObject viewObject) problems.Add($"{source}.views: must be {{planRcp?, frontBack?, leftRight?, whenCut?}} with true or false.");
            else
            {
                ModelFamilyJson.CheckKeys(viewObject, $"{source}.views", problems, "planRcp", "frontBack", "leftRight", "whenCut");
                request.PlanRcp = ModelFamilyJson.Bool(viewObject, "planRcp", $"{source}.views", problems);
                request.FrontBack = ModelFamilyJson.Bool(viewObject, "frontBack", $"{source}.views", problems);
                request.LeftRight = ModelFamilyJson.Bool(viewObject, "leftRight", $"{source}.views", problems);
                request.WhenCut = ModelFamilyJson.Bool(viewObject, "whenCut", $"{source}.views", problems);
            }
        }
        return request.IsEmpty ? null : request;
    }

    /// <summary>
    /// The visibility an instance, curve group or line group carries inline: its <c>visibility</c> object, with the
    /// owner's own <c>visibleParameter</c> as a shortcut for the same key.
    /// </summary>
    public static ComponentVisibilityRequest? ParseInline(JObject owner, string source, List<string> problems)
    {
        ComponentVisibilityRequest? request = null;
        if (ModelFamilyJson.Get(owner, "visibility") is { } token)
        {
            if (token is not JObject nested) problems.Add($"{source}.visibility: must be an object: {{visible?, visibleParameter?, detailLevels?, views?}}.");
            else
            {
                ModelFamilyJson.CheckKeys(nested, $"{source}.visibility", problems, Keys);
                request = Parse(nested, $"{source}.visibility", problems);
            }
        }
        var shortcut = owner.Properties().FirstOrDefault(p => string.Equals(p.Name, "visibleParameter", StringComparison.OrdinalIgnoreCase));
        if (shortcut != null)
        {
            var only = new JObject { ["visibleParameter"] = shortcut.Value };
            var parsed = Parse(only, source, problems);
            if (parsed != null)
            {
                request ??= new ComponentVisibilityRequest();
                request.VisibleParameterGiven = true;
                request.VisibleParameter = parsed.VisibleParameter;
            }
        }
        return request;
    }
}

/// <summary>An element's detail-level and view display flags as Revit's <c>FamilyElementVisibility</c> holds them.</summary>
public sealed class ComponentVisibilityFlags
{
    /// <summary>False for model elements (forms, model lines), true for view-specific ones (symbolic lines).</summary>
    public bool ViewSpecific { get; set; }
    public bool Coarse { get; set; } = true;
    public bool Medium { get; set; } = true;
    public bool Fine { get; set; } = true;
    /// <summary>Model elements only: shown in plan and RCP views (<c>IsShownInTopBottom</c>).</summary>
    public bool PlanRcp { get; set; } = true;
    public bool FrontBack { get; set; } = true;
    public bool LeftRight { get; set; } = true;
    /// <summary>Model elements: shown when cut in plan (<c>IsShownInPlanRCPCut</c>).</summary>
    public bool PlanRcpCut { get; set; }
    /// <summary>View-specific elements: shown only when the instance is cut (<c>IsShownOnlyWhenCut</c>).</summary>
    public bool OnlyWhenCut { get; set; }

    public ComponentVisibilityFlags Copy() => (ComponentVisibilityFlags)MemberwiseClone();

    public bool SameAs(ComponentVisibilityFlags other) =>
        ViewSpecific == other.ViewSpecific && Coarse == other.Coarse && Medium == other.Medium && Fine == other.Fine &&
        PlanRcp == other.PlanRcp && FrontBack == other.FrontBack && LeftRight == other.LeftRight &&
        PlanRcpCut == other.PlanRcpCut && OnlyWhenCut == other.OnlyWhenCut;

    /// <summary>The flags as a compact object for results.</summary>
    public JObject ToJson()
    {
        var json = new JObject
        {
            ["kind"] = ViewSpecific ? "viewSpecific" : "model",
            ["detailLevels"] = new JObject { ["coarse"] = Coarse, ["medium"] = Medium, ["fine"] = Fine }
        };
        json["views"] = ViewSpecific
            ? new JObject { ["whenCut"] = OnlyWhenCut }
            : new JObject { ["planRcp"] = PlanRcp, ["frontBack"] = FrontBack, ["leftRight"] = LeftRight, ["whenCut"] = PlanRcpCut };
        return json;
    }

    /// <summary>
    /// Applies a request to the current flags. A flag that has no meaning for this kind of element is skipped with a
    /// warning; turning every detail level off is an error (Revit refuses it).
    /// </summary>
    public static ComponentVisibilityFlags? Merge(ComponentVisibilityFlags current, ComponentVisibilityRequest request,
        string source, List<string> warnings, out string? error)
    {
        error = null;
        var flags = current.Copy();
        if (request.Coarse is { } coarse) flags.Coarse = coarse;
        if (request.Medium is { } medium) flags.Medium = medium;
        if (request.Fine is { } fine) flags.Fine = fine;
        if (!flags.Coarse && !flags.Medium && !flags.Fine)
        {
            error = $"{source}: at least one of coarse, medium and fine must stay on; Revit does not allow an element to be hidden at every detail level (use visible=false to hide it).";
            return null;
        }
        if (flags.ViewSpecific)
        {
            foreach (var (name, value) in new[] { ("planRcp", request.PlanRcp), ("frontBack", request.FrontBack), ("leftRight", request.LeftRight) })
                if (value != null)
                    warnings.Add($"{source}: views.{name} was skipped; a view-specific element (a symbolic line) is shown in the view it was drawn in. Only views.whenCut applies.");
            if (request.WhenCut is { } onlyWhenCut) flags.OnlyWhenCut = onlyWhenCut;
        }
        else
        {
            if (request.PlanRcp is { } planRcp) flags.PlanRcp = planRcp;
            if (request.FrontBack is { } frontBack) flags.FrontBack = frontBack;
            if (request.LeftRight is { } leftRight) flags.LeftRight = leftRight;
            if (request.WhenCut is { } cut) flags.PlanRcpCut = cut;
        }
        return flags;
    }
}
