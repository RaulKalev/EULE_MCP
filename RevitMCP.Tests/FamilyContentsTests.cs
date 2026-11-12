using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Families;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>revit_get_family_contents: the read model, its result shape and the spec that copies a family.</summary>
public class FamilyContentsTests
{
    private static DetailPoint P(double x, double y) => new(x, y);

    private static FamilyContentsLoop Polygon(params DetailPoint[] corners)
    {
        var loop = new FamilyContentsLoop();
        for (var i = 0; i < corners.Length; i++)
            loop.Segments.Add(new FamilyContentsSegment { Kind = "line", Start = corners[i], End = corners[(i + 1) % corners.Length] });
        return loop;
    }

    /// <summary>A circle as Revit returns a region boundary: two half arcs.</summary>
    private static FamilyContentsLoop CircleLoop(DetailPoint c, double r)
    {
        var loop = new FamilyContentsLoop();
        loop.Segments.Add(new FamilyContentsSegment { Kind = "arc", Start = P(c.X + r, c.Y), Mid = P(c.X, c.Y + r), End = P(c.X - r, c.Y), Center = c, Radius = r });
        loop.Segments.Add(new FamilyContentsSegment { Kind = "arc", Start = P(c.X - r, c.Y), Mid = P(c.X, c.Y - r), End = P(c.X + r, c.Y), Center = c, Radius = r });
        return loop;
    }

    private static Dictionary<string, object?> Args(JObject spec, string savePath = @"C:\Families")
    {
        var args = spec.Properties().ToDictionary(p => p.Name, p => (object?)p.Value);
        args["savePath"] = savePath;
        return args;
    }

    // ── Geometry and units ───────────────────────────────────────────────────

    [Fact]
    public void Arc_angles_run_counter_clockwise_and_a_clockwise_arc_is_read_from_its_end()
    {
        var (a, b) = FamilyContentsGeometry.ArcAngles(P(0, 0), P(10, 0), P(0, 10), clockwise: false);
        Assert.Equal(0, a, 6);
        Assert.Equal(90, b, 6);

        var (c, d) = FamilyContentsGeometry.ArcAngles(P(0, 0), P(0, 10), P(10, 0), clockwise: true);
        Assert.Equal(0, c, 6);
        Assert.Equal(90, d, 6);

        Assert.Equal(270, FamilyContentsGeometry.AngleOf(P(5, 5), P(5, 0)), 6);
    }

    [Theory]
    [InlineData("length", 1.0, 304.8)]
    [InlineData("angle", Math.PI / 2, 90.0)]
    [InlineData("area", 10.763910416709722, 1.0)]
    [InlineData("volume", 35.31466672148859, 1.0)]
    [InlineData("number", 2.5, 2.5)]
    [InlineData(null, 7.0, 7.0)]
    public void Internal_units_become_millimetres_square_metres_cubic_metres_and_degrees(string? friendly, double value, double expected)
        => Assert.Equal(expected, FamilyContentsGeometry.FromInternal(friendly, value), 6);

    [Fact]
    public void A_closed_loop_of_arcs_on_one_circle_is_a_circle_and_the_largest_loop_is_outer()
    {
        Assert.True(FamilyContentsGeometry.TryCircle(CircleLoop(P(1, 2), 3), out var center, out var radius));
        Assert.Equal(1, center.X, 6);
        Assert.Equal(3, radius, 6);
        Assert.False(FamilyContentsGeometry.TryCircle(Polygon(P(0, 0), P(1, 0), P(1, 1)), out _, out _));

        var hole = Polygon(P(-1, -1), P(1, -1), P(1, 1), P(-1, 1));
        var outer = Polygon(P(-10, -10), P(10, -10), P(10, 10), P(-10, 10));
        Assert.Equal(1, FamilyContentsGeometry.OuterLoop(new[] { hole, outer }));
        Assert.Same(outer, FamilyContentsGeometry.OuterFirst(new[] { hole, outer })[0]);
    }

