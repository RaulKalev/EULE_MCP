using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Documentation.Views;

/// <summary>
/// The Revit calls of revit_create_views per kind (#104), and the common post-steps: name, template, scale, detail
/// level, discipline, phase, scope box, view range, crop and sheet placement. Everything here MUST run inside an open
/// transaction; the caller wraps each view in its own SubTransaction so one failure keeps the rest.
/// </summary>
public static class ViewCreator
{
    /// <summary>Creates the view and runs the post-steps. Throws when Revit refuses; post-step problems become warnings.</summary>
    public static View? Create(Document doc, ResolvedView view)
    {
        var plan = view.Plan;
        var spec = plan.Spec;
        View? created = plan.Kind switch
        {
            ViewKinds.FloorPlan or ViewKinds.CeilingPlan or ViewKinds.StructuralPlan =>
                ViewPlan.Create(doc, view.Type!.Id, view.Level!.Id),
            ViewKinds.AreaPlan => ViewPlan.CreateAreaPlan(doc, view.AreaScheme!.Id, view.Level!.Id),
            ViewKinds.Section => CreateSection(doc, view),
            ViewKinds.Callout => CreateCallout(doc, view),
            ViewKinds.ThreeD => spec.Isometric ? View3D.CreateIsometric(doc, view.Type!.Id) : View3D.CreatePerspective(doc, view.Type!.Id),
            ViewKinds.Drafting => ViewDrafting.Create(doc, view.Type!.Id),
            ViewKinds.Legend => DuplicateLegend(doc, view),
            _ => throw new InvalidOperationException($"Unknown kind '{plan.Kind}'.")
        };
        if (created == null) return null;   // a reference callout: no view of its own
        view.Created = created;

        if (plan.Name != null) created.Name = plan.Name;
        ApplyTemplate(created, view);
        if (created is View3D view3D) Apply3D(view3D, view);

        var controlled = ControlledParameters(view.Template);
        if (spec.Scale.HasValue) Step(view, "scale", () => SetScale(created, spec.Scale.Value, controlled, view));
        if (view.DetailLevel.HasValue)
            Step(view, "detailLevel", () =>
            {
                if (IsControlled(controlled, BuiltInParameter.VIEW_DETAIL_LEVEL)) view.Warnings.Add("detailLevel is controlled by the template; not set.");
                else created.DetailLevel = view.DetailLevel.Value;
            });
        if (view.Discipline.HasValue)
            Step(view, "discipline", () =>
            {
                if (IsControlled(controlled, BuiltInParameter.VIEW_DISCIPLINE)) view.Warnings.Add("discipline is controlled by the template; not set.");
                else if (created.HasViewDiscipline()) created.Discipline = view.Discipline.Value;
                else view.Warnings.Add($"A {created.ViewType} view has no discipline; not set.");
            });
        if (view.Phase != null)
            Step(view, "phase", () =>
            {
                var parameter = created.get_Parameter(BuiltInParameter.VIEW_PHASE);
                if (IsControlled(controlled, BuiltInParameter.VIEW_PHASE)) view.Warnings.Add("phase is controlled by the template; not set.");
                else if (parameter == null || parameter.IsReadOnly) view.Warnings.Add($"A {created.ViewType} view has no writable phase; not set.");
                else parameter.Set(view.Phase.Id);
            });
        if (view.ScopeBox != null)
            Step(view, "scopeBox", () =>
            {
                if (IsControlled(controlled, BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP)) view.Warnings.Add("The scope box is controlled by the template; not set.");
                else ViewExtentService.AssignScopeBox(created, view.ScopeBox);
            });

        if (created is ViewPlan viewPlan)
        {
            if (spec.ViewRange != null && !spec.ViewRange.IsEmpty)
            {
                if (IsControlled(controlled, BuiltInParameter.PLAN_VIEW_RANGE)) view.Warnings.Add("The view range is controlled by the template; not set.");
                else ApplyViewRange(viewPlan, spec.ViewRange);   // a refused view range fails the view: the user asked for it
            }
            if (view.ScopeBox == null && view.CropScopeBox != null)
                Step(view, "cropTo", () => ViewExtentService.AssignScopeBox(created, view.CropScopeBox));
            else if (view.ScopeBox == null && view.ExtentBox != null)
                Step(view, "cropTo", () => ViewExtentService.ApplyCrop(created, view.ExtentBox, cropVisible: false));
        }

        if (view.Sheet != null) PlaceOnSheet(doc, created, view);
        return created;
    }

