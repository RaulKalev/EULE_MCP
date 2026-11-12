using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Views;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Services;
using RevitMCP.Addin.Tools.ViewFilters;
using RevitMCP.Addin.ViewFilters;
using RevitMCP.Addin.ViewTemplates;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ViewTemplates;

/// <summary>The view template tools. App.OnStartup calls <see cref="Register"/> once.</summary>
public static class ViewTemplateToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new GetViewTemplateTool());
        handler.RegisterTool(new PreviewCreateViewTemplatesTool());
        handler.RegisterTool(new CreateViewTemplatesTool());
        handler.RegisterTool(new PreviewSetViewTemplatesTool());
        handler.RegisterTool(new SetViewTemplatesTool());
    }
}

/// <summary>revit_get_view_template: what one view template holds and controls, and the views that use it.</summary>
public sealed class GetViewTemplateTool : IRevitMcpTool
{
    public string Name => "revit_get_view_template";

    public string Description =>
        "For one view template (template: name or id): view type, scale, detail level, discipline, display style, phase and " +
        "phase filter, view range (plans), the parameters it controls and does not control, hidden categories, categories with " +
        "graphics overrides (capped by maxCategories, default 50), applied filters in order with visibility and graphics, and " +
        "the views that use it (capped by maxViews, default 100).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;

        var reference = ToolArguments.GetString(request.Arguments, "template").Trim();
        if (reference.Length == 0) return Task.FromResult(ViewTemplateModel.Fail(request, "Pass template: a view template name or id (revit_list_view_templates lists them).", "invalid_arguments"));
        var template = ViewTemplateModel.FindTemplate(doc, ViewTemplateModel.Templates(doc), reference, "template", out var problem);
        if (template == null) return Task.FromResult(ViewTemplateModel.Fail(request, problem!, "template_not_found"));

        var maxCategories = Math.Max(1, ToolArguments.GetInt(request.Arguments, "maxCategories", 50));
        var maxViews = Math.Max(1, ToolArguments.GetInt(request.Arguments, "maxViews", 100));

