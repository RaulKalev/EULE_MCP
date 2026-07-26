using System.Text;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.CadManagement;

internal sealed class CadOverrideChange
{
    public long ImportInstanceId { get; set; }
    public string ImportName { get; set; } = string.Empty;
    public string? LayerName { get; set; }
    public bool AllImports { get; set; }
    public bool HasVisible { get; set; }
    public bool Visible { get; set; }
    public bool HasHalftone { get; set; }
    public bool Halftone { get; set; }
    public bool HasLineColor { get; set; }
    public string? LineColor { get; set; }
    public bool HasLineWeight { get; set; }
    public int LineWeight { get; set; }
    public bool HasLinePattern { get; set; }
    public long LinePatternId { get; set; }
    public string? LinePatternName { get; set; }
    public bool ClearGraphics { get; set; }

    public bool HasGraphicsChange =>
        ClearGraphics || HasHalftone || HasLineColor || HasLineWeight || HasLinePattern;
}

internal sealed class CadCategorySnapshot
{
    public long ImportInstanceId { get; set; }
    public string ImportName { get; set; } = string.Empty;
    public string? LayerName { get; set; }
    public long CategoryId { get; set; }
    public bool Visible { get; set; }
    public bool Halftone { get; set; }
    public string? LineColor { get; set; }
    public int? LineWeight { get; set; }
    public long? LinePatternId { get; set; }
    public string? LinePatternName { get; set; }
}

internal sealed class CadOverridePlanItem
{
    public View RequestedView { get; set; } = null!;
    public View SettingsView { get; set; } = null!;
    public Category Category { get; set; } = null!;
    public string ImportName { get; set; } = string.Empty;
    public long ImportInstanceId { get; set; }
    public string? LayerName { get; set; }
    public CadOverrideChange Change { get; set; } = null!;
    public ElementId? ResolvedLinePatternId { get; set; }
}

internal static class CadOverrideSupport
{
    public static IReadOnlyList<View> ResolveViews(
        Document document,
        View activeView,
        long viewId,
        IEnumerable<long> viewIds,
        bool allowActiveFallback,
        List<string> warnings)
    {
        var ids = viewIds.Where(id => id > 0).Distinct().ToList();
        if (viewId > 0)
            ids.Insert(0, viewId);

        if (ids.Count == 0 && allowActiveFallback)
            return new[] { activeView };

        var result = new List<View>();
        foreach (var id in ids.Distinct())
        {
            if (document.GetElement(new ElementId(id)) is not View view)
            {
                warnings.Add($"View {id} was not found.");
                continue;
            }

            if (view.IsTemplate)
            {
                warnings.Add($"View {id} ('{view.Name}') is a template. Select a project view and use useViewTemplate=true instead.");
                continue;
            }

            result.Add(view);
        }

        return result;
    }

    public static View ResolveSettingsView(Document document, View requestedView, bool useViewTemplate)
    {
        if (!useViewTemplate || requestedView.IsTemplate ||
            requestedView.ViewTemplateId == ElementId.InvalidElementId)
            return requestedView;

        return document.GetElement(requestedView.ViewTemplateId) as View ?? requestedView;
    }