    // ── Kinds ─────────────────────────────────────────────────────────────────

    private static View CreateSection(Document doc, ResolvedView view)
    {
        var box = view.Plan.Section!;
        var transform = Transform.Identity;
        transform.Origin = ViewExtentService.ToXyz(box.Origin);
        transform.BasisX = ViewExtentService.ToXyzUnit(box.BasisX);
        transform.BasisY = ViewExtentService.ToXyzUnit(box.BasisY);
        transform.BasisZ = ViewExtentService.ToXyzUnit(box.BasisZ);
        var sectionBox = new BoundingBoxXYZ
        {
            Transform = transform,
            Min = ViewExtentService.ToXyz(box.Min),
            Max = ViewExtentService.ToXyz(box.Max)
        };
        var section = ViewSection.CreateSection(doc, view.Type!.Id, sectionBox);

        // Self-check of the documented convention: the section's ViewDirection should be BasisZ and its cut plane
        // should run through the drawn line.
        try
        {
            var direction = ViewExtentService.ToUnit(section.ViewDirection);
            var origin = ViewExtentService.ToMm(section.Origin);
            var cutOffset = box.Origin.Minus(origin).Dot(direction);
            view.Actual = new
            {
                viewDirection = direction.ToUnit(),
                lookDirection = direction.Negated().ToUnit(),
                cutPlaneOffsetFromLineMm = Math.Round(cutOffset, 1)
            };
            if (direction.Dot(box.BasisZ) < 0.999)
                view.Warnings.Add($"Revit gave the section the view direction {direction.ToUnit()} instead of BasisZ {box.BasisZ.ToUnit()}: " +
                                  "check which way it looks and use flip if needed.");
            if (Math.Abs(cutOffset) > 1.0)
                view.Warnings.Add($"The section's cut plane lies {Math.Round(cutOffset, 1)} mm from the drawn line (expected 0): check its far clip and position.");
        }
        catch
        {
            // Diagnostics only.
        }
        return section;
    }

    private static View? CreateCallout(Document doc, ResolvedView view)
    {
        var (first, second) = view.CalloutCorners!.Value;
        var p1 = ViewExtentService.ToXyz(first);
        var p2 = ViewExtentService.ToXyz(second);
        if (view.ReferenceView != null)
        {
            ViewSection.CreateReferenceCallout(doc, view.Parent!.Id, view.ReferenceView.Id, p1, p2);
            return null;
        }
        return ViewSection.CreateCallout(doc, view.Parent!.Id, view.Type!.Id, p1, p2);
    }

    private static View DuplicateLegend(Document doc, ResolvedView view)
    {
        var id = view.SourceLegend!.Duplicate(ViewDuplicateOption.WithDetailing);
        return doc.GetElement(id) as View ?? throw new InvalidOperationException("Revit duplicated the legend but returned no view.");
    }

    private static void Apply3D(View3D view3D, ResolvedView view)
    {
        if (view.Orientation != null)
            Step(view, "orientation", () => view3D.SetOrientation(new ViewOrientation3D(
                ViewExtentService.ToXyz(view.Orientation.EyeMm),
                ViewExtentService.ToXyzUnit(view.Orientation.Up),
                ViewExtentService.ToXyzUnit(view.Orientation.Forward))));
        if (view.ExtentBox != null)
            ViewExtentService.ApplySectionBox(view3D, view.ExtentBox);   // the extent is what the user asked for: a refusal fails the view
    }

    // ── Post-steps ────────────────────────────────────────────────────────────

    private static void ApplyTemplate(View created, ResolvedView view)
    {
        if (view.Template == null)
        {
            // The type's default template, when it has one, is applied by Revit on creation.
            view.TemplateApplied = created.ViewTemplateId != ElementId.InvalidElementId;
            return;
        }
        if (!created.IsValidViewTemplate(view.Template.Id))
            throw new InvalidOperationException($"Template '{view.Template.Name}' ({view.Template.ViewType}) cannot be applied to a {created.ViewType} view.");
        created.ViewTemplateId = view.Template.Id;
        view.TemplateApplied = true;
    }

    private static void SetScale(View created, int scale, HashSet<long> controlled, ResolvedView view)
    {
        if (IsControlled(controlled, BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC) ||
            IsControlled(controlled, BuiltInParameter.VIEW_SCALE_PULLDOWN_IMPERIAL) ||
            IsControlled(controlled, BuiltInParameter.VIEW_SCALE))
        {
            view.Warnings.Add($"scale 1:{scale} ignored: the template controls the scale (1:{created.Scale}).");
            return;
        }
        if (created is View3D { IsPerspective: true })
        {
            view.Warnings.Add("A perspective view has no scale; not set.");
            return;
        }
        created.Scale = scale;
    }

