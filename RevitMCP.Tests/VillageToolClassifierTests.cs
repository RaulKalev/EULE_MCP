using RevitMCP.Addin.Village;
using Xunit;

namespace RevitMCP.Tests;

public class VillageToolClassifierTests
{
    /// <summary>
    /// Every tool name exposed by the bridge (RevitMcpTools.cs) and every add-in tool name
    /// (IRevitMcpTool.Name) at the time the village was added. The coverage test below fails
    /// when a name no longer maps to a known area or activity, so new tools get a deliberate mapping.
    /// </summary>
    private const string KnownToolNames =
        "config_get_project_config config_read config_set_project_config config_update config_write convert_ifc_spaces_to_rooms " +
        "delivery_check_against_excel_register delivery_check_against_revit_sheets delivery_run_full_check delivery_scan_folder " +
        "excel_append_table_rows excel_insert_rows excel_inspect_workbook excel_read_range excel_update_cells file_backup file_copy " +
        "file_inspect file_list_directory file_read_text file_write_text ifc_list_links ifc_preview_space_geometry ifc_preview_spaces " +
        "revit_add_elements_to_circuit revit_align_elements revit_align_in_view revit_analyze_selected_tag_template " +
        "revit_annotate_detail_lines revit_apply_circuit_load_names revit_apply_circuit_numbering revit_apply_selected_tag_template " +
        "revit_apply_sheet_naming revit_apply_view_template revit_assign_data_devices_to_patch_panels " +
        "revit_change_circuit_cable_or_wire_type revit_check_circuit_health revit_check_circuit_parameter_completeness " +
        "revit_check_panel_utilization revit_check_parameter_completeness revit_compare_skill_override_to_master " +
        "revit_configure_sheet_naming_skill revit_copy_cad_overrides revit_count_elements revit_create_clash_review_view " +
        "revit_create_electrical_circuit revit_create_lines revit_create_panel_schematic_symbol_from_dwg " +
        "revit_create_project_skill_override revit_create_revision revit_create_sheets_from_table revit_create_skill " +
        "revit_create_text_notes revit_delete revit_delete_elements revit_delete_sheets revit_delete_views revit_detect_clashes " +
        "revit_detect_clearance_clashes revit_detect_hard_clashes revit_duplicate revit_duplicate_family_types revit_duplicate_sheets " +
        "revit_duplicate_views revit_edit_family_types revit_estimate_circuit_length revit_estimate_circuit_lengths " +
        "revit_export_circuit_health_to_excel revit_export_clash_report_to_excel revit_export_electrical_dashboard_to_excel " +
        "revit_export_fire_alarm_circuit_preset_to_excel revit_export_issues revit_export_issues_excel revit_export_issues_json " +
        "revit_export_issues_markdown revit_export_list_to_excel revit_export_panel_circuit_list_to_excel revit_export_query_to_excel " +
        "revit_export_schedule_list_to_excel revit_export_sheet_list_to_excel revit_export_skill_override_diff_markdown " +
        "revit_export_uncircuited_elements_to_excel revit_export_view_list_to_excel revit_export_voltage_drop_input_to_excel " +
        "revit_find_circuits_by_element_parameter revit_find_elements_by_parameter revit_find_elements_on_circuit " +
        "revit_find_managed_tags revit_find_uncircuited_elements revit_find_unplaced_views revit_focus_clash revit_get_adjacent_clash " +
        "revit_get_available_cable_types revit_get_available_panels revit_get_available_parameters revit_get_available_wire_types " +
        "revit_get_cad_placement_points revit_get_cad_shapes revit_get_circuit_compatible_elements revit_get_circuit_info " +
        "revit_get_circuit_load_summary revit_get_circuit_route_assumptions revit_get_circuits_for_selected_elements " +
        "revit_get_clash_candidates revit_get_clash_dashboard_summary revit_get_clash_preset revit_get_clash_summary " +
        "revit_get_connection_status revit_get_electrical_circuits revit_get_electrical_dashboard_summary " +
        "revit_get_element_parameters revit_get_elements_info revit_get_fire_alarm_visualization_data " +
        "revit_get_fire_alarm_voltage_drop_summary revit_get_matching_cable_resistance_profile revit_get_next_clash " +
        "revit_get_panel_issue_summary revit_get_previous_clash revit_get_selected_elements revit_get_sheet_revisions " +
        "revit_get_sheet_viewports revit_get_skill_details revit_get_text_notes revit_get_view_sheet_preset " +
        "revit_get_view_sheet_summary revit_get_voltage_drop_precheck revit_graph_build revit_graph_query revit_graph_status " +
        "revit_graph_summary revit_group_by_parameter revit_group_elements revit_inspect_selected_elements " +
        "revit_list_cable_resistance_profiles revit_list_cad_imports revit_list_clash_presets revit_list_clashable_categories " +
        "revit_list_clashable_links revit_list_dimension_types revit_list_extensible_storage_schemas revit_list_family_types " +
        "revit_list_instances revit_list_parameter_qa_rule_sets revit_list_query_presets revit_list_revision_numbering_sequences " +
        "revit_list_revisions revit_list_schedules revit_list_sheets revit_list_skill_tasks revit_list_skills revit_list_tag_types " +
        "revit_list_titleblocks revit_list_view_sheet_presets revit_list_view_templates revit_list_views " +
        "revit_manage_project_skill_override revit_merge_issue_reports revit_move_elements revit_place_dimensions " +
        "revit_place_family_instances revit_place_from_cad revit_place_from_cad_shapes revit_place_tags revit_place_views_on_sheets " +
        "revit_preview_align_elements revit_preview_align_in_view revit_preview_apply_sheet_naming " +
        "revit_preview_assign_data_devices_to_patch_panels revit_preview_circuit_load_names revit_preview_circuit_numbering " +
        "revit_preview_copy_cad_overrides revit_preview_create_sheets_from_table revit_preview_delete revit_preview_delete_elements " +
        "revit_preview_delete_sheets revit_preview_delete_views revit_preview_duplicate revit_preview_duplicate_family_types " +
        "revit_preview_duplicate_sheets revit_preview_duplicate_views revit_preview_edit_family_types revit_preview_move_elements " +
        "revit_preview_place_from_cad revit_preview_place_from_cad_shapes revit_preview_place_tags revit_preview_place_views_on_sheets " +
        "revit_preview_rename revit_preview_rename_sheets revit_preview_rename_views revit_preview_retag revit_preview_set_cad_overrides " +
        "revit_preview_set_view_crop_regions revit_preview_skill_run revit_propose_master_skill_update revit_query_linked_elements " +
        "revit_read_extensible_storage revit_reassign_circuit_panel revit_rename revit_rename_sheets revit_rename_views " +
        "revit_reset_project_skill_override revit_retag revit_run_clash_preset revit_run_fire_alarm_circuit_preset " +
        "revit_run_parameter_qa_rule_set revit_run_query_preset revit_run_skill revit_run_skill_task " +
        "revit_run_view_sheet_workflow_preset revit_select_circuit_elements revit_select_clash_elements revit_select_elements " +
        "revit_select_elements_by_panel revit_select_elements_by_query revit_select_instance revit_select_linked_elements " +
        "revit_select_uncircuited_elements revit_set_cad_overrides revit_set_circuit_parameter revit_set_circuit_parameters_bulk " +
        "revit_set_circuit_path_mode revit_set_parameter revit_set_parameters_bulk revit_set_sheet_parameters_bulk " +
        "revit_set_view_crop_regions revit_set_view_parameters_bulk revit_skill_builder_guide revit_trace_circuit " +
        "revit_update_project_skill_override revit_update_skill revit_validate_clash_preset revit_validate_view_sheet_preset " +
        "standards_get_document_chunk standards_index_sources standards_list_sources standards_search " +
        "standards_validate_source_config sync_ifc_space_room_data validate_ifc_space_room_conversion";