        var state = ViewTemplateModel.ReadState(template, null, 0, includeCategories: false);
        var controlled = state.TemplateParameters.Where(p => !state.NonControlled.Contains(p.Id)).Select(p => p.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var notControlled = state.TemplateParameters.Where(p => state.NonControlled.Contains(p.Id)).Select(p => p.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        // Categories: hidden ones (a hidden subcategory only when its parent is shown), and overrides.
        var hidden = new List<string>();
        var overridden = new List<object>();
        var overriddenCount = 0;
        foreach (Category category in doc.Settings.Categories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parentHidden = ViewTemplateModel.IsHidden(template, category);
            if (parentHidden) hidden.Add(category.Name);
            Override(category, category.Name);
            foreach (Category sub in category.SubCategories)
            {
                if (!parentHidden && ViewTemplateModel.IsHidden(template, sub)) hidden.Add($"{category.Name}: {sub.Name}");
                Override(sub, $"{category.Name}: {sub.Name}");
            }
        }

        void Override(Category category, string name)
        {
            try
            {
                if (!template.IsCategoryOverridable(category.Id)) return;
                var graphics = ViewFilterGraphics.Describe(doc, template.GetCategoryOverrides(category.Id));
                if (graphics.Count == 0) return;
                overriddenCount++;
                if (overridden.Count < maxCategories) overridden.Add(new { id = category.Id.Value, name, graphics });
            }
            catch (Exception)
            {
                // A category Revit does not take overrides for in this view type.
            }
        }

        // Filters, read the way revit_get_view_filters reads a template.
        var filters = new List<object>();
        if (ViewFilterModel.AllowsFilters(template))
        {
            var position = 0;
            foreach (var id in template.GetOrderedFilters())
            {
                var filter = doc.GetElement(id) as FilterElement;
                filters.Add(new
                {
                    order = ++position,
                    id = id.Value,
                    name = filter?.Name,
                    kind = filter == null ? null : ViewFilterModel.KindOf(filter),
                    enabled = template.GetIsFilterEnabled(id),
                    visible = template.GetFilterVisibility(id),
                    graphics = ViewFilterGraphics.Describe(doc, template.GetFilterOverrides(id))
                });
            }
        }

        var views = ViewFilterModel.ViewsByTemplate(doc).TryGetValue(template.Id.Value, out var users) ? users : new List<View>();
        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"View template '{template.Name}' ({template.ViewType}): controls {controlled.Count} of {state.TemplateParameters.Count} " +
                      $"parameters, {hidden.Count} hidden categor{(hidden.Count == 1 ? "y" : "ies")}, {filters.Count} filter(s), used by {views.Count} view(s).",
            Data = new
            {
                document = doc.Title,
                id = template.Id.Value,
                name = template.Name,
                viewType = template.ViewType.ToString(),
                scale = ViewTemplatePlanner.ScaleLabel(state.Scale),
                detailLevel = state.DetailLevel,
                discipline = state.Discipline,
                displayStyle = state.DisplayStyle,
                phase = ViewTemplateModel.ElementParameterName(template, BuiltInParameter.VIEW_PHASE),
                phaseFilter = ViewTemplateModel.ElementParameterName(template, BuiltInParameter.VIEW_PHASE_FILTER),
                viewRange = ViewTemplateModel.DescribeViewRange(template),
                controlled,
                notControlled,
                categories = new
                {
                    hiddenCount = hidden.Count,
                    hidden = hidden.Take(200).ToList(),
                    overriddenCount,
                    overridden,
                    overriddenTruncated = overriddenCount > overridden.Count ? true : (bool?)null
                },
                filters,
                viewCount = views.Count,
                views = views.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Take(maxViews).Select(ViewFilterModel.ViewRef).ToList()
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>revit_preview_create_view_templates: what revit_create_view_templates would create.</summary>
public sealed class PreviewCreateViewTemplatesTool : IRevitMcpTool
{
    public string Name => "revit_preview_create_view_templates";

    public string Description =>
        "Previews revit_create_view_templates without changing the model. Same arguments, plus dryRun (default true: the " +
        "creation and edits run in a transaction that is rolled back, so what Revit refuses is reported). Per template: " +
        "source, method (fromView | duplicate | copy) and the edits' from/to; ready and problems.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ViewTemplateExecutor.ExecuteCreate(uiapp, request, apply: false));
}

/// <summary>revit_create_view_templates: new view templates from views or as copies of templates, optionally edited.</summary>
public sealed class CreateViewTemplatesTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_create_view_templates";

    public string Description =>
        "Creates view templates. Requires approval. templates [{name, fromView? (a view name or id: the template takes its " +
        "settings) | duplicateFrom? (a template to copy), and optionally the edits of revit_set_view_templates: scale, " +
        "detailLevel, discipline, displayStyle, include, exclude, hideCategories, showCategories, parameters}]. One " +
        "transaction, one undo; any problem leaves the document unchanged. Run revit_preview_create_view_templates first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ViewTemplateExecutor.ExecuteCreate(uiapp, request, apply: true));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var errors = new List<string>();
        var specs = ViewTemplateRequestParser.ReadCreates(request.Arguments, "templates", errors);
        return ViewTemplateApprovalText.ForCreate(specs, errors.Count, ToolArguments.GetString(request.Arguments, "document").Trim());
    }
}

/// <summary>revit_preview_set_view_templates: what revit_set_view_templates would change, and how many views that reaches.</summary>
public sealed class PreviewSetViewTemplatesTool : IRevitMcpTool
{
    public string Name => "revit_preview_set_view_templates";

    public string Description =>
        "Previews revit_set_view_templates without changing the model. Same arguments, plus dryRun (default true: the edits " +
        "run in a transaction that is rolled back, so what Revit refuses is reported). Per template: action (edit | " +
        "unchanged), changes [{field, name, from, to}], viewCount (every view using it changes), warnings; ready and problems.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ViewTemplateExecutor.ExecuteSet(uiapp, request, apply: false));
}

/// <summary>revit_set_view_templates: edits view templates.</summary>
public sealed class SetViewTemplatesTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_set_view_templates";

    public string Description =>
        "Edits view templates. Requires approval. templates [{template (name or id), newName?, scale? (50 or \"1:50\"), " +
        "detailLevel?, discipline?, displayStyle?, include? / exclude? (template parameter names it should / should not " +
        "control), hideCategories? / showCategories? (category names, ids or OST_ names), parameters? {name: value}}]. " +
        "Editing a template changes every view that uses it. One transaction, one undo; any problem leaves the document " +
        "unchanged. Filters on a template: revit_apply_view_filters with the template as the view. Run " +
        "revit_preview_set_view_templates first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ViewTemplateExecutor.ExecuteSet(uiapp, request, apply: true));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var errors = new List<string>();
        var edits = ViewTemplateRequestParser.ReadEdits(request.Arguments, "templates", errors);
        var counts = new Dictionary<string, (string Name, int Views)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var target = RevitDocumentResolver.Resolve(uiapp, request, out _);
            if (target != null)
            {
                var doc = target.Document;
                var templates = ViewTemplateModel.Templates(doc);
                var byTemplate = ViewFilterModel.ViewsByTemplate(doc);
                foreach (var edit in edits)
                {
                    var template = ViewTemplateModel.FindTemplate(doc, templates, edit.Template, edit.Source, out _);
                    if (template != null)
                        counts[edit.Template] = (template.Name, byTemplate.TryGetValue(template.Id.Value, out var list) ? list.Count : 0);
                }
            }
        }
        catch (Exception)
        {
            // The summary falls back to the names as given.
        }
        return ViewTemplateApprovalText.ForSet(edits, errors.Count, ToolArguments.GetString(request.Arguments, "document").Trim(), counts);
    }
}