    /// <summary>Sets the plan's view range offsets; each plane keeps the level it is measured from. Throws when Revit refuses.</summary>
    public static void ApplyViewRange(ViewPlan plan, ViewRangeSpec range)
    {
        var current = plan.GetViewRange();
        if (range.TopOffsetMm.HasValue) current.SetOffset(PlanViewPlane.TopClipPlane, range.TopOffsetMm.Value / ViewExtentService.MmPerFoot);
        if (range.CutOffsetMm.HasValue) current.SetOffset(PlanViewPlane.CutPlane, range.CutOffsetMm.Value / ViewExtentService.MmPerFoot);
        if (range.BottomOffsetMm.HasValue) current.SetOffset(PlanViewPlane.BottomClipPlane, range.BottomOffsetMm.Value / ViewExtentService.MmPerFoot);
        if (range.DepthOffsetMm.HasValue) current.SetOffset(PlanViewPlane.ViewDepthPlane, range.DepthOffsetMm.Value / ViewExtentService.MmPerFoot);
        plan.SetViewRange(current);
    }

    /// <summary>The view range of a plan in mm, with the level each plane is measured from.</summary>
    public static object? DescribeViewRange(ViewPlan plan)
    {
        try
        {
            var range = plan.GetViewRange();
            object Plane(PlanViewPlane p)
            {
                var levelId = range.GetLevelId(p);
                var level = levelId == PlanViewRange.Unlimited ? "unlimited"
                    : levelId == PlanViewRange.LevelAbove ? "level above"
                    : levelId == PlanViewRange.Current ? "associated level"
                    : plan.Document.GetElement(levelId)?.Name ?? levelId.Value.ToString();
                return new { level, offsetMm = Math.Round(range.GetOffset(p) * ViewExtentService.MmPerFoot, 1) };
            }
            return new
            {
                top = Plane(PlanViewPlane.TopClipPlane),
                cut = Plane(PlanViewPlane.CutPlane),
                bottom = Plane(PlanViewPlane.BottomClipPlane),
                depth = Plane(PlanViewPlane.ViewDepthPlane)
            };
        }
        catch
        {
            return null;
        }
    }

    private static void PlaceOnSheet(Document doc, View created, ResolvedView view)
    {
        var sheet = view.Sheet!;
        doc.Regenerate();
        if (!Viewport.CanAddViewToSheet(doc, sheet.Id, created.Id))
            throw new InvalidOperationException($"Revit cannot place '{created.Name}' on sheet {sheet.SheetNumber} " +
                                                "(a view other than a legend can be on one sheet only, and schedules need another tool).");
        var placement = view.Plan.Spec.PlaceOnSheet!;
        XYZ point;
        if (placement.XMm.HasValue && placement.YMm.HasValue)
            point = new XYZ(placement.XMm.Value / ViewExtentService.MmPerFoot, placement.YMm.Value / ViewExtentService.MmPerFoot, 0);
        else
        {
            var outline = sheet.Outline;
            point = new XYZ((outline.Min.U + outline.Max.U) / 2, (outline.Min.V + outline.Max.V) / 2, 0);
        }
        var viewport = Viewport.Create(doc, sheet.Id, created.Id, point);
        view.ViewportId = viewport?.Id.Value;
    }

    // ── Template control ──────────────────────────────────────────────────────

    /// <summary>Parameter ids the template controls (its template parameters minus the ones left uncontrolled).</summary>
    public static HashSet<long> ControlledParameters(View? template)
    {
        var result = new HashSet<long>();
        if (template == null) return result;
        try
        {
            var notControlled = new HashSet<long>(template.GetNonControlledTemplateParameterIds().Select(id => id.Value));
            foreach (var id in template.GetTemplateParameterIds())
                if (!notControlled.Contains(id.Value)) result.Add(id.Value);
        }
        catch
        {
            // Unknown: treat nothing as controlled; Revit refuses what it must.
        }
        return result;
    }

    private static bool IsControlled(HashSet<long> controlled, BuiltInParameter parameter) => controlled.Contains((long)parameter);

    private static void Step(ResolvedView view, string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            view.Warnings.Add($"{what} not applied: {ex.Message}");
        }
    }
}
