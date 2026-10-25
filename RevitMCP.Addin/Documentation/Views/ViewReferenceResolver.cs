using System.Globalization;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Tools;

namespace RevitMCP.Addin.Documentation.Views;

/// <summary>Everything a planned view needs from the model, resolved before any transaction opens.</summary>
public sealed class ResolvedView
{
    public ResolvedView(PlannedView plan) => Plan = plan;

    public PlannedView Plan { get; }

    public ViewFamilyType? Type { get; set; }
    public string? TypeHow { get; set; }
    public Level? Level { get; set; }
    public View? Template { get; set; }
    public Element? ScopeBox { get; set; }
    public Phase? Phase { get; set; }
    public ViewSheet? Sheet { get; set; }
    public View? Parent { get; set; }
    public View? ReferenceView { get; set; }
    public View? SourceLegend { get; set; }
    public Element? AreaScheme { get; set; }
    public ViewDetailLevel? DetailLevel { get; set; }
    public ViewDiscipline? Discipline { get; set; }

    /// <summary>Plans: the padded box of cropTo. Callouts and 3D views: the padded box of the region.</summary>
    public BoxMm? ExtentBox { get; set; }

    /// <summary>Plans: cropTo names a scope box, which is assigned rather than copied.</summary>
    public Element? CropScopeBox { get; set; }

    /// <summary>Callouts: the two corners handed to Revit, model mm.</summary>
    public (Vec3 First, Vec3 Second)? CalloutCorners { get; set; }

    public ViewOrientationMath.Orientation? Orientation { get; set; }

    public List<string> Warnings { get; } = new();

    // Results
    public View? Created { get; set; }

    /// <summary>Captured right after creation: a trial run's rolled-back view objects are invalid afterwards.</summary>
    public long? CreatedId { get; set; }
    public string? CreatedName { get; set; }
    public string? CreatedViewType { get; set; }
    public string? Status { get; set; }
    public long? ViewportId { get; set; }
    public bool TemplateApplied { get; set; }
    public string? Error { get; set; }
    public object? Actual { get; set; }

    public void Invalidate(string code, string reason) => Plan.Invalidate(code, reason);
}

/// <summary>
/// Finds the model elements a view spec names (#104): view family types (through <see cref="ViewFamilyTypeResolver"/>),
/// templates, scope boxes, sheets, parent and reference views, source legends, area schemes and phases. A reference is a
/// name (exact, case-insensitive) or an element id. Every failure marks the planned view invalid with a status code, so
/// the preview reports it before anything is created.
/// </summary>
public static class ViewReferenceResolver
{
    public static List<LevelInfo> Levels(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .Select(l => new LevelInfo(l.Id.Value, l.Name, l.ProjectElevation * ViewExtentService.MmPerFoot))
            .OrderBy(l => l.ElevationMm)
            .ToList();

    /// <summary>Every view name in the document (views and templates; sheets keep their own names).</summary>
    public static HashSet<string> ViewNames(Document doc) =>
        new(new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.ViewType != ViewType.DrawingSheet && v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser)
                .Select(v => v.Name),
            StringComparer.OrdinalIgnoreCase);

    public static ViewFamily? FamilyForKind(string kind) => kind switch
    {
        ViewKinds.FloorPlan => ViewFamily.FloorPlan,
        ViewKinds.CeilingPlan => ViewFamily.CeilingPlan,
        ViewKinds.StructuralPlan => ViewFamily.StructuralPlan,
        ViewKinds.AreaPlan => ViewFamily.AreaPlan,
        ViewKinds.Section => ViewFamily.Section,
        ViewKinds.ThreeD => ViewFamily.ThreeDimensional,
        ViewKinds.Drafting => ViewFamily.Drafting,
        ViewKinds.Legend => ViewFamily.Legend,
        _ => null
    };