/// <summary>Finds templates and views, reads a template into a <see cref="ViewTemplateState"/>, and writes a plan.</summary>
internal static class ViewTemplateModel
{
    public static List<View> Templates(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).ToList();

    public static View? FindTemplate(Document doc, List<View> templates, string reference, string path, out string? problem)
    {
        problem = null;
        var refs = templates.Select(t => new NamedRef(t.Id.Value, t.Name)).ToList();
        var match = ViewFilterRefMatcher.Match(reference, refs);
        switch (match.Kind)
        {
            case RefMatchKind.Found:
                return templates.First(t => t.Id.Value == match.Found!.Id);
            case RefMatchKind.Ambiguous:
                problem = $"{path}: '{reference}' matches several view templates: {ViewFilterRefMatcher.List(match.Candidates)}. Use the id.";
                return null;
        }
        var view = ViewFilterModel.FindView(reference, ViewFilterModel.Views(doc), path, out _);
        problem = view != null && !view.IsTemplate
            ? $"{path}: '{reference}' is a view, not a view template{(view.ViewTemplateId != ElementId.InvalidElementId ? $" (it uses template '{doc.GetElement(view.ViewTemplateId)?.Name}')" : string.Empty)}."
            : $"{path}: no view template '{reference}' in '{doc.Title}'{ViewTemplatePlanner.Suggest(reference, templates.Select(t => t.Name))}";
        return null;
    }

    /// <summary>A view (not a template) a new template is made from.</summary>
    public static View? FindSourceView(Document doc, string reference, string path, out string? problem)
    {
        problem = null;
        var views = ViewFilterModel.Views(doc).Where(v => !v.IsTemplate).ToList();
        var view = ViewFilterModel.FindView(reference, views, path, out var issue);
        if (view != null) return view;
        if (issue?.Code == ViewFilterCodes.ViewAmbiguous) { problem = issue.Reason; return null; }
        var isTemplate = Templates(doc).Any(t => string.Equals(t.Name, reference, StringComparison.OrdinalIgnoreCase) || t.Id.Value.ToString(CultureInfo.InvariantCulture) == reference);
        problem = isTemplate
            ? $"{path}.fromView: '{reference}' is a view template; use duplicateFrom to copy a template."
            : $"{path}.fromView: no view '{reference}' in '{doc.Title}'{ViewTemplatePlanner.Suggest(reference, views.Select(v => v.Name))}";
        return null;
    }

    public static bool IsHidden(View view, Category category)
    {
        try { return view.CanCategoryBeHidden(category.Id) && view.GetCategoryHidden(category.Id); }
        catch (Exception) { return false; }
    }

