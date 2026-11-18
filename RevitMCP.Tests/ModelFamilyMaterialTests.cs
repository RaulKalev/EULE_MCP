using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Model-family materials (#173): argument parsing, the material resolution rule, element state and the tool surface.</summary>
public class ModelFamilyMaterialTests
{
    private static Dictionary<string, object?> Args(string json)
    {
        var args = new Dictionary<string, object?>();
        foreach (var property in JObject.Parse(json).Properties()) args[property.Name] = property.Value;
        return args;
    }

    private static (ModelFamilyMaterialPlan Plan, List<string> Problems) Parse(string json)
    {
        var problems = new List<string>();
        return (ModelFamilyMaterialArgs.Parse(Args(json), problems), problems);
    }

    // ---------------------------------------------------------------- materials

    [Fact]
    public void Materials_parse_every_property()
    {
        var (plan, problems) = Parse("""
            {"materials":[{"name":"Plastic White","color":[250,250,245],"transparency":10,"shininess":64,"smoothness":50,
              "materialClass":"Plastic","duplicateFrom":"Plastic"}]}
            """);
        Assert.Empty(problems);
        var op = Assert.Single(plan.Materials);
        Assert.Equal("Plastic White", op.Name);
        Assert.Equal(new[] { 250, 250, 245 }, op.Color);
        Assert.Equal(10, op.Transparency);
        Assert.Equal(64, op.Shininess);
        Assert.Equal(50, op.Smoothness);
        Assert.Equal("Plastic", op.MaterialClass);
        Assert.Equal("Plastic", op.DuplicateFrom);
        Assert.False(op.CopyFromProject);
        Assert.True(op.HasProperties);
    }

    [Fact]
    public void A_material_needs_a_name_and_rejects_names_Revit_refuses()
    {
        var (_, missing) = Parse("""{"materials":[{"color":[1,2,3]}]}""");
        Assert.Contains(missing, p => p.StartsWith("materials[0].name is required"));
        var (_, bad) = Parse("""{"materials":[{"name":"Steel [grey]"}]}""");
        Assert.Contains(bad, p => p.Contains("materials[0].name") && p.Contains("does not allow"));
    }

    [Theory]
    [InlineData("""{"color":[256,0,0]}""", "materials[0].color[0]")]
    [InlineData("""{"color":[0,-1,0]}""", "materials[0].color[1]")]
    [InlineData("""{"color":[0,0,1.5]}""", "materials[0].color[2]")]
    [InlineData("""{"color":[0,0]}""", "materials[0].color")]
    [InlineData("""{"color":"red"}""", "materials[0].color")]
    [InlineData("""{"transparency":101}""", "materials[0].transparency")]
    [InlineData("""{"transparency":-1}""", "materials[0].transparency")]
    [InlineData("""{"transparency":12.5}""", "materials[0].transparency")]
    [InlineData("""{"transparency":"lots"}""", "materials[0].transparency")]
    [InlineData("""{"shininess":129}""", "materials[0].shininess")]
    [InlineData("""{"smoothness":101}""", "materials[0].smoothness")]
    public void Material_properties_are_range_checked(string extra, string expectedSource)
    {
        var json = "{\"materials\":[" + extra.Insert(1, "\"name\":\"M\",").Replace(",}", "}") + "]}";
        var (plan, problems) = Parse(json);
        Assert.Contains(problems, p => p.StartsWith(expectedSource));
        Assert.Empty(plan.Materials);
    }

    [Fact]
    public void Range_limits_are_accepted_exactly()
    {
        var (plan, problems) = Parse("""{"materials":[{"name":"A","color":[0,128,255],"transparency":100,"shininess":128,"smoothness":0}]}""");
        Assert.Empty(problems);
        Assert.Equal(128, plan.Materials[0].Shininess);
        Assert.Equal(100, plan.Materials[0].Transparency);
        Assert.Equal(0, plan.Materials[0].Smoothness);
    }

    [Fact]
    public void Duplicate_from_and_copy_from_project_exclude_each_other()
    {
        var (_, problems) = Parse("""{"materials":[{"name":"A","duplicateFrom":"B","copyFromProject":true}]}""");
        Assert.Contains(problems, p => p.Contains("not both"));
    }

    [Fact]
    public void A_material_listed_twice_is_a_problem_and_unknown_keys_are_reported()
    {
        var (_, twice) = Parse("""{"materials":[{"name":"A"},{"name":"A","color":[1,2,3]}]}""");
        Assert.Contains(twice, p => p.Contains("materials[1].name") && p.Contains("materials[0]"));
        var (_, unknown) = Parse("""{"materials":[{"name":"A","colour":[1,2,3]}]}""");
        Assert.Contains(unknown, p => p.Contains("materials[0].colour: unknown key"));
    }

    // ---------------------------------------------------------------- assign

    [Fact]
    public void Assign_takes_a_name_an_id_or_null()
    {
        var (plan, problems) = Parse("""
            {"assign":[{"elements":[101,102],"material":"Plastic White"},
                       {"elements":[103],"material":445},
                       {"elements":["104"],"material":null,"replaceAssociation":true}]}
            """);
        Assert.Empty(problems);
        Assert.Equal(3, plan.Assign.Count);
        Assert.Equal("Plastic White", plan.Assign[0].Material.Name);
        Assert.Equal(new long[] { 101, 102 }, plan.Assign[0].Elements);
        Assert.Equal(445, plan.Assign[1].Material.Id);
        Assert.True(plan.Assign[2].Material.IsNone);
        Assert.True(plan.Assign[2].ReplaceAssociation);
        Assert.Equal(new long[] { 104 }, plan.Assign[2].Elements);
    }

    [Fact]
    public void Assign_requires_elements_and_an_explicit_material()
    {
        var (_, problems) = Parse("""{"assign":[{"material":"A"},{"elements":[1]},{"elements":[],"material":"A"},{"elements":["x"],"material":"A"}]}""");
        Assert.Contains(problems, p => p.StartsWith("assign[0].elements is required"));
        Assert.Contains(problems, p => p.StartsWith("assign[1].material is required"));
        Assert.Contains(problems, p => p.StartsWith("assign[2].elements: the list is empty"));
        Assert.Contains(problems, p => p.StartsWith("assign[3].elements[0]: must be an element id"));
    }

    [Fact]
    public void Assign_deduplicates_ids_and_accepts_a_single_id()
    {
        var (plan, problems) = Parse("""{"assign":[{"elements":7,"material":"A"},{"elements":[5,5,6],"material":"A"}]}""");
        Assert.Empty(problems);
        Assert.Equal(new long[] { 7 }, plan.Assign[0].Elements);
        Assert.Equal(new long[] { 5, 6 }, plan.Assign[1].Elements);
    }

    [Fact]
    public void An_empty_or_bad_material_reference_is_a_problem()
    {
        var (_, problems) = Parse("""{"assign":[{"elements":[1],"material":""},{"elements":[1],"material":-3},{"elements":[1],"material":true}]}""");
        Assert.Contains(problems, p => p.StartsWith("assign[0].material: the material name is empty"));
        Assert.Contains(problems, p => p.StartsWith("assign[1].material: -3 is not an element id"));
        Assert.Contains(problems, p => p.StartsWith("assign[2].material: must be"));
    }

    // ---------------------------------------------------------------- associate

    [Fact]
    public void Associate_parses_a_parameter_a_create_object_and_null()
    {
        var (plan, problems) = Parse("""
            {"associate":[{"elements":[201],"parameter":"Housing Material","create":{"instance":true}},
                          {"elements":[202],"parameter":null}]}
            """);
        Assert.Empty(problems);
        Assert.Equal("Housing Material", plan.Associate[0].Parameter);
        Assert.True(plan.Associate[0].Create!.Instance);
        Assert.Equal(ModelFamilyMaterialArgs.DefaultGroup, plan.Associate[0].Create!.Group);
        Assert.Null(plan.Associate[1].Parameter);
        Assert.Null(plan.Associate[1].Create);
    }

    [Fact]
    public void Associate_create_defaults_to_a_type_parameter_in_materials_and_finishes()
    {
        var (plan, problems) = Parse("""{"associate":[{"elements":[1],"parameter":"P","create":{}}]}""");
        Assert.Empty(problems);
        Assert.False(plan.Associate[0].Create!.Instance);
        Assert.Equal("Materials and Finishes", plan.Associate[0].Create!.Group);
    }

    [Fact]
    public void Associate_needs_the_parameter_key_and_rejects_bad_create()
    {
        var (_, problems) = Parse("""
            {"associate":[{"elements":[1]},
                          {"elements":[1],"parameter":"P","create":{"instanse":true}},
                          {"elements":[1],"parameter":null,"create":{}},
                          {"elements":[1],"parameter":"P","create":"yes"}]}
            """);
        Assert.Contains(problems, p => p.StartsWith("associate[0].parameter is required"));
        Assert.Contains(problems, p => p.Contains("associate[1].create.instanse: unknown key"));
        Assert.Contains(problems, p => p.StartsWith("associate[2].create: only makes sense"));
        Assert.Contains(problems, p => p.StartsWith("associate[3].create: must be an object"));
    }

    // ---------------------------------------------------------------- defaults

    [Fact]
    public void Defaults_parse_parameter_material_and_optional_type()
    {
        var (plan, problems) = Parse("""
            {"defaults":[{"parameter":"Housing Material","material":"Plastic White","type":"Type A"},
                         {"parameter":"Housing Material","material":null}]}
            """);
        Assert.Empty(problems);
        Assert.Equal("Type A", plan.Defaults[0].Type);
        Assert.Equal(string.Empty, plan.Defaults[1].Type);
        Assert.True(plan.Defaults[1].Material.IsNone);
    }

    [Fact]
    public void Defaults_need_a_parameter_and_a_material_key()
    {
        var (_, problems) = Parse("""{"defaults":[{"material":"A"},{"parameter":"P"}]}""");
        Assert.Contains(problems, p => p.StartsWith("defaults[0].parameter is required"));
        Assert.Contains(problems, p => p.StartsWith("defaults[1].material is required"));
    }

    // ---------------------------------------------------------------- ordering and same-call references

    [Fact]
    public void Lists_run_in_the_documented_order()
    {
        Assert.Equal(new[] { "materials", "assign", "associate", "defaults" }, ModelFamilyMaterialArgs.Order);
    }

    [Fact]
    public void A_material_made_in_the_same_call_resolves_for_the_later_lists()
    {
        // The executor adds each created material to the catalog; the same rule then finds it by name.
        var catalog = new ModelFamilyMaterialCatalog(new[] { (10L, "Steel") });
        Assert.Equal(ModelFamilyMaterialMatch.Missing, catalog.Resolve("Plastic White").Match);
        catalog.Add(11, "Plastic White");
        var found = catalog.Resolve("Plastic White");
        Assert.Equal(ModelFamilyMaterialMatch.Exact, found.Match);
        Assert.Equal(11, found.Id);
    }

    [Fact]
    public void Describe_counts_what_the_call_asks_for()
    {
        var (plan, _) = Parse("""
            {"materials":[{"name":"A"},{"name":"B"}],"assign":[{"elements":[1,2],"material":"A"}],
             "associate":[{"elements":[3],"parameter":"P"}],"defaults":[{"parameter":"P","material":"B"}]}
            """);
        Assert.Equal("2 materials, 2 direct assignments, 1 association, 1 type default", plan.Describe());
        Assert.False(plan.IsEmpty);
        Assert.True(Parse("{}").Plan.IsEmpty);
        Assert.Equal("no changes", Parse("{}").Plan.Describe());
    }

    [Fact]
    public void A_single_object_counts_as_a_list_of_one_and_a_non_object_is_a_problem()
    {
        var (plan, problems) = Parse("""{"materials":{"name":"A"},"assign":[3]}""");
        Assert.Single(plan.Materials);
        Assert.Contains(problems, p => p.StartsWith("assign[0]: must be an object"));
    }

    // ---------------------------------------------------------------- resolution rule

    private static readonly (long, string)[] Known =
    {
        (1, "Steel"), (2, "steel"), (3, "Plastic White"), (4, "Oak"), (5, "OAK ")
    };

    [Fact]
    public void An_exact_name_wins_over_case_insensitive_matches()
    {
        var result = ModelFamilyMaterialCatalog.Resolve("Steel", Known);
        Assert.Equal(ModelFamilyMaterialMatch.Exact, result.Match);
        Assert.Equal(1, result.Id);
        Assert.True(result.Found);
        Assert.Null(ModelFamilyMaterialCatalog.ProblemOf(result, "Steel", "assign[0].material"));
        Assert.Null(ModelFamilyMaterialCatalog.WarningOf(result, "Steel", "assign[0].material"));
    }

    [Fact]
    public void A_single_case_insensitive_match_is_used_with_a_warning()
    {
        var result = ModelFamilyMaterialCatalog.Resolve("plastic white", Known);
        Assert.Equal(ModelFamilyMaterialMatch.CaseInsensitive, result.Match);
        Assert.Equal(3, result.Id);
        Assert.True(result.Found);
        var warning = ModelFamilyMaterialCatalog.WarningOf(result, "plastic white", "assign[0].material");
        Assert.Contains("'Plastic White'", warning);
        Assert.Contains("capitalisation", warning);
        Assert.Null(ModelFamilyMaterialCatalog.ProblemOf(result, "plastic white", "assign[0].material"));
    }

    [Fact]
    public void Several_case_insensitive_matches_are_ambiguous_and_list_ids()
    {
        var result = ModelFamilyMaterialCatalog.Resolve("STEEL", Known);
        Assert.Equal(ModelFamilyMaterialMatch.Ambiguous, result.Match);
        Assert.False(result.Found);
        Assert.Null(result.Id);
        Assert.Equal(2, result.Candidates.Count);
        var problem = ModelFamilyMaterialCatalog.ProblemOf(result, "STEEL", "defaults[0].material")!;
        Assert.StartsWith("defaults[0].material: 'STEEL' is ambiguous", problem);
        Assert.Contains("'Steel' id 1", problem);
        Assert.Contains("'steel' id 2", problem);
        Assert.Contains("element id", problem);
    }

    [Fact]
    public void A_missing_name_suggests_close_names_and_the_materials_list()
    {
        var result = ModelFamilyMaterialCatalog.Resolve("Plastic Whit", Known);
        Assert.Equal(ModelFamilyMaterialMatch.Missing, result.Match);
        Assert.Contains("Plastic White", result.Suggestions);
        var problem = ModelFamilyMaterialCatalog.ProblemOf(result, "Plastic Whit", "assign[1].material")!;
        Assert.Contains("the family has no material 'Plastic Whit'", problem);
        Assert.Contains("did you mean 'Plastic White'", problem);
        Assert.Contains("materials list", problem);
    }

    [Fact]
    public void A_missing_name_with_no_close_match_gives_no_suggestion_and_the_project_variant_drops_the_create_hint()
    {
        var result = ModelFamilyMaterialCatalog.Resolve("Zinc", Known);
        var problem = ModelFamilyMaterialCatalog.ProblemOf(result, "Zinc", "assign[0].material")!;
        Assert.DoesNotContain("did you mean", problem);
        var inProject = ModelFamilyMaterialCatalog.ProblemOf(result, "Zinc", "materials[0].copyFromProject", "the project 'Office'")!;
        Assert.Contains("the project 'Office' has no material 'Zinc'", inProject);
        Assert.DoesNotContain("materials list", inProject);
    }

    [Fact]
    public void Whitespace_around_a_wanted_name_is_ignored_and_an_empty_catalog_is_missing()
    {
        Assert.Equal(ModelFamilyMaterialMatch.Exact, ModelFamilyMaterialCatalog.Resolve("  Oak", Known).Match);
        Assert.Equal(ModelFamilyMaterialMatch.Missing, ModelFamilyMaterialCatalog.Resolve("Oak", Array.Empty<(long, string)>()).Match);
    }

    [Fact]
    public void Near_duplicate_names_are_a_warning_when_creating()
    {
        var catalog = new ModelFamilyMaterialCatalog(new[] { (1L, "Steel"), (3L, "Plastic White") });
        var near = catalog.NearDuplicates("steel");
        Assert.Equal(new[] { "Steel" }, near);
        Assert.Contains("differs only in capitalisation", ModelFamilyMaterialCatalog.NearDuplicateWarning("materials[0]", "steel", near));
        Assert.Empty(catalog.NearDuplicates("Steel"));
        Assert.Null(ModelFamilyMaterialCatalog.NearDuplicateWarning("materials[0]", "Zinc", catalog.NearDuplicates("Zinc")));
    }

    [Fact]
    public void Catalog_finds_a_material_by_id()
    {
        var catalog = new ModelFamilyMaterialCatalog(new[] { (1L, "Steel") });
        Assert.Equal("Steel", catalog.ById(1)!.Value.Name);
        Assert.Null(catalog.ById(9));
    }

    // ---------------------------------------------------------------- final state

    [Theory]
    [InlineData("Housing Material", 5, "parameter")]
    [InlineData("Housing Material", -1, "parameter")]
    [InlineData(null, 5, "direct")]
    [InlineData("", 5, "direct")]
    [InlineData(null, -1, "byCategory")]
    [InlineData(null, 0, "byCategory")]
    public void Material_source_follows_the_driving_parameter_then_the_value(string? parameter, long materialId, string expected) =>
        Assert.Equal(expected, ModelFamilyMaterialState.SourceOf(parameter, materialId));

    [Fact]
    public void State_row_describes_a_direct_assignment()
    {
        var row = ModelFamilyMaterialState.Describe(101, "Extrusion 101", null, null, 11, "Plastic White");
        Assert.Equal("direct", (string?)row["materialSource"]);
        Assert.Equal("Plastic White", (string?)row["material"]);
        Assert.Equal(11, (long?)row["materialId"]);
        Assert.Equal(JTokenType.Null, row["parameter"]!.Type);
        Assert.Null(row["parameterIsInstance"]);
    }

    [Fact]
    public void State_row_describes_a_parameter_driven_instance_material()
    {
        var row = ModelFamilyMaterialState.Describe(102, "Blend 102", "Housing Material", true, 12, "Oak");
        Assert.Equal("parameter", (string?)row["materialSource"]);
        Assert.Equal("Housing Material", (string?)row["parameter"]);
        Assert.True((bool?)row["parameterIsInstance"]);
        Assert.Equal("Oak", (string?)row["material"]);
    }

    [Fact]
    public void State_row_describes_by_category_with_no_material()
    {
        var row = ModelFamilyMaterialState.Describe(103, "Sweep 103", null, null, -1, "");
        Assert.Equal("byCategory", (string?)row["materialSource"]);
        Assert.Equal(JTokenType.Null, row["material"]!.Type);
        Assert.Equal(JTokenType.Null, row["materialId"]!.Type);
    }

    [Theory]
    [InlineData("Extrusion", "extrusion")]
    [InlineData("SweptBlend", "swept blend")]
    [InlineData("Revolution", "revolution")]
    [InlineData("ReferencePlane", "ReferencePlane")]
    public void Element_kinds_are_named_in_plain_words(string typeName, string expected) =>
        Assert.Equal(expected, ModelFamilyMaterialState.KindOf(typeName));

    [Fact]
    public void An_element_without_a_material_property_names_its_kind()
    {
        var message = ModelFamilyMaterialState.NoMaterialParameter("assign[0].elements", 77, "ReferencePlane");
        Assert.Contains("element 77 is a ReferencePlane", message);
        Assert.Contains("extrusion", message);
    }

    // ---------------------------------------------------------------- tool surface

    private const string PreviewName = "revit_preview_edit_model_family_materials";
    private const string EditName = "revit_edit_model_family_materials";

    [Fact]
    public void The_pair_exists_with_the_right_permissions_and_category()
    {
        var tools = ToolSourceScan.Addin.Value;
        Assert.Equal("ReadOnly", tools[PreviewName].Permission);
        Assert.Equal("RequiresApproval", tools[EditName].Permission);
        foreach (var name in new[] { PreviewName, EditName })
            Assert.Contains("ToolCategory.FamilyCreation", tools[name].Source);
        Assert.Contains("IApprovalSummaryTool", tools[EditName].Source);
    }

    [Fact]
    public void The_tools_are_registered_and_the_sections_added()
    {
        var registration = ToolSourceScan.AddinClasses.Value.Single(c => c.Class == "ModelFamilyMaterialTools").Body;
        Assert.Contains("new PreviewEditModelFamilyMaterialsTool()", registration);
        Assert.Contains("new EditModelFamilyMaterialsTool()", registration);
        Assert.Contains("AddSection(\"materials\"", registration);
        Assert.Contains("AddSection(\"materialAssignments\"", registration);
    }

    [Fact]
    public void The_bridge_methods_exist_and_only_the_preview_is_read_only()
    {
        var bridge = ToolSourceScan.Bridge.Value.ToDictionary(b => b.Name);
        Assert.True(bridge[PreviewName].ReadOnly);
        Assert.False(bridge[EditName].ReadOnly);
        foreach (var name in new[] { PreviewName, EditName })
            Assert.Contains(name, bridge[name].Targets);
    }

    [Theory]
    [InlineData(PreviewName)]
    [InlineData(EditName)]
    public void Both_tools_are_in_the_family_editor_group(string tool) =>
        Assert.Equal("familyeditor", BridgeToolGroups.GroupOf(tool));

    [Fact]
    public void The_edit_tool_is_classified_as_creating_for_plans()
    {
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify(EditName));
        Assert.True(PlanToolClassification.MayCreate(EditName));
    }

    [Fact]
    public void The_bridge_sends_exactly_the_lists_the_add_in_reads()
    {
        var source = SendSource();
        foreach (var key in ModelFamilyMaterialArgs.Order)
            Assert.Contains($"args[\"{key}\"]", source);
    }

    private static string SendSource() =>
        File.ReadAllText(Path.Combine(ToolSourceScan.Root(), "RevitMCP.Bridge", "RevitMcpTools.ModelFamilyMaterials.cs"));
}