    public static List<ImportInstance> GetImports(Document document, View contextView)
    {
        IEnumerable<ImportInstance> query;
        try
        {
            query = new FilteredElementCollector(document, contextView.Id)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>();
        }
        catch
        {
            query = new FilteredElementCollector(document)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>();
        }

        return query
            .Where(import => import.Category != null)
            .OrderBy(import => GetImportName(document, import), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(import => import.Id.Value)
            .ToList();
    }

    public static string GetImportName(Document document, ImportInstance import)
    {
        var type = document.GetElement(import.GetTypeId()) as ElementType;
        var name = type?.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = import.Category?.Name;
        return NormalizeName(name ?? $"CAD {import.Id.Value}");
    }

    public static List<CadCategorySnapshot> Capture(
        Document document,
        View contextView,
        View settingsView,
        bool includeLayers,
        string importNameFilter,
        int limit)
    {
        var snapshots = new List<CadCategorySnapshot>();
        foreach (var import in GetImports(document, contextView))
        {
            var importName = GetImportName(document, import);
            if (!string.IsNullOrWhiteSpace(importNameFilter) &&
                importName.IndexOf(importNameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            snapshots.Add(CaptureCategory(document, settingsView, import, import.Category, importName, null));

            if (includeLayers)
            {
                foreach (Category layer in import.Category.SubCategories)
                {
                    if (layer == null || string.IsNullOrWhiteSpace(layer.Name))
                        continue;
                    snapshots.Add(CaptureCategory(
                        document,
                        settingsView,
                        import,
                        layer,
                        importName,
                        NormalizeName(layer.Name)));
                }
            }

            if (limit > 0 && snapshots.Count >= limit)
                break;
        }

        return limit > 0 ? snapshots.Take(limit).ToList() : snapshots;
    }

    public static List<CadOverrideChange> ParseChanges(
        Dictionary<string, object?> arguments,
        List<string> errors)
    {
        if (!arguments.TryGetValue("changes", out var raw) || raw == null)
        {
            errors.Add("changes is required.");
            return new List<CadOverrideChange>();
        }

        JArray? array;
        try
        {
            array = raw switch
            {
                JArray jArray => jArray,
                string json => JToken.Parse(json) as JArray,
                _ => JArray.FromObject(raw)
            };
        }
        catch (Exception ex)
        {
            errors.Add($"changes could not be parsed: {ex.Message}");
            return new List<CadOverrideChange>();
        }

        if (array == null || array.Count == 0)
        {
            errors.Add("changes must contain at least one object.");
            return new List<CadOverrideChange>();
        }

        var result = new List<CadOverrideChange>();
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JObject item)
            {
                errors.Add($"changes[{index}] must be an object.");
                continue;
            }

            var change = new CadOverrideChange
            {
                ImportInstanceId = item["importInstanceId"]?.Value<long>() ?? 0,
                ImportName = NormalizeName(item["importName"]?.Value<string>() ?? string.Empty),
                LayerName = item.TryGetValue("layerName", out var layerToken)
                    ? NormalizeName(layerToken.Value<string>() ?? string.Empty)
                    : null,
                AllImports = item["allImports"]?.Value<bool>() ?? false,
                ClearGraphics = item["clearGraphics"]?.Value<bool>() ?? false
            };

            if (item.TryGetValue("visible", out var visible))
            {
                change.HasVisible = true;
                change.Visible = visible.Value<bool>();
            }

            if (item.TryGetValue("halftone", out var halftone))
            {
                change.HasHalftone = true;
                change.Halftone = halftone.Value<bool>();
            }

            if (item.TryGetValue("lineColor", out var colorToken))
            {
                change.HasLineColor = true;
                change.LineColor = colorToken.Value<string>();
                if (!TryParseColor(change.LineColor, out _))
                    errors.Add($"changes[{index}].lineColor must be #RRGGBB.");
            }

            if (item.TryGetValue("lineWeight", out var weightToken))
            {
                change.HasLineWeight = true;
                change.LineWeight = weightToken.Value<int>();
                if (change.LineWeight < 1 || change.LineWeight > 16)
                    errors.Add($"changes[{index}].lineWeight must be between 1 and 16.");
            }

            if (item.TryGetValue("linePatternId", out var patternIdToken))
            {
                change.HasLinePattern = true;
                change.LinePatternId = patternIdToken.Value<long>();
            }
            if (item.TryGetValue("linePatternName", out var patternNameToken))
            {
                change.HasLinePattern = true;
                change.LinePatternName = patternNameToken.Value<string>();
            }

            if (!change.AllImports && change.ImportInstanceId <= 0 &&
                string.IsNullOrWhiteSpace(change.ImportName))
                errors.Add($"changes[{index}] must provide importInstanceId, importName, or allImports=true.");

            if (!change.HasVisible && !change.HasGraphicsChange)
                errors.Add($"changes[{index}] does not contain a setting to change.");

            result.Add(change);
        }

        return result;
    }

    public static List<CadOverridePlanItem> BuildPlan(
        Document document,
        IReadOnlyList<View> requestedViews,
        IReadOnlyList<CadOverrideChange> changes,
        bool useViewTemplate,
        List<string> warnings)
    {
        var result = new List<CadOverridePlanItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var requestedView in requestedViews)
        {
            var settingsView = ResolveSettingsView(document, requestedView, useViewTemplate);
            var imports = GetImports(document, requestedView);

            for (var changeIndex = 0; changeIndex < changes.Count; changeIndex++)
            {
                var change = changes[changeIndex];
                var matchingImports = imports.Where(import =>
                    change.AllImports ||
                    (change.ImportInstanceId > 0 && import.Id.Value == change.ImportInstanceId) ||
                    (!string.IsNullOrWhiteSpace(change.ImportName) &&
                     string.Equals(GetImportName(document, import), change.ImportName,
                         StringComparison.CurrentCultureIgnoreCase))).ToList();

                if (matchingImports.Count == 0)
                {
                    warnings.Add(
                        $"No CAD import matched '{DescribeSelector(change)}' in view '{requestedView.Name}'.");
                    continue;
                }

                foreach (var import in matchingImports)
                {
                    var categories = ResolveCategories(import, change.LayerName).ToList();
                    if (categories.Count == 0)
                    {
                        warnings.Add(
                            $"Layer '{change.LayerName}' was not found in CAD import '{GetImportName(document, import)}' " +
                            $"for view '{requestedView.Name}'.");
                        continue;
                    }

                    foreach (var category in categories)
                    {
                        var key = $"{settingsView.Id.Value}:{category.Id.Value}:{changeIndex}";
                        if (!seen.Add(key))
                            continue;

                        ElementId? linePatternId = null;
                        if (change.HasLinePattern)
                        {
                            linePatternId = ResolveLinePattern(document, change, out var patternError);
                            if (linePatternId == null)
                            {
                                warnings.Add(patternError);
                                continue;
                            }
                        }

                        result.Add(new CadOverridePlanItem
                        {
                            RequestedView = requestedView,
                            SettingsView = settingsView,
                            Category = category,
                            ImportName = GetImportName(document, import),
                            ImportInstanceId = import.Id.Value,
                            LayerName = category.Id == import.Category.Id ? null : NormalizeName(category.Name),
                            Change = change,
                            ResolvedLinePatternId = linePatternId
                        });
                    }
                }
            }
        }

        return result;
    }