    public static IEnumerable<object[]> AllKnownToolNames() =>
        KnownToolNames.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(AllKnownToolNames))]
    public void EveryKnownTool_MapsToAKnownAreaAndActivity(string toolName)
    {
        var c = VillageToolClassifier.Default.Classify(toolName);
        Assert.NotEqual(VillageAreas.Unknown, c.Area);
        Assert.NotEqual(VillageActivities.Unknown, c.Activity);
        Assert.True(VillageAreas.IsKnown(c.Area));
        Assert.True(VillageActivities.IsKnown(c.Activity));
    }

    [Theory]
    // project / graph
    [InlineData("revit_get_connection_status", "project", "inspect")]
    [InlineData("revit_list_instances", "project", "inspect")]
    [InlineData("revit_select_instance", "project", "inspect")]
    [InlineData("revit_graph_build", "graph", "build_graph")]
    [InlineData("revit_graph_status", "graph", "inspect")]
    [InlineData("revit_graph_query", "graph", "search")]
    [InlineData("revit_graph_summary", "graph", "inspect")]
    // sheets / views / schedules
    [InlineData("revit_list_sheets", "sheets", "inspect")]
    [InlineData("revit_create_sheets_from_table", "sheets", "create")]
    [InlineData("revit_place_views_on_sheets", "sheets", "create")]
    [InlineData("revit_preview_place_views_on_sheets", "sheets", "analyze")]
    [InlineData("revit_delete_sheets", "sheets", "delete")]
    [InlineData("revit_get_sheet_revisions", "sheets", "inspect")]
    [InlineData("revit_create_revision", "sheets", "create")]
    [InlineData("revit_list_titleblocks", "sheets", "inspect")]
    [InlineData("revit_export_sheet_list_to_excel", "sheets", "export")]
    [InlineData("revit_list_views", "views", "inspect")]
    [InlineData("revit_apply_view_template", "views", "modify")]
    [InlineData("revit_rename_views", "views", "modify")]
    [InlineData("revit_duplicate_views", "views", "create")]
    [InlineData("revit_delete_views", "views", "delete")]
    [InlineData("revit_find_unplaced_views", "views", "search")]
    [InlineData("revit_set_cad_overrides", "views", "modify")]
    [InlineData("revit_list_cad_imports", "views", "inspect")]
    [InlineData("revit_list_schedules", "schedules", "inspect")]
    [InlineData("revit_export_schedule_list_to_excel", "schedules", "export")]
    // families / tags
    [InlineData("revit_list_family_types", "families_types", "inspect")]
    [InlineData("revit_edit_family_types", "families_types", "modify")]
    [InlineData("revit_duplicate_family_types", "families_types", "create")]
    [InlineData("revit_create_panel_schematic_symbol_from_dwg", "families_types", "create")]
    [InlineData("revit_place_tags", "tags_annotations", "create")]
    [InlineData("revit_preview_place_tags", "tags_annotations", "analyze")]
    [InlineData("revit_retag", "tags_annotations", "modify")]
    [InlineData("revit_find_managed_tags", "tags_annotations", "search")]
    [InlineData("revit_place_dimensions", "tags_annotations", "create")]
    [InlineData("revit_create_text_notes", "tags_annotations", "create")]
    [InlineData("revit_get_text_notes", "tags_annotations", "inspect")]
    [InlineData("revit_annotate_detail_lines", "tags_annotations", "create")]
    [InlineData("revit_create_lines", "tags_annotations", "create")]
    [InlineData("revit_align_in_view", "tags_annotations", "modify")]
    [InlineData("revit_preview_retag", "tags_annotations", "analyze")]
    [InlineData("ifc_preview_spaces", "elements", "analyze")]
    [InlineData("revit_create_clash_review_view", "coordination", "create")]
    // elements
    [InlineData("revit_get_elements_info", "elements", "inspect")]
    [InlineData("revit_find_elements_by_parameter", "elements", "search")]
    [InlineData("revit_set_parameter", "elements", "modify")]
    [InlineData("revit_set_parameters_bulk", "elements", "modify")]
    [InlineData("revit_delete_elements", "elements", "delete")]
    [InlineData("revit_delete", "elements", "delete")]
    [InlineData("revit_preview_delete", "elements", "analyze")]
    [InlineData("revit_place_family_instances", "elements", "create")]
    [InlineData("revit_place_from_cad", "elements", "create")]
    [InlineData("revit_get_cad_placement_points", "elements", "inspect")]
    [InlineData("revit_move_elements", "elements", "modify")]
    [InlineData("revit_align_elements", "elements", "modify")]
    [InlineData("revit_select_elements_by_query", "elements", "inspect")]
    [InlineData("revit_count_elements", "elements", "inspect")]
    [InlineData("revit_check_parameter_completeness", "elements", "validate")]
    [InlineData("revit_run_query_preset", "elements", "search")]
    [InlineData("revit_export_query_to_excel", "elements", "export")]
    [InlineData("revit_read_extensible_storage", "elements", "inspect")]
    [InlineData("convert_ifc_spaces_to_rooms", "elements", "create")]
    [InlineData("sync_ifc_space_room_data", "elements", "modify")]
    [InlineData("validate_ifc_space_room_conversion", "elements", "validate")]
    // electrical and ELV systems
    [InlineData("revit_get_electrical_circuits", "electrical", "inspect")]
    [InlineData("revit_create_electrical_circuit", "electrical", "create")]
    [InlineData("revit_add_elements_to_circuit", "electrical", "modify")]
    [InlineData("revit_check_circuit_health", "electrical", "validate")]
    [InlineData("revit_trace_circuit", "electrical", "analyze")]
    [InlineData("revit_estimate_circuit_length", "electrical", "analyze")]
    [InlineData("revit_get_voltage_drop_precheck", "electrical", "inspect")]
    [InlineData("revit_export_uncircuited_elements_to_excel", "electrical", "export")]
    [InlineData("revit_get_fire_alarm_visualization_data", "fire_alarm", "inspect")]
    [InlineData("revit_run_fire_alarm_circuit_preset", "fire_alarm", "analyze")]
    [InlineData("revit_export_fire_alarm_circuit_preset_to_excel", "fire_alarm", "export")]
    [InlineData("revit_assign_data_devices_to_patch_panels", "it_av", "modify")]
    [InlineData("revit_preview_assign_data_devices_to_patch_panels", "it_av", "analyze")]
    // coordination
    [InlineData("revit_detect_hard_clashes", "coordination", "analyze")]
    [InlineData("revit_run_clash_preset", "coordination", "analyze")]
    [InlineData("revit_focus_clash", "coordination", "inspect")]
    [InlineData("revit_create_clash_review_view", "coordination", "create")]
    [InlineData("revit_export_clash_report_to_excel", "coordination", "export")]
    // office
    [InlineData("file_read_text", "office", "inspect")]
    [InlineData("file_write_text", "office", "modify")]
    [InlineData("excel_update_cells", "office", "modify")]
    [InlineData("excel_inspect_workbook", "office", "inspect")]
    [InlineData("config_update", "office", "modify")]
    [InlineData("config_get_project_config", "office", "inspect")]
    [InlineData("standards_search", "office", "search")]
    [InlineData("standards_index_sources", "office", "modify")]
    [InlineData("delivery_run_full_check", "office", "validate")]
    [InlineData("delivery_scan_folder", "office", "search")]
    [InlineData("revit_run_skill", "office", "modify")]
    [InlineData("revit_preview_skill_run", "office", "analyze")]
    [InlineData("revit_skill_builder_guide", "office", "inspect")]
    [InlineData("revit_export_issues", "office", "export")]
    [InlineData("revit_merge_issue_reports", "office", "export")]
    [InlineData("revit_run_parameter_qa_rule_set", "elements", "validate")]
    public void Classify_MapsKnownToolsDeterministically(string tool, string area, string activity)
    {
        var c = VillageToolClassifier.Default.Classify(tool);
        Assert.Equal(area, c.Area);
        Assert.Equal(activity, c.Activity);
        Assert.False(string.IsNullOrEmpty(c.AreaRule));
        Assert.False(string.IsNullOrEmpty(c.ActivityRule));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("totally_new_tool")]
    [InlineData("frobnicate")]
    [InlineData("revit_frobnicate_widgets")]
    public void Classify_UnknownToolsMapToUnknownWithoutThrowing(string? tool)
    {
        var c = VillageToolClassifier.Default.Classify(tool);
        Assert.Equal(VillageAreas.Unknown, c.Area);
        Assert.Equal(VillageActivities.Unknown, c.Activity);
    }

    [Theory]
    [InlineData("revit_preview_skill_run", "office")]      // "preview" must not match the "view" keyword
    [InlineData("revit_get_voltage_drop_precheck", "electrical")] // "voltage" must not match the "tag" keyword
    [InlineData("revit_preview_place_views_on_sheets", "sheets")]
    public void Classify_MatchesKeywordsAtTokenBoundaries(string tool, string area)
    {
        Assert.Equal(area, VillageToolClassifier.Default.Classify(tool).Area);
    }

    [Fact]
    public void Classify_IsCaseAndWhitespaceInsensitive()
    {
        var a = VillageToolClassifier.Default.Classify("  REVIT_List_Sheets ");
        Assert.Equal("sheets", a.Area);
        Assert.Equal("inspect", a.Activity);
    }

    [Fact]
    public void Classify_PartiallyKnownToolKeepsWhatItCanResolve()
    {
        var c = VillageToolClassifier.Default.Classify("revit_frobnicate_sheets");
        Assert.Equal("sheets", c.Area);
        Assert.Equal("unknown", c.Activity);

        var d = VillageToolClassifier.Default.Classify("revit_get_widgets");
        Assert.Equal("unknown", d.Area);
        Assert.Equal("inspect", d.Activity);
    }

    [Fact]
    public void Overrides_ApplyOnTopOfDefaultsAndIgnoreInvalidValues()
    {
        var classifier = new VillageToolClassifier(
            areaOverrides: new Dictionary<string, string>
            {
                ["revit_list_sheets"] = "lighting",
                ["revit_list_views"] = "moon_base",
                ["totally_new_tool"] = " Security "
            },
            activityOverrides: new Dictionary<string, string>
            {
                ["revit_list_sheets"] = "EXPORT",
                ["revit_list_views"] = "dance",
                ["totally_new_tool"] = "validate"
            });

        var sheets = classifier.Classify("revit_list_sheets");
        Assert.Equal("lighting", sheets.Area);
        Assert.Equal("export", sheets.Activity);
        Assert.Equal("override", sheets.AreaRule);

        var views = classifier.Classify("revit_list_views");
        Assert.Equal("views", views.Area);
        Assert.Equal("inspect", views.Activity);

        var novel = classifier.Classify("totally_new_tool");
        Assert.Equal("security", novel.Area);
        Assert.Equal("validate", novel.Activity);

        // The shared default instance is untouched.
        Assert.Equal("sheets", VillageToolClassifier.Default.Classify("revit_list_sheets").Area);
    }

    [Theory]
    [InlineData("revit_preview_delete_views", "preview")]
    [InlineData("revit_delete", "delete")]
    [InlineData("config_update", "update")]
    [InlineData("sync_ifc_space_room_data", "sync")]
    [InlineData("excel_append_table_rows", "append")]
    [InlineData("revit", "revit")]
    [InlineData("", "")]
    public void ExtractVerb_TakesFirstTokenAfterNamespace(string name, string verb)
    {
        Assert.Equal(verb, VillageToolClassifier.ExtractVerb(name));
    }
}