    /// <summary>Reads what the request needs: parameters it names, categories when it shows or hides any.</summary>
    public static ViewTemplateState ReadState(View view, ViewTemplateEdit? edit, int viewCount, bool includeCategories)
    {
        var doc = view.Document;
        var names = new Dictionary<long, string>();
        var allNames = new List<string>();
        foreach (Parameter parameter in view.Parameters)
        {
            var name = parameter.Definition?.Name;
            if (string.IsNullOrEmpty(name)) continue;
            names[parameter.Id.Value] = name!;
            allNames.Add(name!);
        }

        var state = new ViewTemplateState
        {
            Id = view.Id.Value,
            Name = view.Name,
            ViewType = view.ViewType.ToString(),
            ViewCount = viewCount,
            ParameterNames = allNames.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
        };

        try { state.Scale = view.Scale > 0 ? view.Scale : null; } catch (Exception) { state.Scale = null; }
        state.CanSetScale = view.get_Parameter(BuiltInParameter.VIEW_SCALE) != null || view.get_Parameter(BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC) != null;

        if (Try(view.HasDetailLevel)) state.DetailLevel = view.DetailLevel.ToString();
        state.CanSetDetailLevel = Try(view.HasDetailLevel) && Try(view.CanModifyDetailLevel);
        if (Try(view.HasViewDiscipline)) state.Discipline = Try(() => view.Discipline.ToString());
        state.CanSetDiscipline = Try(view.HasViewDiscipline) && Try(view.CanModifyViewDiscipline);
        if (Try(view.HasDisplayStyle)) state.DisplayStyle = view.DisplayStyle.ToString();
        state.CanSetDisplayStyle = Try(view.HasDisplayStyle) && Try(view.CanModifyDisplayStyle);

        try
        {
            foreach (var id in view.GetTemplateParameterIds())
            {
                var builtIn = id.Value < 0 ? Enum.GetName(typeof(BuiltInParameter), (BuiltInParameter)id.Value) : null;
                var name = names.TryGetValue(id.Value, out var own) ? own : ViewFilterParameterCatalog.Identify(doc, id).Name;
                state.TemplateParameters.Add(new NamedRef(id.Value, name, builtIn));
            }
            state.TemplateParameters.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            state.NonControlled = new HashSet<long>(view.GetNonControlledTemplateParameterIds().Select(i => i.Value));
        }
        catch (Exception)
        {
            // Not every view type has template parameters.
        }

        if (includeCategories || (edit != null && (edit.HideCategories.Count > 0 || edit.ShowCategories.Count > 0)))
        {
            foreach (Category category in doc.Settings.Categories)
            {
                var kind = category.CategoryType.ToString();
                state.Categories.Add(CategoryOf(view, category, category.Name, kind));
                foreach (Category sub in category.SubCategories)
                    state.Categories.Add(CategoryOf(view, sub, $"{category.Name}: {sub.Name}", kind));
            }
        }

        if (edit != null)
        {
            foreach (var pair in edit.Parameters)
            {
                var parameter = FindParameter(view, pair.Key);
                if (parameter == null) continue;
                state.Parameters[pair.Key] = new ViewTemplateParameterValue
                {
                    Id = parameter.Id.Value,
                    Name = parameter.Definition.Name,
                    ReadOnly = parameter.IsReadOnly || parameter.StorageType == StorageType.None,
                    Value = ReadValue(doc, parameter)
                };
            }
        }
        return state;
    }

    private static ViewTemplateCategory CategoryOf(View view, Category category, string name, string kind)
    {
        var canHide = false;
        var hidden = false;
        try
        {
            canHide = view.CanCategoryBeHidden(category.Id);
            hidden = canHide && view.GetCategoryHidden(category.Id);
        }
        catch (Exception)
        {
            // Left as "cannot be hidden".
        }
        return new ViewTemplateCategory
        {
            Id = category.Id.Value,
            Name = name,
            BuiltInName = ViewFilterModel.BuiltInCategoryName(category.Id.Value),
            Kind = kind,
            CanHide = canHide,
            Hidden = hidden
        };
    }

    private static bool Try(Func<bool> check)
    {
        try { return check(); }
        catch (Exception) { return false; }
    }

    private static string? Try(Func<string> read)
    {
        try { return read(); }
        catch (Exception) { return null; }
    }

    /// <summary>A parameter by its name, its BuiltInParameter name, or its name in another case.</summary>
    public static Parameter? FindParameter(View view, string name)
    {
        var parameter = view.LookupParameter(name);
        if (parameter != null) return parameter;
        if (name.Length > 0 && char.IsLetter(name[0]) && Enum.TryParse<BuiltInParameter>(name, true, out var builtIn))
        {
            parameter = view.get_Parameter(builtIn);
            if (parameter != null) return parameter;
        }
        foreach (Parameter candidate in view.Parameters)
            if (string.Equals(candidate.Definition?.Name, name, StringComparison.OrdinalIgnoreCase)) return candidate;
        return null;
    }

