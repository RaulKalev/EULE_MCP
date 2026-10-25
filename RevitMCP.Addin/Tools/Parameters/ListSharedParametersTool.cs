using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Parameters;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Parameters;

/// <summary>revit_list_shared_parameters (#102): the definitions of the company shared parameter file.</summary>
public sealed class ListSharedParametersTool : IRevitMcpTool
{
    public string Name => "revit_list_shared_parameters";

    public string Description =>
        "Lists the definitions of a shared parameter file: groups, names, GUIDs, data types, descriptions, visible and " +
        "user-modifiable flags, and whether each is already bound in the document. The file is sharedParameterFile when " +
        "given, else parameters.sharedParameterFile from the project, user or company config, else Revit's own setting. " +
        "Filters: group, nameContains. Read-only: the file is never changed, and Revit's shared parameter file setting " +
        "is restored afterwards.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Parameters;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var args = request.Arguments;
        // The document only picks the project config and marks what is bound; the file can be listed without one.
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null && ToolArguments.GetString(args, "document").Trim().Length > 0) return Task.FromResult(failure!);
        var doc = target?.Document;

        var groupFilter = ToolArguments.GetString(args, "group").Trim();
        var nameContains = ToolArguments.GetString(args, "nameContains").Trim();
        var warnings = new List<string>();

        var choice = SharedParameterFileReader.Choose(uiapp.Application, doc, ToolArguments.GetString(args, "sharedParameterFile"));
        if (choice.Error != null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Status = "validation_failed", Message = choice.Error });

        var rows = SharedParameterFileReader.WithFile(uiapp.Application, choice.Path!, file => file == null ? null : SharedParameterFileReader.ReadDefinitions(file), warnings);
        if (rows == null)
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId, Success = false, Status = "validation_failed",
                Message = $"The shared parameter file '{choice.Path}' ({choice.Source}) does not exist or Revit cannot read it.",
                Warnings = warnings
            });

        var bound = doc == null
            ? new Dictionary<string, BindingRow>(StringComparer.OrdinalIgnoreCase)
            : ParameterBindingReader.Read(doc).Where(r => r.Guid != null).GroupBy(r => r.Guid!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var matching = rows
            .Where(r => groupFilter.Length == 0 || string.Equals(r.Info.Group, groupFilter, StringComparison.OrdinalIgnoreCase))
            .Where(r => nameContains.Length == 0 || r.Info.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(r => r.Info.Group, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Info.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groups = matching.GroupBy(r => r.Info.Group, StringComparer.OrdinalIgnoreCase).Select(g => new
        {
            group = g.Key,
            definitions = g.Select(r =>
            {
                var spec = ParameterRevitTypes.SpecOf(r.Info.DataTypeId);
                bound.TryGetValue(r.Info.Guid, out var binding);
                return new
                {
                    name = r.Info.Name,
                    guid = r.Info.Guid,
                    dataType = r.Info.DataTypeId,
                    dataTypeLabel = ParameterRevitTypes.SpecLabel(spec),
                    dataTypeName = ParameterRevitTypes.FriendlyOf(spec),
                    description = r.Description,
                    visible = r.Visible,
                    userModifiable = r.UserModifiable,
                    boundInDocument = binding != null,
                    binding = binding == null ? null : binding.IsInstance ? "instance" : "type",
                    boundCategories = binding?.Categories
                };
            }).ToList()
        }).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{matching.Count} of {rows.Count} definition(s) in {groups.Count} group(s) of '{choice.Path}' ({choice.Source})" +
                      $"{(doc != null ? $"; {matching.Count(r => bound.ContainsKey(r.Info.Guid))} already bound in '{doc.Title}'" : string.Empty)}.",
            Data = new
            {
                file = choice.Path,
                source = choice.Source,
                documentTitle = doc?.Title,
                total = rows.Count,
                returned = matching.Count,
                groups
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
