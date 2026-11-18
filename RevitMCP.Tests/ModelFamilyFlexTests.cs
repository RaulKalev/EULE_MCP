using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Flex tests and placed-instance checks (#176): case generation, expectations, evaluation, fixed positions, comparison and the tool surface.</summary>
public class ModelFamilyFlexTests
{
    private static Dictionary<string, object?> Args(string json) =>
        JObject.Parse(json).Properties().ToDictionary(p => p.Name, p => (object?)p.Value);

    private static Dictionary<string, JToken?> Values(string json) =>
        JObject.Parse(json).Properties().ToDictionary(p => p.Name, p => (JToken?)p.Value, StringComparer.OrdinalIgnoreCase);

    private static ModelFamilyFlexCheck Check(string json, ModelFamilyFlexScope scope = ModelFamilyFlexScope.Family)
    {
        var problems = new List<string>();
        var checks = ModelFamilyFlexChecks.Parse(new List<JObject> { JObject.Parse(json) }, scope, problems);
        Assert.Empty(problems);
        return Assert.Single(checks);
    }

    private static List<string> CheckProblems(string json, ModelFamilyFlexScope scope = ModelFamilyFlexScope.Family)
    {
        var problems = new List<string>();
        ModelFamilyFlexChecks.Parse(new List<JObject> { JObject.Parse(json) }, scope, problems);
        return problems;
    }

    private static ModelFamilyFlexPlan Plan(string json, List<string> problems, string[]? types = null, string? current = "Small")
    {
        var spec = ModelFamilyFlexSpec.Parse(Args(json), problems);
        return ModelFamilyFlexCases.Build(spec, types ?? new[] { "Small", "Large" }, current, problems);
    }

    // ---------------------------------------------------------------- expressions

    [Fact]
    public void A_literal_expression_is_its_number()
    {
        var expr = ModelFamilyFlexExpr.Parse(new JValue(150), "x", new List<string>(), numeric: true)!;
        Assert.Equal(150, expr.Number(Values("{}")).Value);
    }

    [Fact]
    public void A_parameter_expression_scales_and_shifts_the_case_value()
    {
        var expr = ModelFamilyFlexExpr.Parse(JObject.Parse("{\"parameter\":\"Width\",\"factor\":-0.5,\"offset\":10}"), "x", new List<string>(), numeric: true)!;
        Assert.Equal(-140, expr.Number(Values("{\"width\":300}")).Value);
        Assert.Equal("Width × -0.5 + 10", expr.Text());
    }

    [Fact]
    public void A_missing_parameter_is_an_error_not_zero()
    {
        var expr = ModelFamilyFlexExpr.Of("Depth");
        var (value, error) = expr.Number(Values("{\"Width\":300}"));
        Assert.Null(value);
        Assert.Contains("'Depth' has no value", error);
    }

    [Fact]
    public void Yes_no_counts_as_one_and_zero()
    {
        Assert.Equal(1, ModelFamilyFlexExpr.Of("Lens").Number(Values("{\"Lens\":true}")).Value);
    }

    [Fact]
    public void An_unscaled_parameter_keeps_its_token_for_text_values()
    {
        var (value, _) = ModelFamilyFlexExpr.Of("Housing Material").Value(Values("{\"Housing Material\":\"Steel\"}"));
        Assert.Equal("Steel", (string?)value);
    }

    [Fact]
    public void A_numeric_expectation_refuses_text_and_lists()
    {
        var problems = new List<string>();
        ModelFamilyFlexExpr.Parse(new JValue("wide"), "checks[0].size.x", problems, numeric: true);
        ModelFamilyFlexExpr.Parse(new JArray(1, 2), "checks[0].about", problems, numeric: true);
        ModelFamilyFlexExpr.Parse(JObject.Parse("{\"factor\":2}"), "checks[0].min[0]", problems, numeric: true);
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("checks[0].min[0].parameter is required"));
    }

    // ---------------------------------------------------------------- checks

    [Fact]
    public void Every_check_kind_parses()
    {
        var problems = new List<string>();
        var items = new[]
        {
            "{\"kind\":\"boundingBox\",\"size\":{\"x\":{\"parameter\":\"Width\"}},\"min\":[{\"parameter\":\"Width\",\"factor\":-0.5},null,0]}",
            "{\"kind\":\"plane\",\"plane\":\"Back\",\"fixed\":true}",
            "{\"kind\":\"face\",\"element\":12,\"face\":\"Back\",\"onPlane\":\"Back\"}",
            "{\"kind\":\"centered\",\"axis\":\"x\"}",
            "{\"kind\":\"parameter\",\"parameter\":\"Voltage\",\"equals\":230}",
            "{\"kind\":\"material\",\"element\":12,\"equals\":{\"parameter\":\"Housing Material\"}}",
            "{\"kind\":\"connector\",\"connector\":\"Power\",\"origin\":[0,{\"parameter\":\"Depth\"},{\"parameter\":\"Height\",\"factor\":0.5}],\"direction\":[0,1,0],\"values\":{\"RBS_ELEC_VOLTAGE\":{\"parameter\":\"Voltage\"}}}",
            "{\"kind\":\"symbol\",\"instance\":40,\"at\":[0,0,0]}",
            "{\"kind\":\"exists\",\"elements\":[12,13]}",
            "{\"kind\":\"cut\",\"void\":14,\"solid\":12}"
        }.Select(JObject.Parse).ToList();
        var checks = ModelFamilyFlexChecks.Parse(items, ModelFamilyFlexScope.Family, problems);
        Assert.Empty(problems);
        Assert.Equal(10, checks.Count);
        Assert.Equal("centred", checks[3].Kind);
        Assert.Equal("back", checks[2].Face);
        Assert.Equal("checks[4]", checks[4].Id);
        Assert.Contains("Voltage", checks[6].ReferencedParameters());
        Assert.Contains("Height", checks[6].ReferencedParameters());
    }

    [Fact]
    public void Unknown_kinds_and_keys_are_problems()
    {
        Assert.Contains(CheckProblems("{\"kind\":\"volume\"}"), p => p.Contains("'volume' is not a check"));
        Assert.Contains(CheckProblems("{\"kind\":\"plane\",\"plane\":\"Back\",\"fixed\":true,\"offset\":2}"), p => p.Contains("checks[0].offset: unknown key"));
        Assert.Contains(CheckProblems("{\"plane\":\"Back\"}"), p => p.Contains("kind is required"));
    }

    [Fact]
    public void A_check_of_the_other_tool_says_where_it_belongs()
    {
        Assert.Contains(CheckProblems("{\"kind\":\"hosted\"}"), p => p.Contains("revit_check_model_family_instances"));
        Assert.Contains(CheckProblems("{\"kind\":\"symbol\",\"instance\":4,\"fixed\":true}", ModelFamilyFlexScope.Instance), p => p.Contains("revit_flex_model_family"));
    }

    [Fact]
    public void Instance_checks_have_no_family_element_keys()
    {
        Assert.Contains(CheckProblems("{\"kind\":\"boundingBox\",\"element\":12,\"size\":{\"x\":100}}", ModelFamilyFlexScope.Instance), p => p.Contains("element: unknown key"));
        Assert.Contains(CheckProblems("{\"kind\":\"connector\",\"connector\":1,\"originFixed\":true}", ModelFamilyFlexScope.Instance), p => p.Contains("originFixed: unknown key"));
        Assert.Contains(CheckProblems("{\"kind\":\"connector\",\"connector\":1,\"connected\":true}"), p => p.Contains("connected: unknown key"));
    }

    [Fact]
    public void A_check_needs_something_to_verify()
    {
        Assert.Contains(CheckProblems("{\"kind\":\"boundingBox\"}"), p => p.Contains("give size, min or max"));
        Assert.Contains(CheckProblems("{\"kind\":\"plane\",\"plane\":\"Back\"}"), p => p.Contains("at {axis, value} or fixed"));
        Assert.Contains(CheckProblems("{\"kind\":\"connector\",\"connector\":3}"), p => p.Contains("give at least one of"));
        Assert.Contains(CheckProblems("{\"kind\":\"count\",\"type\":\"Small\"}", ModelFamilyFlexScope.Instance), p => p.Contains("give min or max"));
    }

    [Fact]
    public void Check_ids_must_be_unique()
    {
        var problems = new List<string>();
        ModelFamilyFlexChecks.Parse(new List<JObject>
        {
            JObject.Parse("{\"id\":\"rear\",\"kind\":\"plane\",\"plane\":\"Back\",\"fixed\":true}"),
            JObject.Parse("{\"id\":\"Rear\",\"kind\":\"plane\",\"plane\":\"Front\",\"fixed\":true}")
        }, ModelFamilyFlexScope.Family, problems);
        Assert.Contains(problems, p => p.Contains("'Rear' is used by another check"));
    }

    [Fact]
    public void Bad_axis_face_and_tolerance_are_problems()
    {
        Assert.Contains(CheckProblems("{\"kind\":\"centred\",\"axis\":\"w\"}"), p => p.Contains("'w' is not an axis"));
        Assert.Contains(CheckProblems("{\"kind\":\"face\",\"element\":1,\"face\":\"side\",\"fixed\":true}"), p => p.Contains("'side' is not one of"));
        Assert.Contains(CheckProblems("{\"kind\":\"centred\",\"axis\":\"x\",\"tolerance\":-1}"), p => p.Contains("must not be negative"));
    }

    [Theory]
    [InlineData("PowerBalanced", "powerBalanced")]
    [InlineData("Data", "data")]
    [InlineData("DataCircuit", "data")]
    [InlineData("SupplyAir", "supplyAir")]
    [InlineData("Something", "something")]
    public void Placed_connector_system_types_get_the_connector_tools_names(string member, string wire) =>
        Assert.Equal(wire, ModelFamilyFlexChecks.SystemTypeWireName(member));

    // ---------------------------------------------------------------- arguments

    [Fact]
    public void Ranges_must_be_numeric_or_yes_no_with_min_below_max()
    {
        var problems = new List<string>();
        ModelFamilyFlexSpec.Parse(Args("{\"ranges\":[{\"parameter\":\"Width\",\"min\":300,\"max\":200},{\"parameter\":\"Mat\",\"min\":\"Steel\",\"max\":\"PVC\"},{\"parameter\":\"Depth\",\"min\":1,\"max\":2,\"steps\":6},{\"parameter\":\"X\",\"min\":1}]}"), problems);
        Assert.Contains(problems, p => p.Contains("ranges[0]: min (300) must be less than max (200)"));
        Assert.Contains(problems, p => p.Contains("ranges[1]: min and max must both be numbers"));
        Assert.Contains(problems, p => p.Contains("ranges[2].steps"));
        Assert.Contains(problems, p => p.Contains("ranges[3]: min and max are required"));
    }

    [Fact]
    public void Combine_maxCases_and_reopenCopy_are_checked()
    {
        var problems = new List<string>();
        var spec = ModelFamilyFlexSpec.Parse(Args("{\"combine\":\"all\",\"maxCases\":301,\"reopenCopy\":\"maybe\",\"cases\":[{\"type\":\"Small\"}]}"), problems);
        Assert.Contains(problems, p => p.StartsWith("combine: 'all'"));
        Assert.Contains(problems, p => p.StartsWith("maxCases: must be a whole number from 1 to 300"));
        Assert.Contains(problems, p => p.StartsWith("reopenCopy"));
        Assert.Contains(problems, p => p.Contains("cases[0].values is required"));
        Assert.False(spec.ReopenCopy);
    }

    // ---------------------------------------------------------------- cases

    [Fact]
    public void Every_type_runs_as_it_is_and_each_range_value_alone()
    {
        var problems = new List<string>();
        var plan = Plan("{\"ranges\":[{\"parameter\":\"Width\",\"min\":200,\"max\":400,\"steps\":3},{\"parameter\":\"Depth\",\"min\":80,\"max\":120}]}", problems);
        Assert.Empty(problems);
        // 2 types × (1 + 3 + 2)
        Assert.Equal(12, plan.Cases.Count);
        Assert.Equal(new[] { "Small", "Small: Width = 200", "Small: Width = 300", "Small: Width = 400", "Small: Depth = 80", "Small: Depth = 120" },
            plan.Cases.Take(6).Select(c => c.Name));
        Assert.All(plan.Cases.Where(c => c.Kind == "range"), c => Assert.Single(c.Values));
    }

    [Fact]
    public void Extremes_add_all_minimum_and_all_maximum_per_type()
    {
        var problems = new List<string>();
        var plan = Plan("{\"combine\":\"extremes\",\"types\":[\"Large\"],\"ranges\":[{\"parameter\":\"Width\",\"min\":200,\"max\":400},{\"parameter\":\"Depth\",\"min\":80,\"max\":120}]}", problems);
        Assert.Empty(problems);
        var low = plan.Cases.Single(c => c.Name == "Large: all minimum");
        Assert.Equal(new[] { 200.0, 80.0 }, low.Values.Select(v => v.Value.Value<double>()));
        Assert.Equal(new[] { 400.0, 120.0 }, plan.Cases.Single(c => c.Name == "Large: all maximum").Values.Select(v => v.Value.Value<double>()));
    }

    [Fact]
    public void A_range_can_be_limited_to_some_types()
    {
        var problems = new List<string>();
        var plan = Plan("{\"ranges\":[{\"parameter\":\"Width\",\"min\":200,\"max\":400,\"types\":[\"large\"]}]}", problems);
        Assert.Empty(problems);
        Assert.Equal(new[] { "Small", "Large", "Large: Width = 200", "Large: Width = 400" }, plan.Cases.Select(c => c.Name));
    }

    [Fact]
    public void Yes_no_ranges_take_false_and_true()
    {
        var problems = new List<string>();
        var plan = Plan("{\"types\":[\"Small\"],\"ranges\":[{\"parameter\":\"Lens\",\"min\":false,\"max\":true}]}", problems);
        Assert.Empty(problems);
        Assert.Equal(new[] { "Small", "Small: Lens = false", "Small: Lens = true" }, plan.Cases.Select(c => c.Name));
    }

    [Fact]
    public void Explicit_cases_default_to_the_current_type_and_duplicates_are_dropped()
    {
        var problems = new List<string>();
        var plan = Plan("{\"types\":[\"Small\"],\"ranges\":[{\"parameter\":\"Width\",\"min\":200,\"max\":400}],\"cases\":[{\"values\":{\"width\":200.0}},{\"name\":\"steel\",\"type\":\"Large\",\"values\":{\"Housing Material\":\"Steel\"}}]}", problems);
        Assert.Empty(problems);
        Assert.Equal(1, plan.Duplicates);
        Assert.Equal(4, plan.Cases.Count);
        Assert.Equal("Large", plan.Cases.Single(c => c.Name == "steel").Type);
    }

    [Fact]
    public void Repeated_names_are_made_unique()
    {
        var problems = new List<string>();
        var plan = Plan("{\"types\":[\"Small\"],\"cases\":[{\"name\":\"a\",\"values\":{\"Width\":1}},{\"name\":\"a\",\"values\":{\"Width\":2}}]}", problems);
        Assert.Equal(new[] { "Small", "a", "a (2)" }, plan.Cases.Select(c => c.Name));
    }

    [Fact]
    public void Too_many_cases_is_a_problem_that_says_how_many()
    {
        var problems = new List<string>();
        var plan = Plan("{\"maxCases\":5,\"ranges\":[{\"parameter\":\"Width\",\"min\":200,\"max\":400,\"steps\":5}]}", problems);
        Assert.Equal(12, plan.Cases.Count);
        Assert.Contains(problems, p => p.Contains("12 cases were requested") && p.Contains("maxCases = 5"));
    }

    [Fact]
    public void Unknown_type_names_get_suggestions()
    {
        var problems = new List<string>();
        Plan("{\"types\":[\"Smal\"],\"cases\":[{\"type\":\"Huge\",\"values\":{\"Width\":1}}]}", problems);
        Assert.Contains(problems, p => p.StartsWith("types[0]: the family has no type 'Smal'; did you mean 'Small'"));
        Assert.Contains(problems, p => p.StartsWith("cases[0].type: the family has no type 'Huge'"));
    }

    [Fact]
    public void A_family_without_types_flexes_its_current_state_once()
    {
        var problems = new List<string>();
        var plan = Plan("{}", problems, Array.Empty<string>(), current: null);
        Assert.Empty(problems);
        var only = Assert.Single(plan.Cases);
        Assert.Equal(ModelFamilyFlexCases.CurrentStateName, only.Name);
        Assert.Equal(string.Empty, only.Type);
    }

    [Fact]
    public void Types_run_in_the_family_order()
    {
        var problems = new List<string>();
        var plan = Plan("{\"types\":[\"Large\",\"Small\"]}", problems);
        Assert.Equal(new[] { "Small", "Large" }, plan.Types);
    }

    // ---------------------------------------------------------------- evaluation

    private static ModelFamilyFlexObservation Box(double[] min, double[] max) => new() { Min = min, Max = max };

    [Fact]
    public void A_bounding_box_that_follows_its_parameters_passes()
    {
        var check = Check("{\"kind\":\"boundingBox\",\"size\":{\"x\":{\"parameter\":\"Width\"},\"z\":{\"parameter\":\"Height\"}},\"min\":[{\"parameter\":\"Width\",\"factor\":-0.5},null,null]}");
        var failures = ModelFamilyFlexEvaluator.Evaluate(check, Box(new[] { -150.0, 0, 0 }, new[] { 150.0, 80, 400 }), Values("{\"Width\":300,\"Height\":400}"));
        Assert.Empty(failures);
    }

    [Fact]
    public void A_bounding_box_that_does_not_follow_reports_expected_actual_and_difference()
    {
        var check = Check("{\"id\":\"enclosure\",\"kind\":\"boundingBox\",\"size\":{\"x\":{\"parameter\":\"Width\"}}}");
        var failure = Assert.Single(ModelFamilyFlexEvaluator.Evaluate(check, Box(new[] { -150.0, 0, 0 }, new[] { 150.0, 80, 400 }), Values("{\"Width\":400}")));
        Assert.Equal("enclosure", failure.Id);
        Assert.Equal("size.x", failure.What);
        Assert.Equal(400, failure.Expected!.Value<double>());
        Assert.Equal(300, failure.Actual!.Value<double>());
        Assert.Equal(-100, failure.Difference);
        Assert.Equal("Width", failure.Expression);
    }

    [Fact]
    public void Centred_compares_the_middle_of_the_box()
    {
        var check = Check("{\"kind\":\"centred\",\"axis\":\"x\"}");
        Assert.Empty(ModelFamilyFlexEvaluator.Evaluate(check, Box(new[] { -150.0, 0, 0 }, new[] { 150.0, 1, 1 }), Values("{}")));
        var failure = Assert.Single(ModelFamilyFlexEvaluator.Evaluate(check, Box(new[] { -100.0, 0, 0 }, new[] { 200.0, 1, 1 }), Values("{}")));
        Assert.Equal(50, failure.Difference);
    }

    [Fact]
    public void A_plane_at_a_coordinate_must_be_perpendicular_to_that_axis()
    {
        var check = Check("{\"kind\":\"plane\",\"plane\":\"Back\",\"at\":{\"axis\":\"y\",\"value\":{\"parameter\":\"Depth\"}}}");
        var onY = new ModelFamilyFlexObservation { Plane = new ModelFamilyFlexPlane { Point = new[] { 5.0, 100, 0 }, Normal = new[] { 0.0, -1, 0 } } };
        Assert.Empty(ModelFamilyFlexEvaluator.Evaluate(check, onY, Values("{\"Depth\":100}")));
        var onX = new ModelFamilyFlexObservation { Plane = new ModelFamilyFlexPlane { Point = new[] { 5.0, 100, 0 }, Normal = new[] { 1.0, 0, 0 } } };
        Assert.Contains(ModelFamilyFlexEvaluator.Evaluate(check, onX, Values("{\"Depth\":100}")), f => f.Message.Contains("not perpendicular to y"));
    }

    [Fact]
    public void A_face_on_a_plane_must_be_on_it_and_parallel()
    {
        var check = Check("{\"kind\":\"face\",\"element\":12,\"face\":\"back\",\"onPlane\":\"Back\"}");
        var face = new ModelFamilyFlexPlane { Point = new[] { 10.0, 0.2, 50 }, Normal = new[] { 0.0, 1, 0 } };
        var plane = new ModelFamilyFlexPlane { Point = new[] { 0.0, 0, 0 }, Normal = new[] { 0.0, -1, 0 } };
        Assert.Empty(ModelFamilyFlexEvaluator.Evaluate(check, new ModelFamilyFlexObservation { Plane = face, Target = plane }, Values("{}")));
        face.Point = new[] { 10.0, 3, 50 };
        var failure = Assert.Single(ModelFamilyFlexEvaluator.Evaluate(check, new ModelFamilyFlexObservation { Plane = face, Target = plane }, Values("{}")));
        Assert.Equal("onPlane", failure.What);
        Assert.Equal(3, failure.Difference);
    }

    [Fact]
    public void A_parameter_check_compares_formula_results_and_bounds()
    {
        var check = Check("{\"kind\":\"parameter\",\"parameter\":\"Half Height\",\"equals\":{\"parameter\":\"Height\",\"factor\":0.5},\"max\":250}");
        Assert.Empty(ModelFamilyFlexEvaluator.Evaluate(check, new ModelFamilyFlexObservation { Value = 200 }, Values("{\"Height\":400}")));
        var failures = ModelFamilyFlexEvaluator.Evaluate(check, new ModelFamilyFlexObservation { Value = 300 }, Values("{\"Height\":400}"));
        Assert.Equal(new[] { "equals", "max" }, failures.Select(f => f.What));
    }

    [Fact]
    public void Text_values_compare_without_case()
    {
        var check = Check("{\"kind\":\"material\",\"element\":12,\"equals\":{\"parameter\":\"Housing Material\"}}");
        Assert.Empty(ModelFamilyFlexEvaluator.Evaluate(check, new ModelFamilyFlexObservation { Value = "steel" }, Values("{\"Housing Material\":\"Steel\"}")));
        Assert.Single(ModelFamilyFlexEvaluator.Evaluate(check, new ModelFamilyFlexObservation { Value = JValue.CreateNull() }, Values("{\"Housing Material\":\"Steel\"}")));
    }

    [Fact]
    public void An_instance_material_passes_when_one_of_its_solids_has_it()
    {
        var check = Check("{\"kind\":\"material\",\"equals\":\"Steel\"}", ModelFamilyFlexScope.Instance);
        Assert.Empty(ModelFamilyFlexEvaluator.Evaluate(check, new ModelFamilyFlexObservation { Materials = new List<string> { "Glass", "Steel" } }, Values("{}")));
        Assert.Contains(ModelFamilyFlexEvaluator.Evaluate(check, new ModelFamilyFlexObservation { Materials = new List<string> { "Glass" } }, Values("{}")), f => f.Message.Contains("'Glass'"));
    }

    [Fact]
    public void A_connector_follows_height_points_back_and_carries_the_family_values()
    {
        var check = Check("{\"kind\":\"connector\",\"connector\":\"Power\",\"origin\":[0,0,{\"parameter\":\"Height\",\"factor\":0.5}],\"direction\":[0,-1,0],\"systemType\":\"powerBalanced\",\"values\":{\"RBS_ELEC_VOLTAGE\":{\"parameter\":\"Voltage\"},\"RBS_ELEC_APPARENT_LOAD\":{\"parameter\":\"Apparent Load\"}}}");
        var observation = new ModelFamilyFlexObservation
        {
            Point = new[] { 0.0, 0, 200 },
            Direction = new[] { 0.0, -1, 0 },
            SystemType = "PowerBalanced"
        };
        observation.Values["RBS_ELEC_VOLTAGE"] = 230;
        observation.Values["RBS_ELEC_APPARENT_LOAD"] = 15.0;
        var values = Values("{\"Height\":400,\"Voltage\":230,\"Apparent Load\":15}");
        Assert.Empty(ModelFamilyFlexEvaluator.Evaluate(check, observation, values));

        observation.Point = new[] { 0.0, 0, 150 };
        observation.Direction = new[] { 0.0, 1, 0 };
        observation.Values["RBS_ELEC_APPARENT_LOAD"] = 20.0;
        var failures = ModelFamilyFlexEvaluator.Evaluate(check, observation, values);
        Assert.Equal(new[] { "origin.z", "direction", "values.RBS_ELEC_APPARENT_LOAD" }, failures.Select(f => f.What));
        Assert.Equal(180, failures[1].Difference);
    }

    [Fact]
    public void A_connector_on_a_face_must_sit_on_it_and_point_out_of_it()
    {
        var check = Check("{\"kind\":\"connector\",\"connector\":5,\"onFace\":{\"element\":12,\"face\":\"back\"}}");
        var back = new ModelFamilyFlexPlane { Point = new[] { 0.0, 0, 100 }, Normal = new[] { 0.0, 1, 0 } };
        Assert.Empty(ModelFamilyFlexEvaluator.Evaluate(check, new ModelFamilyFlexObservation { Point = new[] { 10.0, 0, 200 }, Direction = new[] { 0.0, 1, 0 }, Target = back }, Values("{}")));
        var failures = ModelFamilyFlexEvaluator.Evaluate(check, new ModelFamilyFlexObservation { Point = new[] { 10.0, 0, 200 }, Direction = new[] { 0.0, -1, 0 }, Target = back }, Values("{}"));
        Assert.Equal("onFace.direction", Assert.Single(failures).What);
    }

    [Fact]
    public void An_instance_connector_reports_its_system_membership()
    {
        var check = Check("{\"kind\":\"connector\",\"connector\":\"Data\",\"connected\":true,\"system\":\"D-01\"}", ModelFamilyFlexScope.Instance);
        var failures = ModelFamilyFlexEvaluator.Evaluate(check, new ModelFamilyFlexObservation { Connected = false }, Values("{}"));
        Assert.Equal(new[] { "system", "connected" }, failures.Select(f => f.What));
        Assert.Empty(ModelFamilyFlexEvaluator.Evaluate(check, new ModelFamilyFlexObservation { Connected = true, System = "d-01" }, Values("{}")));
    }

    [Fact]
    public void A_missing_value_on_the_connector_is_named()
    {
        var check = Check("{\"kind\":\"connector\",\"connector\":5,\"values\":{\"Voltage\":230}}");
        Assert.Contains("has no parameter 'Voltage'", Assert.Single(ModelFamilyFlexEvaluator.Evaluate(check, new ModelFamilyFlexObservation(), Values("{}"))).Message);
    }

    [Fact]
    public void Exists_cut_symbol_and_hosted_checks_decide_from_the_observation()
    {
        var exists = Check("{\"kind\":\"exists\",\"elements\":[12]}");
        var empty = new ModelFamilyFlexObservation { Exists = true };
        empty.Empty.Add(12);
        Assert.Equal("geometry", Assert.Single(ModelFamilyFlexEvaluator.Evaluate(exists, empty, Values("{}"))).What);

        var cut = Check("{\"kind\":\"cut\",\"void\":14,\"solid\":12}");
        Assert.Empty(ModelFamilyFlexEvaluator.Evaluate(cut, new ModelFamilyFlexObservation { CutBy = new List<long> { 14 } }, Values("{}")));
        Assert.Single(ModelFamilyFlexEvaluator.Evaluate(cut, new ModelFamilyFlexObservation { CutBy = new List<long>() }, Values("{}")));

        var symbol = Check("{\"kind\":\"symbol\",\"instance\":40,\"at\":[0,0,0]}");
        Assert.Equal("at.y", Assert.Single(ModelFamilyFlexEvaluator.Evaluate(symbol, new ModelFamilyFlexObservation { Point = new[] { 0.0, 2, 0 } }, Values("{}"))).What);

        var hosted = Check("{\"kind\":\"hosted\"}", ModelFamilyFlexScope.Instance);
        Assert.Empty(ModelFamilyFlexEvaluator.Evaluate(hosted, new ModelFamilyFlexObservation { Hosted = false, NeedsHost = false }, Values("{}")));
        Assert.Contains("needs a host", Assert.Single(ModelFamilyFlexEvaluator.Evaluate(hosted, new ModelFamilyFlexObservation { Hosted = false, NeedsHost = true, Placement = "OneLevelBasedHosted" }, Values("{}"))).Message);
    }

    [Fact]
    public void Count_checks_compare_against_min_and_max()
    {
        var check = Check("{\"kind\":\"count\",\"type\":\"Small\",\"min\":1,\"max\":2}", ModelFamilyFlexScope.Instance);
        Assert.Empty(ModelFamilyFlexEvaluator.EvaluateCount(check, 1));
        Assert.Equal("min", Assert.Single(ModelFamilyFlexEvaluator.EvaluateCount(check, 0)).What);
        Assert.Equal("max", Assert.Single(ModelFamilyFlexEvaluator.EvaluateCount(check, 3)).What);
    }

    [Fact]
    public void Something_that_could_not_be_measured_fails_once_with_the_reason()
    {
        var check = Check("{\"kind\":\"symbol\",\"instance\":40,\"at\":[0,0,0]}");
        var failure = Assert.Single(ModelFamilyFlexEvaluator.Evaluate(check, new ModelFamilyFlexObservation { Missing = "The nested instance 40 no longer exists." }, Values("{}")));
        Assert.Equal("measure", failure.What);
    }

    [Fact]
    public void An_expectation_that_cannot_be_evaluated_is_a_failure_not_a_pass()
    {
        var check = Check("{\"kind\":\"centred\",\"axis\":\"x\",\"about\":{\"parameter\":\"Offset\"}}");
        Assert.Contains("cannot be evaluated", Assert.Single(ModelFamilyFlexEvaluator.Evaluate(check, Box(new[] { 0.0, 0, 0 }, new[] { 1.0, 1, 1 }), Values("{}"))).Message);
    }

    // ---------------------------------------------------------------- across cases

    private static ModelFamilyFlexCaseResult Result(string name, bool committed, string checkId, ModelFamilyFlexObservation observation)
    {
        var result = new ModelFamilyFlexCaseResult { Case = new ModelFamilyFlexCase { Name = name, Type = "Small" }, Committed = committed };
        result.Observations[checkId] = observation;
        return result;
    }

    private static ModelFamilyFlexObservation PlaneAtY(double y) =>
        new() { Plane = new ModelFamilyFlexPlane { Point = new[] { 0.0, y, 0 }, Normal = new[] { 0.0, -1, 0 } } };

    [Fact]
    public void A_fixed_plane_that_moves_fails_in_the_case_where_it_moved()
    {
        var check = Check("{\"id\":\"rear\",\"kind\":\"plane\",\"plane\":\"Back\",\"fixed\":true}");
        var results = new List<ModelFamilyFlexCaseResult>
        {
            Result("Small", true, "rear", PlaneAtY(0)),
            Result("Small: Depth = 80", false, "rear", PlaneAtY(40)),
            Result("Small: Depth = 120", true, "rear", PlaneAtY(0.2)),
            Result("Small: Width = 400", true, "rear", PlaneAtY(25))
        };
        ModelFamilyFlexRun.ApplyFixed(results, new[] { check });
        Assert.Equal(new[] { "ok", "refused", "ok", "failed" }, results.Select(r => r.Status));
        var failure = Assert.Single(results[3].Failures);
        Assert.Equal("fixed", failure.What);
        Assert.Contains("'Small'", failure.Message);
    }

    [Fact]
    public void A_fixed_connector_origin_is_compared_as_a_point()
    {
        var check = Check("{\"kind\":\"connector\",\"connector\":5,\"originFixed\":true}");
        var results = new List<ModelFamilyFlexCaseResult>
        {
            Result("a", true, check.Id, new ModelFamilyFlexObservation { Point = new[] { 0.0, 0, 200 } }),
            Result("b", true, check.Id, new ModelFamilyFlexObservation { Point = new[] { 0.0, 3, 204 } })
        };
        ModelFamilyFlexRun.ApplyFixed(results, new[] { check });
        Assert.Equal(5, Assert.Single(results[1].Failures).Difference);
    }

    [Fact]
    public void The_summary_counts_cases_and_failures_per_check()
    {
        var check = Check("{\"id\":\"rear\",\"kind\":\"plane\",\"plane\":\"Back\",\"fixed\":true}");
        var results = new List<ModelFamilyFlexCaseResult>
        {
            Result("a", true, "rear", PlaneAtY(0)),
            Result("b", true, "rear", PlaneAtY(10)),
            Result("c", false, "rear", PlaneAtY(0))
        };
        ModelFamilyFlexRun.ApplyFixed(results, new[] { check });
        var summary = ModelFamilyFlexRun.Summarize(results, new[] { check });
        Assert.Equal(3, (int)summary["cases"]!);
        Assert.Equal(1, (int)summary["passed"]!);
        Assert.Equal(1, (int)summary["failed"]!);
        Assert.Equal(1, (int)summary["refused"]!);
        Assert.Equal(1, (int)summary["checks"]![0]!["failedCases"]!);
    }

    [Fact]
    public void A_case_writes_its_inputs_status_and_measurements()
    {
        var flexCase = new ModelFamilyFlexCase { Name = "Small: Width = 400", Type = "Small", Kind = "range" };
        flexCase.Values.Add(new KeyValuePair<string, JToken>("Width", 400));
        var result = new ModelFamilyFlexCaseResult { Case = flexCase, Committed = true };
        result.Observations["box"] = Box(new[] { -200.0, 0, 0 }, new[] { 200.0, 80, 400 });
        var json = result.ToJson();
        Assert.Equal(400, (double)json["inputs"]!["Width"]!);
        Assert.Equal("ok", (string?)json["status"]);
        Assert.Equal(400, (double)json["measurements"]!["box"]!["size"]![0]!);

        var refused = new ModelFamilyFlexCaseResult { Case = flexCase, Committed = false, Error = "Constraints are not satisfied." }.ToJson();
        Assert.Equal("refused", (string?)refused["status"]);
        Assert.Null(refused["measurements"]);
    }

    // ---------------------------------------------------------------- reopened copy

    private static JObject CaseJson(string type, string status, double width, params string[] failedIds) => new()
    {
        ["case"] = type,
        ["kind"] = "type",
        ["type"] = type,
        ["status"] = status,
        ["failedChecks"] = new JArray(failedIds.Select(id => new JObject { ["id"] = id })),
        ["measurements"] = new JObject { ["box"] = new JObject { ["size"] = new JArray(width, 80, 400) } },
        ["parameters"] = new JObject { ["Width"] = width }
    };

    [Fact]
    public void Two_equal_runs_compare_equal_per_type()
    {
        var first = new JArray(CaseJson("Small", "ok", 300), CaseJson("Large", "ok", 400), new JObject { ["kind"] = "range", ["type"] = "Small" });
        var second = new JArray(CaseJson("Small", "ok", 300.004), CaseJson("Large", "ok", 400));
        var rows = ModelFamilyFlexRun.Compare(first, second);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True((bool)r["equal"]!));
    }

    [Fact]
    public void A_copy_that_behaves_differently_is_reported_with_the_differences()
    {
        var first = new JArray(CaseJson("Small", "ok", 300), CaseJson("Large", "ok", 400));
        var second = new JArray(CaseJson("Small", "failed", 310, "box"));
        var rows = ModelFamilyFlexRun.Compare(first, second);
        var small = (JArray)rows[0]["differences"]!;
        Assert.Contains(small, d => ((string?)d)!.StartsWith("status: ok before, failed after"));
        Assert.Contains(small, d => ((string?)d)!.Contains("check 'box' fails only after reopening"));
        Assert.Contains(small, d => ((string?)d)!.StartsWith("measurements.box.size[0]"));
        Assert.Contains("not exercised", (string?)rows[1]["differences"]![0]);
    }

    // ---------------------------------------------------------------- tool surface

    private const string FlexName = "revit_flex_model_family";
    private const string InstancesName = "revit_check_model_family_instances";

    [Fact]
    public void Both_tools_are_read_only_family_creation_tools()
    {
        var tools = ToolSourceScan.Addin.Value;
        foreach (var name in new[] { FlexName, InstancesName })
        {
            Assert.Equal("ReadOnly", tools[name].Permission);
            Assert.Contains("ToolCategory.FamilyCreation", tools[name].Source);
            Assert.DoesNotContain("IApprovalSummaryTool", tools[name].Source);
        }
    }

    [Fact]
    public void The_tools_are_registered()
    {
        var registration = ToolSourceScan.AddinClasses.Value.Single(c => c.Class == "ModelFamilyFlexTools").Body;
        Assert.Contains("new FlexModelFamilyTool()", registration);
        Assert.Contains("new CheckModelFamilyInstancesTool()", registration);
    }

    [Fact]
    public void The_flex_runs_in_a_trial_that_always_rolls_back()
    {
        var source = File.ReadAllText(Path.Combine(ToolSourceScan.Root(), "RevitMCP.Addin", "Tools", "ModelFamilies", "ModelFamilyFlexExecutor.cs"));
        Assert.Contains("ModelFamilySession.Trial(", source);
        Assert.DoesNotContain("ModelFamilySession.Edit(", source);
        Assert.DoesNotContain(".Save(", source);
    }

    [Fact]
    public void The_bridge_methods_are_read_only_and_reach_their_tools()
    {
        var bridge = ToolSourceScan.Bridge.Value.ToDictionary(b => b.Name);
        foreach (var name in new[] { FlexName, InstancesName })
        {
            Assert.True(bridge[name].ReadOnly);
            Assert.Contains(name, bridge[name].Targets);
        }
    }

    [Theory]
    [InlineData(FlexName)]
    [InlineData(InstancesName)]
    public void Both_tools_are_in_the_family_editor_group(string tool) =>
        Assert.Equal("familyeditor", BridgeToolGroups.GroupOf(tool));

    [Fact]
    public void The_bridge_sends_every_argument_the_tools_read()
    {
        var source = File.ReadAllText(Path.Combine(ToolSourceScan.Root(), "RevitMCP.Bridge", "RevitMcpTools.ModelFamilyFlex.cs"));
        foreach (var key in ModelFamilyFlexArgs.FlexOrder.Concat(ModelFamilyFlexArgs.InstanceOrder))
            Assert.Contains($"[\"{key}\"]", source);
    }

    [Fact]
    public void The_docs_exist()
    {
        foreach (var doc in new[] { "model-family-flex.md", "model-family-acceptance.md" })
            Assert.True(File.Exists(Path.Combine(ToolSourceScan.Root(), "docs", doc)), doc);
    }
}