    public static string ReadValue(Document doc, Parameter parameter)
    {
        if (parameter.StorageType == StorageType.ElementId)
        {
            var id = parameter.AsElementId();
            return id == null || id == ElementId.InvalidElementId ? string.Empty : doc.GetElement(id)?.Name ?? id.Value.ToString(CultureInfo.InvariantCulture);
        }
        var formatted = parameter.AsValueString();
        if (!string.IsNullOrWhiteSpace(formatted)) return formatted.Trim();
        return parameter.StorageType switch
        {
            StorageType.String => parameter.AsString()?.Trim() ?? string.Empty,
            StorageType.Integer => parameter.AsInteger().ToString(CultureInfo.InvariantCulture),
            StorageType.Double => parameter.AsDouble().ToString(CultureInfo.InvariantCulture),
            _ => string.Empty
        };
    }

    public static string? ElementParameterName(View view, BuiltInParameter builtIn)
    {
        var parameter = view.get_Parameter(builtIn);
        if (parameter == null || parameter.StorageType != StorageType.ElementId) return null;
        var value = ReadValue(view.Document, parameter);
        return value.Length > 0 ? value : null;
    }

    /// <summary>The view range of a plan template: per plane its level (or Associated level, Level above, …) and offset in mm.</summary>
    public static object? DescribeViewRange(View view)
    {
        if (view is not ViewPlan plan) return null;
        try
        {
            var range = plan.GetViewRange();
            object Plane(PlanViewPlane p)
            {
                var level = range.GetLevelId(p);
                string? levelName =
                    level == PlanViewRange.Current ? "Associated level"
                    : level == PlanViewRange.LevelAbove ? "Level above"
                    : level == PlanViewRange.LevelBelow ? "Level below"
                    : level == PlanViewRange.Unlimited ? "Unlimited"
                    : level == ElementId.InvalidElementId ? null
                    : view.Document.GetElement(level)?.Name ?? level.Value.ToString(CultureInfo.InvariantCulture);
                return new { level = levelName, offsetMm = Math.Round(range.GetOffset(p) * 304.8, 1) };
            }
            return new
            {
                top = Plane(PlanViewPlane.TopClipPlane),
                cut = Plane(PlanViewPlane.CutPlane),
                bottom = Plane(PlanViewPlane.BottomClipPlane),
                depth = Plane(PlanViewPlane.ViewDepthPlane)
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Writes a plan to a template. Throws when Revit refuses a change; the caller rolls back.</summary>
    public static void Apply(Document doc, View template, ViewTemplatePlan plan)
    {
        if (plan.NewName != null) template.Name = plan.NewName;
        if (plan.Scale != null) template.Scale = plan.Scale.Value;
        if (plan.DetailLevel != null) template.DetailLevel = (ViewDetailLevel)Enum.Parse(typeof(ViewDetailLevel), plan.DetailLevel, true);
        if (plan.Discipline != null) template.Discipline = (ViewDiscipline)Enum.Parse(typeof(ViewDiscipline), plan.Discipline, true);
        if (plan.DisplayStyle != null)
        {
            if (!Enum.TryParse<DisplayStyle>(plan.DisplayStyle, true, out var style))
                throw new InvalidOperationException($"This Revit version has no display style '{plan.DisplayStyle}'.");
            template.DisplayStyle = style;
        }
        foreach (var pair in plan.Parameters)
        {
            var parameter = FindParameter(template, pair.Key)
                            ?? throw new InvalidOperationException($"Parameter '{pair.Key}' was not found on template '{template.Name}'.");
            if (parameter.StorageType == StorageType.ElementId) parameter.Set(ResolveElementValue(doc, parameter, pair.Value));
            else ViewParameterService.SetValue(parameter, pair.Value);
        }
        foreach (var id in plan.Hide) template.SetCategoryHidden(new ElementId(id), true);
        foreach (var id in plan.Show) template.SetCategoryHidden(new ElementId(id), false);
        if (plan.NonControlled != null)
            template.SetNonControlledTemplateParameterIds(plan.NonControlled.Select(i => new ElementId(i)).ToList());
    }

    /// <summary>An element-id parameter's value: empty or none clears it; an id; or a phase, phase filter, scope box or level by name.</summary>
    private static ElementId ResolveElementValue(Document doc, Parameter parameter, string value)
    {
        var text = value.Trim();
        if (text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase) || text == "-1") return ElementId.InvalidElementId;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return new ElementId(id);

        var builtIn = (parameter.Definition as InternalDefinition)?.BuiltInParameter ?? BuiltInParameter.INVALID;
        IEnumerable<Element> candidates = builtIn switch
        {
            BuiltInParameter.VIEW_PHASE => new FilteredElementCollector(doc).OfClass(typeof(Phase)).ToElements(),
            BuiltInParameter.VIEW_PHASE_FILTER => new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).ToElements(),
            _ => new FilteredElementCollector(doc).OfClass(typeof(Phase)).ToElements()
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).ToElements())
                .Concat(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().ToElements())
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(Level)).ToElements())
        };
        var list = candidates.ToList();
        var matches = list.Where(e => string.Equals(e.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0].Id;
        if (matches.Count > 1)
            throw new InvalidOperationException($"'{text}' for '{parameter.Definition.Name}' matches {matches.Count} elements; use the id.");
        throw new InvalidOperationException($"No element named '{text}' for '{parameter.Definition.Name}'{ViewTemplatePlanner.Suggest(text, list.Select(e => e.Name))}");
    }

    /// <summary>A copy of a template: View.Duplicate when Revit allows it, else ElementTransformUtils.CopyElements in the same document.</summary>
    public static string DuplicateMethod(View source)
    {
        try { return source.CanViewBeDuplicated(ViewDuplicateOption.Duplicate) ? "duplicate" : "copy"; }
        catch (Exception) { return "copy"; }
    }

    public static View Duplicate(Document doc, View source)
    {
        ElementId id;
        if (DuplicateMethod(source) == "duplicate") id = source.Duplicate(ViewDuplicateOption.Duplicate);
        else
        {
            var copied = ElementTransformUtils.CopyElements(doc, new List<ElementId> { source.Id }, doc, Transform.Identity, new CopyPasteOptions());
            id = copied.FirstOrDefault() ?? ElementId.InvalidElementId;
        }
        if (doc.GetElement(id) is not View view || !view.IsTemplate)
            throw new InvalidOperationException($"Revit did not return a view template when copying '{source.Name}'.");
        return view;
    }

    public static McpToolResult Fail(McpToolRequest request, string message, string? status = null, List<string>? warnings = null) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Status = status,
        Message = message,
        Errors = [message],
        Warnings = warnings ?? new List<string>()
    };

    public static object Describe(IEnumerable<ViewTemplateChange> changes) =>
        changes.Select(c => new { field = c.Field, name = c.Name, from = c.From, to = c.To }).ToList();
}

