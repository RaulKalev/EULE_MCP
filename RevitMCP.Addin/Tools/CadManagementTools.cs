using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.CadManagement;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public sealed class ListCadImportsTool : IRevitMcpTool
{
    public string Name => "revit_list_cad_imports";
    public string Description =>
        "Lists imported/linked CAD instances and their layer visibility, halftone, projection line color, " +
        "line weight, and line pattern in a view. Defaults to the active view. When useViewTemplate=true " +
        "(default), settings controlled by the assigned template are read from that template. The returned " +
        "presetChanges array can be saved and passed to revit_preview_set_cad_overrides / revit_set_cad_overrides.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null || uidoc.ActiveView == null)
            return Task.FromResult(Fail(request, "No active document or view."));

        var warnings = new List<string>();
        var viewId = ToolArguments.GetLong(request.Arguments, "viewId");
        var includeLayers = ToolArguments.GetBool(request.Arguments, "includeLayers", true);
        var useViewTemplate = ToolArguments.GetBool(request.Arguments, "useViewTemplate", true);
        var importNameFilter = ToolArguments.GetString(request.Arguments, "importNameFilter");
        var limit = Math.Max(0, ToolArguments.GetInt(request.Arguments, "limit", 0));
        var views = CadOverrideSupport.ResolveViews(
            uidoc.Document, uidoc.ActiveView, viewId, Array.Empty<long>(), true, warnings);
        if (views.Count == 0)
            return Task.FromResult(Fail(request, warnings.FirstOrDefault() ?? "View was not found."));

        var view = views[0];
        var settingsView = CadOverrideSupport.ResolveSettingsView(uidoc.Document, view, useViewTemplate);
        var snapshots = CadOverrideSupport.Capture(
            uidoc.Document, view, settingsView, includeLayers, importNameFilter, limit);
        var portable = CadOverrideSupport.ToPortableChanges(snapshots);

        var imports = snapshots
            .GroupBy(snapshot => new
            {
                snapshot.ImportInstanceId,
                snapshot.ImportName
            })
            .Select(group =>
            {
                var root = group.FirstOrDefault(item => item.LayerName == null);
                return new
                {
                    importInstanceId = group.Key.ImportInstanceId,
                    importName = group.Key.ImportName,
                    categoryId = root?.CategoryId,
                    visible = root?.Visible,
                    halftone = root?.Halftone,
                    lineColor = root?.LineColor,
                    lineWeight = root?.LineWeight,
                    linePatternId = root?.LinePatternId,
                    linePatternName = root?.LinePatternName,
                    layers = group.Where(item => item.LayerName != null).Select(item => new
                    {
                        layerName = item.LayerName,
                        categoryId = item.CategoryId,
                        visible = item.Visible,
                        halftone = item.Halftone,
                        lineColor = item.LineColor,
                        lineWeight = item.LineWeight,
                        linePatternId = item.LinePatternId,
                        linePatternName = item.LinePatternName
                    }).ToList()
                };
            })
            .ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Returned {imports.Count} CAD import instance(s) and {snapshots.Count} category setting(s).",
            Data = new
            {
                viewId = view.Id.Value,
                viewName = view.Name,
                settingsViewId = settingsView.Id.Value,
                settingsViewName = settingsView.Name,
                settingsOwner = settingsView.IsTemplate ? "ViewTemplate" : "View",
                imports,
                presetChanges = portable.Select(CadOverrideSupport.ToPortableChangeResult).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

public sealed class PreviewSetCadOverridesTool : IRevitMcpTool
{
    public string Name => "revit_preview_set_cad_overrides";
    public string Description =>
        "Previews CAD import/layer visibility and graphic override changes without modifying the model. " +
        "Provide changes (array), optional viewId/viewIds, and useViewTemplate. Each change selects an import " +
        "by importInstanceId, importName, or allImports=true; layerName omitted targets the import, '*' targets all layers.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(CadSetToolExecutor.Execute(uiapp, request, false, cancellationToken));
    }
}

public sealed class SetCadOverridesTool : IRevitMcpTool
{
    public string Name => "revit_set_cad_overrides";
    public string Description =>
        "Sets CAD import/layer visibility, halftone, projection line color (#RRGGBB), line weight (1-16), " +
        "and line pattern in one safe transaction. Requires approval. Provide the same arguments reviewed by " +
        "revit_preview_set_cad_overrides. clearGraphics=true resets graphic overrides before applying supplied values. " +
        "useViewTemplate=true (default) modifies an assigned template, which can affect every view using it.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(CadSetToolExecutor.Execute(uiapp, request, true, cancellationToken));
    }
}

