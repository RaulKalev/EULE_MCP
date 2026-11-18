using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// The one body of revit_create_model_family and revit_edit_model_family (and their previews), #169: sets the family
/// category first, then the family settings, reads each back, and reports what changed and that nothing else did.
/// With neither argument it changes nothing, and the session's target does the work (save as, reload, load).
/// </summary>
internal static class ModelFamilyDocumentEdit
{
    public const string CategoryArg = "category";
    public const string SettingsArg = "settings";

    /// <summary>One line for the approval prompt.</summary>
    public static string Summary(McpToolRequest request, ModelFamilySessionMode mode)
    {
        var problems = new List<string>();
        var args = request.Arguments ?? new Dictionary<string, object?>();
        var category = ModelFamilyDocumentSpec.ParseCategory(ModelFamilyJson.Token(args, CategoryArg), CategoryArg, problems);
        var settings = ModelFamilyDocumentSpec.ParseSettings(ModelFamilyJson.Token(args, SettingsArg), SettingsArg, problems);
        var verb = mode == ModelFamilySessionMode.Create ? "Create" : "Edit";
        return $"{verb} {ModelFamilySession.DescribeTarget(request, mode)}. Changes: {ModelFamilyDocumentSpec.DescribeChanges(category, settings)}.";
    }

    public static object? Run(ModelFamilyContext context)
    {
        var doc = context.FamilyDoc;
        var problems = context.Problems;
        var category = ModelFamilyDocumentSpec.ParseCategory(ModelFamilyJson.Token(context.Args, CategoryArg), CategoryArg, problems);
        var settings = ModelFamilyDocumentSpec.ParseSettings(ModelFamilyJson.Token(context.Args, SettingsArg), SettingsArg, problems);
        if (problems.Count > 0) return null;

        var countsBefore = ModelFamilyDocumentRevit.CountsByClass(doc);
        var parametersBefore = ModelFamilyDocumentRevit.ParameterNames(doc);
        var categoryBefore = SafeCategory(doc);
        var settingsBefore = ModelFamilyDocumentRevit.SettingsJson(doc);
        var placementBefore = ModelFamilyDocumentRevit.Placement(doc);
        var changes = new List<string>();
        var result = new JObject();

        // 1. Category: first, because it decides which settings and parameters the family has.
        result["categoryBefore"] = ModelFamilyDocumentRevit.CategoryJson(categoryBefore);
        if (category.Length > 0)
        {
            var target = ResolveCategory(doc, category, categoryBefore, problems);
            if (target == null) return Partial(result, doc);
            if (categoryBefore != null && target.Id == categoryBefore.Id)
                context.Warnings.Add($"category: the family is already {ModelFamilyDocumentRevit.DescribeCategory(categoryBefore)}; nothing to change.");
            else
            {
                string? refusal = null;
                context.Transact("Category", () =>
                {
                    try { doc.OwnerFamily.FamilyCategory = target; }
                    catch (Autodesk.Revit.Exceptions.ArgumentException ex) { refusal = ex.Message; }
                });
                if (refusal != null)
                {
                    problems.Add($"category: Revit refused {ModelFamilyDocumentRevit.DescribeCategory(target)} for this family, which stays {ModelFamilyDocumentRevit.DescribeCategory(categoryBefore)}: {refusal.TrimEnd('.')}. Some templates fix the category; start from {ModelFamilyTargetSpec.DefaultTemplateFileName} with revit_create_model_family.");
                    return Partial(result, doc);
                }
                var now = SafeCategory(doc);
                if (now == null || now.Id != target.Id)
                    throw new ModelFamilyStepException("Category",
                        $"Category: Revit accepted {ModelFamilyDocumentRevit.DescribeCategory(target)} but the family reads back as {ModelFamilyDocumentRevit.DescribeCategory(now)}.",
                        new List<ModelFamilyFailure>());
                changes.Add($"category {ModelFamilyDocumentRevit.DescribeCategory(target)}");
            }
        }
        result["categoryAfter"] = ModelFamilyDocumentRevit.CategoryJson(SafeCategory(doc));

