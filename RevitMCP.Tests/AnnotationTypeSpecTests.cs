using Newtonsoft.Json.Linq;
using RevitMCP.Addin.AnnotationTypes;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Text type and dimension type specs: revit_set_text_types and revit_set_dimension_types.</summary>
public class AnnotationTypeSpecTests
{
    private static (List<AnnotationTypeSpec> Types, List<string> Errors) ReadText(string json)
    {
        var errors = new List<string>();
        var types = AnnotationTypeSpecParser.Read(new Dictionary<string, object?> { ["types"] = JToken.Parse(json) }, "types",
            AnnotationTypeSpecParser.TextTypeFields, allowStyle: false, errors);
        return (types, errors);
    }

    private static (List<AnnotationTypeSpec> Types, List<string> Errors) ReadDimension(string json)
    {
        var errors = new List<string>();
        var types = AnnotationTypeSpecParser.Read(new Dictionary<string, object?> { ["types"] = JToken.Parse(json) }, "types",
            AnnotationTypeSpecParser.DimensionTypeFields, allowStyle: true, errors);
        return (types, errors);
    }

    [Fact]
    public void Reads_every_text_type_field()
    {
        var (types, errors) = ReadText(
            "[{\"name\":\"EL 2.5mm\",\"duplicateFrom\":\"2.5mm Arial\",\"font\":\"Arial Narrow\",\"size\":2.5,\"widthFactor\":0.8," +
            "\"bold\":true,\"italic\":\"no\",\"underline\":0,\"color\":\"255,0,0\",\"background\":\"Transparent\",\"showBorder\":false," +
            "\"leaderBorderOffset\":0,\"tabSize\":12.7,\"lineWeight\":2,\"leaderArrowhead\":\"Arrow Filled 30 Degree\"}]");

        Assert.Empty(errors);
        var spec = Assert.Single(types);
        Assert.Equal("EL 2.5mm", spec.Name);
        Assert.Equal("2.5mm Arial", spec.DuplicateFrom);
        Assert.Equal("Arial Narrow", spec.Values["font"]);
        Assert.Equal(2.5, spec.Values["size"]);
        Assert.Equal(0.8, spec.Values["widthFactor"]);
        Assert.Equal(true, spec.Values["bold"]);
        Assert.Equal(false, spec.Values["italic"]);
        Assert.Equal(false, spec.Values["underline"]);
        Assert.Equal("#FF0000", spec.Values["color"]);
        Assert.Equal("transparent", spec.Values["background"]);
        Assert.Equal(false, spec.Values["showBorder"]);
        Assert.Equal(0.0, spec.Values["leaderBorderOffset"]);
        Assert.Equal(12.7, spec.Values["tabSize"]);
        Assert.Equal(2, spec.Values["lineWeight"]);
        Assert.Equal("Arrow Filled 30 Degree", spec.Values["leaderArrowhead"]);
    }

    [Fact]
    public void Aliases_and_none_are_accepted()
    {
        var (types, errors) = ReadText("[{\"name\":\"A\",\"textSize\":3,\"colour\":[0,0,255],\"arrowhead\":\"none\"},{\"name\":\"B\",\"newName\":\"C\"}]");
        Assert.Empty(errors);
        Assert.Equal(3.0, types[0].Values["size"]);
        Assert.Equal("#0000FF", types[0].Values["color"]);
        Assert.Equal(string.Empty, types[0].Values["leaderArrowhead"]);
        Assert.Equal("C", types[1].NewName);
        Assert.False(types[1].HasValues);
    }