public sealed class PreviewCopyCadOverridesTool : IRevitMcpTool
{
    public string Name => "revit_preview_copy_cad_overrides";
    public string Description =>
        "Previews copying all CAD import and layer visibility/graphic settings from a source view to target views. " +
        "Matches CAD imports and layers by normalized name and reports the actual view/template owners that would change.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(CadCopyToolExecutor.Execute(uiapp, request, false, cancellationToken));
    }
}

public sealed class CopyCadOverridesTool : IRevitMcpTool
{
    public string Name => "revit_copy_cad_overrides";
    public string Description =>
        "Copies all CAD import and layer visibility/graphic settings from a source view to target views. " +
        "Requires approval and should follow revit_preview_copy_cad_overrides. Imports/layers are matched by normalized name. " +
        "useTargetViewTemplates=true (default) modifies assigned templates and can affect every view using those templates.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(CadCopyToolExecutor.Execute(uiapp, request, true, cancellationToken));
    }
}

internal static class CadSetToolExecutor
{
    public static McpToolResult Execute(
        UIApplication uiapp,
        McpToolRequest request,
        bool apply,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null || uidoc.ActiveView == null)
            return Fail(request, "No active document or view.");

        var warnings = new List<string>();
        var errors = new List<string>();
        var changes = CadOverrideSupport.ParseChanges(request.Arguments, errors);
        if (errors.Count > 0)
            return Fail(request, string.Join(" ", errors));

        var viewId = ToolArguments.GetLong(request.Arguments, "viewId");
        var viewIds = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var useViewTemplate = ToolArguments.GetBool(request.Arguments, "useViewTemplate", true);
        var views = CadOverrideSupport.ResolveViews(
            uidoc.Document, uidoc.ActiveView, viewId, viewIds, true, warnings);
        if (views.Count == 0)
            return Fail(request, warnings.FirstOrDefault() ?? "No target views were found.");

        var plan = CadOverrideSupport.BuildPlan(
            uidoc.Document, views, changes, useViewTemplate, warnings);
        if (plan.Count == 0)
            return Fail(request, "No applicable CAD categories were found. " + string.Join(" ", warnings));

