namespace RevitMCP.Village;

/// <summary>Result of classifying a tool name, with the rule that decided it (for the diagnostics panel).</summary>
public sealed class VillageClassification
{
    public string Area { get; set; } = VillageAreas.Unknown;
    public string Activity { get; set; } = VillageActivities.Unknown;
    public string AreaRule { get; set; } = string.Empty;
    public string ActivityRule { get; set; } = string.Empty;
}

/// <summary>
/// Deterministic tool-name → (area, activity) mapping. Pure string rules, no model access, no AI.
/// Area: explicit overrides, then namespace prefixes, then ordered keyword rules. Activity: explicit
/// overrides, then the verb (first token after a known namespace prefix). Anything unmatched is
/// <c>unknown</c>. Both bridge-side names (e.g. <c>revit_delete</c>) and add-in names
/// (e.g. <c>revit_delete_views</c>) resolve.
/// </summary>
public sealed class VillageToolClassifier
{
    private static readonly string[] NamespacePrefixes =
    {
        "revit_", "config_", "file_", "excel_", "standards_", "delivery_", "ifc_"
    };

    /// <summary>Exact tool name → area. Checked before the keyword rules.</summary>
    private static readonly Dictionary<string, string> DefaultAreaOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["revit_get_connection_status"] = VillageAreas.Project,
        ["revit_list_instances"]        = VillageAreas.Project,
        ["revit_select_instance"]       = VillageAreas.Project,
        ["revit_skill_builder_guide"]   = VillageAreas.Office,
        ["revit_align_in_view"]         = VillageAreas.TagsAnnotations,
        ["revit_preview_align_in_view"] = VillageAreas.TagsAnnotations,
        ["revit_delete"]                = VillageAreas.Elements,
        ["revit_preview_delete"]        = VillageAreas.Elements,
        ["revit_duplicate"]             = VillageAreas.Views,
        ["revit_preview_duplicate"]     = VillageAreas.Views,
        ["revit_rename"]                = VillageAreas.Views,
        ["revit_preview_rename"]        = VillageAreas.Views,
        ["revit_export_list_to_excel"]  = VillageAreas.Office,
        ["revit_export_query_to_excel"] = VillageAreas.Elements
    };

    /// <summary>Ordered (keyword, area) rules. The first keyword that starts a token of the name wins.</summary>
    private static readonly (string Keyword, string Area)[] AreaRules =
    {
        ("graph",              VillageAreas.Graph),
        ("clash",              VillageAreas.Coordination),
        ("tag",                VillageAreas.TagsAnnotations),
        ("retag",              VillageAreas.TagsAnnotations),
        ("dimension",          VillageAreas.TagsAnnotations),
        ("text_note",          VillageAreas.TagsAnnotations),
        ("detail_lines",       VillageAreas.TagsAnnotations),
        ("create_lines",       VillageAreas.TagsAnnotations),
        ("fire_alarm",         VillageAreas.FireAlarm),
        ("patch_panel",        VillageAreas.ItAv),
        ("data_device",        VillageAreas.ItAv),
        ("lighting",           VillageAreas.Lighting),
        ("dali",               VillageAreas.Lighting),
        ("security",           VillageAreas.Security),
        ("access_control",     VillageAreas.Security),
        ("sheet",              VillageAreas.Sheets),
        ("titleblock",         VillageAreas.Sheets),
        ("revision",           VillageAreas.Sheets),
        ("schedule",           VillageAreas.Schedules),
        ("place_family",       VillageAreas.Elements),
        ("place_from_cad",     VillageAreas.Elements),
        ("cad_placement",      VillageAreas.Elements),
        ("cad_shapes",         VillageAreas.Elements),
        ("from_dwg",           VillageAreas.FamiliesTypes),
        ("family",             VillageAreas.FamiliesTypes),
        ("families",           VillageAreas.FamiliesTypes),
        ("view",               VillageAreas.Views),
        ("cad",                VillageAreas.Views),
        ("uncircuited",        VillageAreas.Electrical),
        ("circuit",            VillageAreas.Electrical),
        ("panel",              VillageAreas.Electrical),
        ("cable",              VillageAreas.Electrical),
        ("wire",               VillageAreas.Electrical),
        ("electrical",         VillageAreas.Electrical),
        ("voltage",            VillageAreas.Electrical),
        ("skill",              VillageAreas.Office),
        ("issue",              VillageAreas.Office),
        ("report",             VillageAreas.Office),
        ("extensible_storage", VillageAreas.Elements),
        ("ifc",                VillageAreas.Elements),
        ("space",              VillageAreas.Elements),
        ("room",               VillageAreas.Elements),
        ("element",            VillageAreas.Elements),
        ("parameter",          VillageAreas.Elements),
        ("select",             VillageAreas.Elements),
        ("query",              VillageAreas.Elements),
        ("count",              VillageAreas.Elements),
        ("group",              VillageAreas.Elements),
        ("align",              VillageAreas.Elements),
        ("move",               VillageAreas.Elements),
        ("place",              VillageAreas.Elements),
        ("instance",           VillageAreas.Elements)
    };

    /// <summary>Namespace prefixes whose tools always live in the office, unless an override says otherwise.</summary>
    private static readonly (string Prefix, string Area)[] PrefixAreas =
    {
        ("config_",    VillageAreas.Office),
        ("file_",      VillageAreas.Office),
        ("excel_",     VillageAreas.Office),
        ("standards_", VillageAreas.Office),
        ("delivery_",  VillageAreas.Office)
    };

    /// <summary>Exact tool name → activity. Checked before the verb table.</summary>
    private static readonly Dictionary<string, string> DefaultActivityOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["revit_graph_build"]                    = VillageActivities.BuildGraph,
        ["revit_graph_query"]                    = VillageActivities.Search,
        ["revit_graph_status"]                   = VillageActivities.Inspect,
        ["revit_graph_summary"]                  = VillageActivities.Inspect,
        ["revit_run_query_preset"]               = VillageActivities.Search,
        ["revit_run_skill"]                      = VillageActivities.Modify,
        ["revit_run_skill_task"]                 = VillageActivities.Modify,
        ["revit_run_view_sheet_workflow_preset"] = VillageActivities.Modify,
        ["revit_run_parameter_qa_rule_set"]      = VillageActivities.Validate,
        ["revit_run_clash_preset"]               = VillageActivities.Analyze,
        ["revit_run_fire_alarm_circuit_preset"]  = VillageActivities.Analyze,
        ["delivery_run_full_check"]              = VillageActivities.Validate,
        ["revit_get_cad_placement_points"]       = VillageActivities.Inspect,
        ["revit_skill_builder_guide"]            = VillageActivities.Inspect,
        ["revit_select_instance"]                = VillageActivities.Inspect,
        ["revit_merge_issue_reports"]            = VillageActivities.Export
    };

    /// <summary>Verb (first token after the namespace prefix) → activity.</summary>
    private static readonly Dictionary<string, string> VerbActivities = new(StringComparer.OrdinalIgnoreCase)
    {
        // read
        ["get"] = VillageActivities.Inspect, ["list"] = VillageActivities.Inspect, ["read"] = VillageActivities.Inspect,
        ["inspect"] = VillageActivities.Inspect, ["count"] = VillageActivities.Inspect, ["group"] = VillageActivities.Inspect,
        ["select"] = VillageActivities.Inspect, ["focus"] = VillageActivities.Inspect,
        // search
        ["find"] = VillageActivities.Search, ["search"] = VillageActivities.Search, ["query"] = VillageActivities.Search,
        ["scan"] = VillageActivities.Search,
        // analyze (dry runs and calculations)
        ["preview"] = VillageActivities.Analyze, ["analyze"] = VillageActivities.Analyze, ["compare"] = VillageActivities.Analyze,
        ["estimate"] = VillageActivities.Analyze, ["detect"] = VillageActivities.Analyze, ["trace"] = VillageActivities.Analyze,
        ["run"] = VillageActivities.Analyze,
        // validate
        ["check"] = VillageActivities.Validate, ["validate"] = VillageActivities.Validate,
        // export
        ["export"] = VillageActivities.Export,
        // delete
        ["delete"] = VillageActivities.Delete,
        // create
        ["create"] = VillageActivities.Create, ["place"] = VillageActivities.Create, ["duplicate"] = VillageActivities.Create,
        ["annotate"] = VillageActivities.Create, ["convert"] = VillageActivities.Create,
        // modify
        ["set"] = VillageActivities.Modify, ["apply"] = VillageActivities.Modify, ["rename"] = VillageActivities.Modify,
        ["reassign"] = VillageActivities.Modify, ["change"] = VillageActivities.Modify, ["assign"] = VillageActivities.Modify,
        ["update"] = VillageActivities.Modify, ["move"] = VillageActivities.Modify, ["align"] = VillageActivities.Modify,
        ["retag"] = VillageActivities.Modify, ["sync"] = VillageActivities.Modify, ["copy"] = VillageActivities.Modify,
        ["edit"] = VillageActivities.Modify, ["write"] = VillageActivities.Modify, ["insert"] = VillageActivities.Modify,
        ["append"] = VillageActivities.Modify, ["configure"] = VillageActivities.Modify, ["manage"] = VillageActivities.Modify,
        ["propose"] = VillageActivities.Modify, ["reset"] = VillageActivities.Modify, ["add"] = VillageActivities.Modify,
        ["backup"] = VillageActivities.Modify, ["index"] = VillageActivities.Modify
    };

    private readonly Dictionary<string, string> _areaOverrides;
    private readonly Dictionary<string, string> _activityOverrides;

    /// <summary>Classifier with the built-in rules only.</summary>
    public static VillageToolClassifier Default { get; } = new();

    /// <summary>
    /// Creates a classifier. Optional per-tool overrides (from <c>village.toolAreas</c> /
    /// <c>village.toolActivities</c> in the config) are applied on top of the built-in table.
    /// Values outside the vocabulary are ignored so a typo cannot break the viewer.
    /// </summary>
    public VillageToolClassifier(
        IDictionary<string, string>? areaOverrides = null,
        IDictionary<string, string>? activityOverrides = null)
    {
        _areaOverrides = new Dictionary<string, string>(DefaultAreaOverrides, StringComparer.OrdinalIgnoreCase);
        _activityOverrides = new Dictionary<string, string>(DefaultActivityOverrides, StringComparer.OrdinalIgnoreCase);

        if (areaOverrides != null)
        {
            foreach (var kv in areaOverrides)
            {
                var value = (kv.Value ?? string.Empty).Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(kv.Key) && VillageAreas.IsKnown(value))
                    _areaOverrides[kv.Key.Trim()] = value;
            }
        }

        if (activityOverrides != null)
        {
            foreach (var kv in activityOverrides)
            {
                var value = (kv.Value ?? string.Empty).Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(kv.Key) && VillageActivities.IsKnown(value))
                    _activityOverrides[kv.Key.Trim()] = value;
            }
        }
    }

    public VillageClassification Classify(string? toolName)
    {
        var name = (toolName ?? string.Empty).Trim().ToLowerInvariant();
        var result = new VillageClassification();
        if (name.Length == 0)
        {
            result.AreaRule = "empty";
            result.ActivityRule = "empty";
            return result;
        }

        ClassifyArea(name, result);
        ClassifyActivity(name, result);
        return result;
    }

    private void ClassifyArea(string name, VillageClassification result)
    {
        if (_areaOverrides.TryGetValue(name, out var area))
        {
            result.Area = area;
            result.AreaRule = "override";
            return;
        }

        foreach (var (prefix, prefixArea) in PrefixAreas)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                result.Area = prefixArea;
                result.AreaRule = "prefix:" + prefix;
                return;
            }
        }

        // Keywords must start a token ("_tag" matches "_tags" and "_tag_types" but not "_voltage";
        // "_view" matches "_views" but not "_preview"). The leading underscore makes the first
        // token of an un-prefixed name (e.g. "ifc_...") behave like every other token.
        var padded = "_" + name;
        foreach (var (keyword, ruleArea) in AreaRules)
        {
            if (padded.IndexOf("_" + keyword, StringComparison.Ordinal) >= 0)
            {
                result.Area = ruleArea;
                result.AreaRule = "keyword:" + keyword;
                return;
            }
        }

        result.Area = VillageAreas.Unknown;
        result.AreaRule = "none";
    }

    private void ClassifyActivity(string name, VillageClassification result)
    {
        if (_activityOverrides.TryGetValue(name, out var activity))
        {
            result.Activity = activity;
            result.ActivityRule = "override";
            return;
        }

        var verb = ExtractVerb(name);
        if (verb.Length > 0 && VerbActivities.TryGetValue(verb, out var verbActivity))
        {
            result.Activity = verbActivity;
            result.ActivityRule = "verb:" + verb;
            return;
        }

        result.Activity = VillageActivities.Unknown;
        result.ActivityRule = "none";
    }

    /// <summary>First token after a known namespace prefix, e.g. <c>revit_preview_delete</c> → <c>preview</c>.</summary>
    public static string ExtractVerb(string name)
    {
        var rest = name;
        foreach (var prefix in NamespacePrefixes)
        {
            if (rest.StartsWith(prefix, StringComparison.Ordinal))
            {
                rest = rest.Substring(prefix.Length);
                break;
            }
        }

        var underscore = rest.IndexOf('_');
        return underscore < 0 ? rest : rest.Substring(0, underscore);
    }
}