        // 2. Settings, checked against the family as the category left it.
        var settingRows = new JArray();
        if (settings.Count > 0)
        {
            var planned = PlanSettings(doc, settings, problems);
            if (problems.Count > 0) return Partial(result, doc);
            var refused = new Dictionary<string, string>(StringComparer.Ordinal);
            context.Transact("Settings", () =>
            {
                foreach (var (request, parameter, value) in planned)
                {
                    try
                    {
                        if (!parameter.Set(value)) refused[request.Info.Key] = "Revit did not accept the value";
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        refused[request.Info.Key] = ex.Message.TrimEnd('.');
                    }
                }
            });
            foreach (var (request, _, value) in planned)
            {
                var before = settingsBefore[request.Info.Key];
                var after = ModelFamilyDocumentRevit.ReadSetting(doc, request.Info);
                var wanted = request.Info.Kind == ModelFamilySettingKind.Bool
                    ? (JToken)new JValue(value != 0)
                    : new JValue(ModelFamilyDocumentRevit.PartTypeName(value));
                settingRows.Add(new JObject { ["key"] = request.Info.Key, ["before"] = before?.DeepClone(), ["after"] = after?.DeepClone(), ["requested"] = wanted });
                if (refused.TryGetValue(request.Info.Key, out var why))
                    problems.Add($"settings.{request.Info.Key}: Revit refused '{request.Info.Label}' = {wanted}: {why}.");
                else if (after == null || !JToken.DeepEquals(after, wanted))
                    problems.Add($"settings.{request.Info.Key}: '{request.Info.Label}' reads back as {after?.ToString() ?? "nothing"} instead of {wanted}; this category or template does not keep it.");
                else if (!JToken.DeepEquals(before, after))
                    changes.Add($"{request.Info.Key} {after}");
            }
            if (problems.Count > 0)
            {
                result["settings"] = settingRows;
                return Partial(result, doc);
            }
        }
        result["settings"] = settingRows;

        // 3. What the edit did not name stays as it was.
        var parametersAfter = ModelFamilyDocumentRevit.ParameterNames(doc);
        var dropped = ModelFamilyDocumentSpec.Disappeared(parametersBefore, parametersAfter);
        if (dropped.Count > 0)
        {
            result["droppedParameters"] = new JArray(dropped);
            context.Warnings.Add($"The category change removed {dropped.Count} parameter(s) that belong to the old category: {string.Join(", ", dropped.Select(n => $"'{n}'"))}.");
        }
        var countsAfter = ModelFamilyDocumentRevit.CountsByClass(doc);
        var diff = ModelFamilyDocumentSpec.DiffCounts(countsBefore, countsAfter);
        // A category change may add or drop category parameters; that is reported above, not as unexpected.
        var categoryChanged = categoryBefore?.Id.Value != SafeCategory(doc)?.Id.Value;
        var expected = categoryChanged ? new[] { "FamilyParameter" } : Array.Empty<string>();
        context.Warnings.AddRange(ModelFamilyDocumentSpec.CountWarnings(diff, expected));
        foreach (var property in diff.ToJson().Properties()) result[property.Name] = property.Value;
        result["preserved"] = "Only the category and the settings named were changed; forms, reference planes, dimensions, parameters, types, connectors and nested families were not touched (unchangedCounts compares them before and after).";

        var placementAfter = ModelFamilyDocumentRevit.Placement(doc);
        result["placementBefore"] = placementBefore;
        foreach (var property in ModelFamilyDocumentRevit.PlacementJson(doc).Properties()) result[property.Name] = property.Value;
        if (ModelFamilyDocumentSpec.PlacementNeedsHost(placementAfter) == true)
            context.Warnings.Add($"The family is hosted ({placementAfter}): an instance needs a host face. For a device placed without a host, start from {ModelFamilyTargetSpec.DefaultTemplateFileName}.");
        AddViewsAndLevels(result, doc);

        context.Summary = changes.Count > 0 ? string.Join("; ", changes) : "no setting changes";
        return result;
    }