        if (!apply)
        {
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Previewed {plan.Count} CAD category change(s) across {views.Count} requested view(s).",
                Data = new
                {
                    requestedViews = views.Count,
                    uniqueSettingsOwners = plan.Select(item => item.SettingsView.Id.Value).Distinct().Count(),
                    plannedChanges = plan.Select(CadOverrideSupport.ToPlanResult).ToList()
                },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<object>();
        var transaction = CadOverrideSupport.ApplyPlan(
            uidoc.Document, plan, cancellationToken, warnings, results);
        sw.Stop();

        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = transaction.Success && results.Count > 0,
            Message = transaction.Success
                ? $"Applied {results.Count}/{plan.Count} CAD category change(s)."
                : $"CAD override transaction failed: {transaction.Diagnostics.OriginalError ?? "unknown transaction failure"}",
            Data = new
            {
                planned = plan.Count,
                applied = results.Count,
                failed = plan.Count - results.Count,
                results,
                transaction = transaction.Diagnostics
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

internal static class CadCopyToolExecutor
{
    public static McpToolResult Execute(
        UIApplication uiapp,
        McpToolRequest request,
        bool apply,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null || uidoc.ActiveView == null)
            return Fail(request, "No active document or view.");

        var warnings = new List<string>();
        var sourceViewId = ToolArguments.GetLong(request.Arguments, "sourceViewId");
        var targetViewIds = ToolArguments.GetLongArray(request.Arguments, "targetViewIds");
        var includeLayers = ToolArguments.GetBool(request.Arguments, "includeLayers", true);
        var useSourceViewTemplate = ToolArguments.GetBool(request.Arguments, "useSourceViewTemplate", true);
        var useTargetViewTemplates = ToolArguments.GetBool(request.Arguments, "useTargetViewTemplates", true);
        var importNameFilter = ToolArguments.GetString(request.Arguments, "importNameFilter");

        var sourceViews = CadOverrideSupport.ResolveViews(
            uidoc.Document, uidoc.ActiveView, sourceViewId, Array.Empty<long>(), true, warnings);
        if (sourceViews.Count == 0)
            return Fail(request, warnings.FirstOrDefault() ?? "Source view was not found.");
        if (targetViewIds.Length == 0)
            return Fail(request, "targetViewIds is required.");

        var sourceView = sourceViews[0];
        var targetViews = CadOverrideSupport.ResolveViews(
                uidoc.Document, uidoc.ActiveView, 0, targetViewIds, false, warnings)
            .Where(view => view.Id != sourceView.Id)
            .ToList();
        if (targetViews.Count == 0)
            return Fail(request, "No valid target views were found.");

        var sourceSettingsView = CadOverrideSupport.ResolveSettingsView(
            uidoc.Document, sourceView, useSourceViewTemplate);
        var snapshots = CadOverrideSupport.Capture(
            uidoc.Document,
            sourceView,
            sourceSettingsView,
            includeLayers,
            importNameFilter,
            0);
        if (snapshots.Count == 0)
            return Fail(request, $"No CAD imports were found in source view '{sourceView.Name}'.");

        var changes = CadOverrideSupport.ToPortableChanges(snapshots);
        var plan = CadOverrideSupport.BuildPlan(
            uidoc.Document, targetViews, changes, useTargetViewTemplates, warnings);
        if (plan.Count == 0)
            return Fail(request, "No matching CAD imports/layers were found in the target views. " +
                                 string.Join(" ", warnings));

        if (!apply)
        {
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Previewed {plan.Count} copied CAD category setting(s) for {targetViews.Count} target view(s).",
                Data = new
                {
                    sourceViewId = sourceView.Id.Value,
                    sourceViewName = sourceView.Name,
                    sourceSettingsViewId = sourceSettingsView.Id.Value,
                    sourceSettingsViewName = sourceSettingsView.Name,
                    capturedSettings = changes.Count,
                    targetViews = targetViews.Count,
                    uniqueSettingsOwners = plan.Select(item => item.SettingsView.Id.Value).Distinct().Count(),
                    plannedChanges = plan.Select(CadOverrideSupport.ToPlanResult).ToList()
                },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<object>();
        var transaction = CadOverrideSupport.ApplyPlan(
            uidoc.Document, plan, cancellationToken, warnings, results);
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = transaction.Success && results.Count > 0,
            Message = transaction.Success
                ? $"Copied {results.Count}/{plan.Count} CAD category setting(s) from '{sourceView.Name}'."
                : $"CAD override copy failed: {transaction.Diagnostics.OriginalError ?? "unknown transaction failure"}",
            Data = new
            {
                sourceViewId = sourceView.Id.Value,
                sourceViewName = sourceView.Name,
                planned = plan.Count,
                applied = results.Count,
                failed = plan.Count - results.Count,
                results,
                transaction = transaction.Diagnostics
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}
