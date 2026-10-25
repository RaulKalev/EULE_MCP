using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Tools.Families;

/// <summary>revit_list_families (#102): loaded families per category with type and instance counts.</summary>
public sealed class ListFamiliesTool : IRevitMcpTool
{
    public string Name => "revit_list_families";

    public string Description =>
        "Lists the families of the document: name, category, kind (loadable | system | inPlace), type count, instance " +
        "count, whether it is editable, and the file it was last loaded from by revit_load_family when known. Filters: " +
        "category, nameContains, kind, unusedOnly (no placed instance). includeTypes adds each family's types with their " +
        "ids and instance counts. Paged: pageSize, page. Read-only.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;
        var args = request.Arguments;

        var categoryName = ToolArguments.GetString(args, "category").Trim();
        var nameContains = ToolArguments.GetString(args, "nameContains").Trim();
        var kind = ToolArguments.GetString(args, "kind", "all").Trim();
        var unusedOnly = ToolArguments.GetBool(args, "unusedOnly");
        var includeTypes = ToolArguments.GetBool(args, "includeTypes");
        var pageSize = QueryGuard.NormalizePageSize(ToolArguments.GetInt(args, "pageSize", 0) is var ps and > 0 ? ps : null);
        var page = Math.Max(1, ToolArguments.GetInt(args, "page", 1));
        var warnings = new List<string>();

        var kinds = new[] { "all", "loadable", "system", "inPlace" };
        var kindFilter = kinds.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase));
        if (kindFilter == null)
            return Task.FromResult(Fail(request, $"kind '{kind}' is not one of {string.Join(", ", kinds)}."));

        string? category = null;
        if (categoryName.Length > 0)
        {
            var resolved = new CategoryResolver().Resolve(doc, categoryName);
            if (resolved.Category == null)
                return Task.FromResult(Fail(request, resolved.Message +
                    (resolved.Suggestions.Count > 0 ? $" Did you mean: {string.Join(", ", resolved.Suggestions)}?" : string.Empty)));
            category = resolved.Category.Name;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var counts = FamilyTypeSupport.CountInstancesByType(doc);
        var includeSystem = kindFilter is "all" or "system";
        var rows = FamilyInventory.Rows(doc, counts, includeSystem)
            .Where(r => kindFilter == "all" || string.Equals(r.Kind, kindFilter, StringComparison.OrdinalIgnoreCase))
            .Where(r => category == null || string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase))
            .Where(r => nameContains.Length == 0 || r.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
            .Where(r => !unusedOnly || r.InstanceCount == 0)
            .OrderBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        IReadOnlyDictionary<string, FamilyPathEntry> paths = new Dictionary<string, FamilyPathEntry>();
        try { paths = FamilyPathStore.Default().All(CheckpointChangeTracker.ModelIdOf(doc)); }
        catch (Exception ex) { warnings.Add($"The family path store could not be read: {ex.Message}"); }

        var pageRows = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        if (rows.Count > page * pageSize)
            warnings.Add($"Page {page} of {(rows.Count + pageSize - 1) / pageSize}: pass page={page + 1} for the next {Math.Min(pageSize, rows.Count - page * pageSize)} famil{(rows.Count - page * pageSize == 1 ? "y" : "ies")}.");

        var families = pageRows.Select(r => new
        {
            familyId = r.FamilyId,
            name = r.Name,
            category = r.Category,
            kind = r.Kind,
            typeCount = r.Types.Count,
            instanceCount = r.InstanceCount,
            unused = r.InstanceCount == 0,
            isEditable = r.IsEditable,
            isUserCreated = r.IsUserCreated,
            sourcePath = r.Kind == "loadable" && paths.TryGetValue(r.Name, out var entry) ? entry.Path : null,
            types = includeTypes ? r.Types.Select(t => new { typeId = t.Id, name = t.Name, instanceCount = t.Instances }).ToList() : null
        }).ToList();

        var byKind = rows.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count());
        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{rows.Count} famil{(rows.Count == 1 ? "y" : "ies")} match ({string.Join(", ", byKind.Select(k => $"{k.Value} {k.Key}"))}); " +
                      $"showing {families.Count} on page {page}.",
            Data = new
            {
                documentTitle = doc.Title,
                total = rows.Count,
                page,
                pageSize,
                byKind,
                families,
                note = "sourcePath is the file revit_load_family last loaded the family from (Revit does not store it); " +
                       "revit_reload_family uses it when no path is passed."
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Status = "validation_failed", Message = message };
}
