namespace RevitMCP.Bridge;

/// <summary>
/// Explicit tool grouping for the MCP surface (#66). Every bridge tool belongs to exactly one group;
/// profiles are built from groups. Assignment is an exact-name table for tools whose name does not
/// say where they belong, then ordered name rules. Pure — unit tested in RevitMCP.Tests, including
/// "every tool has a group" and "the full profile loses nothing".
/// </summary>
public static class BridgeToolGroups
{
    public const string Core = "core";
    public const string Discovery = "discovery";
    public const string Graph = "graph";

    /// <summary>Group name → one-line description (also shown by revit_tools_search).</summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [Core] = "Connection, instance routing, selection, live reads by element id, memory recall and remember",
        [Discovery] = "Search, describe, load and call any connector tool",
        [Graph] = "Model graph: build, status, summary, query, route (cheap id-first discovery)",
        ["query"] = "Broad element queries, parameter filters, grouping, query presets, parameter QA",
        ["edit"] = "Generic model edits: parameters, move/align/rotate/elevation, delete/duplicate/rename, family types, placement",
        ["devices"] = "Room geometry, device codes, room-based device placement and device/fire-alarm audits",
        ["electrical"] = "Circuits, panels, wires/cables, voltage drop, fire alarm circuits, patch panels, cable tray and conduit runs (containment)",
        ["panelschematics"] = "PanelSchematics plugin (Kilbiskeemid): panel tables, empty rows, circuit row parameters, its Extensible Storage data",
        ["ifc"] = "IFC links, IFC spaces to rooms, linked-model element queries",
        ["views"] = "Views, sheets, schedules, title blocks, revisions, view templates",
        ["tags"] = "Tags, dimensions, text notes, detail lines, view alignment",
        ["coordination"] = "Clash detection, clash review, issue reports",
        ["cad"] = "DWG/CAD imports, overrides and placement from CAD",
        ["pages"] = "Model pages: publish a generated HTML report as a local page whose rows select elements in Revit, show live parameter values and can be edited",
        ["memory"] = "Shared memory: list, review page, forget, weekly consolidation (recall and remember are in core)",
        ["documents"] = "Open documents: open your own local copy, close, activate, worksets, ownership/checkout/relinquish, reload latest, reload/remove DWG/IFC/RVT links, link visibility, save and sync with central",
        ["autonomy"] = "Checkpoints, Revit model warnings, what changed since a checkpoint (change diff) and rollback; plans (one approval for several write steps); jobs the connector runs by itself",
        ["skills"] = "Skill builder and skill runs",
        ["office"] = "Configuration, files, Excel, standards lookup, delivery checks, monthly usage report"
    };

    private static readonly Dictionary<string, string> Exact = Build(
        (Core, new[]
        {
            "revit_get_connection_status", "revit_list_instances", "revit_select_instance", "revit_get_selected_elements",
            "revit_inspect_selected_elements", "revit_get_elements_info", "revit_get_element_parameters", "revit_count_elements",
            "revit_get_approval_status", "revit_list_open_documents", "revit_memory_recall", "revit_memory_remember"
        }),
        (Discovery, new[] { "revit_tools_search", "revit_tools_describe", "revit_tools_call", "revit_tools_load" }),
        ("devices", new[]
        {
            "revit_list_levels", "revit_get_room_geometry", "revit_get_room_walls", "revit_get_device_codes", "revit_set_device_codes",
            "revit_preview_ensure_device_types", "revit_ensure_device_types", "revit_preview_place_at_wall", "revit_place_at_wall",
            "revit_preview_place_in_room", "revit_place_in_room", "revit_check_devices_per_room", "revit_check_device_alignment",
            "revit_check_coverage", "revit_check_fire_alarm", "revit_preview_assign_room_to_elements", "revit_assign_room_to_elements",
            "revit_get_linked_elements_in_room", "revit_export_view_image", "revit_get_element_rooms", "revit_get_room_boundaries"
        }),
        ("edit", new[]
        {
            "revit_preview_rotate_elements", "revit_rotate_elements", "revit_preview_set_elevation", "revit_set_elevation",
            "revit_preview_move_elements", "revit_move_elements", "revit_preview_copy_elements", "revit_copy_elements",
            "revit_preview_place_on_face", "revit_place_on_face",
            "revit_preview_align_elements", "revit_align_elements",
            "revit_preview_delete", "revit_delete", "revit_preview_duplicate", "revit_duplicate", "revit_preview_rename", "revit_rename",
            "revit_set_parameter", "revit_set_parameters_bulk", "revit_place_family_instances", "revit_preview_edit_family_types",
            "revit_edit_family_types", "revit_list_family_types", "revit_create_lines", "revit_list_extensible_storage_schemas",
            "revit_read_extensible_storage"
        }),
        ("query", new[]
        {
            "revit_find_elements_by_parameter", "revit_group_by_parameter", "revit_group_elements", "revit_get_available_parameters",
            "revit_list_query_presets", "revit_run_query_preset", "revit_select_elements", "revit_select_elements_by_query",
            "revit_check_parameter_completeness", "revit_list_parameter_qa_rule_sets", "revit_run_parameter_qa_rule_set",
            "revit_export_query_to_excel", "revit_export_list_to_excel"
        }),
        ("tags", new[] { "revit_create_text_notes", "revit_get_text_notes", "revit_preview_set_text_notes", "revit_set_text_notes", "revit_annotate_detail_lines", "revit_preview_align_in_view", "revit_align_in_view" }),
        ("ifc", new[] { "revit_query_linked_elements", "revit_select_linked_elements" }),
        ("cad", new[] { "revit_create_panel_schematic_symbol_from_dwg" }),
        ("documents", new[]
        {
            "revit_activate_document", "revit_preview_reload_links_from", "revit_reload_links_from", "revit_preview_remove_links",
            "revit_remove_links", "revit_preview_set_link_visibility", "revit_set_link_visibility", "revit_save_document",
            "revit_sync_with_central",
            "revit_open_document", "revit_close_document", "revit_list_worksets", "revit_preview_set_workset", "revit_set_workset",
            "revit_get_ownership", "revit_checkout_elements", "revit_relinquish", "revit_reload_latest"
        }),
        ("pages", new[] { "revit_publish_page", "revit_list_pages", "revit_remove_page" }),
        ("autonomy", new[]
        {
            "revit_get_warnings", "revit_checkpoint_create", "revit_checkpoint_diff", "revit_checkpoint_list", "revit_checkpoint_remove",
            "revit_preview_checkpoint_rollback", "revit_checkpoint_rollback",
            "revit_plan_submit", "revit_plan_status", "revit_plan_close", "revit_plan_use",
            "revit_job_submit", "revit_job_status", "revit_job_list", "revit_job_remove", "revit_job_cancel", "revit_job_resume", "revit_job_report"
        }),
        ("memory", new[] { "revit_memory_list", "revit_memory_page", "revit_memory_forget", "revit_memory_consolidate" }),
        // MEP routing (#106): "preview" contains "view", and conduit/mep names match no electrical keyword.
        ("electrical", new[]
        {
            "revit_list_mep_curve_types", "revit_preview_create_mep_run", "revit_create_mep_run", "revit_connect_mep_curves",
            "revit_get_mep_run_summary"
        }),
        ("office", new[] { "revit_usage_report" }),
        ("coordination", new[] { "revit_export_issues", "revit_merge_issue_reports" }));

    /// <summary>Ordered rules for everything not in the exact table: the first keyword found in the name wins.</summary>
    private static readonly (string Keyword, string Group)[] Rules =
    {
        ("revit_graph_", Graph),
        // Before the electrical rules: these names contain "panel" and "circuit".
        ("revit_panelschematics_", "panelschematics"),
        ("config_", "office"), ("file_", "office"), ("excel_", "office"), ("standards_", "office"), ("delivery_", "office"),
        ("skill", "skills"),
        ("clash", "coordination"),
        ("ifc", "ifc"),
        ("cad", "cad"),
        // Electrical before tags: "voltage" contains "tag".
        ("circuit", "electrical"), ("panel", "electrical"), ("wire", "electrical"), ("cable", "electrical"),
        ("voltage", "electrical"), ("fire_alarm", "electrical"), ("electrical", "electrical"), ("data_device", "electrical"),
        ("tag", "tags"), ("dimension", "tags"),
        ("view", "views"), ("sheet", "views"), ("schedule", "views"), ("titleblock", "views"), ("revision", "views")
    };

    /// <summary>The group of a tool, or null when no rule covers it (the unit test forbids that).</summary>
    public static string? GroupOf(string toolName)
    {
        if (Exact.TryGetValue(toolName, out var g)) return g;
        foreach (var (keyword, group) in Rules)
            if (toolName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return group;
        return null;
    }

    public static bool IsKnownGroup(string group) => Descriptions.ContainsKey(group);

    /// <summary>Groups always present in the core profile.</summary>
    public static readonly string[] CoreProfileGroups = [Core, Discovery, Graph];

    /// <summary>
    /// Tools of a group-based profile: the core groups plus <paramref name="extraGroups"/>.
    /// Unknown group names are returned in <paramref name="unknown"/>.
    /// </summary>
    public static List<string> SelectByGroups(IEnumerable<string> allTools, IEnumerable<string> extraGroups, out List<string> unknown)
    {
        var groups = new HashSet<string>(CoreProfileGroups, StringComparer.OrdinalIgnoreCase);
        unknown = [];
        foreach (var g in extraGroups.Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            if (g.Equals("all", StringComparison.OrdinalIgnoreCase)) { groups.UnionWith(Descriptions.Keys); continue; }
            if (IsKnownGroup(g)) groups.Add(g); else unknown.Add(g);
        }
        return allTools.Where(t => GroupOf(t) is { } grp && groups.Contains(grp)).ToList();
    }

    private static Dictionary<string, string> Build(params (string Group, string[] Names)[] entries)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (group, names) in entries)
            foreach (var n in names)
                d[n] = group;
        return d;
    }
}
