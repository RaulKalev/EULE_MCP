using RevitMCP.Addin.Families;
using RevitMCP.Addin.Parameters;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Approval;

/// <summary>
/// Approval summaries of the families and parameters write tools (#102). ApprovalSummaryBuilder chains
/// <see cref="TryBuild"/> into its default case; revit_purge_unused and revit_create_parameters build theirs live
/// (IApprovalSummaryTool) and fall back to these when the live build fails.
/// </summary>
public static class FamiliesAndParametersApprovalSummaries
{
    public static string? TryBuild(McpToolRequest request) => request.ToolName switch
    {
        "revit_load_family" => LoadFamily(request),
        "revit_reload_family" => ReloadFamily(request),
        "revit_purge_unused" => PurgeUnused(request),
        "revit_create_parameters" => CreateParameters(request, null),
        "revit_bind_parameter_categories" => BindCategories(request),
        _ => null
    };

    private static string LoadFamily(McpToolRequest request)
    {
        var args = request.Arguments;
        var paths = ToolArguments.GetStringArray(args, "paths").Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var folder = ToolArguments.GetString(args, "folder").Trim();
        FamilyLoadPolicy.TryParseIfExists(ToolArguments.GetString(args, "ifExists"), FamilyIfExists.Skip, out var policy, out _);
        var what = new List<string>();
        if (paths.Count > 0)
            what.Add($"{paths.Count} family file(s): {string.Join(", ", paths.Take(5).Select(FileName))}{(paths.Count > 5 ? ", …" : string.Empty)}");
        if (folder.Length > 0)
        {
            var pattern = ToolArguments.GetString(args, "pattern").Trim();
            what.Add($"the {(pattern.Length > 0 ? pattern : FamilyLoadPolicy.DefaultPattern)} files of '{folder}'{(ToolArguments.GetBool(args, "recursive") ? " and its subfolders" : string.Empty)}");
        }
        var existing = policy switch
        {
            FamilyIfExists.Skip => "families already loaded are skipped",
            FamilyIfExists.Overwrite => "families already loaded are overwritten (their parameter values kept)",
            FamilyIfExists.OverwriteParameters => "families already loaded are overwritten with the file's parameter values",
            _ => "families already loaded, and their shared nested families, are overwritten with the file's version and values"
        };
        return $"Load {string.Join(" and ", what.DefaultIfEmpty("family files"))}{DocumentSuffix(request)}; {existing}.";
    }

    private static string ReloadFamily(McpToolRequest request)
    {
        var args = request.Arguments;
        var path = ToolArguments.GetString(args, "path").Trim();
        FamilyLoadPolicy.TryParseIfExists(ToolArguments.GetString(args, "ifExists"), FamilyIfExists.Overwrite, out var policy, out _);
        return $"Reload family '{ToolArguments.GetString(args, "family").Trim()}'{DocumentSuffix(request)} from " +
               $"{(path.Length > 0 ? $"'{path}'" : "the file it was last loaded from")} (ifExists={FamilyLoadPolicy.Describe(policy)}" +
               $"{(policy == FamilyIfExists.Overwrite ? ", the project's parameter values kept" : ", the file's parameter values written")}).";
    }

    private static string PurgeUnused(McpToolRequest request)
    {
        var ids = ToolArguments.GetLongArray(request.Arguments, "elementIds").Distinct().Count();
        var categories = ToolArguments.GetStringArray(request.Arguments, "categories").Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        var scope = categories.Count > 0 ? $" in {string.Join(", ", categories)}" : string.Empty;
        return ids > 0
            ? $"DESTRUCTIVE: purge up to {ids} unused family type(s), families and elements listed by the preview{scope}{DocumentSuffix(request)}. Manual approval required."
            : $"DESTRUCTIVE: purge the unused family types and families{scope}{(ToolArguments.GetBool(request.Arguments, "includeOther") ? " and other unused elements" : string.Empty)}{DocumentSuffix(request)}. Manual approval required.";
    }

    /// <summary>
    /// "Create N project parameter(s) from the shared file '&lt;file&gt;': &lt;name&gt; (&lt;type&gt;, instance, 3 categories), …".
    /// <paramref name="sharedFile"/> is the file the call will use when the live summary knows it.
    /// </summary>
    public static string CreateParameters(McpToolRequest request, string? sharedFile)
    {
        request.Arguments.TryGetValue("parameters", out var raw);
        var items = ParameterCreatePlanner.Parse(raw, out _);
        var shared = items.Where(i => i.IsShared).ToList();
        var project = items.Where(i => !i.IsShared).ToList();
        var parts = new List<string>();
        if (shared.Count > 0)
            parts.Add($"Create {shared.Count} project parameter(s) from the shared file{(sharedFile != null ? $" '{System.IO.Path.GetFileName(sharedFile)}'" : string.Empty)}: " +
                      string.Join(", ", shared.Take(8).Select(Describe)) + (shared.Count > 8 ? ", …" : string.Empty));
        if (project.Count > 0)
            parts.Add($"Create {project.Count} project parameter(s) as definitions of the connector's scratch file (source=project): " +
                      string.Join(", ", project.Take(8).Select(Describe)) + (project.Count > 8 ? ", …" : string.Empty));
        var text = string.Join(". ", parts.DefaultIfEmpty("Create project parameters")) + DocumentSuffix(request) + ".";
        if (items.Any(i => i.Rebind)) text += " Existing bindings marked ifExists=rebind get the extra categories.";
        if (ToolArguments.GetBool(request.Arguments, "createInFileIfMissing") && shared.Count > 0)
            text += " createInFileIfMissing=true: missing definitions are ADDED to the shared parameter file.";
        return text;
    }

    private static string BindCategories(McpToolRequest request)
    {
        var args = request.Arguments;
        var name = ToolArguments.GetString(args, "name").Trim();
        var guid = ToolArguments.GetString(args, "guid").Trim();
        var add = ToolArguments.GetStringArray(args, "addCategories").Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        var remove = ToolArguments.GetStringArray(args, "removeCategories").Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        var changes = new List<string>();
        if (add.Count > 0) changes.Add($"add {string.Join(", ", add)}");
        if (remove.Count > 0) changes.Add($"remove {string.Join(", ", remove)} (values on those elements are dropped)");
        return $"Change the categories of parameter '{(name.Length > 0 ? name : guid)}'{DocumentSuffix(request)}: {string.Join("; ", changes)}.";
    }

    private static string Describe(ParameterRequest item)
    {
        var type = item.DataType.Length > 0 ? item.DataType : "type from the file";
        return $"{item.Name} ({type}, {(item.IsInstance ? "instance" : "type")}, {item.Categories.Count} categor{(item.Categories.Count == 1 ? "y" : "ies")})";
    }

    private static string FileName(string path)
    {
        try { return System.IO.Path.GetFileName(path.Trim()); }
        catch { return path; }
    }

    private static string DocumentSuffix(McpToolRequest request)
        => ToolArguments.GetString(request.Arguments, "document") is { Length: > 0 } doc ? $" in '{doc}'" : string.Empty;
}