    /// <summary>Resolves every reference of a ready planned view; marks it invalid on the first failure.</summary>
    public static ResolvedView Resolve(Document doc, PlannedView plan)
    {
        var resolved = new ResolvedView(plan);
        if (!plan.IsReady) return resolved;
        var spec = plan.Spec;

        try
        {
            if (plan.Level != null)
                resolved.Level = doc.GetElement(new ElementId(plan.Level.Id)) as Level;

            if (!ResolveKind(doc, resolved)) return resolved;
            if (plan.IsReferenceCallout) return resolved;   // creates no view: nothing else applies

            if (!string.IsNullOrWhiteSpace(spec.Template))
            {
                resolved.Template = FindTemplate(doc, spec.Template!, out var error);
                if (resolved.Template == null)
                {
                    resolved.Invalidate(ViewCreateCodes.TemplateNotFound, error!);
                    return resolved;
                }
            }

            if (!string.IsNullOrWhiteSpace(spec.ScopeBox))
            {
                resolved.ScopeBox = ViewExtentService.FindScopeBox(doc, spec.ScopeBox!, out var error);
                if (resolved.ScopeBox == null)
                {
                    resolved.Invalidate(ViewCreateCodes.ScopeBoxNotFound, error!);
                    return resolved;
                }
                if (plan.Kind is ViewKinds.Drafting or ViewKinds.Legend)
                {
                    resolved.Invalidate(ViewCreateCodes.InvalidSpec, $"A {ViewKinds.Label(plan.Kind, 1)} cannot take a scope box.");
                    return resolved;
                }
            }

            if (!string.IsNullOrWhiteSpace(spec.Phase))
            {
                if (!ElementRoomSupport.TryResolvePhase(doc, spec.Phase!, out var phase, out var error))
                {
                    resolved.Invalidate(ViewCreateCodes.PhaseNotFound, error!);
                    return resolved;
                }
                resolved.Phase = phase;
            }

            if (!string.IsNullOrWhiteSpace(spec.DetailLevel))
            {
                if (!Enum.TryParse<ViewDetailLevel>(spec.DetailLevel, true, out var detail) || detail == ViewDetailLevel.Undefined)
                {
                    resolved.Invalidate(ViewCreateCodes.InvalidSpec, $"detailLevel must be Coarse, Medium or Fine (got '{spec.DetailLevel}').");
                    return resolved;
                }
                resolved.DetailLevel = detail;
            }

            if (!string.IsNullOrWhiteSpace(spec.Discipline))
            {
                if (!Enum.TryParse<ViewDiscipline>(spec.Discipline, true, out var discipline))
                {
                    resolved.Invalidate(ViewCreateCodes.InvalidSpec,
                        $"discipline must be one of {string.Join(", ", Enum.GetNames(typeof(ViewDiscipline)))} (got '{spec.Discipline}').");
                    return resolved;
                }
                resolved.Discipline = discipline;
            }

            if (plan.Sheet != null)
            {
                resolved.Sheet = FindSheet(doc, plan.Sheet, out var error);
                if (resolved.Sheet == null)
                {
                    resolved.Invalidate(ViewCreateCodes.SheetNotFound, error!);
                    return resolved;
                }
            }
        }
        catch (Exception ex)
        {
            resolved.Invalidate(ViewCreateCodes.InvalidSpec, $"Could not resolve the spec's references: {ex.Message}");
        }
        return resolved;
    }

