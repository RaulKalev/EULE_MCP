using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ModelState;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelState;

/// <summary>revit_list_phases (#109): phases in sequence, the views using them, phase filters, optional element counts.</summary>
public sealed class ListPhasesTool : IRevitMcpTool
{
    public string Name => "revit_list_phases";
    public string Description =>
        "Lists the project's phases in sequence {id, name, sequenceNumber}, the views whose phase is each one, and the phase " +
        "filters with their New/Existing/Demolished/Temporary presentation and how many views use them. countElements=true " +
        "adds elements created and demolished per phase (one scan, capped). Filter elements by phase with the pseudo-parameters " +
        "Phase Created / Phase Demolished in revit_find_elements_by_parameter; write them with revit_set_phase.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;

        var countElements = ToolArguments.GetBool(request.Arguments, "countElements", false);
        var includeViews = ToolArguments.GetBool(request.Arguments, "includeViews", true);
        var listing = PhaseReader.Read(doc, countElements, PhaseReader.DefaultScanCap, cancellationToken);

        var warnings = new List<string>();
        if (listing.CountNote != null) warnings.Add(listing.CountNote);
        if (includeViews && listing.Phases.Any(p => p.ViewCount > PhaseReader.MaxViewsPerPhase))
            warnings.Add($"Views are listed up to {PhaseReader.MaxViewsPerPhase} per phase; viewCount has the full number.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{listing.Phases.Count} phase(s) and {listing.PhaseFilters.Count} phase filter(s) in '{doc.Title}'" +
                      (listing.Counted ? $"; counted {listing.Scanned} element(s)." : "."),
            Data = new
            {
                document = doc.Title,
                phases = listing.Phases.Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    sequenceNumber = p.SequenceNumber,
                    viewCount = p.ViewCount,
                    viewsUsingAsPhase = includeViews
                        ? p.ViewsUsingAsPhase.Select(v => new { id = v.Id, name = v.Name, viewType = v.ViewType }).ToList()
                        : null,
                    elementCounts = listing.Counted ? new { created = p.Created ?? 0, demolished = p.Demolished ?? 0 } : null
                }).ToList(),
                phaseFilters = listing.PhaseFilters.Select(f => new
                {
                    id = f.Id,
                    name = f.Name,
                    newStatus = f.NewStatus,
                    existingStatus = f.ExistingStatus,
                    demolishedStatus = f.DemolishedStatus,
                    temporaryStatus = f.TemporaryStatus,
                    viewCount = f.ViewCount
                }).ToList(),
                countElements,
                scanned = listing.Counted || listing.CountNote != null ? listing.Scanned : (int?)null
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>revit_list_design_options (#109): option sets, options, primary flag, optional element counts.</summary>
public sealed class ListDesignOptionsTool : IRevitMcpTool
{
    public string Name => "revit_list_design_options";
    public string Description =>
        "Lists design option sets and their options {setId, setName, options[] {id, name, isPrimary, elementCount?}}; " +
        "countElements=true counts the elements in each option. Read-only by nature: the API cannot move elements into or " +
        "out of an option, and the views dedicated to an option are not reported (status design_option_set_only). Filter " +
        "elements with the pseudo-parameter Design Option (empty = main model) in revit_find_elements_by_parameter.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;

        var countElements = ToolArguments.GetBool(request.Arguments, "countElements", false);
        var sets = DesignOptionReader.Read(doc, countElements, cancellationToken);
        var options = sets.Sum(s => s.Options.Count);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = sets.Count == 0
                ? $"'{doc.Title}' has no design options."
                : $"{sets.Count} design option set(s) with {options} option(s) in '{doc.Title}'.",
            Data = new
            {
                document = doc.Title,
                status = DesignOptionReader.SetOnlyStatus,
                note = DesignOptionReader.SetOnlyNote,
                sets = sets.Select(s => new
                {
                    setId = s.SetId,
                    setName = s.SetName,
                    options = s.Options.Select(o => new { id = o.Id, name = o.Name, isPrimary = o.IsPrimary, elementCount = o.ElementCount }).ToList()
                }).ToList()
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
