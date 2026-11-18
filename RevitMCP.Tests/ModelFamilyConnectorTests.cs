using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>MEP connectors of a model family (#174): argument parsing, the per-domain tables, units and the tool surface.</summary>
public class ModelFamilyConnectorTests
{
    private static ConnectorEditPlan Parse(out List<string> problems, string? connectors = null, string? edit = null, string? remove = null)
    {
        problems = new List<string>();
        var args = new Dictionary<string, object?>();
        if (connectors != null) args["connectors"] = JToken.Parse(connectors);
        if (edit != null) args["edit"] = JToken.Parse(edit);
        if (remove != null) args["remove"] = JToken.Parse(remove);
        return ModelFamilyConnectorParser.Parse(args, "remove", "connectors", "edit", problems);
    }

    private const string PowerAndData = """
        [
          {"name": "Power", "domain": "electrical", "host": {"element": 1001, "face": "back"}, "systemType": "powerBalanced",
           "position": [0, 30, 100], "primary": true,
           "properties": {"RBS_ELEC_NUMBER_OF_POLES": 1, "RBS_ELEC_POWER_FACTOR": 0.9},
           "associations": {"RBS_ELEC_VOLTAGE": "Voltage", "RBS_ELEC_APPARENT_LOAD": "Apparent Load"}},
          {"name": "Data", "domain": "electrical", "host": {"element": 1001, "face": "back"}, "systemType": "data", "position": [0, 30, 40]}
        ]
        """;

    // â”€â”€ Parsing â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void A_power_and_a_data_connector_parse_with_their_own_labels_and_classifications()
    {
        var plan = Parse(out var problems, PowerAndData);
        Assert.Empty(problems);
        Assert.Equal(2, plan.Create.Count);
        var power = plan.Create[0];
        Assert.Equal("Power", power.Name);
        Assert.Equal(ConnectorDomain.Electrical, power.Domain);
        Assert.Equal("PowerBalanced", power.System!.CreateMember);
        Assert.Equal(ConnectorHostKind.ElementFace, power.Host.Kind);
        Assert.Equal("back", power.Host.FaceLabel);
        Assert.Equal(new[] { 0.0, 1, 0 }, power.Host.FaceNormal);
        Assert.Equal(new[] { 0.0, 30, 100 }, power.Position);
        Assert.True(power.Primary);
        Assert.Equal(2, power.Properties.Count);
        Assert.Equal(1, power.Properties[0].Value.Number);
        Assert.Contains(("RBS_ELEC_VOLTAGE", (string?)"Voltage"), power.Associations);
        Assert.Null(power.Shape);
        var data = plan.Create[1];
        Assert.Equal("Data", data.System!.CreateMember);
        Assert.Equal("DataCircuit", data.System.Classification);
    }

    [Fact]
    public void Operations_run_remove_then_create_then_edit_and_the_plan_keeps_them_apart()
    {
        var plan = Parse(out var problems, PowerAndData, """[{"connector": 2001, "flip": true}]""", "[3001, 3001, 3002]");
        Assert.Empty(problems);
        Assert.Equal(new long[] { 3001, 3002 }, plan.Remove);
        Assert.Equal(2, plan.Create.Count);
        Assert.Equal(2001, Assert.Single(plan.Edit).ConnectorId);
        Assert.Equal(5, plan.Count);
    }

    [Fact]
    public void An_empty_call_is_a_problem()
    {
        Parse(out var problems);
        Assert.Contains(problems, p => p.StartsWith("Nothing to do", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""[{"domain": "gas", "host": "Center (Front/Back)"}]""", "connectors[0].domain: 'gas'")]
    [InlineData("""[{"domain": "electrical", "host": {"element": 5, "face": "back"}}]""", "connectors[0].systemType is required for an electrical connector: powerBalanced")]
    [InlineData("""[{"domain": "electrical", "host": {"element": 5, "face": "back"}, "systemType": "supplyAir"}]""", "is not a system type for electrical connectors")]
    [InlineData("""[{"domain": "conduit", "host": {"element": 5, "face": "back"}, "systemType": "data"}]""", "a conduit connector has no system type; remove it")]
    [InlineData("""[{"domain": "electrical", "host": {"element": 5, "face": "sideways"}, "systemType": "data"}]""", "'sideways' is not a face")]
    [InlineData("""[{"domain": "electrical", "systemType": "data"}]""", "connectors[0].host is required")]
    [InlineData("""[{"domain": "electrical", "host": {"element": 5, "face": "back"}, "systemType": "data", "colour": 1}]""", "connectors[0].colour: unknown key")]
    [InlineData("""[{"domain": "electrical", "host": {"element": 5, "face": "back"}, "systemType": "data", "diameter": 20}]""", "an electrical connector has no shape or size")]
    [InlineData("""[{"domain": "electrical", "host": {"element": 5, "face": "back"}, "systemType": "data", "angle": 20}]""", "an electrical connector has no angle")]
    [InlineData("""[{"domain": "electrical", "host": {"element": 5, "face": [0,0,0]}, "systemType": "data"}]""", "the normal must not be zero")]
    [InlineData("""[{"domain": "electrical", "host": {"reference": ""}, "systemType": "data"}]""", "stable representation")]
    [InlineData("""[{"domain": "electrical", "host": {"element": 5, "face": "back"}, "systemType": "data", "position": [1, 2]}]""", "connectors[0].position: must be [x, y, z]")]
    public void Bad_create_operations_name_the_problem_and_the_fix(string connectors, string expected)
    {
        Parse(out var problems, connectors);
        Assert.Contains(problems, p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void A_property_both_set_and_associated_is_a_problem()
    {
        Parse(out var problems, """[{"domain": "electrical", "host": {"element": 5, "face": "back"}, "systemType": "powerBalanced", "properties": {"Voltage": 230}, "associations": {"voltage": "Voltage"}}]""");
        Assert.Contains(problems, p => p.Contains("properties.Voltage: it is also associated", StringComparison.Ordinal));
    }

    [Fact]
    public void Property_values_take_numbers_texts_flags_and_explicit_units()
    {
        var plan = Parse(out var problems, """[{"domain": "electrical", "host": {"element": 5, "face": "back"}, "systemType": "powerBalanced", "properties": {"a": 230, "b": {"value": 2, "unit": "kVA"}, "c": "Power", "d": true}}]""");
        Assert.Empty(problems);
        var values = plan.Create[0].Properties.ToDictionary(p => p.Name, p => p.Value);
        Assert.Equal(230, values["a"].Number);
        Assert.Equal("kVA", values["b"].Unit);
        Assert.Equal("Power", values["c"].Text);
        Assert.True(values["d"].Flag);
        Assert.Equal(JToken.Parse("""{"value": 2.0, "unit": "kVA"}""").ToString(), values["b"].ToJson().ToString());

        Parse(out problems, """[{"domain": "electrical", "host": {"element": 5, "face": "back"}, "systemType": "powerBalanced", "properties": {"a": [1], "b": {"unit": "V"}}}]""");
        Assert.Contains(problems, p => p.StartsWith("connectors[0].properties.a: give a number", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("connectors[0].properties.b.value is required", StringComparison.Ordinal));
    }

    [Fact]
    public void Hosts_can_be_a_face_label_a_normal_a_stable_reference_or_a_reference_plane()
    {
        var problems = new List<string>();
        var normal = ModelFamilyConnectorParser.ParseHost(JToken.Parse("""{"element": 7, "face": [0, 0.9999, 0.001]}"""), "h", problems)!;
        Assert.Equal("back", normal.FaceLabel);
        var odd = ModelFamilyConnectorParser.ParseHost(JToken.Parse("""{"element": 7, "face": [1, 1, 0]}"""), "h", problems)!;
        Assert.Null(odd.FaceLabel);
        Assert.Equal(ConnectorHostKind.StableReference, ModelFamilyConnectorParser.ParseHost(JToken.Parse("""{"reference": "abc:0:SURFACE"}"""), "h", problems)!.Kind);
        Assert.Equal(ConnectorHostKind.ReferencePlane, ModelFamilyConnectorParser.ParseHost(new JValue("Center (Front/Back)"), "h", problems)!.Kind);
        Assert.Equal(ConnectorHostKind.ReferencePlane, ModelFamilyConnectorParser.ParseHost(new JValue(412), "h", problems)!.Kind);
        Assert.Empty(problems);
    }

    // â”€â”€ Same-call references and links â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void LinkTo_names_an_id_or_a_connector_created_in_the_same_call()
    {
        var plan = Parse(out var problems, """
            [{"domain": "duct", "host": {"element": 5, "face": "left"}, "systemType": "supplyAir", "diameter": 200, "linkTo": {"created": "connectors[1]"}},
             {"domain": "duct", "host": {"element": 5, "face": "right"}, "systemType": "returnAir", "diameter": 200, "linkTo": {"created": 0}}]
            """, """[{"connector": 99, "linkTo": 77}, {"connector": 98, "linkTo": null}]""");
        Assert.Empty(problems);
        Assert.Equal(1, plan.Create[0].LinkTo!.CreatedIndex);
        Assert.Equal(0, plan.Create[1].LinkTo!.CreatedIndex);
        Assert.Equal(77, plan.Edit[0].LinkTo!.Id);
        Assert.True(plan.Edit[1].LinkGiven);
        Assert.Null(plan.Edit[1].LinkTo);
    }

    [Theory]
    [InlineData("""{"created": "connectors[0]"}""", "cannot be linked to itself")]
    [InlineData("""{"created": "connectors[5]"}""", "connectors[5] is not created in this call")]
    [InlineData("""{"created": "pipes[0]"}""", "give a connector id or")]
    [InlineData("null", "a new connector has no link to remove")]
    public void Bad_links_are_problems(string link, string expected)
    {
        Parse(out var problems, $$"""[{"domain": "pipe", "host": {"element": 5, "face": "top"}, "systemType": "sanitary", "linkTo": {{link}}}]""");
        Assert.Contains(problems, p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Two_primary_connectors_of_one_domain_and_edits_of_removed_connectors_are_problems()
    {
        Parse(out var problems, """
            [{"domain": "electrical", "host": {"element": 5, "face": "back"}, "systemType": "powerBalanced", "primary": true},
             {"domain": "electrical", "host": {"element": 5, "face": "back"}, "systemType": "data", "primary": true}]
            """, """[{"connector": 10, "flip": true}, {"connector": 10, "angle": 5}, {"connector": 11, "linkTo": 12}]""", "[10, 12]");
        Assert.Contains(problems, p => p.Contains("only one electrical connector of the family can be primary", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("connector 10 is edited more than once", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("connector 10 is removed in the same call", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("edit[2].linkTo: connector 12 is removed", StringComparison.Ordinal));
    }

    [Fact]
    public void Edits_need_a_connector_and_something_to_change_and_associations_may_dissociate()
    {
        Parse(out var problems, edit: """[{"flip": true}, {"connector": 4}]""");
        Assert.Contains(problems, p => p.StartsWith("edit[0].connector is required", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("edit[1]: nothing to change", StringComparison.Ordinal));

        var plan = Parse(out problems, edit: """[{"connector": 4, "associations": {"RBS_ELEC_VOLTAGE": null, "RBS_ELEC_APPARENT_LOAD": "Apparent Load"}}]""");
        Assert.Empty(problems);
        Assert.Contains(("RBS_ELEC_VOLTAGE", (string?)null), plan.Edit[0].Associations);

        Parse(out problems, """[{"domain": "electrical", "host": {"element": 5, "face": "back"}, "systemType": "data", "associations": {"RBS_ELEC_VOLTAGE": null}}]""");
        Assert.Contains(problems, p => p.Contains("null only dissociates in edit", StringComparison.Ordinal));
    }

    [Fact]
    public void Remove_takes_ids_and_rejects_anything_else()
    {
        var plan = Parse(out var problems, remove: "[1, \"2\", {\"connector\": 3}]");
        Assert.Empty(problems);
        Assert.Equal(new long[] { 1, 2, 3 }, plan.Remove);
        Parse(out problems, remove: "[\"power\"]");
        Assert.Contains(problems, p => p.StartsWith("remove[0]: give a connector element id", StringComparison.Ordinal));
    }

    // â”€â”€ System types per domain â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void Each_domain_has_its_own_system_types()
    {
        Assert.Equal(new[] { "powerBalanced", "powerUnBalanced", "data", "telephone", "security", "fireAlarm", "nurseCall", "controls", "communication" },
            ModelFamilyConnectorTables.SystemTypes(ConnectorDomain.Electrical).Select(t => t.WireName));
        Assert.Equal(new[] { "supplyAir", "returnAir", "exhaustAir", "otherAir", "fitting", "global" },
            ModelFamilyConnectorTables.SystemTypes(ConnectorDomain.Duct).Select(t => t.WireName));
        Assert.Equal(13, ModelFamilyConnectorTables.SystemTypes(ConnectorDomain.Pipe).Count);
        Assert.Empty(ModelFamilyConnectorTables.SystemTypes(ConnectorDomain.Conduit));
        Assert.Empty(ModelFamilyConnectorTables.SystemTypes(ConnectorDomain.CableTray));
        Assert.Equal("FireProtectPreaction", ModelFamilyConnectorTables.SystemType(ConnectorDomain.Pipe, "FIREPROTECTPREACTION")!.CreateMember);
        Assert.Null(ModelFamilyConnectorTables.SystemType(ConnectorDomain.Duct, "sanitary"));
    }

    [Theory]
    [InlineData("DataCircuit", "data")]
    [InlineData("PowerBalanced", "powerBalanced")]
    [InlineData("SupplyAir", "supplyAir")]
    [InlineData("DomesticColdWater", "domesticColdWater")]
    [InlineData("CableTrayConduit", "cableTrayConduit")]
    public void Classifications_read_back_under_their_wire_names(string classification, string wire) =>
        Assert.Equal(wire, ModelFamilyConnectorTables.ClassificationWireName(classification));

    [Theory]
    [InlineData("DomainElectrical", "Invalid", "electrical")]
    [InlineData("DomainHvac", "Rectangular", "duct")]
    [InlineData("DomainPiping", "Round", "pipe")]
    [InlineData("DomainCableTrayConduit", "Round", "conduit")]
    [InlineData("DomainCableTrayConduit", "Rectangular", "cableTray")]
    public void Domains_read_back_and_conduit_is_told_from_cable_tray_by_shape(string domain, string shape, string wire) =>
        Assert.Equal(wire, ModelFamilyConnectorTables.DomainWireName(domain, shape));

    // â”€â”€ Shapes and sizes â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [InlineData(ConnectorDomain.Duct, null, 200.0, null, null, "Round")]
    [InlineData(ConnectorDomain.Duct, null, null, 300.0, 200.0, "Rectangular")]
    [InlineData(ConnectorDomain.Duct, "Oval", null, 300.0, 200.0, "Oval")]
    [InlineData(ConnectorDomain.Pipe, null, 25.0, null, null, "Round")]
    [InlineData(ConnectorDomain.Conduit, null, null, null, null, "Round")]
    [InlineData(ConnectorDomain.CableTray, null, null, 300.0, 100.0, "Rectangular")]
    public void Shapes_follow_the_domain_and_the_sizes(ConnectorDomain domain, string? shape, double? diameter, double? width, double? height, string expected)
    {
        var problems = new List<string>();
        Assert.Equal(expected, ModelFamilyConnectorTables.CheckShapeAndSize(domain, shape, diameter, width, height, "c", problems));
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData(ConnectorDomain.Duct, null, null, null, null, "a duct connector needs shape")]
    [InlineData(ConnectorDomain.Duct, "Round", null, 300.0, null, "a round connector takes diameter")]
    [InlineData(ConnectorDomain.Duct, "Rectangular", 200.0, null, null, "takes width and height, not diameter")]
    [InlineData(ConnectorDomain.Pipe, "Rectangular", null, null, null, "a pipe connector is round")]
    [InlineData(ConnectorDomain.CableTray, null, 50.0, null, null, "takes width and height, not diameter")]
    [InlineData(ConnectorDomain.Pipe, null, -5.0, null, null, "c.diameter: must be greater than 0")]
    public void Wrong_shapes_and_sizes_are_problems(ConnectorDomain domain, string? shape, double? diameter, double? width, double? height, string expected)
    {
        var problems = new List<string>();
        ModelFamilyConnectorTables.CheckShapeAndSize(domain, shape, diameter, width, height, "c", problems);
        Assert.Contains(problems, p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void An_edit_keeps_the_current_shape_when_only_the_size_changes()
    {
        var problems = new List<string>();
        Assert.Equal("Rectangular", ModelFamilyConnectorTables.CheckShapeAndSize(ConnectorDomain.Duct, null, null, 400, null, "e", problems, currentShape: "Rectangular"));
        Assert.Empty(problems);
        ModelFamilyConnectorTables.CheckShapeAndSize(ConnectorDomain.Duct, null, 200, null, null, "e", problems, currentShape: "Rectangular");
        Assert.Contains(problems, p => p.Contains("takes width and height, not diameter", StringComparison.Ordinal));
    }

    // â”€â”€ Faces and positions â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [InlineData(-1, 0, 0, "left")]
    [InlineData(1, 0, 0, "right")]
    [InlineData(0, -1, 0, "front")]
    [InlineData(0, 1, 0, "back")]
    [InlineData(0, 0, 1, "top")]
    [InlineData(0, 0, -1, "bottom")]
    [InlineData(0, 1, 0.0174, "back")]
    [InlineData(0, 1, 0.0175, null)]
    [InlineData(1, 1, 0, null)]
    public void Face_labels_are_the_axis_directions_within_one_degree(double x, double y, double z, string? label) =>
        Assert.Equal(label, ModelFamilyConnectorTables.LabelOf(new[] { x, y, z }));

    [Fact]
    public void The_outermost_matching_face_is_picked()
    {
        var faces = new List<(double[] Normal, double[] Origin)>
        {
            (new[] { 0.0, -1, 0 }, new[] { 0.0, -50, 0 }),
            (new[] { 0.0, 1, 0 }, new[] { 0.0, 20, 0 }),
            (new[] { 0.0, 1, 0 }, new[] { 0.0, 50, 0 }),
            (new[] { 1.0, 0, 0 }, new[] { 40.0, 0, 0 })
        };
        Assert.Equal((2, 2), ModelFamilyConnectorTables.PickFace(faces, new[] { 0.0, 1, 0 }));
        Assert.Equal((0, 1), ModelFamilyConnectorTables.PickFace(faces, new[] { 0.0, -1, 0 }));
        Assert.Equal((-1, 0), ModelFamilyConnectorTables.PickFace(faces, new[] { 0.0, 0, 1 }));
    }

    [Fact]
    public void A_position_is_projected_onto_the_connector_plane()
    {
        var (projected, distance) = ModelFamilyConnectorTables.ProjectOntoPlane(new[] { 10.0, 80, 100 }, new[] { 0.0, 50, 0 }, new[] { 0.0, 2, 0 });
        Assert.Equal(new[] { 10.0, 50, 100 }, projected);
        Assert.Equal(30, distance, 9);
    }

    // â”€â”€ Units â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [InlineData("electricalPotential", 230, null, 230)]
    [InlineData("electricalPotential", 0.4, "kV", 400)]
    [InlineData("apparentPower", 1.5, "kVA", 1500)]
    [InlineData("electricalPower", 2, "kW", 2000)]
    [InlineData("current", 500, "mA", 0.5)]
    [InlineData("airFlow", 360, "m3/h", 100)]
    [InlineData("airFlow", 50, "L/S", 50)]
    [InlineData("flow", 120, "l/min", 2)]
    [InlineData("length", 2.5, "cm", 25)]
    [InlineData("angle", Math.PI, "rad", 180)]
    [InlineData("pipingPressure", 1.5, "bar", 150000)]
    [InlineData("number", 0.9, null, 0.9)]
    public void Values_are_scaled_to_the_display_unit(string spec, double value, string? unit, double expected)
    {
        var (converted, error) = ModelFamilyConnectorUnits.ToDefault(spec, value, unit);
        Assert.Null(error);
        Assert.Equal(expected, converted, 9);
    }

    [Fact]
    public void Unknown_units_and_specs_say_what_to_pass()
    {
        Assert.Contains("use V, kV, mV", ModelFamilyConnectorUnits.ToDefault("electricalPotential", 1, "VA").Error);
        Assert.Contains("plain number", ModelFamilyConnectorUnits.ToDefault("number", 1, "V").Error);
        Assert.NotNull(ModelFamilyConnectorUnits.ToDefault(null, 1, null).Error);
        Assert.NotNull(ModelFamilyConnectorUnits.ToDefault("color", 1, "x").Error);
        Assert.Equal("VA", ModelFamilyConnectorUnits.DisplayUnit("apparentPower"));
        Assert.Equal("mm", ModelFamilyConnectorUnits.DisplayUnit("length"));
        Assert.Null(ModelFamilyConnectorUnits.DisplayUnit("color"));
        Assert.Equal(ModelFamilyConnectorUnits.DefaultUnits.Keys.OrderBy(k => k), ModelFamilyConnectorUnits.Aliases.Keys.OrderBy(k => k));
        foreach (var (spec, unit) in ModelFamilyConnectorUnits.DefaultUnits)
            Assert.Equal(1, ModelFamilyConnectorUnits.Aliases[spec][unit]);
    }

    // â”€â”€ Category plausibility and enumerated parameters â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void A_duct_connector_in_an_electrical_fixture_and_any_connector_in_a_generic_model_are_warned_about()
    {
        Assert.Null(ModelFamilyConnectorTables.CategoryWarning("OST_DataDevices", "Data Devices", ConnectorDomain.Electrical));
        Assert.Null(ModelFamilyConnectorTables.CategoryWarning("OST_MechanicalEquipment", "Mechanical Equipment", ConnectorDomain.Duct));
        var unusual = ModelFamilyConnectorTables.CategoryWarning("OST_ElectricalFixtures", "Electrical Fixtures", ConnectorDomain.Duct);
        Assert.Contains("A duct connector is unusual in a 'Electrical Fixtures' family", unusual);
        Assert.Contains("Part Type", unusual);
        var generic = ModelFamilyConnectorTables.CategoryWarning("OST_GenericModel", "Generic Models", ConnectorDomain.Electrical);
        Assert.Contains("not an MEP category", generic);
    }

    [Fact]
    public void Flow_parameters_take_their_enum_values_by_name()
    {
        var (enumName, values) = ModelFamilyConnectorTables.EnumParameters["RBS_DUCT_FLOW_DIRECTION_PARAM"];
        Assert.Equal("FlowDirectionType", enumName);
        Assert.Equal("Out", values["OUT"]);
        Assert.Equal("Demand", ModelFamilyConnectorTables.EnumParameters["RBS_PIPE_FLOW_CONFIGURATION_PARAM"].Values["demand"]);
        Assert.False(ModelFamilyConnectorTables.EnumParameters["RBS_DUCT_FLOW_CONFIGURATION_PARAM"].Values.ContainsKey("demand"));
    }

    // â”€â”€ Tool surface â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [InlineData("revit_preview_edit_model_family_connectors", "ReadOnly")]
    [InlineData("revit_edit_model_family_connectors", "RequiresApproval")]
    public void The_connector_tools_match_on_both_sides(string tool, string permission)
    {
        Assert.Equal("familyeditor", BridgeToolGroups.GroupOf(tool));
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
        Assert.Contains("SendEditModelFamilyConnectors", bridge.Source);
        Assert.Contains("ModelFamilySession.Edit", ToolSourceScan.Addin.Value[tool].Source);
    }

    [Fact]
    public void Editing_connectors_counts_as_creating_and_the_bridge_sends_every_operation_list()
    {
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_edit_model_family_connectors"));
        var edit = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == "revit_edit_model_family_connectors").Source;
        foreach (var argument in new[] { "\"remove\"", "\"connectors\"", "\"edit\"" })
            Assert.Contains(argument, edit);
        Assert.Contains("IApprovalSummaryTool", ToolSourceScan.Addin.Value["revit_edit_model_family_connectors"].Source);
    }
}
