using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Families;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>revit_create_detail_family: filled regions and their types, parameter labels and visibility parameters.</summary>
public class DetailFamilyRegionsTests
{
    private static (DetailFamilySpec Spec, List<string> Errors) Parse(params (string Key, string Json)[] values)
    {
        var args = new Dictionary<string, object?>
        {
            ["savePath"] = @"C:\Families\Detail",
            ["familyName"] = "EL_Kilp"
        };
        foreach (var (key, json) in values)
            args[key] = json.StartsWith("[") || json.StartsWith("{") ? JToken.Parse(json) : json;
        var errors = new List<string>();
        var spec = DetailFamilySpecParser.Parse(args, errors);
        return (spec, errors);
    }

    [Fact]
    public void Parses_a_polygon_a_rectangle_and_a_circle()
    {
        var (spec, errors) = Parse(("filledRegions",
            "[{\"points\":[[0,0],[0,10],[10,10],[10,0],[0,0]],\"type\":\"Solid Black\",\"lineStyle\":\"EL_Õhuke\"}," +
            "{\"min\":[20,5],\"max\":[30,0]},{\"center\":[50,0],\"radius\":4}]"));

        Assert.Empty(errors);
        Assert.Equal(3, spec.FilledRegions.Count);

        var polygon = spec.FilledRegions[0];
        Assert.Equal("Solid Black", polygon.Type);
        Assert.Equal("EL_Õhuke", polygon.LineStyle);
        Assert.Equal(4, polygon.Loops[0].Points.Count);                       // the repeated closing point is dropped
        Assert.True(DetailFamilySpecParser.SignedArea(polygon.Loops[0].Points) > 0);   // given clockwise, stored counter-clockwise

        Assert.Equal(4, spec.FilledRegions[1].Loops[0].Points.Count);
        Assert.True(spec.FilledRegions[2].Loops[0].IsCircle);
        Assert.Equal(4, spec.FilledRegions[2].Loops[0].Radius);
        Assert.Equal(new[] { "EL_Õhuke" }, spec.LineStyleNames());
    }

    [Fact]
    public void A_region_alone_is_enough_to_draw()
    {
        var (_, errors) = Parse(("filledRegions", "[{\"min\":[0,0],\"max\":[10,10]}]"));
        Assert.Empty(errors);
    }

    [Fact]
    public void The_default_line_style_is_for_curves_not_region_boundaries()
    {
        var (spec, errors) = Parse(("lineStyle", "Thin Lines"), ("filledRegions", "[{\"min\":[0,0],\"max\":[10,10]}]"));
        Assert.Empty(errors);
        Assert.Equal(string.Empty, spec.FilledRegions[0].LineStyle);
        Assert.Empty(spec.LineStyleNames());
    }

    [Fact]
    public void Holes_are_stored_clockwise_after_the_outer_loop()
    {
        var (spec, errors) = Parse(("filledRegions",
            "[{\"min\":[0,0],\"max\":[100,100],\"holes\":[[[10,10],[20,10],[20,20],[10,20]],{\"center\":[60,60],\"radius\":5}]}]"));

        Assert.Empty(errors);
        var loops = spec.FilledRegions[0].Loops;
        Assert.Equal(3, loops.Count);
        Assert.True(DetailFamilySpecParser.SignedArea(loops[0].Points) > 0);
        Assert.True(DetailFamilySpecParser.SignedArea(loops[1].Points) < 0);
        Assert.True(loops[2].IsCircle);
    }

    [Theory]
    [InlineData("[{\"points\":[[0,0],[10,0]]}]", "at least three points")]
    [InlineData("[{\"points\":[[0,0],[10,0],[20,0]]}]", "no area")]
    [InlineData("[{\"points\":[[0,0],[10,10],[10,0],[0,10]]}]", "cross or touch")]
    [InlineData("[{\"points\":[[0,0],[0.1,0],[10,10]]}]", "shorter than")]
    [InlineData("[{\"min\":[0,0],\"max\":[10,0.1]}]", "side shorter")]
    [InlineData("[{\"center\":[0,0],\"radius\":0}]", "positive number")]
    [InlineData("[{\"type\":\"Solid Black\"}]", "needs a boundary")]
    [InlineData("[{\"min\":[0,0],\"max\":[10,10],\"holes\":[[[5,5],[20,5],[20,8]]]}]", "not inside")]
    [InlineData("[{\"min\":[0,0],\"max\":[50,50],\"holes\":[{\"min\":[5,5],\"max\":[20,20]},{\"center\":[20,20],\"radius\":3}]}]", "overlaps another hole")]
    public void Refuses_boundaries_Revit_cannot_fill(string regions, string expected)
    {
        var (spec, errors) = Parse(("filledRegions", regions));
        Assert.Contains(errors, e => e.Contains(expected));
        Assert.Empty(spec.FilledRegions);
    }

