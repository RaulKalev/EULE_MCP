using System.Globalization;
using RevitMCP.Addin.Documentation.Placement;
using RevitMCP.Addin.ViewFilters;

namespace RevitMCP.Addin.ViewTemplates;

/// <summary>A category or subcategory a template can show or hide.</summary>
public sealed class ViewTemplateCategory
{
    public long Id { get; set; }
    /// <summary>The category name; a subcategory is "Parent: Sub".</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>The BuiltInCategory name (OST_…), when it has one.</summary>
    public string? BuiltInName { get; set; }
    /// <summary>Model, Annotation, AnalyticalModel or Internal (the parent's, for a subcategory).</summary>
    public string Kind { get; set; } = string.Empty;
    public bool CanHide { get; set; }
    public bool Hidden { get; set; }
}

/// <summary>A parameter of the template a request names.</summary>
public sealed class ViewTemplateParameterValue
{
    public long Id { get; set; }
    /// <summary>The parameter's own name (the request may differ in case).</summary>
    public string Name { get; set; } = string.Empty;
    public bool ReadOnly { get; set; }
    /// <summary>The value as Revit shows it; an element by its name.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>What a template (or the view a template is made from) holds now, read from Revit. Plain data.</summary>
public sealed class ViewTemplateState
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ViewType { get; set; } = string.Empty;
    public int? Scale { get; set; }
    public string? DetailLevel { get; set; }
    public string? Discipline { get; set; }
    public string? DisplayStyle { get; set; }
    public bool CanSetScale { get; set; }
    public bool CanSetDetailLevel { get; set; }
    public bool CanSetDiscipline { get; set; }
    public bool CanSetDisplayStyle { get; set; }
    /// <summary>GetTemplateParameterIds: every parameter the template can control, with its name and BuiltInParameter name.</summary>
    public List<NamedRef> TemplateParameters { get; set; } = new();
    /// <summary>GetNonControlledTemplateParameterIds.</summary>
    public HashSet<long> NonControlled { get; set; } = new();
    /// <summary>Filled when the request shows or hides categories.</summary>
    public List<ViewTemplateCategory> Categories { get; set; } = new();
    /// <summary>The parameters the request names, by the name it uses; a name it does not have is missing.</summary>
    public Dictionary<string, ViewTemplateParameterValue> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Every parameter name of the template, for suggestions.</summary>
    public List<string> ParameterNames { get; set; } = new();
    public int ViewCount { get; set; }
}

/// <summary>One from/to of a plan. Field: name, scale, detailLevel, discipline, displayStyle, controls, category, parameter.</summary>
public sealed class ViewTemplateChange
{
    public ViewTemplateChange(string field, string? name, string? from, string? to)
    {
        Field = field;
        Name = name;
        From = from;
        To = to;
    }

    public string Field { get; }
    /// <summary>The parameter or category, for controls, category and parameter changes.</summary>
    public string? Name { get; }
    public string? From { get; }
    public string? To { get; }
}

/// <summary>What one edit does to one template: the changes, the values to set, and the problems that stop it.</summary>
public sealed class ViewTemplatePlan
{
    public ViewTemplateEdit Edit { get; set; } = new();
    public List<ViewTemplateChange> Changes { get; } = new();
    public List<string> Problems { get; } = new();
    public List<string> Warnings { get; } = new();

    public string? NewName { get; set; }
    public int? Scale { get; set; }
    public string? DetailLevel { get; set; }
    public string? Discipline { get; set; }
    public string? DisplayStyle { get; set; }
    /// <summary>The new non-controlled set; null when it does not change.</summary>
    public List<long>? NonControlled { get; set; }
    public List<long> Hide { get; } = new();
    public List<long> Show { get; } = new();
    /// <summary>Parameter name (the template's spelling) → value to set.</summary>
    public List<KeyValuePair<string, string>> Parameters { get; } = new();

    public string Action => Problems.Count > 0 ? "invalid" : Changes.Count > 0 ? "edit" : "unchanged";
}

