using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Parameters;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Parameters;

/// <summary>revit_bind_parameter_categories (#102): adds or removes categories on an existing parameter binding.</summary>
public sealed class BindParameterCategoriesTool : IRevitMcpTool
{
    public string Name => "revit_bind_parameter_categories";

    public string Description =>
        "Adds or removes categories on an existing project or shared parameter binding (BindingMap.ReInsert). Requires " +
        "approval. name or guid (revit_list_project_parameters), addCategories[], removeCategories[]. The binding kind " +
        "(instance/type) and group stay as they are. Refuses to remove the last category. Removing a category drops the " +
        "parameter's values on that category's elements.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Parameters;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;
        var args = request.Arguments;

        var name = ToolArguments.GetString(args, "name").Trim();
        var guid = ToolArguments.GetString(args, "guid").Trim();
        var add = ToolArguments.GetStringArray(args, "addCategories").Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        var remove = ToolArguments.GetStringArray(args, "removeCategories").Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        if (name.Length == 0 && guid.Length == 0) return Task.FromResult(Fail(request, "name or guid is required (revit_list_project_parameters)."));
        if (add.Count == 0 && remove.Count == 0) return Task.FromResult(Fail(request, "Pass addCategories and/or removeCategories."));

        var bindings = ParameterBindingReader.Read(doc);
        var row = ParameterBindingReader.Find(bindings, name, guid);
        if (row == null)
            return Task.FromResult(Fail(request, $"No parameter binding {(guid.Length > 0 ? $"with GUID {guid}" : $"named '{name}'")}. Use revit_list_project_parameters."));

        var resolvedAdd = new List<string>();
        var unknown = new List<string>();
        foreach (var category in add)
        {
            var (resolved, why) = ParameterBindingReader.ResolveCategory(doc, category);
            if (resolved == null) unknown.Add(why == null ? category : $"{category} ({why})");
            else resolvedAdd.Add(resolved.Name);
        }
        if (unknown.Count > 0)
            return Task.FromResult(Fail(request, $"Unknown or not bindable categor{(unknown.Count == 1 ? "y" : "ies")}: {string.Join(", ", unknown)}."));

        var change = ParameterCreatePlanner.PlanCategoryChange(row.Categories, resolvedAdd, remove);
        if (change.Error != null) return Task.FromResult(Fail(request, change.Error));
        var warnings = new List<string>();
        if (change.NotBound.Count > 0) warnings.Add($"Not bound, nothing to remove: {string.Join(", ", change.NotBound)}.");
        if (change.AlreadyBound.Count > 0) warnings.Add($"Already bound: {string.Join(", ", change.AlreadyBound)}.");

        if (!change.Changes)
        {
            sw.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"'{row.Name}' already has the requested categories; nothing changed.",
                Data = new { name = row.Name, guid = row.Guid, categories = row.Categories },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var transactions = ItemTransactions.Start(doc, "Revit MCP - Bind Parameter Categories", out var startError);
        if (transactions == null) return Task.FromResult(Fail(request, startError!));
        var error = transactions.Run($"Revit MCP - Bind {row.Name}", () =>
        {
            var set = ParameterBindingReader.CategorySet(doc, change.Categories);
            if (set.Size != change.Categories.Count) return "Some categories could not be resolved any more.";
            var binding = ParameterBindingReader.NewBinding(doc, row.IsInstance, set);
            return doc.ParameterBindings.ReInsert(row.Definition, binding) ? null : "Revit refused the new category set (ReInsert returned false).";
        });
        var groupError = transactions.Finish();
        warnings.AddRange(transactions.Warnings.Distinct().Select(w => "Revit: " + w));
        error ??= groupError;

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = error == null,
            Status = error == null ? null : "transaction_failed",
            Message = error == null
                ? $"'{row.Name}' is now bound to {change.Categories.Count} categor{(change.Categories.Count == 1 ? "y" : "ies")}" +
                  $"{(change.Added.Count > 0 ? $"; added {string.Join(", ", change.Added)}" : string.Empty)}" +
                  $"{(change.Removed.Count > 0 ? $"; removed {string.Join(", ", change.Removed)}" : string.Empty)}."
                : $"'{row.Name}' was not changed: {error}",
            Data = new
            {
                documentTitle = doc.Title,
                name = row.Name,
                guid = row.Guid,
                binding = row.IsInstance ? "instance" : "type",
                before = row.Categories,
                categories = error == null ? change.Categories : row.Categories,
                added = change.Added,
                removed = change.Removed
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Status = "validation_failed", Message = message };
}