internal static class ViewTemplateExecutor
{
    private sealed class PlannedEdit
    {
        public ViewTemplateEdit Edit { get; set; } = new();
        public View? Template { get; set; }
        public ViewTemplatePlan? Plan { get; set; }
        public List<View> Views { get; set; } = new();
    }

    public static McpToolResult ExecuteSet(UIApplication uiapp, McpToolRequest request, bool apply)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;

        var problems = new List<string>();
        var warnings = new List<string>();
        var edits = ViewTemplateRequestParser.ReadEdits(request.Arguments, "templates", problems);
        if (edits.Count == 0 && problems.Count == 0)
            problems.Add("Provide 'templates': an array of {template, newName?, scale?, detailLevel?, discipline?, displayStyle?, include?, exclude?, hideCategories?, showCategories?, parameters?}.");

        var templates = ViewTemplateModel.Templates(doc);
        var byTemplate = ViewFilterModel.ViewsByTemplate(doc);
        var claimed = new List<string>();
        var seenIds = new Dictionary<long, string>();
        var planned = new List<PlannedEdit>();
        foreach (var edit in edits)
        {
            var item = new PlannedEdit { Edit = edit };
            planned.Add(item);
            item.Template = ViewTemplateModel.FindTemplate(doc, templates, edit.Template, edit.Source, out var problem);
            if (item.Template == null) { problems.Add(problem!); continue; }
            if (seenIds.TryGetValue(item.Template.Id.Value, out var earlier))
            {
                problems.Add($"{edit.Source}: '{edit.Template}' is the same template as {earlier}; put all its changes in one item.");
                continue;
            }
            seenIds[item.Template.Id.Value] = edit.Source;
            item.Views = byTemplate.TryGetValue(item.Template.Id.Value, out var list) ? list : new List<View>();
            var state = ViewTemplateModel.ReadState(item.Template, edit, item.Views.Count, includeCategories: false);
            var others = templates.Where(t => t.Id != item.Template.Id).Select(t => t.Name).Concat(claimed).ToList();
            item.Plan = ViewTemplatePlanner.Plan(edit, state, others);
            if (item.Plan.NewName != null) claimed.Add(item.Plan.NewName);
            problems.AddRange(item.Plan.Problems);
            warnings.AddRange(item.Plan.Warnings);
        }