    [Fact]
    public void Place_turns_then_moves_and_rotation_is_normalised()
    {
        var placed = FamilyContentsGeometry.Place(P(10, 0), P(5, 5), 90);
        Assert.Equal(5, placed.X, 6);
        Assert.Equal(15, placed.Y, 6);
        Assert.Equal(-90, FamilyContentsGeometry.NormalizeRotation(270), 6);
        Assert.Equal(180, FamilyContentsGeometry.NormalizeRotation(-180), 6);
        Assert.Equal(0, FamilyContentsGeometry.NormalizeRotation(360), 6);
    }

    // ── Result shape ─────────────────────────────────────────────────────────

    [Fact]
    public void Include_defaults_to_every_section_and_refuses_unknown_names()
    {
        var errors = new List<string>();
        Assert.Equal(FamilyContentsView.Sections.Count, FamilyContentsView.ParseInclude(Array.Empty<string>(), errors).Count);
        Assert.Equal(FamilyContentsView.Sections.Count, FamilyContentsView.ParseInclude(new[] { "all" }, errors).Count);
        var picked = FamilyContentsView.ParseInclude(new[] { "Curves, filled_regions", "types" }, errors);
        Assert.Empty(errors);
        Assert.Equal(new[] { "curves", "filledRegions", "types" }.OrderBy(s => s), picked.OrderBy(s => s));

        FamilyContentsView.ParseInclude(new[] { "dimensions" }, errors);
        Assert.Contains(errors, e => e.Contains("'dimensions' is not a section"));
    }

    [Fact]
    public void Lists_are_cut_to_the_limit_and_named_in_truncated_while_counts_stay_whole()
    {
        var s = new FamilyContentsSnapshot { FamilyName = "F" };
        for (var i = 0; i < 7; i++) s.Curves.Add(new FamilyContentsCurve { Id = i, Kind = "line", Start = P(i, 0), End = P(i, 10) });
        s.Parameters.Add(new FamilyContentsParameter { Name = "A", DataType = "text", Group = "Text" });

        var o = FamilyContentsView.Describe(s, new HashSet<string> { "curves" }, 3);
        Assert.Equal(7, (int)o["counts"]!["curves"]!);
        Assert.Equal(1, (int)o["counts"]!["parameters"]!);
        Assert.Equal(3, ((JArray)o["curves"]!).Count);
        Assert.Null(o["parameters"]);                          // not included
        Assert.Equal(new[] { "curves" }, o["truncated"]!.Values<string>());
        Assert.Equal(3, (int)o["limit"]!);

        var whole = FamilyContentsView.Describe(s, new HashSet<string>(FamilyContentsView.Sections), 500);
        Assert.Null(whole["truncated"]);
    }