    private static JObject Partial(JObject result, Document doc)
    {
        result["categoryAfter"] ??= ModelFamilyDocumentRevit.CategoryJson(SafeCategory(doc));
        result["changeableSettings"] = new JArray(ModelFamilyDocumentRevit.ChangeableSettings(doc));
        return result;
    }

    private static void AddViewsAndLevels(JObject result, Document doc)
    {
        result["views"] = ModelFamilyDocumentRevit.Views(doc, ModelFamilyDocumentSpec.MaxLimit, out _);
        result["levels"] = ModelFamilyDocumentRevit.Levels(doc, ModelFamilyDocumentSpec.MaxLimit, out _);
    }

    private static Category? SafeCategory(Document doc)
    {
        try { return doc.OwnerFamily?.FamilyCategory; }
        catch { return null; }
    }

    /// <summary>The category the argument names, from the document's own categories; a model category only.</summary>
    private static Category? ResolveCategory(Document doc, string wanted, Category? current, List<string> problems)
    {
        var categories = ModelFamilyDocumentRevit.Categories(doc);
        var candidates = categories.Select(c => (ModelFamilyDocumentRevit.BuiltInName(c), c.Name)).ToList();
        var index = ModelFamilyDocumentSpec.MatchCategory(wanted, candidates, out var error);
        if (index < 0)
        {
            problems.Add($"category: {error} The family is {ModelFamilyDocumentRevit.DescribeCategory(current)}.");
            return null;
        }
        var category = categories[index];
        CategoryType type;
        try { type = category.CategoryType; }
        catch { type = CategoryType.Invalid; }
        if (type != CategoryType.Model)
        {
            problems.Add($"category: {ModelFamilyDocumentRevit.DescribeCategory(category)} is a {type} category; a model family needs a model category such as OST_ElectricalFixtures or OST_GenericModel. The family is {ModelFamilyDocumentRevit.DescribeCategory(current)}.");
            return null;
        }
        return category;
    }

    /// <summary>Each requested setting with its parameter and integer value; a missing or read-only one is a problem.</summary>
    private static List<(ModelFamilySettingRequest Request, Parameter Parameter, int Value)> PlanSettings(Document doc,
        List<ModelFamilySettingRequest> settings, List<string> problems)
    {
        var planned = new List<(ModelFamilySettingRequest, Parameter, int)>();
        string Allowed()
        {
            var keys = ModelFamilyDocumentRevit.ChangeableSettings(doc);
            return keys.Count == 0 ? "none of the settings" : string.Join(", ", keys);
        }
        var category = ModelFamilyDocumentRevit.DescribeCategory(SafeCategory(doc));
        foreach (var request in settings)
        {
            var source = $"settings.{request.Info.Key}";
            var parameter = ModelFamilyDocumentRevit.SettingParameter(doc, request.Info);
            if (parameter == null)
            {
                problems.Add($"{source}: a {category} family has no '{request.Info.Label}' setting. It allows: {Allowed()}.");
                continue;
            }
            if (ModelFamilyDocumentRevit.SafeReadOnly(parameter) || parameter.StorageType != StorageType.Integer)
            {
                problems.Add($"{source}: '{request.Info.Label}' is fixed for a {category} family (or by its template). It allows: {Allowed()}.");
                continue;
            }
            if (request.Info.Kind == ModelFamilySettingKind.Bool)
            {
                planned.Add((request, parameter, request.BoolValue == true ? 1 : 0));
                continue;
            }
            var names = ModelFamilyDocumentRevit.PartTypeNames();
            var name = ModelFamilyDocumentSpec.MatchEnumName(request.TextValue, names);
            var value = name == null ? null : ModelFamilyDocumentRevit.PartTypeValue(name);
            if (value == null)
            {
                var close = ModelFamilyJson.Suggest(request.TextValue, names);
                problems.Add($"{source}: '{request.TextValue}' is not a part type" +
                             (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(c => $"'{c}'"))}?" : $". Part types: {string.Join(", ", names.Take(20))}, …"));
                continue;
            }
            planned.Add((request, parameter, value.Value));
        }
        return planned;
    }
}