        var toChange = planned.Where(p => p.Plan is { Action: "edit" }).ToList();
        var dryRun = !apply && ToolArguments.GetBool(request.Arguments, "dryRun", true);
        var refused = new List<string>();
        if ((apply || dryRun) && problems.Count == 0 && toChange.Count > 0)
        {
            try
            {
                using var tx = new Transaction(doc, "Revit MCP - View templates");
                tx.Start();
                foreach (var item in toChange)
                {
                    try { ViewTemplateModel.Apply(doc, item.Template!, item.Plan!); }
                    catch (Exception ex) { refused.Add($"{item.Edit.Source} ('{item.Template!.Name}'): Revit refused the change ({ex.Message})."); break; }
                }
                if (apply && refused.Count == 0) TransactionCommitGuard.CommitOrThrow(tx);
                else tx.RollBack();
            }
            catch (Exception ex)
            {
                refused.Add($"Changing the view templates failed: {ex.Message}");
            }
        }

        var viewTotal = toChange.Sum(p => p.Views.Count);
        object Describe(PlannedEdit p) => new
        {
            source = p.Edit.Source,
            template = p.Template == null ? null : new { id = p.Template.Id.Value, name = p.Template.Name, viewType = p.Template.ViewType.ToString() },
            action = p.Plan?.Action ?? "invalid",
            viewCount = p.Views.Count,
            views = p.Views.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Take(20).Select(v => v.Name).ToList(),
            changes = p.Plan == null ? null : ViewTemplateModel.Describe(p.Plan.Changes)
        };