    [Fact]
    public void Items_are_compact_points_rounded_and_empty_fields_left_out()
    {
        var arc = FamilyContentsView.DescribeCurve(new FamilyContentsCurve
        {
            Id = 5, Kind = "arc", Start = P(10, 0), Mid = P(7.0710678118, 7.0710678118), End = P(0, 10), Center = P(0, 0),
            Radius = 10, StartAngle = 0, EndAngle = 90, LineStyle = "EL_Kaabel"
        });
        Assert.Equal("[7.0711,7.0711]", arc["mid"]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("EL_Kaabel", (string?)arc["lineStyle"]);
        Assert.Null(arc["visibleParameter"]);

        var region = FamilyContentsView.DescribeRegion(new FamilyContentsRegion
        {
            Id = 1, Type = "Solid Black",
            Loops = { Polygon(P(-1, -1), P(1, -1), P(1, 1)), CircleLoop(P(0, 0), 20) }
        });
        var loops = (JArray)region["loops"]!;
        Assert.Equal(20, (double)loops[0]["radius"]!);        // the outer loop first
        Assert.Equal(3, ((JArray)loops[1]["points"]!).Count);

        var type = new FamilyContentsType { Name = "T", Values = { ["Laius"] = 20.000000001, ["Näita"] = true, ["Tühi"] = null } };
        var described = FamilyContentsView.DescribeType(type, new[]
        {
            new FamilyContentsParameter { Name = "Näita" }, new FamilyContentsParameter { Name = "Laius" }, new FamilyContentsParameter { Name = "Tühi" }
        });
        Assert.Equal(new[] { "Näita", "Laius", "Tühi" }, ((JObject)described["values"]!).Properties().Select(p => p.Name));
        Assert.Equal(20, (double)described["values"]!["Laius"]!);
        Assert.Equal(JTokenType.Null, described["values"]!["Tühi"]!.Type);
    }

    [Fact]
    public void Suggest_offers_close_family_names()
    {
        var close = FamilyContentsView.Suggest("EL_Kilp", new[] { "EL_Kilp_Sümbol", "EL_Pistik", "Kilp", "Muu" });
        Assert.Equal("EL_Kilp_Sümbol", close[0]);
        Assert.Contains("Kilp", close);
        Assert.DoesNotContain("Muu", close);
    }

    // ── Spec: round trip through the create tool's parser ────────────────────

    /// <summary>What a family the create tool made from <paramref name="spec"/> holds, read back in the read model.</summary>
    private static FamilyContentsSnapshot SnapshotOf(DetailFamilySpec spec)
    {
        var s = new FamilyContentsSnapshot { FamilyName = spec.FamilyName, Category = "Detail Items", IsDetailItem = true, CurrentType = spec.FamilyName };
        var type = new FamilyContentsType { Name = spec.FamilyName };
        s.Types.Add(type);
        foreach (var p in spec.Parameters)
        {
            s.Parameters.Add(new FamilyContentsParameter
            {
                Name = p.Name, DataType = p.Spec!.Friendly!, Friendly = p.Spec.Friendly, IsInstance = p.IsInstance, Formula = p.Formula,
                Group = RevitMCP.Addin.Parameters.ParameterSpecMap.LabelOfGroupKey(p.GroupKey ?? string.Empty)
            });
            if (p.Value != null)
            {
                var (value, _) = DetailFamilySpecParser.ConvertValue(p.Spec.Friendly!, p.Value);
                type.Values[p.Name] = value switch
                {
                    double d => FamilyContentsGeometry.FromInternal(p.Spec.Friendly, d),
                    int i when p.Spec.Friendly == "yesNo" => i != 0,
                    int i => (long)i,
                    _ => value
                };
            }
        }

        var id = 100;
        foreach (var c in spec.Curves)
        {
            var curve = new FamilyContentsCurve { Id = id++, Kind = c.Kind, LineStyle = c.LineStyle, VisibleParameter = c.VisibleParameter };
            if (c.Kind == "line") { curve.Start = c.Start; curve.End = c.End; }
            else if (c.Kind == "circle") { curve.Center = c.Center; curve.Radius = c.Radius; }
            else if (c.Mid != null) { curve.Start = c.Start; curve.Mid = c.Mid; curve.End = c.End; }
            else
            {
                var sweep = DetailFamilySpecParser.Sweep(c.StartAngle, c.EndAngle);
                DetailPoint At(double deg) => P(c.Center.X + c.Radius * Math.Cos(deg * Math.PI / 180), c.Center.Y + c.Radius * Math.Sin(deg * Math.PI / 180));
                curve.Start = At(c.StartAngle);
                curve.Mid = At(c.StartAngle + sweep / 2);
                curve.End = At(c.StartAngle + sweep);
                curve.Center = c.Center;
                curve.Radius = c.Radius;
            }
            s.Curves.Add(curve);
        }

        foreach (var r in spec.FilledRegions)
        {
            var region = new FamilyContentsRegion { Id = id++, Type = r.Type, LineStyle = r.LineStyle, VisibleParameter = r.VisibleParameter };
            foreach (var loop in r.Loops)
                region.Loops.Add(loop.IsCircle ? CircleLoop(loop.Center, loop.Radius) : Polygon(loop.Points.ToArray()));
            s.FilledRegions.Add(region);
        }
        foreach (var t in spec.FilledRegionTypes)
            s.FilledRegionTypes.Add(new FamilyContentsRegionType
            {
                Name = t.Name, ForegroundPattern = t.ForegroundPattern ?? "none", ForegroundColor = t.ForegroundColor ?? string.Empty,
                BackgroundPattern = t.BackgroundPattern ?? "none", BackgroundColor = t.BackgroundColor ?? string.Empty,
                LineWeight = t.LineWeight, IsMasking = t.IsMasking ?? false
            });
        foreach (var l in spec.LineStyles)
        {
            var merged = DetailFamilySpecParser.MergeLineStyle(l, null);
            s.LineStyles.Add(new FamilyContentsLineStyle { Name = merged.Name, Parent = "Lines", Weight = merged.Weight, Color = merged.Color, Pattern = merged.Pattern });
        }

        // Text labels: one nested Generic Annotation family per group, its text at the family's origin.
        foreach (var group in DetailFamilySpecParser.GroupLabels(spec, new List<string>()))
        {
            var family = new FamilyContentsNestedFamily { Name = group.FamilyName, Opened = true };
            family.Texts.Add(new FamilyContentsText { Text = group.Template.Text, SizeMm = group.Template.SizeMm, Alignment = group.Template.Alignment });
            s.NestedFamilies.Add(family);
            foreach (var label in group.Instances)
                s.NestedInstances.Add(new FamilyContentsNested
                {
                    Id = id++, Family = group.FamilyName, Type = group.FamilyName, Category = "Generic Annotations", IsGenericAnnotation = true,
                    Position = label.Position, RotationDeg = label.RotationDeg, VisibleParameter = label.VisibleParameter
                });
        }
        foreach (var label in spec.ParameterLabels)
        {
            var nested = new FamilyContentsNested
            {
                Id = id++, Family = label.LabelFamily, Type = label.LabelType, Category = "Generic Annotations", IsGenericAnnotation = true,
                Position = label.Position, RotationDeg = label.RotationDeg, VisibleParameter = label.VisibleParameter
            };
            nested.LinkedParameters.Add(new FamilyContentsLink { Parameter = label.LabelParameter, FamilyParameter = label.Parameter });
            s.NestedInstances.Add(nested);
        }
        return s;
    }

    private const string SymbolSpec = """
        {
          "familyName": "EL_Kilp_Sümbol",
          "lineStyle": "EL_Õhuke",
          "lineStyles": [ { "name": "EL_Õhuke", "weight": 1, "color": "#000000", "pattern": "solid" },
                          { "name": "EL_Kaabel", "weight": 3, "color": "#FF0000", "pattern": "Dash" } ],
          "rectangles": [ { "min": [-10, -6], "max": [10, 6] } ],
          "lines": [ { "start": [-10, -6], "end": [10, 6], "lineStyle": "EL_Kaabel", "visibleParameter": "Näita" } ],
          "arcs": [ { "center": [0, 0], "radius": 4, "startAngle": 0, "endAngle": 120 },
                    { "start": [-3, 0], "mid": [0, 3], "end": [3, 0] } ],
          "circles": [ { "center": [0, 0], "radius": 2 } ],
          "parameters": [
            { "name": "Kilbi tähis", "dataType": "text", "value": "K1" },
            { "name": "Laius", "dataType": "length", "value": 20, "instance": true },
            { "name": "Nurk", "dataType": "angle", "value": 45 },
            { "name": "Kogus", "dataType": "integer", "value": 3 },
            { "name": "Pool", "dataType": "length", "formula": "Laius / 2" },
            { "name": "Näita", "dataType": "yesNo", "value": true, "instance": true }
          ],
          "filledRegionTypes": [ { "name": "EL_Täide", "foregroundPattern": "solid", "foregroundColor": "#C0C0C0", "isMasking": true, "lineWeight": 2 } ],
          "filledRegions": [
            { "points": [[-8, -4], [8, -4], [8, 4], [-8, 4]], "holes": [ [[-2, -2], [2, -2], [2, 2], [-2, 2]] ], "type": "EL_Täide", "lineStyle": "EL_Kaabel" },
            { "center": [20, 0], "radius": 2, "type": "EL_Täide", "visibleParameter": "Näita" }
          ],
          "labels": [
            { "text": "K1", "position": [0, 9], "alignment": "center", "size": 2.5 },
            { "text": "K1", "position": [0, -9], "alignment": "center", "size": 2.5, "rotation": 90 },
            { "parameter": "Kilbi tähis", "position": [12, 0], "labelFamily": "EULE_Silt", "labelType": "2.5 mm", "labelParameter": "Tähis" }
          ]
        }
        """;

    private static DetailFamilySpec ParseSpec(JObject spec, out List<string> errors)
    {
        errors = new List<string>();
        return DetailFamilySpecParser.Parse(Args(spec), errors);
    }

    [Fact]
    public void A_family_read_back_gives_a_spec_the_create_tool_parses_to_the_same_symbol()
    {
        var original = ParseSpec(JObject.Parse(SymbolSpec), out var errors);
        Assert.Empty(errors);

        var built = FamilyContentsSpecBuilder.Build(SnapshotOf(original), linePatterns: new[] { "Dash" }, fillPatterns: Array.Empty<string>());
        Assert.Empty(built.Problems);
        Assert.Empty(built.Omitted);
        Assert.Null(built.Spec["savePath"]);                    // the user says where the copy goes

        var copy = ParseSpec(built.Spec, out errors);
        Assert.Empty(errors);
        Assert.Equal(original.FamilyName, copy.FamilyName);

        // Curves: the same kinds, places, styles and visibility (rectangles come back as their four lines).
        Assert.Equal(original.Curves.Count, copy.Curves.Count);
        Assert.Equal(original.CountsByKind()["line"], copy.CountsByKind()["line"]);
        Assert.Equal(original.CountsByKind()["arc"], copy.CountsByKind()["arc"]);
        Assert.Equal(original.CountsByKind()["circle"], copy.CountsByKind()["circle"]);
        var diagonal = copy.Curves.Single(c => c.LineStyle == "EL_Kaabel");
        Assert.Equal(-10, diagonal.Start.X, 4);
        Assert.Equal(6, diagonal.End.Y, 4);
        Assert.Equal("Näita", diagonal.VisibleParameter);
        Assert.All(copy.Curves.Where(c => c != diagonal), c => Assert.Equal("EL_Õhuke", c.LineStyle));
        var angleArc = copy.Curves.First(c => c.Kind == "arc");
        Assert.Equal(4, angleArc.Start.DistanceTo(new DetailPoint(0, 0)), 4);
        Assert.Equal(4, angleArc.Start.X, 4);                   // from 0°
        Assert.Equal(4 * Math.Cos(120 * Math.PI / 180), angleArc.End.X, 4);

        // Line styles and filled region types are defined in the spec itself.
        Assert.Equal(new[] { "EL_Kaabel", "EL_Õhuke" }, copy.LineStyles.Select(l => l.Name).OrderBy(n => n, StringComparer.Ordinal));
        var cable = copy.LineStyles.Single(l => l.Name == "EL_Kaabel");
        Assert.Equal(3, cable.Weight);
        Assert.Equal("#FF0000", cable.Color);
        Assert.Equal("Dash", cable.Pattern);
        var fill = Assert.Single(copy.FilledRegionTypes);
        Assert.Equal("solid", fill.ForegroundPattern);
        Assert.Equal("#C0C0C0", fill.ForegroundColor);
        Assert.Equal(2, fill.LineWeight);
        Assert.True(fill.IsMasking);

        // Filled regions: the polygon with its hole, the circle, types, boundary style and visibility.
        Assert.Equal(2, copy.FilledRegions.Count);
        var polygon = copy.FilledRegions[0];
        Assert.Equal(2, polygon.Loops.Count);
        Assert.Equal(4, polygon.Loops[1].Points.Count);
        Assert.Equal("EL_Kaabel", polygon.LineStyle);
        Assert.Equal(128, Math.Abs(DetailFamilySpecParser.SignedArea(polygon.Loops[0].Points)), 4);
        var circle = copy.FilledRegions[1];
        Assert.True(circle.Loops[0].IsCircle);
        Assert.Equal(2, circle.Loops[0].Radius, 4);
        Assert.Equal("Näita", circle.VisibleParameter);

        // Parameters: data types, instance, values in mm and degrees, formulas.
        Assert.Equal(original.Parameters.Select(p => p.Name), copy.Parameters.Select(p => p.Name));
        Assert.Equal("K1", (string?)copy.Parameters.Single(p => p.Name == "Kilbi tähis").Value);
        var width = copy.Parameters.Single(p => p.Name == "Laius");
        Assert.True(width.IsInstance);
        Assert.Equal(20, (double)width.Value!, 4);
        Assert.Equal(45, (double)copy.Parameters.Single(p => p.Name == "Nurk").Value!, 4);
        Assert.Equal(3, (long)copy.Parameters.Single(p => p.Name == "Kogus").Value!);
        Assert.Equal("Laius / 2", copy.Parameters.Single(p => p.Name == "Pool").Formula);
        Assert.True((bool)copy.Parameters.Single(p => p.Name == "Näita").Value!);
        Assert.Equal(original.Parameters.Select(p => p.GroupKey), copy.Parameters.Select(p => p.GroupKey));

        // Labels: the two texts (one text family, two instances) and the parameter label.
        Assert.Equal(2, copy.TextLabels.Count());
        Assert.All(copy.TextLabels, l => { Assert.Equal("K1", l.Text); Assert.Equal("center", l.Alignment); Assert.Equal(2.5, l.SizeMm); });
        Assert.Equal(90, copy.TextLabels.Single(l => l.Position.Y < 0).RotationDeg, 4);
        var parameterLabel = Assert.Single(copy.ParameterLabels);
        Assert.Equal("Kilbi tähis", parameterLabel.Parameter);
        Assert.Equal("EULE_Silt", parameterLabel.LabelFamily);
        Assert.Equal("2.5 mm", parameterLabel.LabelType);
        Assert.Equal("Tähis", parameterLabel.LabelParameter);
        Assert.Contains(built.Notes, n => n.Contains("'EULE_Silt' must be loaded"));
    }

    [Fact]
    public void A_second_round_trip_gives_the_same_spec()
    {
        var once = FamilyContentsSpecBuilder.Build(SnapshotOf(ParseSpec(JObject.Parse(SymbolSpec), out _)));
        var twice = FamilyContentsSpecBuilder.Build(SnapshotOf(ParseSpec(once.Spec, out var errors)));
        Assert.Empty(errors);
        Assert.True(JToken.DeepEquals(once.Spec, twice.Spec), twice.Spec.ToString());
    }

    [Fact]
    public void What_the_create_tool_cannot_express_is_listed_not_guessed()
    {
        var s = new FamilyContentsSnapshot { FamilyName = "Võõras", Category = "Generic Annotations", IsGenericAnnotation = true, CurrentType = "A" };
        s.Types.Add(new FamilyContentsType { Name = "A", Values = { ["Materjal"] = "Betoon", ["Laius"] = 10.0 } });
        s.Types.Add(new FamilyContentsType { Name = "B" });
        s.Parameters.Add(new FamilyContentsParameter { Name = "Laius", DataType = "length", Friendly = "length", Group = "Dimensions" });
        s.Parameters.Add(new FamilyContentsParameter { Name = "Mõõt", DataType = "length", Friendly = "length", Group = "Dimensions", IsReporting = true });
        s.Parameters.Add(new FamilyContentsParameter { Name = "Sisseehitatud", DataType = "text", Friendly = "text", IsBuiltIn = true });
        s.Parameters.Add(new FamilyContentsParameter { Name = "Materjal", DataType = "material", Friendly = "material", Group = "Materials and Finishes" });
        s.Parameters.Add(new FamilyContentsParameter { Name = "Pinge", DataType = "Pinge (eriline)", Group = "Electrical" });
        s.Parameters.Add(new FamilyContentsParameter { Name = "Tähis", DataType = "text", Friendly = "text", Group = "Text", IsShared = true, Guid = "8f2c2f5e-0a8c-4e47-9b3e-6c0c7c3f2a11" });

        s.Curves.Add(new FamilyContentsCurve { Id = 1, Kind = "line", Start = P(0, 0), End = P(0.5, 0) });
        s.Curves.Add(new FamilyContentsCurve { Id = 2, Kind = "other", CurveType = "Ellipse" });
        s.Curves.Add(new FamilyContentsCurve { Id = 3, Kind = "line", Start = P(0, 0), End = P(10, 0), VisibleParameter = "Laius", LineStyle = "Oma" });
        s.LineStyles.Add(new FamilyContentsLineStyle { Name = "Oma", Parent = "Lines", Weight = 2, Pattern = "Puudub" });

        var arcRegion = new FamilyContentsRegion { Id = 4, Type = "Täide" };
        var loop = Polygon(P(0, 0), P(10, 0), P(10, 10));
        loop.Segments[1] = new FamilyContentsSegment { Kind = "arc", Start = P(10, 0), Mid = P(12, 5), End = P(10, 10), Center = P(5, 5), Radius = 7 };
        arcRegion.Loops.Add(loop);
        s.FilledRegions.Add(arcRegion);

        s.NestedInstances.Add(new FamilyContentsNested { Id = 5, Family = "Tool", Category = "Detail Items" });
        s.NestedInstances.Add(new FamilyContentsNested { Id = 6, Family = "Kaks teksti", Category = "Generic Annotations", IsGenericAnnotation = true });
        var label = new FamilyContentsNested { Id = 7, Family = "Silt", Type = "2.5", Category = "Generic Annotations", IsGenericAnnotation = true };
        label.LinkedParameters.Add(new FamilyContentsLink { Parameter = "Tekst", FamilyParameter = "Mõõt" });
        s.NestedInstances.Add(label);
        var twoTexts = new FamilyContentsNestedFamily { Name = "Kaks teksti", Opened = true };
        twoTexts.Texts.Add(new FamilyContentsText { Text = "A" });
        twoTexts.Texts.Add(new FamilyContentsText { Text = "B" });
        s.NestedFamilies.Add(twoTexts);
        s.TextNotes.Add(new FamilyContentsText { Id = 8, Text = "Märkus", Position = P(0, 20), TextType = "2.5mm Arial", SizeMm = 2.5 });

        var built = FamilyContentsSpecBuilder.Build(s, linePatterns: new[] { "Dash" });
        var omitted = string.Join("\n", built.Omitted);

        Assert.Contains("1 type(s) besides 'A'", omitted);
        Assert.Contains("'Mõõt': a reporting parameter", omitted);
        Assert.DoesNotContain("Sisseehitatud", omitted + string.Join("\n", built.Spec["parameters"]!.Select(p => (string?)p["name"])));
        Assert.Contains("'Materjal': value \"Betoon\"", omitted);
        Assert.Contains("'Pinge': its data type 'Pinge (eriline)'", omitted);
        Assert.Contains("curves[0] (id 1): a line shorter than", omitted);
        Assert.Contains("curves[1] (id 2): a Ellipse", omitted);
        Assert.Contains("visibleParameter 'Laius' is not a yes/no parameter", omitted);
        Assert.Contains("line pattern 'Puudub' is not in the project", omitted);
        Assert.Contains("filledRegions[0] (id 4): has arc or curved edges", omitted);
        Assert.Contains("nestedInstances[0] ('Tool', id 5): a nested Detail Items instance", omitted);
        Assert.Contains("nestedInstances[1] ('Kaks teksti', id 6): it holds 2 text(s)", omitted);
        Assert.Contains("nestedInstances[2] ('Silt', id 7): it shows parameter 'Mõõt', which the spec does not have", omitted);

        Assert.Contains(built.Notes, n => n.Contains("Generic Annotations family; revit_create_detail_family makes Detail Items"));
        Assert.Contains(built.Notes, n => n.Contains("Shared parameters must already be in the shared parameter file"));
        Assert.Contains(built.Notes, n => n.Contains("Text types are not copied"));

        var spec = built.Spec;
        var shared = spec["parameters"]!.Single(p => (string?)p["name"] == "Tähis");
        Assert.True((bool)shared["shared"]!);
        Assert.Equal("8f2c2f5e-0a8c-4e47-9b3e-6c0c7c3f2a11", (string?)shared["guid"]);
        Assert.Null(shared["dataType"]);
        Assert.Equal(10, (double)spec["parameters"]!.Single(p => (string?)p["name"] == "Laius")["value"]!);
        Assert.Null(spec["parameters"]!.Single(p => (string?)p["name"] == "Materjal")["value"]);
        var style = Assert.Single(spec["lineStyles"]!);
        Assert.Null(style["pattern"]);                          // left out, not guessed
        Assert.Null(spec["lines"]![0]!["visibleParameter"]);
        var text = Assert.Single(spec["labels"]!);
        Assert.Equal("Märkus", (string?)text["text"]);
        Assert.Equal(2.5, (double)text["size"]!);
        Assert.Null(text["textType"]);
        Assert.Empty(built.Problems);
    }

    [Fact]
    public void A_nested_text_family_turned_and_moved_places_its_text_like_the_instance()
    {
        var s = new FamilyContentsSnapshot { FamilyName = "F", IsDetailItem = true };
        s.Curves.Add(new FamilyContentsCurve { Kind = "line", Start = P(0, 0), End = P(10, 0) });
        var family = new FamilyContentsNestedFamily { Name = "F_Text1", Opened = true };
        family.Texts.Add(new FamilyContentsText { Text = "X", Position = P(2, 0), SizeMm = 3.5, Alignment = "right" });
        s.NestedFamilies.Add(family);
        s.NestedInstances.Add(new FamilyContentsNested { Family = "F_Text1", IsGenericAnnotation = true, Position = P(10, 10), RotationDeg = 90 });

        var label = Assert.Single(FamilyContentsSpecBuilder.Build(s).Spec["labels"]!);
        Assert.Equal("[10.0,12.0]", label["position"]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal(90, (double)label["rotation"]!);
        Assert.Equal("right", (string?)label["alignment"]);
        Assert.Equal(3.5, (double)label["size"]!);
    }

    [Fact]
    public void A_built_in_line_style_is_named_but_not_defined_and_patterns_are_checked_only_against_a_known_project()
    {
        var s = new FamilyContentsSnapshot { FamilyName = "F", IsDetailItem = true };
        s.Curves.Add(new FamilyContentsCurve { Kind = "line", Start = P(0, 0), End = P(10, 0), LineStyle = "<Hidden>" });
        s.Curves.Add(new FamilyContentsCurve { Kind = "line", Start = P(0, 5), End = P(10, 5), LineStyle = "Kriips" });
        s.LineStyles.Add(new FamilyContentsLineStyle { Name = "<Hidden>", Parent = "Lines", BuiltIn = true, Pattern = "Hidden" });
        s.LineStyles.Add(new FamilyContentsLineStyle { Name = "Kriips", Parent = "Lines", Weight = 2, Pattern = "Dash 2mm" });

        var built = FamilyContentsSpecBuilder.Build(s);          // no project: the pattern is kept as it is
        var style = Assert.Single(built.Spec["lineStyles"]!);
        Assert.Equal("Kriips", (string?)style["name"]);
        Assert.Equal("Dash 2mm", (string?)style["pattern"]);
        Assert.Equal("<Hidden>", (string?)built.Spec["lines"]![0]!["lineStyle"]);
        Assert.Empty(built.Problems);
    }

    [Fact]
    public void Check_reports_what_the_parser_still_refuses()
    {
        var problems = FamilyContentsSpecBuilder.Check(new JObject { ["familyName"] = "Tühi" });
        Assert.Contains(problems, p => p.Contains("Nothing to draw"));
    }

    // ── Tool surface ─────────────────────────────────────────────────────────

    [Fact]
    public void The_tool_is_read_only_on_both_sides_and_in_the_edit_group()
    {
        const string tool = "revit_get_family_contents";
        Assert.Equal("edit", BridgeToolGroups.GroupOf(tool));
        Assert.Equal("ReadOnly", ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.True(bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
        Assert.Contains("\"document\"", bridge.Source);
        Assert.Contains("\"asSpec\"", bridge.Source);
    }
}
