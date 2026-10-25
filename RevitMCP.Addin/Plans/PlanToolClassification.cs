namespace RevitMCP.Addin.Plans;

public enum PlanToolKind
{
    /// <summary>Not classified: a plan treats it as creating elements (the cautious choice).</summary>
    Unknown,
    Creates,
    Modifies
}

/// <summary>
/// Which write tools create elements (refused up front when a plan has AllowCreate=false) and which element
/// categories a plan ignores as side effects unless Strict (#99). One static table, unit tested against every
/// write tool the bridge advertises. Pure.
/// </summary>
public static class PlanToolClassification
{
    // Checked first: a name matching both lists is creating.
    private static readonly string[] CreatingPrefixes =
    {
        "revit_place_", "revit_create_", "revit_duplicate", "revit_copy_elements", "revit_draw_", "revit_annotate_",
        "revit_retag", "revit_ensure_", "revit_run_skill", "revit_apply_selected_tag_template", "convert_ifc_spaces_to_rooms"
    };

    private static readonly string[] ModifyingPrefixes =
    {
        "revit_set_", "revit_move_", "revit_rotate_", "revit_align_", "revit_rename", "revit_apply_", "revit_assign_",
        "revit_add_elements_", "revit_change_", "revit_reassign_", "revit_edit_", "revit_select_", "revit_panelschematics_",
        "revit_focus_", "revit_copy_cad_", "revit_activate_", "revit_save_", "revit_sync_", "revit_reload_", "revit_remove_",
        "revit_delete", "revit_checkpoint_", "revit_memory_", "revit_export_", "revit_update_skill", "revit_plan_",
        "revit_page_", "sync_ifc_", "config_", "file_", "excel_"
    };

    public static PlanToolKind Classify(string toolName)
    {
        foreach (var prefix in CreatingPrefixes)
            if (toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return PlanToolKind.Creates;
        foreach (var prefix in ModifyingPrefixes)
            if (toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return PlanToolKind.Modifies;
        return PlanToolKind.Unknown;
    }

    /// <summary>Creates elements, or is not classified (and so treated as if it did).</summary>
    public static bool MayCreate(string toolName) => Classify(toolName) != PlanToolKind.Modifies;

    /// <summary>
    /// Built-in categories Revit changes as a side effect of the elements a call is about: annotation that follows
    /// its host (every OST_*Tags, dimensions, spot dimensions, view markers, viewports, revision clouds) and the
    /// electrical systems and wires a moved or re-parameterised device drags along.
    /// </summary>
    private static readonly HashSet<string> Incidental = new(StringComparer.OrdinalIgnoreCase)
    {
        "OST_Dimensions", "OST_SpotElevations", "OST_SpotCoordinates", "OST_SpotSlopes",
        "OST_Viewports", "OST_RevisionClouds", "OST_Elev", "OST_ElevationMarks", "OST_Sections", "OST_SectionHeads",
        "OST_Callouts", "OST_CalloutHeads", "OST_ReferenceViewer", "OST_Wire", "OST_ElectricalCircuit",
        "OST_ElectricalInternalCircuits"
    };

    /// <summary>
    /// True for a side-effect category. An element with no category (sketches, internal bookkeeping) is incidental too.
    /// </summary>
    public static bool IsIncidental(string? builtInCategory)
    {
        if (string.IsNullOrEmpty(builtInCategory)) return true;
        return Incidental.Contains(builtInCategory!) || builtInCategory!.EndsWith("Tags", StringComparison.OrdinalIgnoreCase);
    }
}