        sw.Stop();
        if (!apply)
        {
            problems.AddRange(refused);
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = problems.Count == 0
                    ? $"Preview: {toChange.Count} view template(s) to edit, {planned.Count - toChange.Count} unchanged in '{doc.Title}'. " +
                      $"Editing a template changes every view that uses it: {viewTotal} view(s) in all."
                    : $"Preview: {problems.Count} problem(s) to fix before changing view templates.",
                Data = new
                {
                    ready = problems.Count == 0,
                    problems,
                    document = doc.Title,
                    trialRun = dryRun && toChange.Count > 0,
                    affectedViews = viewTotal,
                    templates = planned.Select(Describe).ToList()
                },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (problems.Count > 0)
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "invalid_arguments",
                Message = $"Nothing was changed: {problems.Count} problem(s). {string.Join(" ", problems.Take(3))}",
                Errors = problems,
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        if (refused.Count > 0)
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "transaction_failed",
                Message = $"Nothing was changed: {refused[0]}",
                Errors = refused,
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };

        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{toChange.Count} view template(s) edited, {planned.Count - toChange.Count} unchanged in '{doc.Title}'; {viewTotal} view(s) use the edited templates.",
            Data = new
            {
                document = doc.Title,
                edited = toChange.Count,
                affectedViews = viewTotal,
                templates = planned.Select(Describe).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private sealed class PlannedCreate
    {
        public ViewTemplateCreateSpec Spec { get; set; } = new();
        public View? Source { get; set; }
        public string Method { get; set; } = string.Empty;
        public ViewTemplatePlan? Plan { get; set; }
        public long? CreatedId { get; set; }
    }

    public static McpToolResult ExecuteCreate(UIApplication uiapp, McpToolRequest request, bool apply)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;

        var problems = new List<string>();
        var warnings = new List<string>();
        var specs = ViewTemplateRequestParser.ReadCreates(request.Arguments, "templates", problems);
        if (specs.Count == 0 && problems.Count == 0)
            problems.Add("Provide 'templates': an array of {name, fromView? | duplicateFrom?, and optional edits}.");

        var templates = ViewTemplateModel.Templates(doc);
        var existingNames = templates.Select(t => t.Name).ToList();
        var newNames = specs.Select(s => s.Name).ToList();
        var planned = new List<PlannedCreate>();
        foreach (var spec in specs)
        {
            var item = new PlannedCreate { Spec = spec };
            planned.Add(item);
            var taken = ViewTemplatePlanner.CheckNewName(spec, existingNames);
            if (taken != null) { problems.Add(taken); continue; }

            string? problem;
            if (spec.FromView != null)
            {
                item.Source = ViewTemplateModel.FindSourceView(doc, spec.FromView, spec.Source, out problem);
                if (item.Source == null) { problems.Add(problem!); continue; }
                bool valid;
                try { valid = item.Source.IsViewValidForTemplateCreation(); } catch (Exception) { valid = false; }
                if (!valid)
                {
                    problems.Add($"{spec.Source}: Revit cannot make a template from '{item.Source.Name}' ({item.Source.ViewType}).");
                    continue;
                }
                item.Method = "fromView";
            }
            else
            {
                item.Source = ViewTemplateModel.FindTemplate(doc, templates, spec.DuplicateFrom!, spec.Source + ".duplicateFrom", out problem);
                if (item.Source == null) { problems.Add(problem!); continue; }
                item.Method = ViewTemplateModel.DuplicateMethod(item.Source);
            }

            if (spec.Edit.HasChanges)
            {
                // Against the source: a new template starts with its settings. The trial run re-plans against the real template.
                var state = ViewTemplateModel.ReadState(item.Source, spec.Edit, 0, includeCategories: false);
                state.Name = spec.Name;
                item.Plan = ViewTemplatePlanner.Plan(spec.Edit, state, existingNames.Concat(newNames.Where(n => n != spec.Name)).ToList());
                problems.AddRange(item.Plan.Problems);
                warnings.AddRange(item.Plan.Warnings);
            }
        }

        var dryRun = !apply && ToolArguments.GetBool(request.Arguments, "dryRun", true);
        var refused = new List<string>();
        if ((apply || dryRun) && problems.Count == 0 && planned.Count > 0)
        {
            try
            {
                using var tx = new Transaction(doc, "Revit MCP - Create view templates");
                tx.Start();
                foreach (var item in planned)
                {
                    var spec = item.Spec;
                    try
                    {
                        var created = spec.FromView != null ? item.Source!.CreateViewTemplate() : ViewTemplateModel.Duplicate(doc, item.Source!);
                        created.Name = spec.Name;
                        item.CreatedId = created.Id.Value;
                        if (spec.Edit.HasChanges)
                        {
                            var state = ViewTemplateModel.ReadState(created, spec.Edit, 0, includeCategories: false);
                            var others = existingNames.Concat(newNames.Where(n => n != spec.Name)).ToList();
                            item.Plan = ViewTemplatePlanner.Plan(spec.Edit, state, others);
                            if (item.Plan.Problems.Count > 0) throw new InvalidOperationException(string.Join(" ", item.Plan.Problems));
                            ViewTemplateModel.Apply(doc, created, item.Plan);
                        }
                    }
                    catch (Exception ex)
                    {
                        refused.Add($"{spec.Source} ('{spec.Name}'): Revit refused it ({ex.Message}).");
                        break;
                    }
                }
                if (apply && refused.Count == 0) TransactionCommitGuard.CommitOrThrow(tx);
                else tx.RollBack();
            }
            catch (Exception ex)
            {
                refused.Add($"Creating the view templates failed: {ex.Message}");
            }
        }

        object Describe(PlannedCreate p) => new
        {
            source = p.Spec.Source,
            name = p.Spec.Name,
            id = apply && refused.Count == 0 ? p.CreatedId : null,
            method = p.Method.Length > 0 ? p.Method : null,
            from = p.Source == null ? null : new { id = p.Source.Id.Value, name = p.Source.Name, viewType = p.Source.ViewType.ToString(), isTemplate = p.Source.IsTemplate },
            changes = p.Plan == null ? null : ViewTemplateModel.Describe(p.Plan.Changes)
        };

        sw.Stop();
        if (!apply)
        {
            problems.AddRange(refused);
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = problems.Count == 0
                    ? $"Preview: {planned.Count} view template(s) to create in '{doc.Title}'."
                    : $"Preview: {problems.Count} problem(s) to fix before creating view templates.",
                Data = new
                {
                    ready = problems.Count == 0,
                    problems,
                    document = doc.Title,
                    trialRun = dryRun && planned.Count > 0 && problems.Count == 0,
                    templates = planned.Select(Describe).ToList()
                },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (problems.Count > 0)
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "invalid_arguments",
                Message = $"Nothing was changed: {problems.Count} problem(s). {string.Join(" ", problems.Take(3))}",
                Errors = problems,
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        if (refused.Count > 0)
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "transaction_failed",
                Message = $"Nothing was changed: {refused[0]}",
                Errors = refused,
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };

        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{planned.Count} view template(s) created in '{doc.Title}'. Apply them with revit_apply_view_template.",
            Data = new
            {
                document = doc.Title,
                created = planned.Count,
                templates = planned.Select(Describe).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }
}