    private static bool ResolveKind(Document doc, ResolvedView resolved)
    {
        var plan = resolved.Plan;
        var spec = plan.Spec;
        switch (plan.Kind)
        {
            case ViewKinds.AreaPlan:
            {
                resolved.AreaScheme = FindAreaScheme(doc, spec.AreaScheme, out var code, out var error);
                if (resolved.AreaScheme == null)
                {
                    resolved.Invalidate(code!, error!);
                    return false;
                }
                return ResolvePlanExtent(doc, resolved);
            }
            case ViewKinds.FloorPlan or ViewKinds.CeilingPlan or ViewKinds.StructuralPlan:
                return ResolveType(doc, resolved, FamilyForKind(plan.Kind)!.Value) && ResolvePlanExtent(doc, resolved);
            case ViewKinds.Section or ViewKinds.Drafting:
                return ResolveType(doc, resolved, FamilyForKind(plan.Kind)!.Value);
            case ViewKinds.ThreeD:
            {
                if (!ResolveType(doc, resolved, ViewFamily.ThreeDimensional)) return false;
                if (spec.Region != null && !spec.Region.IsEmpty)
                {
                    resolved.ExtentBox = ViewExtentService.RegionBox(doc, spec.Region, resolved.Warnings, out _, out var error);
                    if (resolved.ExtentBox == null)
                    {
                        resolved.Invalidate(ViewCreateCodes.ExtentNotFound, error!);
                        return false;
                    }
                    if (!ViewExtentMath.HasVolume(resolved.ExtentBox))
                    {
                        resolved.Invalidate(ViewCreateCodes.ExtentNotFound, "The section box has no volume: give a box with size in x, y and z, or padding.");
                        return false;
                    }
                }
                var target = resolved.ExtentBox?.Center ?? Vec3.Zero;
                var distance = resolved.ExtentBox != null ? resolved.ExtentBox.Size.Length * 1.5 : 30000;
                if (spec.Orientation != null || resolved.ExtentBox != null)
                {
                    resolved.Orientation = ViewOrientationMath.Resolve(spec.Orientation, target, distance, out var error);
                    if (resolved.Orientation == null)
                    {
                        resolved.Invalidate(ViewCreateCodes.InvalidSpec, error!);
                        return false;
                    }
                }
                return true;
            }
            case ViewKinds.Callout:
                return ResolveCallout(doc, resolved);
            case ViewKinds.Legend:
            {
                resolved.SourceLegend = FindLegend(doc, spec.SourceLegend!, out var error);
                if (resolved.SourceLegend == null)
                {
                    resolved.Invalidate(ViewCreateCodes.LegendSourceRequired, error!);
                    return false;
                }
                if (!resolved.SourceLegend.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing))
                {
                    resolved.Invalidate(ViewCreateCodes.LegendSourceRequired, $"Legend '{resolved.SourceLegend.Name}' cannot be duplicated with detailing.");
                    return false;
                }
                return true;
            }
            default:
                resolved.Invalidate(ViewCreateCodes.InvalidSpec, $"Unknown kind '{plan.Kind}'.");
                return false;
        }
    }

    private static bool ResolveType(Document doc, ResolvedView resolved, ViewFamily family)
    {
        resolved.Type = ViewFamilyTypeResolver.Resolve(doc, family, resolved.Plan.Spec.ViewFamilyType, out var error, out var how);
        resolved.TypeHow = how;
        if (resolved.Type != null) return true;
        resolved.Invalidate(ViewCreateCodes.ViewFamilyTypeNotFound, error!);
        return false;
    }

    private static bool ResolvePlanExtent(Document doc, ResolvedView resolved)
    {
        var crop = resolved.Plan.Spec.CropTo;
        if (crop == null || !string.IsNullOrWhiteSpace(resolved.Plan.Spec.ScopeBox)) return true;
        resolved.ExtentBox = ViewExtentService.RegionBox(doc, crop, resolved.Warnings, out var scopeBox, out var error);
        if (resolved.ExtentBox == null)
        {
            resolved.Invalidate(ViewCreateCodes.ExtentNotFound, $"cropTo: {error}");
            return false;
        }
        resolved.CropScopeBox = scopeBox;
        if (!ViewExtentMath.HasPlanArea(resolved.ExtentBox))
        {
            resolved.Invalidate(ViewCreateCodes.ExtentNotFound, "cropTo gives a region with no area in plan.");
            return false;
        }
        return true;
    }

    private static bool ResolveCallout(Document doc, ResolvedView resolved)
    {
        var spec = resolved.Plan.Spec;
        resolved.Parent = FindView(doc, spec.ParentView!, v => v.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or
            ViewType.EngineeringPlan or ViewType.AreaPlan or ViewType.Section or ViewType.Elevation or ViewType.Detail or ViewType.DraftingView,
            "plan, section, elevation or detail view", out var parentError);
        if (resolved.Parent == null)
        {
            resolved.Invalidate(ViewCreateCodes.ParentViewNotFound, parentError!);
            return false;
        }
        var parentFamily = ViewFamilyTypeResolver.FamilyOf(resolved.Parent).ToString();

        // Corners: given, or the padded box of the region seen in the parent's right/up frame.
        var frame = new FrameMm(Vec3.Zero, ViewExtentService.ToUnit(resolved.Parent.RightDirection),
            ViewExtentService.ToUnit(resolved.Parent.UpDirection), ViewExtentService.ToUnit(resolved.Parent.ViewDirection));
        if (spec.From != null && spec.To != null)
        {
            resolved.CalloutCorners = (spec.From.Value, spec.To.Value);
        }
        else
        {
            resolved.ExtentBox = ViewExtentService.RegionBox(doc, spec.Region!, resolved.Warnings, out _, out var error);
            if (resolved.ExtentBox == null)
            {
                resolved.Invalidate(ViewCreateCodes.ExtentNotFound, error!);
                return false;
            }
            resolved.CalloutCorners = ViewExtentMath.CalloutCorners(resolved.ExtentBox, frame);
        }
        var (first, second) = resolved.CalloutCorners.Value;
        var a = frame.ToLocal(first);
        var b = frame.ToLocal(second);
        if (Math.Abs(a.X - b.X) < 1 || Math.Abs(a.Y - b.Y) < 1)
        {
            resolved.Invalidate(ViewCreateCodes.InvalidSpec,
                $"The callout corners do not span a rectangle in '{resolved.Parent.Name}' (they coincide along its right or up direction).");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(spec.ReferenceView))
        {
            resolved.ReferenceView = FindView(doc, spec.ReferenceView!, v => v.Id != resolved.Parent.Id, "view", out var error);
            if (resolved.ReferenceView == null)
            {
                resolved.Invalidate(ViewCreateCodes.ParentViewNotFound, $"referenceView: {error}");
                return false;
            }
            return true;
        }

        if (!CalloutRules.CanHostCallout(parentFamily))
        {
            resolved.Invalidate(ViewCreateCodes.ViewFamilyTypeIncompatible,
                $"'{resolved.Parent.Name}' is a {parentFamily} view: callouts can be drawn in floor, ceiling and structural plans, " +
                "sections, elevations and detail views (a drafting view takes only reference callouts).");
            return false;
        }

        var compatible = ViewFamilyTypeResolver.OfFamily(doc, null)
            .Where(t => CalloutRules.IsCompatible(parentFamily, t.ViewFamily.ToString()))
            .ToList();
        var compatibleText = compatible.Count == 0
            ? "none in this document"
            : string.Join(", ", compatible.Take(25).Select(t => $"'{t.Name}' ({t.ViewFamily}, {t.Id.Value})"));

        if (!string.IsNullOrWhiteSpace(spec.ViewFamilyType))
        {
            var wanted = spec.ViewFamilyType!.Trim();
            var type = ViewFamilyTypeResolver.OfFamily(doc, null).FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase))
                       ?? (long.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                           ? doc.GetElement(new ElementId(id)) as ViewFamilyType
                           : null);
            if (type == null)
            {
                resolved.Invalidate(ViewCreateCodes.ViewFamilyTypeNotFound, $"No view family type '{wanted}'. Compatible types for this parent: {compatibleText}.");
                return false;
            }
            if (!CalloutRules.IsCompatible(parentFamily, type.ViewFamily.ToString()))
            {
                resolved.Invalidate(ViewCreateCodes.ViewFamilyTypeIncompatible,
                    $"'{type.Name}' ({type.ViewFamily}) cannot make a callout in '{resolved.Parent.Name}' ({parentFamily}). " +
                    $"Compatible types: {compatibleText}.");
                return false;
            }
            resolved.Type = type;
            resolved.TypeHow = ViewFamilyTypeResolver.ByName;
            return true;
        }

        var family = (ViewFamily)Enum.Parse(typeof(ViewFamily), CalloutRules.DefaultTypeFamily(parentFamily));
        resolved.Type = ViewFamilyTypeResolver.Resolve(doc, family, null, out _, out var how)
                        ?? compatible.FirstOrDefault();
        resolved.TypeHow = how;
        if (resolved.Type == null)
        {
            resolved.Invalidate(ViewCreateCodes.ViewFamilyTypeIncompatible, $"No view family type can make a callout in '{resolved.Parent.Name}'. Compatible types: {compatibleText}.");
            return false;
        }
        return true;
    }

    // ── Lookups ───────────────────────────────────────────────────────────────

    public static View? FindTemplate(Document doc, string reference, out string? error)
    {
        var templates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).ToList();
        var found = ByNameOrId(templates, reference);
        error = found != null ? null :
            $"No view template '{reference}'. Templates: {Names(templates)}. revit_list_view_templates lists them with their view types.";
        return found;
    }

    public static ViewSheet? FindSheet(Document doc, string reference, out string? error)
    {
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsTemplate).ToList();
        var wanted = reference.Trim();
        var found = sheets.FirstOrDefault(s => string.Equals(s.SheetNumber, wanted, StringComparison.OrdinalIgnoreCase))
                    ?? sheets.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase))
                    ?? (long.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                        ? sheets.FirstOrDefault(s => s.Id.Value == id)
                        : null);
        error = found != null ? null :
            $"No sheet '{wanted}' (by number, name or id). {(sheets.Count == 0 ? "The document has no sheets." : $"Sheets: {string.Join(", ", sheets.Take(20).Select(s => s.SheetNumber))}{(sheets.Count > 20 ? ", …" : string.Empty)}.")}";
        return found;
    }

    public static View? FindLegend(Document doc, string reference, out string? error)
    {
        var legends = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && v.ViewType == ViewType.Legend).ToList();
        var found = ByNameOrId(legends, reference);
        error = found != null ? null : legends.Count == 0
            ? "Revit's API cannot create a legend view, and the model has none to duplicate. Create one legend by hand " +
              "(View > Legends > Legend), then name it in sourceLegend."
            : $"No legend '{reference}'. Legends: {Names(legends)}.";
        return found;
    }

    /// <summary>A non-template view by name (preferring one that satisfies <paramref name="prefer"/>) or id.</summary>
    public static View? FindView(Document doc, string reference, Func<View, bool> prefer, string what, out string? error)
    {
        error = null;
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
        var wanted = reference.Trim();
        var named = views.Where(v => string.Equals(v.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        var preferred = named.Where(prefer).ToList();
        if (preferred.Count >= 1)
        {
            if (preferred.Count > 1)
            {
                error = $"Several views are named '{wanted}' ({string.Join(", ", preferred.Select(v => $"{v.ViewType} {v.Id.Value}"))}): pass the id.";
                return null;
            }
            return preferred[0];
        }
        if (long.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
            doc.GetElement(new ElementId(id)) is View byId && !byId.IsTemplate)
        {
            if (prefer(byId)) return byId;
            error = $"View {id} ('{byId.Name}', {byId.ViewType}) is not a {what}.";
            return null;
        }
        error = named.Count > 0
            ? $"'{wanted}' is a {named[0].ViewType} view, not a {what}."
            : $"No {what} named '{wanted}' (or with that id).";
        return null;
    }

    /// <summary>The area scheme by name or id; the only scheme when none is named.</summary>
    public static Element? FindAreaScheme(Document doc, string? reference, out string? code, out string? error)
    {
        code = null;
        error = null;
        var schemes = new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).ToElements().ToList();
        var list = schemes.Count == 0 ? "none" : string.Join(", ", schemes.Select(s => $"'{s.Name}' ({s.Id.Value})"));
        if (string.IsNullOrWhiteSpace(reference))
        {
            if (schemes.Count == 1) return schemes[0];
            code = ViewCreateCodes.AreaSchemeRequired;
            error = schemes.Count == 0 ? "The document has no area scheme." : $"An area plan needs 'areaScheme'. Area schemes: {list}.";
            return null;
        }
        var found = ByNameOrId(schemes, reference!);
        if (found == null)
        {
            code = ViewCreateCodes.AreaSchemeRequired;
            error = $"No area scheme '{reference}'. Area schemes: {list}.";
        }
        return found;
    }

    private static T? ByNameOrId<T>(IReadOnlyList<T> elements, string reference) where T : Element
    {
        var wanted = reference.Trim();
        var found = elements.FirstOrDefault(e => string.Equals(e.Name, wanted, StringComparison.Ordinal))
                    ?? elements.FirstOrDefault(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (found != null) return found;
        return long.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? elements.FirstOrDefault(e => e.Id.Value == id)
            : null;
    }

    private static string Names<T>(IReadOnlyCollection<T> elements) where T : Element =>
        elements.Count == 0 ? "none" : string.Join(", ", elements.Take(20).Select(e => $"'{e.Name}'")) + (elements.Count > 20 ? ", …" : string.Empty);
}