    [Fact]
    public void Parses_region_types_with_colours_in_every_form()
    {
        var (spec, errors) = Parse(
            ("filledRegions", "[{\"min\":[0,0],\"max\":[10,10],\"type\":\"EL_Täide\"}]"),
            ("filledRegionTypes",
                "[{\"name\":\"EL_Täide\",\"copyFrom\":\"Solid Black\",\"foregroundPattern\":\"solid\",\"foregroundColor\":\"#ff8000\"," +
                "\"backgroundPattern\":\"none\",\"backgroundColor\":[0,128,255],\"lineWeight\":2,\"isMasking\":true}," +
                "{\"name\":\"EL_Viirutus\",\"foregroundPattern\":\"Diagonal up\",\"foregroundColor\":\"10, 20, 30\"}]"));

        Assert.Empty(errors);
        var type = spec.FilledRegionTypes[0];
        Assert.Equal("Solid Black", type.CopyFrom);
        Assert.Equal("solid", type.ForegroundPattern);
        Assert.Equal("#FF8000", type.ForegroundColor);
        Assert.Equal("none", type.BackgroundPattern);
        Assert.Equal("#0080FF", type.BackgroundColor);
        Assert.Equal(2, type.LineWeight);
        Assert.True(type.IsMasking);
        Assert.True(type.HasOverrides);

        var second = spec.FilledRegionTypes[1];
        Assert.Equal("#0A141E", second.ForegroundColor);
        Assert.Null(second.BackgroundPattern);
        Assert.Null(second.LineWeight);
        Assert.Null(second.IsMasking);
    }