/// <summary>Plans the edits of revit_set_view_templates and revit_create_view_templates. Pure: no Revit types, unit tested.</summary>
public static class ViewTemplatePlanner
{
    /// <summary>The template parameters (BuiltInParameter names) that decide whether views take a field from their template.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> FieldParameters = new Dictionary<string, string[]>
    {
        ["scale"] = new[] { "VIEW_SCALE_PULLDOWN_METRIC", "VIEW_SCALE_PULLDOWN_IMPERIAL", "VIEW_SCALE" },
        ["detailLevel"] = new[] { "VIEW_DETAIL_LEVEL" },
        ["discipline"] = new[] { "VIEW_DISCIPLINE" },
        ["displayStyle"] = new[] { "MODEL_GRAPHICS_STYLE" }
    };

    /// <summary>Category kind → the V/G tab parameter that carries its visibility to the views.</summary>
    public static readonly IReadOnlyDictionary<string, string> CategoryKindParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Model"] = "VIS_GRAPHICS_MODEL",
        ["Annotation"] = "VIS_GRAPHICS_ANNOTATION",
        ["AnalyticalModel"] = "VIS_GRAPHICS_ANALYTICAL_MODEL"
    };

    /// <param name="otherTemplateNames">The names of the document's other templates, and names given out earlier in the request.</param>
    public static ViewTemplatePlan Plan(ViewTemplateEdit edit, ViewTemplateState state, IReadOnlyCollection<string> otherTemplateNames)
    {
        var plan = new ViewTemplatePlan { Edit = edit };
        var label = $"{edit.Source} ('{state.Name}')";

        if (edit.NewName != null && !string.Equals(edit.NewName, state.Name, StringComparison.Ordinal))
        {
            if (otherTemplateNames.Contains(edit.NewName, StringComparer.OrdinalIgnoreCase))
                plan.Problems.Add($"{label}: another view template is already named '{edit.NewName}'.");
            else
            {
                plan.NewName = edit.NewName;
                plan.Changes.Add(new ViewTemplateChange("name", null, state.Name, edit.NewName));
            }
        }

        if (edit.Scale != null)
        {
            if (!state.CanSetScale) plan.Problems.Add($"{label}: a {state.ViewType} template has no scale to set.");
            else if (edit.Scale != state.Scale)
            {
                plan.Scale = edit.Scale;
                plan.Changes.Add(new ViewTemplateChange("scale", null, ScaleLabel(state.Scale), ScaleLabel(edit.Scale)));
            }
        }

        Field(edit.DetailLevel, state.DetailLevel, state.CanSetDetailLevel, "detailLevel", "detail level", v => plan.DetailLevel = v);
        Field(edit.Discipline, state.Discipline, state.CanSetDiscipline, "discipline", "discipline", v => plan.Discipline = v);
        Field(edit.DisplayStyle, state.DisplayStyle, state.CanSetDisplayStyle, "displayStyle", "display style", v => plan.DisplayStyle = v);

        // Controlled parameters: include removes from the non-controlled set, exclude adds to it.
        var nonControlled = new HashSet<long>(state.NonControlled);
        var touched = new Dictionary<long, string>();
        foreach (var reference in edit.Include)
            ControlChange(reference, controls: true);
        foreach (var reference in edit.Exclude)
            ControlChange(reference, controls: false);
        if (!nonControlled.SetEquals(state.NonControlled)) plan.NonControlled = nonControlled.OrderBy(i => i).ToList();

        // Categories.
        if (edit.HideCategories.Count > 0 || edit.ShowCategories.Count > 0)
        {
            var refs = state.Categories.Select(c => new NamedRef(c.Id, c.Name, c.BuiltInName)).ToList();
            var seen = new Dictionary<long, string>();
            foreach (var reference in edit.HideCategories) CategoryChange(reference, hide: true);
            foreach (var reference in edit.ShowCategories) CategoryChange(reference, hide: false);

            void CategoryChange(string reference, bool hide)
            {
                var found = Resolve(reference, refs, "category", label, plan.Problems);
                if (found == null) return;
                var category = state.Categories.First(c => c.Id == found.Id);
                if (seen.TryGetValue(category.Id, out var earlier))
                {
                    plan.Problems.Add($"{label}: '{reference}' and '{earlier}' are the same category ('{category.Name}').");
                    return;
                }
                seen[category.Id] = reference;
                if (!category.CanHide)
                {
                    plan.Problems.Add($"{label}: category '{category.Name}' cannot be hidden or shown in a {state.ViewType} template.");
                    return;
                }
                if (category.Hidden == hide) return;
                (hide ? plan.Hide : plan.Show).Add(category.Id);
                plan.Changes.Add(new ViewTemplateChange("category", category.Name, hide ? "visible" : "hidden", hide ? "hidden" : "visible"));
                if (CategoryKindParameters.TryGetValue(category.Kind, out var tab))
                    WarnIfNotControlled(new[] { tab }, $"the visibility of {category.Kind.ToLowerInvariant()} categories such as '{category.Name}'");
            }
        }

        // Other parameters.
        foreach (var pair in edit.Parameters)
        {
            if (!state.Parameters.TryGetValue(pair.Key, out var current))
            {
                plan.Problems.Add($"{label}: the template has no parameter '{pair.Key}'{Suggest(pair.Key, state.ParameterNames)}");
                continue;
            }
            if (current.ReadOnly)
            {
                plan.Problems.Add($"{label}: parameter '{current.Name}' is read-only.");
                continue;
            }
            if (string.Equals(current.Value.Trim(), pair.Value.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            plan.Parameters.Add(new KeyValuePair<string, string>(current.Name, pair.Value));
            plan.Changes.Add(new ViewTemplateChange("parameter", current.Name, current.Value, pair.Value));
            var templateParameter = state.TemplateParameters.FirstOrDefault(p => p.Id == current.Id);
            if (templateParameter != null && nonControlled.Contains(templateParameter.Id))
                plan.Warnings.Add($"{label}: the template does not control '{templateParameter.Name}', so the views that use it keep their own " +
                                  $"value; add '{templateParameter.Name}' to include.");
        }

        if (plan.Scale != null) WarnIfNotControlled(FieldParameters["scale"], "the view scale");
        if (plan.DetailLevel != null) WarnIfNotControlled(FieldParameters["detailLevel"], "the detail level");
        if (plan.Discipline != null) WarnIfNotControlled(FieldParameters["discipline"], "the discipline");
        if (plan.DisplayStyle != null) WarnIfNotControlled(FieldParameters["displayStyle"], "the display style");

        return plan;

        void Field(string? wanted, string? current, bool canSet, string field, string words, Action<string> set)
        {
            if (wanted == null) return;
            if (!canSet)
            {
                plan.Problems.Add($"{label}: the {words} of a {state.ViewType} template cannot be set.");
                return;
            }
            if (string.Equals(wanted, current, StringComparison.OrdinalIgnoreCase)) return;
            set(wanted);
            plan.Changes.Add(new ViewTemplateChange(field, null, current, wanted));
        }

        void ControlChange(string reference, bool controls)
        {
            var found = Resolve(reference, state.TemplateParameters, "template parameter", label, plan.Problems);
            if (found == null) return;
            if (touched.TryGetValue(found.Id, out var earlier))
            {
                plan.Problems.Add($"{label}: '{reference}' and '{earlier}' are the same template parameter ('{found.Name}').");
                return;
            }
            touched[found.Id] = reference;
            var changed = controls ? nonControlled.Remove(found.Id) : nonControlled.Add(found.Id);
            if (changed) plan.Changes.Add(new ViewTemplateChange("controls", found.Name, controls ? "no" : "yes", controls ? "yes" : "no"));
        }

        void WarnIfNotControlled(IEnumerable<string> builtInNames, string what)
        {
            var names = new HashSet<string>(builtInNames, StringComparer.OrdinalIgnoreCase);
            var parameters = state.TemplateParameters.Where(p => p.BuiltInName != null && names.Contains(p.BuiltInName)).ToList();
            if (parameters.Count == 0 || parameters.Any(p => !nonControlled.Contains(p.Id))) return;
            var message = $"{label}: the template does not control {what} ('{parameters[0].Name}'), so the views that use it keep their own; " +
                          $"add '{parameters[0].Name}' to include.";
            if (!plan.Warnings.Contains(message)) plan.Warnings.Add(message);
        }
    }

    /// <summary>The problem when a new template's name is taken; null when it is free.</summary>
    public static string? CheckNewName(ViewTemplateCreateSpec spec, IReadOnlyCollection<string> existingTemplateNames) =>
        existingTemplateNames.Contains(spec.Name, StringComparer.OrdinalIgnoreCase)
            ? $"{spec.Source}: a view template named '{spec.Name}' already exists; edit it with revit_set_view_templates or pick another name."
            : null;

    /// <summary>Finds a reference among <paramref name="items"/> by id, built-in name or name; a problem otherwise.</summary>
    public static NamedRef? Resolve(string reference, IReadOnlyCollection<NamedRef> items, string what, string label, List<string> problems)
    {
        var match = ViewFilterRefMatcher.Match(reference, items);
        switch (match.Kind)
        {
            case RefMatchKind.Found:
                return match.Found;
            case RefMatchKind.Ambiguous:
                problems.Add($"{label}: '{reference}' matches several {what}s: {ViewFilterRefMatcher.List(match.Candidates)}. Use the id.");
                return null;
            default:
                problems.Add($"{label}: '{reference}' is not a {what} of this template{Suggest(reference, items.Select(i => i.Name))}");
                return null;
        }
    }

    /// <summary>"; did you mean 'A', 'B'?" for close names, else ".".</summary>
    public static string Suggest(string reference, IEnumerable<string> names)
    {
        var text = reference.Trim();
        var close = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => (Name: n, Score: n.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0
                ? 1.0
                : FuzzyNameMatcher.Similarity(n, text)))
            .Where(c => c.Score >= 0.6)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(c => $"'{c.Name}'")
            .ToList();
        return close.Count > 0 ? $"; did you mean {string.Join(", ", close)}?" : ".";
    }

    public static string? ScaleLabel(int? scale) => scale == null ? null : "1:" + scale.Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The approval texts of revit_create_view_templates and revit_set_view_templates. Pure.</summary>
public static class ViewTemplateApprovalText
{
    /// <param name="viewCounts">Template reference → (its name, how many views use it); a reference not found is left out.</param>
    public static string ForSet(IReadOnlyList<ViewTemplateEdit> edits, int invalid, string document,
        IReadOnlyDictionary<string, (string Name, int Views)> viewCounts)
    {
        var total = 0;
        var list = string.Join("; ", edits.Take(6).Select(e =>
        {
            var known = viewCounts.TryGetValue(e.Template, out var found);
            if (known) total += found.Views;
            var name = known ? found.Name : e.Template;
            var used = known ? $", used by {found.Views} view(s)" : string.Empty;
            return $"'{name}'{used}: {e.Describe()}";
        }));
        foreach (var e in edits.Skip(6))
            if (viewCounts.TryGetValue(e.Template, out var found)) total += found.Views;
        return $"Edit {edits.Count} view template(s) in {Doc(document)}: {list}{(edits.Count > 6 ? "; …" : string.Empty)}. " +
               $"Editing a template changes every view that uses it ({total} view(s) in all)." +
               (invalid > 0 ? $" The request has {invalid} invalid item(s)." : string.Empty);
    }

    public static string ForCreate(IReadOnlyList<ViewTemplateCreateSpec> specs, int invalid, string document)
    {
        var list = string.Join("; ", specs.Take(6).Select(s =>
        {
            var from = s.FromView != null ? $"from view '{s.FromView}'" : $"copy of '{s.DuplicateFrom}'";
            var edits = s.Edit.HasChanges ? $", then {s.Edit.Describe()}" : string.Empty;
            return $"'{s.Name}' ({from}{edits})";
        }));
        return $"Create {specs.Count} view template(s) in {Doc(document)}: {list}{(specs.Count > 6 ? "; …" : string.Empty)}. " +
               "No view uses a new template until one is applied." +
               (invalid > 0 ? $" The request has {invalid} invalid item(s)." : string.Empty);
    }

    private static string Doc(string document) => document.Length > 0 ? $"'{document}'" : "the active document";
}