    public static (bool Success, Transactions.TransactionDiagnostics Diagnostics) ApplyPlan(
        Document document,
        IReadOnlyList<CadOverridePlanItem> plan,
        CancellationToken cancellationToken,
        List<string> warnings,
        List<object> results)
    {
        return Transactions.RevitTransactionRunner.Run(
            document,
            "Revit MCP - Set CAD Overrides",
            () =>
            {
                foreach (var item in plan)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        ApplyItem(item);
                        results.Add(ToPlanResult(item));
                    }
                    catch (Exception ex)
                    {
                        warnings.Add(
                            $"Failed on '{item.ImportName}'" +
                            $"{(item.LayerName == null ? string.Empty : $"/{item.LayerName}")} " +
                            $"in '{item.SettingsView.Name}': {ex.Message}");
                    }
                }
            });
    }

    public static object ToPlanResult(CadOverridePlanItem item) => new
    {
        requestedViewId = item.RequestedView.Id.Value,
        requestedViewName = item.RequestedView.Name,
        settingsViewId = item.SettingsView.Id.Value,
        settingsViewName = item.SettingsView.Name,
        settingsOwner = item.SettingsView.IsTemplate ? "ViewTemplate" : "View",
        importInstanceId = item.ImportInstanceId,
        importName = item.ImportName,
        layerName = item.LayerName,
        categoryId = item.Category.Id.Value,
        visible = item.Change.HasVisible ? item.Change.Visible : (bool?)null,
        halftone = item.Change.HasHalftone ? item.Change.Halftone : (bool?)null,
        lineColor = item.Change.HasLineColor ? item.Change.LineColor : null,
        lineWeight = item.Change.HasLineWeight ? item.Change.LineWeight : (int?)null,
        linePatternId = item.ResolvedLinePatternId?.Value,
        clearGraphics = item.Change.ClearGraphics
    };

    public static List<CadOverrideChange> ToPortableChanges(
        IEnumerable<CadCategorySnapshot> snapshots)
    {
        return snapshots
            .GroupBy(
                snapshot => $"{NormalizeName(snapshot.ImportName)}\u001f{NormalizeName(snapshot.LayerName ?? string.Empty)}",
                StringComparer.CurrentCultureIgnoreCase)
            .Select(group =>
            {
                var snapshot = group.First();
                return new CadOverrideChange
                {
                    ImportName = snapshot.ImportName,
                    LayerName = snapshot.LayerName,
                    HasVisible = true,
                    Visible = snapshot.Visible,
                    HasHalftone = true,
                    Halftone = snapshot.Halftone,
                    ClearGraphics = true,
                    HasLineColor = snapshot.LineColor != null,
                    LineColor = snapshot.LineColor,
                    HasLineWeight = snapshot.LineWeight.HasValue,
                    LineWeight = snapshot.LineWeight ?? 0,
                    HasLinePattern = snapshot.LinePatternId.HasValue,
                    LinePatternId = snapshot.LinePatternId ?? 0,
                    LinePatternName = snapshot.LinePatternName
                };
            })
            .ToList();
    }

    public static object ToPortableChangeResult(CadOverrideChange change) => new
    {
        importName = change.ImportName,
        layerName = change.LayerName,
        visible = change.Visible,
        halftone = change.Halftone,
        clearGraphics = change.ClearGraphics,
        lineColor = change.HasLineColor ? change.LineColor : null,
        lineWeight = change.HasLineWeight ? change.LineWeight : (int?)null,
        linePatternId = change.HasLinePattern ? change.LinePatternId : (long?)null,
        linePatternName = change.HasLinePattern ? change.LinePatternName : null
    };

    private static CadCategorySnapshot CaptureCategory(
        Document document,
        View settingsView,
        ImportInstance import,
        Category category,
        string importName,
        string? layerName)
    {
        var overrides = settingsView.GetCategoryOverrides(category.Id);
        var linePatternId = overrides.ProjectionLinePatternId;
        var linePattern = linePatternId != ElementId.InvalidElementId
            ? document.GetElement(linePatternId) as LinePatternElement
            : null;
        var color = overrides.ProjectionLineColor;
        var lineWeight = overrides.ProjectionLineWeight;

        return new CadCategorySnapshot
        {
            ImportInstanceId = import.Id.Value,
            ImportName = importName,
            LayerName = layerName,
            CategoryId = category.Id.Value,
            Visible = !settingsView.GetCategoryHidden(category.Id),
            Halftone = overrides.Halftone,
            LineColor = color.IsValid
                ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}"
                : null,
            LineWeight = lineWeight > 0 ? lineWeight : null,
            LinePatternId = linePatternId != ElementId.InvalidElementId ? linePatternId.Value : null,
            LinePatternName = linePattern?.Name
        };
    }

    private static IEnumerable<Category> ResolveCategories(ImportInstance import, string? layerName)
    {
        if (layerName == null)
        {
            yield return import.Category;
            yield break;
        }

        var normalizedLayer = NormalizeName(layerName);
        foreach (Category subCategory in import.Category.SubCategories)
        {
            if (subCategory == null)
                continue;
            if (normalizedLayer == "*" ||
                string.Equals(NormalizeName(subCategory.Name), normalizedLayer,
                    StringComparison.CurrentCultureIgnoreCase))
                yield return subCategory;
        }
    }

    private static ElementId? ResolveLinePattern(
        Document document,
        CadOverrideChange change,
        out string error)
    {
        error = string.Empty;
        if (change.LinePatternId > 0)
        {
            var id = new ElementId(change.LinePatternId);
            if (document.GetElement(id) is LinePatternElement)
                return id;
            error = $"Line pattern {change.LinePatternId} was not found.";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(change.LinePatternName))
        {
            var pattern = new FilteredElementCollector(document)
                .OfClass(typeof(LinePatternElement))
                .Cast<LinePatternElement>()
                .FirstOrDefault(item => string.Equals(
                    item.Name,
                    change.LinePatternName,
                    StringComparison.CurrentCultureIgnoreCase));
            if (pattern != null)
                return pattern.Id;
            error = $"Line pattern '{change.LinePatternName}' was not found.";
            return null;
        }

        error = "linePatternId or linePatternName is required when changing the line pattern.";
        return null;
    }

    private static void ApplyItem(CadOverridePlanItem item)
    {
        if (item.Change.HasVisible)
        {
            if (!item.SettingsView.CanCategoryBeHidden(item.Category.Id))
                throw new InvalidOperationException("The category cannot be hidden in this view.");
            item.SettingsView.SetCategoryHidden(item.Category.Id, !item.Change.Visible);
        }

        if (!item.Change.HasGraphicsChange)
            return;

        var overrides = item.Change.ClearGraphics
            ? new OverrideGraphicSettings()
            : item.SettingsView.GetCategoryOverrides(item.Category.Id);

        if (item.Change.HasHalftone)
            overrides.SetHalftone(item.Change.Halftone);

        if (item.Change.HasLineColor)
        {
            TryParseColor(item.Change.LineColor, out var color);
            overrides.SetProjectionLineColor(color!);
        }

        if (item.Change.HasLineWeight)
            overrides.SetProjectionLineWeight(item.Change.LineWeight);

        if (item.Change.HasLinePattern && item.ResolvedLinePatternId != null)
            overrides.SetProjectionLinePatternId(item.ResolvedLinePatternId);

        item.SettingsView.SetCategoryOverrides(item.Category.Id, overrides);
    }

    private static bool TryParseColor(string? value, out Autodesk.Revit.DB.Color? color)
    {
        color = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value!.Trim();
        if (text.StartsWith("#", StringComparison.Ordinal))
            text = text.Substring(1);
        if (text.Length != 6 ||
            !byte.TryParse(text.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var red) ||
            !byte.TryParse(text.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var green) ||
            !byte.TryParse(text.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var blue))
            return false;

        color = new Autodesk.Revit.DB.Color(red, green, blue);
        return true;
    }

    private static string DescribeSelector(CadOverrideChange change)
    {
        if (change.AllImports)
            return "all imports";
        if (change.ImportInstanceId > 0)
            return $"element {change.ImportInstanceId}";
        return change.ImportName;
    }

    private static string NormalizeName(string value) =>
        value.Normalize(NormalizationForm.FormKC).Trim();
}