    [Theory]
    [InlineData("[{\"foregroundPattern\":\"solid\"}]", "needs a name")]
    [InlineData("[{\"name\":\"A\"},{\"name\":\"a\"}]", "listed twice")]
    [InlineData("[{\"name\":\"A\",\"lineWeight\":17}]", "1 to 16")]
    [InlineData("[{\"name\":\"A\",\"foregroundColor\":\"orange\"}]", "not a colour")]
    [InlineData("[{\"name\":\"A\",\"backgroundColor\":[0,0,300]}]", "not a colour")]
    public void Refuses_bad_region_types(string types, string expected)
    {
        var (_, errors) = Parse(("filledRegions", "[{\"min\":[0,0],\"max\":[10,10]}]"), ("filledRegionTypes", types));
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void Visibility_parameters_are_read_from_curves_regions_and_labels()
    {
        var (spec, errors) = Parse(
            ("lines", "[{\"start\":[0,0],\"end\":[10,0],\"visibleParameter\":\"Näita\"}]"),
            ("rectangles", "[{\"min\":[0,0],\"max\":[10,10],\"visibleParameter\":\"Näita\"}]"),
            ("filledRegions", "[{\"min\":[0,0],\"max\":[10,10],\"visibleParameter\":\"Näita täidet\"}]"),
            ("labels", "[{\"text\":\"K1\",\"position\":[0,0],\"visibleParameter\":\"Näita\"}]"),
            ("parameters", "[{\"name\":\"Näita\",\"dataType\":\"yesNo\",\"value\":true},{\"name\":\"Näita täidet\",\"dataType\":\"yesNo\",\"instance\":true}]"));

        Assert.Empty(errors);
        Assert.All(spec.Curves, c => Assert.Equal("Näita", c.VisibleParameter));
        Assert.Equal("Näita täidet", spec.FilledRegions[0].VisibleParameter);
        Assert.Equal("Näita", spec.Labels[0].VisibleParameter);
        Assert.Equal(new[] { "Näita", "Näita täidet" }, spec.VisibilityReferences().Select(v => v.Name).Distinct().OrderBy(n => n.Length));
    }

    [Theory]
    [InlineData("[]", "is not in parameters")]
    [InlineData("[{\"name\":\"Näita\",\"dataType\":\"text\"}]", "needs a yesNo parameter")]
    public void A_visibility_parameter_must_be_a_listed_yes_no_parameter(string parameters, string expected)
    {
        var (_, errors) = Parse(
            ("filledRegions", "[{\"min\":[0,0],\"max\":[10,10],\"visibleParameter\":\"Näita\"}]"),
            ("parameters", parameters));
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void A_shared_visibility_parameter_is_left_for_the_Revit_side_to_check()
    {
        var (_, errors) = Parse(
            ("filledRegions", "[{\"min\":[0,0],\"max\":[10,10],\"visibleParameter\":\"EULE_Näita\"}]"),
            ("parameters", "[{\"name\":\"EULE_Näita\",\"shared\":true}]"));
        Assert.Empty(errors);
    }

    [Fact]
    public void Parameter_labels_are_kept_apart_from_text_labels()
    {
        var (spec, errors) = Parse(
            ("labelFamily", "EULE_Label"),
            ("labels",
                "[{\"text\":\"K1\",\"position\":[0,9]}," +
                "{\"parameter\":\"Kilbi tähis\",\"position\":[0,0],\"labelType\":\"2.5 mm\",\"rotation\":90}," +
                "{\"parameter\":\"Kilbi tähis\",\"position\":[0,-9],\"labelFamily\":\"C:\\\\Lib\\\\Silt.rfa\",\"labelParameter\":\"Tekst\"}]"),
            ("parameters", "[{\"name\":\"Kilbi tähis\",\"dataType\":\"text\",\"value\":\"K1\"}]"));

        Assert.Empty(errors);
        Assert.Single(spec.TextLabels);
        var labels = spec.ParameterLabels.ToList();
        Assert.Equal(2, labels.Count);
        Assert.Equal("EULE_Label", labels[0].LabelFamily);          // the default
        Assert.Equal("2.5 mm", labels[0].LabelType);
        Assert.Equal(90, labels[0].RotationDeg);
        Assert.Equal(@"C:\Lib\Silt.rfa", labels[1].LabelFamily);    // its own
        Assert.Equal("Tekst", labels[1].LabelParameter);

        // Only fixed text becomes annotation families the tool builds.
        var groups = DetailFamilySpecParser.GroupLabels(spec, errors);
        Assert.Single(groups);
        Assert.Equal("K1", groups[0].Template.Text);
        Assert.Equal("Silt", DetailFamilySpecParser.LabelFamilyName(labels[1].LabelFamily));
        Assert.True(DetailFamilySpecParser.IsLabelFamilyPath(labels[1].LabelFamily));
        Assert.False(DetailFamilySpecParser.IsLabelFamilyPath(labels[0].LabelFamily));
    }

    [Theory]
    [InlineData("[{\"parameter\":\"Tähis\",\"position\":[0,0]}]", "", "needs labelFamily")]
    [InlineData("[{\"parameter\":\"Puudub\",\"position\":[0,0]}]", "EULE_Label", "is not in parameters")]
    [InlineData("[{\"parameter\":\"Tähis\",\"text\":\"K1\",\"position\":[0,0]}]", "EULE_Label", "both text and parameter")]
    [InlineData("[{\"parameter\":\"Tähis\",\"position\":[0,0]}]", "EL_Kilp", "same name as the family")]
    [InlineData("[{\"position\":[0,0]}]", "EULE_Label", "text is empty")]
    public void Refuses_parameter_labels_that_cannot_work(string labels, string labelFamily, string expected)
    {
        var (_, errors) = Parse(
            ("lines", "[{\"start\":[0,0],\"end\":[10,0]}]"),
            ("labelFamily", labelFamily),
            ("labels", labels),
            ("parameters", "[{\"name\":\"Tähis\",\"dataType\":\"text\"}]"));
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void A_region_boundary_line_style_is_planned_like_a_curve_style()
    {
        var (spec, _) = Parse(
            ("lines", "[{\"start\":[0,0],\"end\":[10,0],\"lineStyle\":\"EL_Kaabel\"}]"),
            ("filledRegions", "[{\"min\":[0,0],\"max\":[10,10],\"lineStyle\":\"EL_Õhuke\"}]"));

        var plan = DetailFamilyLineStylePlanner.Plan(spec, new[]
        {
            new ProjectLineStyleInfo { Name = "EL_Kaabel", Weight = 3 }
        });

        Assert.Equal("project", plan.Single(p => p.Requested == "EL_Kaabel").Status);
        var missing = plan.Single(p => p.Requested == "EL_Õhuke");
        Assert.Equal("missing", missing.Status);
        Assert.Equal(new[] { "filledRegions[0]" }, missing.UsedBy);
    }
}
