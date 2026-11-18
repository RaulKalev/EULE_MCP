using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>revit_edit_model_family_references (#170): selectors, the Is Reference table, plane geometry, view choice and the operation checks.</summary>
public class ModelFamilyReferenceTests
{
    private static ModelFamilyReferenceSelector? Selector(string json, List<string> problems) =>
        ModelFamilyReferenceSelector.Parse(JToken.Parse(json), "ref", problems);

    private static (ModelFamilyReferenceEditSpec Spec, List<string> Problems) Spec(
        string? remove = null, string? planes = null, string? lines = null, string? dimensions = null, string? alignments = null)
    {
        var problems = new List<string>();
        var args = new Dictionary<string, object?>
        {
            ["remove"] = remove,
            ["referencePlanes"] = planes,
            ["referenceLines"] = lines,
            ["dimensions"] = dimensions,
            ["alignments"] = alignments
        };
        var spec = ModelFamilyReferenceEditSpec.Parse(
            ModelFamilyJson.Token(args, "remove"),
            ModelFamilyJson.Objects(args, "referencePlanes", problems),
            ModelFamilyJson.Objects(args, "referenceLines", problems),
            ModelFamilyJson.Objects(args, "dimensions", problems),
            ModelFamilyJson.Objects(args, "alignments", problems),
            problems);
        return (spec, problems);
    }

    private static void Near(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i], 6);
    }

    // ── Selectors ────────────────────────────────────────────────────────────

    [Fact]
    public void A_name_an_id_and_level_select_elements()
    {
        var problems = new List<string>();
        var byName = Selector("\"Center (Left/Right)\"", problems)!;
        Assert.Equal(ModelFamilyReferenceKind.Element, byName.Kind);
        Assert.Equal("Center (Left/Right)", byName.Name);
        Assert.Null(byName.ElementId);

        var byId = Selector("1234", problems)!;
        Assert.Equal(1234, byId.ElementId);
        Assert.Equal(1234, Selector("\"1234\"", problems)!.ElementId);
        Assert.Equal(ModelFamilyReferenceKind.Level, Selector("\"Level\"", problems)!.Kind);
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("left", -1, 0, 0)]
    [InlineData("right", 1, 0, 0)]
    [InlineData("front", 0, -1, 0)]
    [InlineData("back", 0, 1, 0)]
    [InlineData("top", 0, 0, 1)]
    [InlineData("bottom", 0, 0, -1)]
    public void Face_names_stand_for_outward_normals(string face, double x, double y, double z)
    {
        var problems = new List<string>();
        var selector = Selector($"{{\"element\": 77, \"face\": \"{face.ToUpperInvariant()}\"}}", problems)!;
        Assert.Empty(problems);
        Assert.Equal(ModelFamilyReferenceKind.Face, selector.Kind);
        Assert.Equal(77, selector.ElementId);
        Assert.Equal(face, selector.FaceName);
        Near(new[] { x, y, z }, selector.FaceNormal!);
    }

    [Fact]
    public void A_face_can_be_given_as_a_normal_which_is_normalised()
    {
        var problems = new List<string>();
        var selector = Selector("{\"element\": 5, \"face\": [0, 3, 4]}", problems)!;
        Assert.Empty(problems);
        Near(new[] { 0, 0.6, 0.8 }, selector.FaceNormal!);
        Assert.Contains("facing", selector.Describe());

        Assert.Null(Selector("{\"element\": 5, \"face\": [0, 0, 0]}", problems));
        Assert.Contains(problems, p => p.Contains("must not be zero"));
    }

    [Fact]
    public void Instance_references_are_types_or_names()
    {
        var problems = new List<string>();
        var typed = Selector("{\"element\": 9, \"instanceReference\": \"centerLeftRight\"}", problems)!;
        Assert.Equal(ModelFamilyReferenceKind.InstanceReference, typed.Kind);
        Assert.Equal("CenterLeftRight", typed.InstanceReferenceType);
        Assert.Equal("StrongReference", Selector("{\"element\": 9, \"instanceReference\": \"strong\"}", problems)!.InstanceReferenceType);

        var named = Selector("{\"element\": 9, \"instanceReference\": \"Mounting face\"}", problems)!;
        Assert.Null(named.InstanceReferenceType);
        Assert.Equal("Mounting face", named.InstanceReferenceName);
        Assert.Empty(problems);
    }

    [Fact]
    public void A_stable_representation_is_kept_as_given()
    {
        var problems = new List<string>();
        var selector = Selector("{\"reference\": \"a1b2:0:SURFACE/3\"}", problems)!;
        Assert.Equal(ModelFamilyReferenceKind.Stable, selector.Kind);
        Assert.Equal("a1b2:0:SURFACE/3", selector.StableRepresentation);
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("null", "give a reference")]
    [InlineData("\"\"", "empty")]
    [InlineData("0", "not an element id")]
    [InlineData("{\"face\": \"left\"}", "element: give the element id")]
    [InlineData("{\"element\": 4}", "give face")]
    [InlineData("{\"element\": 4, \"face\": \"left\", \"instanceReference\": \"left\"}", "not both")]
    [InlineData("{\"element\": 4, \"face\": \"inside\"}", "not a face name")]
    [InlineData("{\"element\": 4, \"side\": \"left\"}", "unknown key")]
    [InlineData("{\"reference\": \"x\", \"element\": 4}", "not both")]
    [InlineData("[1, 2]", "not array")]
    public void Bad_selectors_are_problems(string json, string expected)
    {
        var problems = new List<string>();
        Assert.Null(Selector(json, problems));
        Assert.Contains(problems, p => p.StartsWith("ref", StringComparison.Ordinal) && p.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Selector_keys_ignore_case_and_spelling_of_the_same_target()
    {
        var problems = new List<string>();
        Assert.Equal(Selector("\"Left\"", problems)!.Key, Selector("\"left\"", problems)!.Key);
        Assert.Equal(Selector("12", problems)!.Key, Selector("\"12\"", problems)!.Key);
        Assert.Equal(Selector("{\"element\": 3, \"face\": \"top\"}", problems)!.Key, Selector("{\"element\": 3, \"face\": [0, 0, 2]}", problems)!.Key);
        Assert.NotEqual(Selector("{\"element\": 3, \"face\": \"top\"}", problems)!.Key, Selector("{\"element\": 3, \"face\": \"bottom\"}", problems)!.Key);
    }

    // ── Is Reference ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("notAReference", 12, "NotAReference")]
    [InlineData("Not a Reference", 12, "NotAReference")]
    [InlineData("strong", 13, "StrongReference")]
    [InlineData("Strong Reference", 13, "StrongReference")]
    [InlineData("weak", 14, "WeakReference")]
    [InlineData("left", 0, "Left")]
    [InlineData("Center (Left/Right)", 1, "CenterLeftRight")]
    [InlineData("right", 2, "Right")]
    [InlineData("front", 3, "Front")]
    [InlineData("centerFrontBack", 4, "CenterFrontBack")]
    [InlineData("back", 5, "Back")]
    [InlineData("bottom", 6, "Bottom")]
    [InlineData("Center (Elevation)", 7, "CenterElevation")]
    [InlineData("TOP", 8, "Top")]
    public void Is_reference_names_map_to_stored_values(string text, int value, string instanceType)
    {
        var entry = ModelFamilyIsReference.Find(text)!;
        Assert.Equal(value, entry.ParameterValue);
        Assert.Equal(instanceType, entry.InstanceType);
        Assert.Same(entry, ModelFamilyIsReference.FromParameterValue(value));
    }

    [Fact]
    public void The_is_reference_table_is_one_to_one_and_follows_the_instance_reference_enum()
    {
        var entries = ModelFamilyIsReference.Entries;
        Assert.Equal(12, entries.Count);
        Assert.Equal(entries.Count, entries.Select(e => e.ParameterValue).Distinct().Count());
        Assert.Equal(entries.Count, entries.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // FamilyInstanceReferenceType: Left = 0 … Top = 8 (read from the Revit 2024 API).
        var positional = new[] { "Left", "CenterLeftRight", "Right", "Front", "CenterFrontBack", "Back", "Bottom", "CenterElevation", "Top" };
        for (var i = 0; i < positional.Length; i++) Assert.Equal(positional[i], ModelFamilyIsReference.FromParameterValue(i)!.InstanceType);
        Assert.Null(ModelFamilyIsReference.Find("medium"));
        Assert.Null(ModelFamilyIsReference.InstanceReferenceType("notAReference"));
    }

    // ── Geometry ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_plane_from_origin_direction_and_normal_is_centred_on_its_origin()
    {
        var problems = new List<string>();
        var ends = ModelFamilyReferenceGeometry.Ends(new[] { 100.0, 0, 0 }, new[] { 0, 2.0, 0 }, new[] { 1.0, 0, 0 }, null, 1200, "p", problems)!;
        Assert.Empty(problems);
        Near(new[] { 100.0, -600, 0 }, ends.BubbleEnd);
        Near(new[] { 100.0, 600, 0 }, ends.FreeEnd);
        Near(new[] { 1.0, 0, 0 }, ends.Normal);
        // The cut vector lies in the plane, at right angles to the drawn line, and fixes the same normal.
        Assert.Equal(0, ModelFamilyReferenceGeometry.Dot(ends.CutVector, ends.Normal), 9);
        Assert.Equal(0, ModelFamilyReferenceGeometry.Dot(ends.CutVector, new[] { 0, 1.0, 0 }), 9);
        Near(ends.Normal, ModelFamilyReferenceGeometry.Cross(new[] { 0, 1.0, 0 }, ends.CutVector));
    }

    [Fact]
    public void Without_normal_a_horizontal_direction_gives_a_vertical_plane()
    {
        var problems = new List<string>();
        var ends = ModelFamilyReferenceGeometry.Ends(new[] { 0.0, 50, 0 }, new[] { 1.0, 0, 0 }, null, null, 400, "p", problems)!;
        Assert.Empty(problems);
        Near(new[] { 0, 0, 1.0 }, ends.CutVector);
        Assert.True(ModelFamilyReferenceGeometry.Parallel(new[] { 0, 1.0, 0 }, ends.Normal));
        Near(new[] { -200.0, 50, 0 }, ends.BubbleEnd);
    }

    [Fact]
    public void A_cut_vector_fixes_the_plane_too()
    {
        var problems = new List<string>();
        var ends = ModelFamilyReferenceGeometry.Ends(new[] { 0.0, 0, 250 }, new[] { 1.0, 0, 0 }, null, new[] { 0, 1.0, 0 }, 1000, "p", problems)!;
        Assert.Empty(problems);
        Near(new[] { 0, 0, 1.0 }, ends.Normal);
    }

    [Theory]
    [InlineData("[0,0,1]", null, null, "vertical direction needs normal")]
    [InlineData("[1,0,0]", "[1,1,0]", null, "right angles to direction")]
    [InlineData("[1,0,0]", null, "[1,0,1]", "right angles to direction")]
    [InlineData("[1,0,0]", "[0,1,0]", "[0,0,1]", "not both")]
    [InlineData("[0,0,0]", null, null, "must not be zero")]
    public void Plane_definitions_that_do_not_fix_a_plane_are_problems(string direction, string? normal, string? cut, string expected)
    {
        static double[]? V(string? json) => json == null ? null : JArray.Parse(json).Select(t => t.Value<double>()).ToArray();
        var problems = new List<string>();
        Assert.Null(ModelFamilyReferenceGeometry.Ends(new[] { 0.0, 0, 0 }, V(direction)!, V(normal), V(cut), 1200, "p", problems));
        Assert.Contains(problems, p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_offset_plane_moves_both_ends_along_the_normal()
    {
        var ends = ModelFamilyReferenceGeometry.Offset(new[] { 0.0, -600, 0 }, new[] { 0.0, 600, 0 }, new[] { 1.0, 0, 0 }, -250);
        Near(new[] { -250.0, -600, 0 }, ends.BubbleEnd);
        Near(new[] { -250.0, 600, 0 }, ends.FreeEnd);
        Near(new[] { 1.0, 0, 0 }, ends.Normal);
        Assert.Equal(0, ModelFamilyReferenceGeometry.Dot(ends.CutVector, ends.Normal), 9);
        Assert.Equal(-250, ModelFamilyReferenceGeometry.SignedDistance(ends.BubbleEnd, new[] { 0.0, 0, 0 }, ends.Normal), 9);
    }

    [Fact]
    public void New_planes_are_drawn_where_they_show_as_lines()
    {
        Assert.Equal("plan", ModelFamilyReferenceGeometry.PlaneViewKinds(new[] { 1.0, 0, 0 })[0]);
        Assert.Equal("plan", ModelFamilyReferenceGeometry.PlaneViewKinds(new[] { 0.6, 0.8, 0 })[0]);
        Assert.Equal(new[] { "front", "left", "back", "right" }, ModelFamilyReferenceGeometry.PlaneViewKinds(new[] { 0, 0, -1.0 }));
        // A plane sloping about X is seen edge-on from the left and right.
        Assert.Equal("left", ModelFamilyReferenceGeometry.PlaneViewKinds(new[] { 0, 0.7071, 0.7071 })[0]);
    }

    [Theory]
    [InlineData(1, 0, 0, "plan,front,back")]
    [InlineData(0, 1, 0, "plan,left,right")]
    [InlineData(0, 0, 1, "front,left,back,right")]
    [InlineData(0, 0.7071067811865476, 0.7071067811865476, "left,right")]
    [InlineData(0.6, 0.48, 0.64, "")]
    public void Dimensions_are_drawn_in_a_view_that_contains_their_direction(double x, double y, double z, string expected)
    {
        Assert.Equal(expected, string.Join(",", ModelFamilyReferenceGeometry.DimensionViewKinds(new[] { x, y, z })));
    }

    [Fact]
    public void Angular_dimensions_look_along_the_planes_intersection()
    {
        Assert.Equal(new[] { "plan" }, ModelFamilyReferenceGeometry.AngularViewKinds(new[] { 0, 0, -1.0 }));
        Assert.Equal(new[] { "front", "back" }, ModelFamilyReferenceGeometry.AngularViewKinds(new[] { 0, 1.0, 0 }));
    }

    [Fact]
    public void Two_planes_meet_on_a_line_and_the_arc_sweeps_between_them()
    {
        // x = 100 and the 45° plane through the origin, seen from above.
        var n2 = ModelFamilyReferenceGeometry.Normalize(new[] { 1.0, -1, 0 })!;
        var point = ModelFamilyReferenceGeometry.Intersection(new[] { 1.0, 0, 0 }, new[] { 100.0, 0, 0 }, n2, new[] { 0.0, 0, 0 })!;
        Near(new[] { 100.0, 100, 0 }, point);
        Assert.Null(ModelFamilyReferenceGeometry.Intersection(new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 0 }, new[] { -1.0, 0, 0 }, new[] { 5.0, 0, 0 }));

        var arc = ModelFamilyReferenceGeometry.Arc(new[] { 1.0, 0, 0 }, n2, new[] { 0, 0, 1.0 })!;
        // The acute angle, from one plane's trace to the other's: both arc ends lie on the planes.
        Assert.Equal(45, arc.EndAngle * 180 / Math.PI, 6);
        Assert.Equal(0, ModelFamilyReferenceGeometry.Dot(arc.XAxis, arc.YAxis), 9);
        var end = ModelFamilyReferenceGeometry.Add(ModelFamilyReferenceGeometry.Scale(arc.XAxis, Math.Cos(arc.EndAngle)), ModelFamilyReferenceGeometry.Scale(arc.YAxis, Math.Sin(arc.EndAngle)));
        var ends = new[] { arc.XAxis, end };
        Assert.Contains(ends, e => Math.Abs(ModelFamilyReferenceGeometry.Dot(e, new[] { 1.0, 0, 0 })) < 1e-9);
        Assert.Contains(ends, e => Math.Abs(ModelFamilyReferenceGeometry.Dot(e, n2)) < 1e-9);
        Assert.Equal(90, ModelFamilyReferenceGeometry.Arc(new[] { 1.0, 0, 0 }, new[] { 0, 1.0, 0 }, new[] { 0, 0, 1.0 })!.EndAngle * 180 / Math.PI, 6);
        Assert.Null(ModelFamilyReferenceGeometry.Arc(new[] { 1.0, 0, 0 }, new[] { -1.0, 0, 0 }, new[] { 0, 0, 1.0 }));
        Assert.Null(ModelFamilyReferenceGeometry.Arc(new[] { 1.0, 0, 0 }, n2, new[] { 0, 1.0, 0 }));
    }

    // ── Operations ───────────────────────────────────────────────────────────

    [Fact]
    public void Planes_are_created_offset_or_edited()
    {
        var (spec, problems) = Spec(planes: """
            [{"name": "Left", "origin": [-200, 0, 0], "direction": [0, 1, 0], "normal": [-1, 0, 0], "isReference": "left", "definesOrigin": false},
             {"name": "Rear", "offset": {"from": "Center (Front/Back)", "distance": 120}, "isReference": "strong"},
             {"plane": "Center (Front/Back)", "rename": "Mid", "move": -5, "definesOrigin": true}]
            """);
        Assert.Empty(problems);
        Assert.True(spec.Planes[0].IsCreate && !spec.Planes[0].IsOffset);
        Assert.Equal("left", spec.Planes[0].IsReference);
        Assert.False(spec.Planes[0].DefinesOrigin);
        Assert.True(spec.Planes[1].IsOffset);
        Assert.Equal(120, spec.Planes[1].OffsetDistanceMm);
        Assert.Equal("strong", spec.Planes[1].IsReference);
        Assert.False(spec.Planes[2].IsCreate);
        Assert.Equal("Mid", spec.Planes[2].Rename);
        Assert.Equal(-5, spec.Planes[2].MoveMm);
        Assert.Equal("2 reference planes created, 1 reference plane edited", spec.Describe());
    }

    [Theory]
    [InlineData("""{"origin": [0,0,0], "direction": [1,0,0]}""", "name is required")]
    [InlineData("""{"name": "A", "origin": [0,0,0]}""", "direction is required")]
    [InlineData("""{"name": "A", "origin": [0,0,0], "direction": [1,0,0], "lenght": 5}""", "unknown key")]
    [InlineData("""{"name": "A", "offset": {"from": "B"}}""", "offset.distance is required")]
    [InlineData("""{"name": "A", "offset": {"distance": 10}}""", "offset.from is required")]
    [InlineData("""{"name": "A", "offset": {"from": "B", "distance": 0}}""", "on top of the from plane")]
    [InlineData("""{"name": "A", "offset": 10}""", "offset: must be")]
    [InlineData("""{"plane": "B"}""", "nothing to change")]
    [InlineData("""{"plane": "B", "isReference": "medium"}""", "not a reference type")]
    [InlineData("""{"plane": "B", "rename": "a:b"}""", "does not allow")]
    [InlineData("""{"plane": "B", "move": 10, "origin": [0,0,0]}""", "unknown key")]
    public void Bad_plane_operations_are_problems(string json, string expected)
    {
        var (_, problems) = Spec(planes: $"[{json}]");
        Assert.Contains(problems, p => p.StartsWith("referencePlanes[0]", StringComparison.Ordinal) && p.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plane_names_must_be_unique_within_the_request_and_the_family()
    {
        var (_, problems) = Spec(planes: """
            [{"name": "Left", "origin": [0,0,0], "direction": [0,1,0]},
             {"name": "left", "offset": {"from": "Center", "distance": 5}},
             {"plane": "Right", "rename": "LEFT"}]
            """);
        Assert.Equal(2, problems.Count(p => p.Contains("'left'", StringComparison.OrdinalIgnoreCase) && p.Contains("unique")));

        var planes = new List<(long, string)> { (10, "Center (Left/Right)"), (11, "Left") };
        Assert.Equal(11, ModelFamilyReferenceEditSpec.NameClash(" left ", planes)!.Value.Id);
        Assert.Null(ModelFamilyReferenceEditSpec.NameClash("Left", planes, self: 11));
        Assert.Null(ModelFamilyReferenceEditSpec.NameClash("Right", planes));
    }

    [Fact]
    public void Contradictory_operations_are_problems()
    {
        var (_, problems) = Spec(
            remove: "[5, 5, 6]",
            planes: """
                [{"plane": 6, "rename": "X"},
                 {"plane": "Back", "move": 1}, {"plane": "back", "move": 2},
                 {"name": "O1", "origin": [0,0,0], "direction": [0,1,0], "definesOrigin": true},
                 {"name": "O2", "origin": [50,0,0], "direction": [0,-1,0], "definesOrigin": true}]
                """,
            dimensions: """[{"references": [5, "Back"]}, {"dimension": 7, "lock": true}, {"dimension": 7, "equal": true}]""");
        Assert.Contains(problems, p => p.StartsWith("remove[1]") && p.Contains("twice"));
        Assert.Contains(problems, p => p.StartsWith("referencePlanes[0]") && p.Contains("removed"));
        Assert.Contains(problems, p => p.StartsWith("referencePlanes[2]") && p.Contains("also edited"));
        Assert.Contains(problems, p => p.StartsWith("referencePlanes[4].definesOrigin") && p.Contains("one plane per direction"));
        Assert.Contains(problems, p => p.StartsWith("dimensions[0].references[0]") && p.Contains("removed"));
        Assert.Contains(problems, p => p.StartsWith("dimensions[2]") && p.Contains("also edited"));
    }

    [Fact]
    public void Dimensions_take_references_label_lock_and_equal()
    {
        var (spec, problems) = Spec(dimensions: """
            [{"references": ["Left", "Center (Left/Right)", "Right"], "equal": true, "view": "plan"},
             {"references": ["Left", "Right"], "label": "Width", "linePoint": [0, -400, 0]},
             {"kind": "angular", "references": ["A", {"element": 3, "face": "top"}], "radius": 300, "label": "Tilt"},
             {"dimension": 9, "label": null},
             {"dimension": 10, "lock": false}]
            """);
        Assert.Empty(problems);
        Assert.True(spec.Dimensions[0].Equal);
        Assert.Equal(3, spec.Dimensions[0].References.Count);
        Assert.Equal("Width", spec.Dimensions[1].Label);
        Assert.Equal(-400, spec.Dimensions[1].LinePoint![1]);
        Assert.Equal("angular", spec.Dimensions[2].Kind);
        Assert.Equal(ModelFamilyReferenceKind.Face, spec.Dimensions[2].References[1].Kind);
        Assert.True(spec.Dimensions[3].Unlabel);
        Assert.False(spec.Dimensions[4].Lock);
        Assert.Equal("3 dimensions created, 2 dimensions edited", spec.Describe());
    }

    [Theory]
    [InlineData("""{"references": ["A", "B"], "value": 600}""", "revit_edit_model_family_parameters")]
    [InlineData("""{"references": ["A", "B"], "label": "W", "lock": true}""", "cannot be locked")]
    [InlineData("""{"references": ["A", "B"], "equal": true}""", "three or more")]
    [InlineData("""{"references": ["A"]}""", "at least two")]
    [InlineData("""{"references": ["A", "a"]}""", "listed twice")]
    [InlineData("""{"label": "W"}""", "references is required")]
    [InlineData("""{"kind": "radial", "references": ["A", "B"]}""", "not supported")]
    [InlineData("""{"kind": "angular", "references": ["A", "B", "C"]}""", "exactly two")]
    [InlineData("""{"references": ["A", "B"], "radius": 50}""", "only an angular")]
    [InlineData("""{"references": ["A", "B"], "label": null}""", "no label to remove")]
    [InlineData("""{"dimension": 4}""", "nothing to change")]
    [InlineData("""{"dimension": 4, "label": "W", "lock": true}""", "cannot be locked")]
    [InlineData("""{"dimension": 4, "references": ["A", "B"]}""", "unknown key")]
    public void Bad_dimension_operations_are_problems(string json, string expected)
    {
        var (_, problems) = Spec(dimensions: $"[{json}]");
        Assert.Contains(problems, p => p.StartsWith("dimensions[0]", StringComparison.Ordinal) && p.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Lines_and_alignments_are_parsed_and_checked()
    {
        var (spec, problems) = Spec(
            lines: """[{"start": [0,0,0], "end": [300,0,0], "workPlane": "level", "isReference": "weak"}, {"line": 42, "isReference": "strong"}]""",
            alignments: """[{"reference": "Back", "element": {"element": 12, "face": "back"}}, {"reference": "level", "element": {"element": 12, "face": "bottom"}, "lock": false}]""");
        Assert.Empty(problems);
        Assert.True(spec.Lines[0].IsCreate);
        Assert.Equal("weak", spec.Lines[0].IsReference);
        Assert.Equal(42, spec.Lines[1].Line);
        Assert.True(spec.Alignments[0].Lock);
        Assert.False(spec.Alignments[1].Lock);
        Assert.Equal(ModelFamilyReferenceKind.Level, spec.Alignments[1].Reference!.Kind);

        var (_, bad) = Spec(
            lines: """[{"start": [0,0,0], "end": [0.5,0,0]}, {"line": 3}]""",
            alignments: """[{"reference": "Back", "element": "back"}, {"reference": "Back"}]""");
        Assert.Contains(bad, p => p.StartsWith("referenceLines[0]") && p.Contains("1 mm"));
        Assert.Contains(bad, p => p.StartsWith("referenceLines[1]") && p.Contains("nothing to change"));
        Assert.Contains(bad, p => p.StartsWith("alignments[0]") && p.Contains("two different references"));
        Assert.Contains(bad, p => p.StartsWith("alignments[1].element") && p.Contains("give a reference"));
    }

    [Fact]
    public void Remove_takes_element_ids()
    {
        var (spec, problems) = Spec(remove: "[11, \"12\"]");
        Assert.Empty(problems);
        Assert.Equal(new long[] { 11, 12 }, spec.Remove);
        Assert.Equal("2 elements removed", spec.Describe());
        var (_, bad) = Spec(remove: "[\"Left\"]");
        Assert.Contains(bad, p => p.StartsWith("remove[0]") && p.Contains("element id"));
    }

    // ── Tool surface ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("revit_preview_edit_model_family_references", "ReadOnly")]
    [InlineData("revit_edit_model_family_references", "RequiresApproval")]
    public void The_reference_tools_match_on_both_sides(string tool, string permission)
    {
        Assert.Equal("familyeditor", BridgeToolGroups.GroupOf(tool));
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
        Assert.Contains("SendEditModelFamilyReferences", bridge.Source);
        Assert.Contains("ModelFamilySession.Edit", ToolSourceScan.Addin.Value[tool].Source);
    }

    [Fact]
    public void Editing_references_counts_as_creating_and_the_preview_does_not()
    {
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_edit_model_family_references"));
        Assert.NotEqual(PlanToolKind.Creates, PlanToolClassification.Classify("revit_preview_edit_model_family_references"));
        Assert.Contains("IApprovalSummaryTool", ToolSourceScan.Addin.Value["revit_edit_model_family_references"].Source);
    }
}
