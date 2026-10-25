using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Model state (#109): phases, design options, phase writes, the model health audit (docs/model-state.md).</summary>
internal sealed partial class RevitMcpTools
{
    // Shared by the three query tools (revit_get_elements_info, revit_find_elements_by_parameter, revit_get_element_parameters).
    private const string IncludeModelStateDescription =
        "Add phaseCreated, phaseDemolished, designOption, workset (names; empty = none) to each element. Default false.";
    private const string IncludeOwnershipDescription =
        "Add ownedBy (who has the element checked out; slower, workshared models). Default false.";

    [McpServerTool(Name = "revit_list_phases", ReadOnly = true),
     Description("Lists the project's phases in sequence {id, name, sequenceNumber}, the views whose phase is each one, and the phase filters with their New/Existing/Demolished/Temporary presentation. countElements=true adds elements created/demolished per phase (one capped scan). Filter by phase with the pseudo-parameters Phase Created / Phase Demolished in revit_find_elements_by_parameter.")]
    public async Task<string> ListPhases(
        [Description("Count elements created and demolished per phase (one scan, capped). Default false.")] bool countElements = false,
        [Description("List the views that use each phase. Default true.")] bool includeViews = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["countElements"] = countElements,
            ["includeViews"] = includeViews,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_list_phases", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_list_design_options", ReadOnly = true),
     Description("Lists design option sets and options {setId, setName, options[] {id, name, isPrimary, elementCount?}}. Read-only by nature: the Revit API cannot move elements between options and views per option are not reported (design_option_set_only). Filter with the pseudo-parameter Design Option (empty = main model).")]
    public async Task<string> ListDesignOptions(
        [Description("Count the elements in each option. Default false.")] bool countElements = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["countElements"] = countElements,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_list_design_options", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_set_phase", ReadOnly = true),
     Description("Previews revit_set_phase without changing the model: per element {id, category, name, phaseCreated {before, after}, phaseDemolished {before, after}, status, reason} with status ready, unchanged, skipped (no phase parameters), read_only, phase_order_invalid or not_found. Unknown phases: phase_not_found with the phase list.")]
    public async Task<string> PreviewSetPhase(
        [Description("Element ids (max 2000).")] long[]? elementIds = null,
        [Description("Use the current selection instead of elementIds.")] bool useSelection = false,
        [Description("Phase Created: phase name or id. Empty keeps it.")] string? phaseCreated = null,
        [Description("Phase Demolished: phase name or id, or none to clear. Empty keeps it.")] string? phaseDemolished = null,
        [Description("true (default): any blocked element rejects the request; false: set the rest.")] bool atomic = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = SetPhaseArgs(elementIds, useSelection, phaseCreated, phaseDemolished, atomic, document);
        return FormatResult(await pipeClient.SendAsync("revit_preview_set_phase", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_set_phase"),
     Description("Sets Phase Created and/or Phase Demolished on elements (elementIds, max 2000, or useSelection). Requires approval. Phases by name or id; phaseDemolished=none clears it. Validates the order (demolished before created is phase_order_invalid; the same phase is Temporary), skips elements without phase parameters and reports read-only ones. One transaction, one undo. Preview first with revit_preview_set_phase.")]
    public async Task<string> SetPhase(
        [Description("Element ids (max 2000).")] long[]? elementIds = null,
        [Description("Use the current selection instead of elementIds.")] bool useSelection = false,
        [Description("Phase Created: phase name or id. Empty keeps it.")] string? phaseCreated = null,
        [Description("Phase Demolished: phase name or id, or none to clear. Empty keeps it.")] string? phaseDemolished = null,
        [Description("true (default): any blocked element rejects the request; false: set the rest.")] bool atomic = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = SetPhaseArgs(elementIds, useSelection, phaseCreated, phaseDemolished, atomic, document);
        return FormatResult(await pipeClient.SendAsync("revit_set_phase", args, cancellationToken));
    }

    private static Dictionary<string, object?> SetPhaseArgs(
        long[]? elementIds, bool useSelection, string? phaseCreated, string? phaseDemolished, bool atomic, string? document) => new()
    {
        ["elementIds"] = elementIds ?? [],
        ["useSelection"] = useSelection,
        ["phaseCreated"] = phaseCreated ?? string.Empty,
        ["phaseDemolished"] = phaseDemolished ?? string.Empty,
        ["atomic"] = atomic,
        ["document"] = document ?? string.Empty
    };

    [McpServerTool(Name = "revit_set_view_phase"),
     Description("Sets the Phase and/or Phase Filter of views. Requires approval. Views whose template controls the setting are reported as controlled_by_template; sheets and legends are skipped. phaseFilter=none clears the filter. One transaction, one undo. revit_list_phases lists phases and phase filters.")]
    public async Task<string> SetViewPhase(
        [Description("View ids (max 500).")] long[] viewIds,
        [Description("Phase name or id.")] string? phase = null,
        [Description("Phase filter name or id, or none.")] string? phaseFilter = null,
        [Description("true (default): any blocked view rejects the request; false: set the rest.")] bool atomic = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["viewIds"] = viewIds,
            ["phase"] = phase ?? string.Empty,
            ["phaseFilter"] = phaseFilter ?? string.Empty,
            ["atomic"] = atomic,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_set_view_phase", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_audit_model", ReadOnly = true),
     Description("Model health audit, published as a model page: summary (file size, elements per category), Revit warnings grouped, unused family types, views not on sheets / without template, sheets without views, unplaced / not enclosed / redundant rooms, CAD imports, in-place families, groups, phases, design options, worksets and elements owned by others (sampled), links (load status, path type, pinned), graph freshness, checkpoints and plans. Each figure is ok/warn/bad by thresholds (audit.thresholds in config). Run it before and after unattended work. The JSON mirrors the page.")]
    public async Task<string> AuditModel(
        [Description("Sections to run (default all): summary, warnings, types, views, sheets, rooms, imports, families, groups, phases, designOptions, worksets, links, graph.")] string[]? sections = null,
        [Description("Publish the report as a model page (same link per model). Default true.")] bool publish = true,
        [Description("Open the page in the browser. Default false.")] bool open = false,
        [Description("List rows per section in the JSON (default 20, max 200). The page always lists every row.")] int itemsPerSection = 20,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sections"] = sections ?? [],
            ["publish"] = publish,
            ["open"] = open,
            ["itemsPerSection"] = itemsPerSection,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_audit_model", args, cancellationToken));
    }
}