    [Theory]
    [InlineData("[{\"size\":2}]", "needs a name")]
    [InlineData("[{\"name\":\"A\"},{\"name\":\"a\"}]", "listed twice")]
    [InlineData("[{\"name\":\"A\",\"size\":0}]", "more than zero")]
    [InlineData("[{\"name\":\"A\",\"size\":-1}]", "more than zero")]
    [InlineData("[{\"name\":\"A\",\"leaderBorderOffset\":-0.5}]", "zero or more")]
    [InlineData("[{\"name\":\"A\",\"widthFactor\":0}]", "0.01 to 100")]
    [InlineData("[{\"name\":\"A\",\"lineWeight\":0.25}]", "1 to 16")]
    [InlineData("[{\"name\":\"A\",\"color\":\"red\"}]", "not a colour")]
    [InlineData("[{\"name\":\"A\",\"background\":\"white\"}]", "opaque or transparent")]
    [InlineData("[{\"name\":\"A\",\"bold\":\"maybe\"}]", "true or false")]
    [InlineData("[{\"name\":\"A\",\"font\":\"\"}]", "must not be empty")]
    [InlineData("[{\"name\":\"A:B\"}]", "characters Revit refuses")]
    [InlineData("[{\"name\":\"A\",\"newName\":\"B<C\"}]", "characters Revit refuses")]
    [InlineData("[{\"name\":\"A\",\"style\":\"linear\"}]", "dimension types only")]
    [InlineData("[{\"name\":\"A\",\"sise\":2}]", "unknown field(s) 'sise'")]
    [InlineData("{\"name\":\"A\"}", "must be an array")]
    public void Refuses_bad_text_types(string json, string expected)
    {
        var (_, errors) = ReadText(json);
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void Reads_dimension_fields_style_and_units()
    {
        var (types, errors) = ReadDimension(
            "[{\"name\":\"EL Linear 2.5\",\"style\":\"Linear\",\"textSize\":2.5,\"textFont\":\"Arial\",\"textOffset\":0.5," +
            "\"tickMark\":\"Diagonal 3mm\",\"tickMarkLineWeight\":4,\"witnessLineExtension\":2,\"witnessLineGap\":1.5," +
            "\"dimensionLineExtension\":0,\"textBackground\":\"opaque\",\"unitsFormat\":{\"unit\":\"mm\",\"accuracy\":1}}," +
            "{\"name\":\"EL Linear 2.5\",\"style\":\"angular\",\"unitsFormat\":\"project\"}]");

        Assert.Empty(errors);
        Assert.Equal(2, types.Count); // the same name is allowed once per style
        var linear = types[0];
        Assert.Equal("Linear", linear.Style);
        Assert.Equal(2.5, linear.Values["textSize"]);
        Assert.Equal("Diagonal 3mm", linear.Values["tickMark"]);
        Assert.Equal(4, linear.Values["tickMarkLineWeight"]);
        var units = Assert.IsType<UnitsFormatSpec>(linear.Values["unitsFormat"]);
        Assert.Equal("millimeters", units.Unit);
        Assert.Equal(1.0, units.Accuracy);
        Assert.Null(units.UseProjectSettings);
        Assert.True(((UnitsFormatSpec)types[1].Values["unitsFormat"]).UseProjectSettings);
    }

    [Theory]
    [InlineData("[{\"name\":\"A\",\"unitsFormat\":{\"unit\":\"furlong\"}}]", "is not a unit here")]
    [InlineData("[{\"name\":\"A\",\"unitsFormat\":{\"accuracy\":0}}]", "positive number")]
    [InlineData("[{\"name\":\"A\",\"unitsFormat\":{\"useProjectSettings\":true,\"unit\":\"mm\"}}]", "takes no unit or accuracy")]
    [InlineData("[{\"name\":\"A\",\"unitsFormat\":{}}]", "gives nothing")]
    [InlineData("[{\"name\":\"A\",\"unitsFormat\":5}]", "useProjectSettings?")]
    [InlineData("[{\"name\":\"A\",\"style\":\"linear\"},{\"name\":\"a\",\"style\":\"Linear\"}]", "listed twice")]
    public void Refuses_bad_dimension_types(string json, string expected)
    {
        var (_, errors) = ReadDimension(json);
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void Size_is_an_alias_of_text_size_for_dimension_types()
    {
        var (types, errors) = ReadDimension("[{\"name\":\"A\",\"size\":2,\"font\":\"Arial\",\"background\":\"transparent\"}]");
        Assert.Empty(errors);
        Assert.Equal(2.0, types[0].Values["textSize"]);
        Assert.Equal("Arial", types[0].Values["textFont"]);
        Assert.Equal("transparent", types[0].Values["textBackground"]);
    }

    [Theory]
    [InlineData("#FF0000", 255)]
    [InlineData("#00FF00", 65280)]
    [InlineData("#0000FF", 16711680)]
    [InlineData("#102030", 0x10 + (0x20 << 8) + (0x30 << 16))]
    public void Colours_round_trip_through_revit_integers(string hex, int revit)
    {
        Assert.Equal(revit, AnnotationTypeSpecParser.ColourToRevit(hex));
        Assert.Equal(hex, AnnotationTypeSpecParser.ColourFromRevit(revit));
    }

    [Fact]
    public void Lengths_and_backgrounds_convert()
    {
        Assert.Equal(2.5, AnnotationTypeSpecParser.FeetToMm(AnnotationTypeSpecParser.MmToFeet(2.5)), 6);
        Assert.Equal(304.8, AnnotationTypeSpecParser.FeetToMm(1), 6);
        Assert.Equal(0, AnnotationTypeSpecParser.BackgroundToRevit("opaque"));
        Assert.Equal(1, AnnotationTypeSpecParser.BackgroundToRevit("transparent"));
        Assert.Equal("opaque", AnnotationTypeSpecParser.BackgroundFromRevit(0));
        Assert.Equal("transparent", AnnotationTypeSpecParser.BackgroundFromRevit(1));
    }

    [Theory]
    [InlineData("autodesk.unit.unit:millimeters-1.0.1", "millimeters")]
    [InlineData("autodesk.unit.unit:degrees-1.0.1", "degrees")]
    [InlineData("millimeters", "millimeters")]
    public void Unit_names_come_from_type_ids(string typeId, string expected)
        => Assert.Equal(expected, AnnotationTypeSpecParser.UnitNameFromTypeId(typeId));

    [Theory]
    [InlineData("Arc Length", "ArcLength", true)]
    [InlineData("spot_elevation", "SpotElevation", true)]
    [InlineData("linear", "Angular", false)]
    public void Styles_compare_loosely(string a, string b, bool same)
        => Assert.Equal(same, AnnotationTypeSpecParser.SameStyle(a, b));

    [Fact]
    public void Changes_list_only_fields_that_differ_with_from_and_to()
    {
        var (types, _) = ReadText("[{\"name\":\"A\",\"size\":2.5,\"bold\":true,\"color\":\"#ff0000\",\"font\":\"arial\",\"leaderArrowhead\":\"none\"}]");
        var current = new Dictionary<string, object?>
        {
            ["size"] = 2.5000000001,      // equal within tolerance
            ["bold"] = false,
            ["color"] = "#FF0000",        // equal without regard to case
            ["font"] = "Arial",
            ["leaderArrowhead"] = "Arrow 30 Degree"
        };

        var changes = AnnotationTypeSpecParser.Changes(types[0], AnnotationTypeSpecParser.TextTypeFields, current);

        Assert.Equal(new[] { "bold", "leaderArrowhead" }, changes.Select(c => c.Field));
        Assert.Equal(false, changes[0].From);
        Assert.Equal(true, changes[0].To);
        Assert.Equal("Arrow 30 Degree", changes[1].From);
        Assert.Equal("none", changes[1].To);
    }

    [Fact]
    public void A_field_the_type_lacks_shows_from_null()
    {
        var (types, _) = ReadText("[{\"name\":\"A\",\"tabSize\":10}]");
        var changes = AnnotationTypeSpecParser.Changes(types[0], AnnotationTypeSpecParser.TextTypeFields, new Dictionary<string, object?>());
        var change = Assert.Single(changes);
        Assert.Null(change.From);
        Assert.Equal(10.0, change.To);
    }

    [Fact]
    public void Units_merge_over_the_current_format()
    {
        var current = new UnitsFormatValue(false, "millimeters", 1);
        Assert.Equal(new UnitsFormatValue(false, "millimeters", 0.5), AnnotationTypeSpecParser.MergeUnits(new UnitsFormatSpec { Accuracy = 0.5 }, current));
        Assert.Equal(new UnitsFormatValue(false, "centimeters", 1), AnnotationTypeSpecParser.MergeUnits(new UnitsFormatSpec { Unit = "centimeters" }, current));
        Assert.Equal(new UnitsFormatValue(true, null, null), AnnotationTypeSpecParser.MergeUnits(new UnitsFormatSpec { UseProjectSettings = true }, current));

        var (types, _) = ReadDimension("[{\"name\":\"A\",\"unitsFormat\":{\"accuracy\":1}}]");
        var unchanged = AnnotationTypeSpecParser.Changes(types[0], AnnotationTypeSpecParser.DimensionTypeFields,
            new Dictionary<string, object?> { ["unitsFormat"] = current });
        Assert.Empty(unchanged);
        var fromProject = AnnotationTypeSpecParser.Changes(types[0], AnnotationTypeSpecParser.DimensionTypeFields,
            new Dictionary<string, object?> { ["unitsFormat"] = new UnitsFormatValue(true, null, null) });
        Assert.Equal("project settings", Assert.Single(fromProject).From);
    }

    [Fact]
    public void Suggests_close_names()
    {
        var names = new[] { "2.5mm Arial", "3.5mm Arial", "EL Tekst 2.5", "Title" };
        Assert.Contains("2.5mm Arial", AnnotationTypeSpecParser.Suggest("2.5 mm Arial", names));
        Assert.Contains("EL Tekst 2.5", AnnotationTypeSpecParser.Suggest("EL Tekst", names));
        Assert.Equal(".", AnnotationTypeSpecParser.DidYouMean("Zzz", names));
    }

    // ── Tool surface ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("revit_list_text_types", "ReadOnly")]
    [InlineData("revit_preview_set_text_types", "ReadOnly")]
    [InlineData("revit_set_text_types", "RequiresApproval")]
    [InlineData("revit_preview_set_dimension_types", "ReadOnly")]
    [InlineData("revit_set_dimension_types", "RequiresApproval")]
    public void The_tools_match_on_both_sides(string tool, string permission)
    {
        Assert.Equal("tags", BridgeToolGroups.GroupOf(tool));
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
        Assert.Contains("\"document\"", bridge.Source);
    }

    [Fact]
    public void The_dimension_type_list_forwards_details_and_usage()
    {
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == "revit_list_dimension_types");
        Assert.Contains("[\"details\"]", bridge.Source);
        Assert.Contains("[\"includeUsage\"]", bridge.Source);
        Assert.Contains("\"details\"", ToolSourceScan.Addin.Value["revit_list_dimension_types"].Source);
    }

    [Theory]
    [InlineData("revit_set_text_types")]
    [InlineData("revit_set_dimension_types")]
    public void Plans_count_setting_types_as_creating(string tool)
        => Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify(tool));
}
