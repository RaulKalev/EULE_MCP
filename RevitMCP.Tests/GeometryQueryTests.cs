using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Coordination.Clash.Geometry;
using RevitMCP.Addin.Geometry;
using RevitMCP.Addin.Placement;
using RevitMCP.Core.Safety;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#107: box maths for the box and radius collectors.</summary>
public class BoxMathTests
{
    [Fact]
    public void FromPointRadius_IsTheCubeAroundThePoint()
    {
        var box = BoxMath.FromPointRadius(new Vec3(1000, 2000, 300), 500);
        Assert.Equal(new[] { 500.0, 1500, -200 }, new[] { box.Min.X, box.Min.Y, box.Min.Z });
        Assert.Equal(new[] { 1500.0, 2500, 800 }, new[] { box.Max.X, box.Max.Y, box.Max.Z });
        Assert.Equal(1000, box.Size.X, 9);
    }

    [Fact]
    public void FromCorners_SortsTheCorners()
    {
        var box = BoxMath.FromCorners(new Vec3(10, -5, 7), new Vec3(-10, 5, 3));
        Assert.Equal(-10, box.Min.X);
        Assert.Equal(-5, box.Min.Y);
        Assert.Equal(3, box.Min.Z);
        Assert.Equal(10, box.Max.X);
        Assert.True(BoxMath.IsValid(box));
    }

    [Fact]
    public void Pad_GrowsAndShrinksButNeverInvertsTheBox()
    {
        var box = new BoxMm(new Vec3(0, 0, 0), new Vec3(100, 40, 10));
        var grown = BoxMath.Pad(box, 50);
        Assert.Equal(-50, grown.Min.X);
        Assert.Equal(150, grown.Max.X);

        var shrunk = BoxMath.Pad(box, -10);
        Assert.Equal(10, shrunk.Min.X);
        Assert.Equal(90, shrunk.Max.X);
        // z is 10 high: shrinking by 10 per side collapses it onto its centre instead of turning it inside out.
        Assert.Equal(5, shrunk.Min.Z);
        Assert.Equal(5, shrunk.Max.Z);
        Assert.True(BoxMath.IsValid(shrunk));
    }

    [Fact]
    public void DistancePointToBox_IsZeroInsideAndEuclideanOutside()
    {
        var box = new BoxMm(new Vec3(0, 0, 0), new Vec3(100, 100, 100));
        Assert.Equal(0, BoxMath.DistancePointToBox(new Vec3(50, 50, 50), box));
        Assert.Equal(0, BoxMath.DistancePointToBox(new Vec3(100, 50, 50), box));
        Assert.Equal(30, BoxMath.DistancePointToBox(new Vec3(130, 50, 50), box), 9);
        Assert.Equal(50, BoxMath.DistancePointToBox(new Vec3(130, 140, 50), box), 9);   // 30-40-50
        Assert.Equal(Math.Sqrt(3) * 10, BoxMath.DistancePointToBox(new Vec3(-10, -10, -10), box), 9);
    }

    [Fact]
    public void ClashMath_DistancePointToBox_AcceptsCornersInAnyOrder()
    {
        Assert.Equal(30, ClashBoundingBoxMath.DistancePointToBox(130, 50, 50, 100, 100, 100, 0, 0, 0), 9);
        Assert.Equal(0, ClashBoundingBoxMath.DistancePointToBox(1, 1, 1, 0, 0, 0, 2, 2, 2));
    }

    [Fact]
    public void InsideVersusIntersects()
    {
        var query = new BoxMm(new Vec3(0, 0, 0), new Vec3(1000, 1000, 1000));
        var within = new BoxMm(new Vec3(100, 100, 100), new Vec3(200, 200, 200));
        var straddling = new BoxMm(new Vec3(900, 900, 900), new Vec3(1100, 1100, 1100));
        var touching = new BoxMm(new Vec3(1000, 0, 0), new Vec3(1200, 100, 100));
        var outside = new BoxMm(new Vec3(1001, 0, 0), new Vec3(1200, 100, 100));

        Assert.True(BoxMath.Matches("intersects", within, query));
        Assert.True(BoxMath.Matches("inside", within, query));
        Assert.True(BoxMath.Matches("intersects", straddling, query));
        Assert.False(BoxMath.Matches("inside", straddling, query));
        Assert.True(BoxMath.Intersects(touching, query));
        Assert.False(BoxMath.Inside(touching, query));
        Assert.False(BoxMath.Intersects(outside, query));
        Assert.True(BoxMath.Inside(query, query));
        Assert.True(BoxMath.IsKnownMode("Inside"));
        Assert.False(BoxMath.IsKnownMode("near"));
    }

    [Fact]
    public void EnclosingAndCorners_RoundTrip()
    {
        var box = new BoxMm(new Vec3(-1, -2, -3), new Vec3(4, 5, 6));
        var corners = BoxMath.Corners(box);
        Assert.Equal(8, corners.Count);
        var again = BoxMath.Enclosing(corners);
        Assert.Equal(box.Min.X, again.Min.X);
        Assert.Equal(box.Max.Z, again.Max.Z);
        Assert.Throws<ArgumentException>(() => BoxMath.Enclosing(Array.Empty<Vec3>()));
    }
}

/// <summary>#107: direction sets, start points on the bounding box and the minimum of the clearance tool.</summary>
public class ClearanceMathTests
{
    [Fact]
    public void Axes_AreSixUnitDirections()
    {
        var axes = ClearanceMath.AxisDirections();
        Assert.Equal(new[] { "+x", "-x", "+y", "-y", "up", "down" }, axes.Select(a => a.Label));
        Assert.All(axes, a => Assert.Equal(1, a.Direction.Length, 9));
    }

    [Fact]
    public void ExitPoint_IsTheFaceCentreForAxisDirections()
    {
        var box = new BoxMm(new Vec3(0, 0, 0), new Vec3(600, 200, 1000));
        var front = ClearanceMath.ExitPoint(box, new Vec3(0, -1, 0));
        Assert.Equal(300, front.X, 9);
        Assert.Equal(0, front.Y, 9);
        Assert.Equal(500, front.Z, 9);

        var up = ClearanceMath.ExitPoint(box, new Vec3(0, 0, 1));
        Assert.Equal(1000, up.Z, 9);
    }

    [Fact]
    public void ExitPoint_ForADiagonalLeavesThroughTheNearestFace()
    {
        var box = new BoxMm(new Vec3(-100, -50, -50), new Vec3(100, 50, 50));
        var p = ClearanceMath.ExitPoint(box, new Vec3(1, 1, 0));
        // The y face (50 away) is met before the x face (100 away).
        Assert.Equal(50, p.X, 9);
        Assert.Equal(50, p.Y, 9);
        Assert.Equal(0, p.Z, 9);
    }

    [Fact]
    public void ExitPoint_OfAPointIsThePoint()
    {
        var p = ClearanceMath.ExitPoint(new BoxMm(new Vec3(5, 5, 5), new Vec3(5, 5, 5)), new Vec3(0, 0, 1));
        Assert.Equal(5, p.Z, 9);
    }

    [Fact]
    public void Resolve_DefaultsToTheGivenSet()
    {
        var directions = ClearanceMath.Resolve(null, ClearanceMath.SetAxes, 8, null, null, out var error);
        Assert.Null(error);
        Assert.Equal(6, directions!.Count);

        var ring = ClearanceMath.Resolve("", ClearanceMath.SetHorizontal, 16, null, null, out _);
        Assert.Equal(16, ring!.Count);
        Assert.Equal("0°", ring[0].Label);
        Assert.Equal("22.5°", ring[1].Label);
        Assert.All(ring, d => Assert.Equal(0, d.Direction.Z, 9));
    }

    [Fact]
    public void Resolve_NamedSetsAndLabels()
    {
        Assert.Single(ClearanceMath.Resolve("up", ClearanceMath.SetAxes, 8, null, null, out _)!);
        Assert.Equal(10, ClearanceMath.Resolve("all", ClearanceMath.SetAxes, 8, null, null, out _)!.Count);
        var labels = ClearanceMath.Resolve("+x, up ,-y", ClearanceMath.SetAxes, 8, null, null, out _)!;
        Assert.Equal(new[] { "+x", "up", "-y" }, labels.Select(l => l.Label));
        var array = ClearanceMath.Resolve(new JArray("down", "+z"), ClearanceMath.SetAxes, 8, null, null, out _)!;
        Assert.Equal(new[] { "down", "up" }, array.Select(l => l.Label));
    }

    [Fact]
    public void Resolve_CustomDirectionsAreNormalisedAndDeduplicated()
    {
        var raw = JArray.Parse("[{\"x\":3,\"y\":4,\"z\":0}, [0,0,-2], {\"x\":6,\"y\":8}]");
        var directions = ClearanceMath.Resolve(raw, ClearanceMath.SetAxes, 8, null, null, out var error);
        Assert.Null(error);
        Assert.Equal(2, directions!.Count);
        Assert.Equal(0.6, directions[0].Direction.X, 9);
        Assert.Equal(0.8, directions[0].Direction.Y, 9);
        Assert.Equal("custom", directions[0].Label);
        Assert.Equal(-1, directions[1].Direction.Z, 9);
    }

    [Fact]
    public void Resolve_OneVectorAsArrayOrJsonText()
    {
        Assert.Single(ClearanceMath.Resolve(new JArray(1, 0, 0), ClearanceMath.SetAxes, 8, null, null, out _)!);
        Assert.Single(ClearanceMath.Resolve("[{\"x\":0,\"y\":1,\"z\":0}]", ClearanceMath.SetAxes, 8, null, null, out _)!);
    }

    [Fact]
    public void Resolve_RejectsZeroVectorsUnknownNamesAndTooMany()
    {
        Assert.Null(ClearanceMath.Resolve(JArray.Parse("[[0,0,0]]"), ClearanceMath.SetAxes, 8, null, null, out var zero));
        Assert.Contains("zero-length", zero);
        Assert.Null(ClearanceMath.Resolve("sideways", ClearanceMath.SetAxes, 8, null, null, out var unknown));
        Assert.Contains("Unknown direction", unknown);

        var many = new JArray(Enumerable.Range(0, 40).Select(i => new JArray(Math.Cos(i * 0.1), Math.Sin(i * 0.1), 0.01 * i)));
        Assert.Null(ClearanceMath.Resolve(many, ClearanceMath.SetAxes, 8, null, null, out var tooMany));
        Assert.Contains("at most 32", tooMany);
    }

    [Fact]
    public void Local_FollowsTheFacingDirection()
    {
        var facing = new Vec3(0, 1, 0);   // facing north
        var local = ClearanceMath.Resolve("local", ClearanceMath.SetAxes, 8, facing, new Vec3(1, 0, 0), out _)!;
        Assert.Equal(new[] { "front", "back", "right", "left", "up", "down" }, local.Select(d => d.Label));
        Assert.Equal(1, local[0].Direction.Y, 9);
        Assert.Equal(1, local[2].Direction.X, 9);

        // Without a usable hand direction the right side is rebuilt from the facing.
        var rebuilt = ClearanceMath.Local(facing, null)!;
        Assert.Equal(1, rebuilt[2].Direction.X, 9);

        Assert.Null(ClearanceMath.Resolve("front", ClearanceMath.SetAxes, 8, null, null, out var error));
        Assert.Contains("facing", error);
        Assert.Null(ClearanceMath.Local(new Vec3(0, 0, 1), null));
    }

    [Fact]
    public void FreeDistanceAndMinimum()
    {
        Assert.Equal(0, ClearanceMath.FreeDistance(0.01, 0.05));
        Assert.Equal(1.0, ClearanceMath.FreeDistance(1.05, 0.05), 9);
        var min = ClearanceMath.Minimum(new (string, double?)[] { ("+x", 1200), ("-x", null), ("front", 850.5), ("up", 900) });
        Assert.Equal("front", min!.Value.Label);
        Assert.Equal(850.5, min.Value.FreeMm);
        Assert.Null(ClearanceMath.Minimum(new (string, double?)[] { ("+x", null) }));
    }
}

/// <summary>#107: the shared DTOs round to 0.1 mm, keep their field names and carry the legacy aliases on request.</summary>
public class GeometryDtoRoundingTests
{
    [Fact]
    public void Point_RoundsToATenthOfAMillimetre()
    {
        var p = PointMmDto.FromMm(1234.56, -0.04, 99.95);
        Assert.Equal(1234.6, p.X);
        Assert.Equal(0, p.Y);   // no "-0"
        Assert.Equal(100.0, p.Z);
        Assert.Equal("{\"x\":1234.6,\"y\":0.0,\"z\":100.0}", JsonConvert.SerializeObject(p));
    }

    [Fact]
    public void Point_FromFeet_UsesMmPerFoot()
    {
        var p = PointMmDto.FromFeet(1, 0.5, -10);
        Assert.Equal(304.8, p.X);
        Assert.Equal(152.4, p.Y);
        Assert.Equal(-3048, p.Z);
        Assert.Equal(MoveElementsMath.MmPerFoot, GeometryUnits.MmPerFoot);
    }

    [Fact]
    public void Direction_IsNormalisedAndRounded()
    {
        var d = DirectionDto.From(3, 4, 0)!;
        Assert.Equal(0.6, d.X);
        Assert.Equal(0.8, d.Y);
        Assert.Null(DirectionDto.From(0, 0, 0));
        var tiny = DirectionDto.From(1, 1e-7, 0)!;
        Assert.Equal(0, tiny.Y);
    }

    [Fact]
    public void Box_HasSizeAndCentre_AndLegacyFieldsOnlyOnRequest()
    {
        var box = BoxMmDto.FromMm(new Vec3(100.04, 0, 0), new Vec3(0, 200.06, 3000));
        Assert.Equal(0, box.Min.X);
        Assert.Equal(100.0, box.Max.X);
        Assert.Equal(200.1, box.Max.Y);
        Assert.Equal(100.0, box.SizeMm.X);
        Assert.Equal(1500, box.CenterMm.Z);

        var plain = JObject.Parse(JsonConvert.SerializeObject(box));
        Assert.Equal(new[] { "min", "max", "sizeMm", "centerMm" }, plain.Properties().Select(p => p.Name));

        var legacy = JObject.Parse(JsonConvert.SerializeObject(box.WithLegacyFields()));
        Assert.Equal(0.0, (double)legacy["minX"]!);
        Assert.Equal(200.1, (double)legacy["maxY"]!);
        Assert.Equal(3000.0, (double)legacy["maxZ"]!);
        Assert.NotNull(legacy["min"]);
    }

    [Fact]
    public void Box_FromFeet_ConvertsBothCorners()
    {
        var box = BoxMmDto.FromFeet(new Vec3(0, 0, 0), new Vec3(1, 2, 3));
        Assert.Equal(304.8, box.Max.X);
        Assert.Equal(914.4, box.Max.Z);
        Assert.Equal(457.2, box.CenterMm.Z);
    }

    [Fact]
    public void Hit_OmitsWhatItDoesNotKnow()
    {
        var hit = new HitDto { ElementId = 5, Category = "Walls", PointMm = PointMmDto.FromMm(1, 2, 3), DistanceMm = 12.3 };
        var json = JObject.Parse(JsonConvert.SerializeObject(hit));
        Assert.Null(json["linkInstanceId"]);
        Assert.Null(json["normal"]);
        Assert.Null(json["surfaceKind"]);
        Assert.Equal(12.3, (double)json["distanceMm"]!);

        hit.LinkInstanceId = 77;
        hit.Normal = DirectionDto.From(0, 0, -1);
        json = JObject.Parse(JsonConvert.SerializeObject(hit));
        Assert.Equal(77, (long)json["linkInstanceId"]!);
        Assert.Equal(-1.0, (double)json["normal"]!["z"]!);
    }
}

/// <summary>#107: surface kinds from the face normal and the hit's category.</summary>
public class SurfaceClassifierTests
{
    private static readonly Vec3 FacingDown = new(0, 0, -1);
    private static readonly Vec3 FacingUp = new(0, 0, 1);
    private static readonly Vec3 Horizontal = new(1, 0, 0);
    private static readonly Vec3 Sloped = new(1, 0, 1);

    [Theory]
    [InlineData("OST_Walls", "Walls", "wall")]
    [InlineData("OST_GenericModel", "Generic Models", "wall")]   // a linked IFC wall mapped onto Generic Models
    [InlineData("", "", "wall")]
    [InlineData("OST_Roofs", "Roofs", "roof")]
    public void HorizontalNormal(string bic, string name, string expected) =>
        Assert.Equal(expected, SurfaceClassifier.Classify(Horizontal, bic, name, 30));

    [Theory]
    [InlineData("OST_Ceilings", "Ceilings", "ceiling")]
    [InlineData("OST_Floors", "Floors", "ceiling")]   // the underside of the slab above is a ceiling from here
    [InlineData("OST_Roofs", "Roofs", "roof")]
    [InlineData("", "Roofs", "roof")]
    public void NormalPointingDown(string bic, string name, string expected) =>
        Assert.Equal(expected, SurfaceClassifier.Classify(FacingDown, bic, name, 30));

    [Theory]
    [InlineData("OST_Floors", "Floors", "floor")]
    [InlineData("OST_Ceilings", "Ceilings", "floor")]   // the top of a suspended ceiling seen from above
    [InlineData("OST_Roofs", "Roofs", "roof")]
    public void NormalPointingUp(string bic, string name, string expected) =>
        Assert.Equal(expected, SurfaceClassifier.Classify(FacingUp, bic, name, 30));

    [Theory]
    [InlineData("OST_Walls", "Walls", "wall")]
    [InlineData("OST_Floors", "Floors", "floor")]
    [InlineData("", "Ceilings", "ceiling")]
    [InlineData("OST_GenericModel", "Generic Models", "other")]
    [InlineData("OST_Roofs", "Roofs", "roof")]
    public void SlopedFaceTakesItsCategory(string bic, string name, string expected) =>
        Assert.Equal(expected, SurfaceClassifier.Classify(Sloped, bic, name, 30));

    [Fact]
    public void ToleranceDecidesWhenAFaceIsSloped()
    {
        Assert.Equal("other", SurfaceClassifier.Classify(Sloped, "", "", 44));   // 45° from up stays outside every band
        var ramp = new Vec3(1, 0, 2);   // 26.6° from up
        Assert.Equal("floor", SurfaceClassifier.Classify(ramp, "", "", 30));
        Assert.Equal("other", SurfaceClassifier.Classify(ramp, "", "", 20));
        Assert.Equal("floor", SurfaceClassifier.Classify(ramp, "OST_GenericModel", "Generic Models", 30));
    }

    [Fact]
    public void SatisfiesAndRequestable()
    {
        Assert.True(SurfaceClassifier.Satisfies("any", "other"));
        Assert.True(SurfaceClassifier.Satisfies("wall", "wall"));
        Assert.False(SurfaceClassifier.Satisfies("ceiling", "roof"));
        Assert.True(SurfaceClassifier.IsRequestable("roof"));
        Assert.False(SurfaceClassifier.IsRequestable("nearest"));
    }

    [Fact]
    public void BlockingCategories_MatchByBuiltInOrDisplayName()
    {
        var defaults = GeometryCategories.DefaultBlocking();
        Assert.True(GeometryCategories.Matches(defaults, "OST_Walls", "Seinad"));        // localized UI, builtin wins
        Assert.True(GeometryCategories.Matches(defaults, "", "Structural Framing"));
        Assert.True(GeometryCategories.Matches(defaults, "OST_CurtainWallPanels", "Curtain Panels"));
        Assert.False(GeometryCategories.Matches(defaults, "OST_LightingFixtures", "Lighting Fixtures"));
        Assert.False(GeometryCategories.Matches(defaults, "OST_Furniture", "Furniture"));

        var custom = new HashSet<string> { "walls", "OST_Furniture" };
        Assert.True(GeometryCategories.Matches(custom, "OST_Walls", "Walls"));
        Assert.True(GeometryCategories.Matches(custom, "OST_Furniture", "Mööbel"));
        Assert.True(GeometryCategories.Matches(new HashSet<string> { "LightingFixtures" }, "OST_LightingFixtures", ""));
        Assert.False(GeometryCategories.Matches(custom, "OST_Doors", "Doors"));
        Assert.True(GeometryCategories.Matches(new HashSet<string>(), "OST_Doors", "Doors"));
    }
}

/// <summary>#107: the level fallback order (copied from the graph extractor) against a fake reader.</summary>
public class LevelResolverTests
{
    private sealed class FakeReader : ILevelReader
    {
        public long? Own;
        public Dictionary<string, long> Parameters = new();
        public Dictionary<long, string> Levels = new();
        public List<string> Asked = new();
        public bool Throws;

        public long? LevelId() => Throws ? throw new InvalidOperationException() : Own;

        public long? ParameterLevelId(string builtInParameter)
        {
            Asked.Add(builtInParameter);
            return Parameters.TryGetValue(builtInParameter, out var id) ? id : null;
        }

        public string? LevelName(long levelId) => Levels.TryGetValue(levelId, out var name) ? name : null;
    }

    [Fact]
    public void OwnLevelIdComesFirst()
    {
        var reader = new FakeReader { Own = 10, Parameters = { ["FAMILY_LEVEL_PARAM"] = 20 }, Levels = { [10] = "1. korrus", [20] = "2. korrus" } };
        var level = LevelResolver.Resolve(reader);
        Assert.Equal(10, level.Id);
        Assert.Equal("1. korrus", level.Name);
        Assert.Equal(LevelResolver.SourceLevelId, level.Source);
        Assert.Empty(reader.Asked);
    }

    [Fact]
    public void ParametersAreReadInOrder()
    {
        var reader = new FakeReader
        {
            Own = -1,
            Parameters = { ["SCHEDULE_LEVEL_PARAM"] = 30, ["LEVEL_PARAM"] = 40 },
            Levels = { [30] = "Schedule level", [40] = "Level param" }
        };
        var level = LevelResolver.Resolve(reader);
        Assert.Equal(30, level.Id);
        Assert.Equal("SCHEDULE_LEVEL_PARAM", level.Source);
        Assert.Equal(new[] { "FAMILY_LEVEL_PARAM", "RBS_START_LEVEL_PARAM", "SCHEDULE_LEVEL_PARAM" }, reader.Asked);
    }

    [Fact]
    public void MepCurvesUseTheStartLevel()
    {
        var reader = new FakeReader { Parameters = { ["RBS_START_LEVEL_PARAM"] = 50 }, Levels = { [50] = "Level 0" } };
        Assert.Equal("RBS_START_LEVEL_PARAM", LevelResolver.Resolve(reader).Source);
    }

    [Fact]
    public void AnIdThatIsNotALevelIsNoLevel()
    {
        var reader = new FakeReader { Own = 99, Parameters = { ["FAMILY_LEVEL_PARAM"] = 20 }, Levels = { [20] = "Level 1" } };
        var level = LevelResolver.Resolve(reader);
        Assert.False(level.Found);
        Assert.Equal(string.Empty, level.Name);
    }

    [Fact]
    public void NothingAndFailuresGiveNone()
    {
        Assert.False(LevelResolver.Resolve(new FakeReader()).Found);
        var throwing = new FakeReader { Throws = true, Parameters = { ["LEVEL_PARAM"] = 7 }, Levels = { [7] = "Roof" } };
        var level = LevelResolver.Resolve(throwing);
        Assert.Equal(7, level.Id);
        Assert.Equal("LEVEL_PARAM", level.Source);
    }
}

/// <summary>#107: argument parsing and the too-broad refusal.</summary>
public class GeometryArgsTests
{
    [Fact]
    public void Point_FromObjectArrayOrJsonText()
    {
        Assert.True(GeometryArgs.TryReadPoint(JObject.Parse("{\"x\":1,\"y\":2,\"z\":3}"), "point", out var a, out _));
        Assert.Equal(3, a.Z);
        Assert.True(GeometryArgs.TryReadPoint(new JArray(4, 5, 6), "point", out var b, out _));
        Assert.Equal(4, b.X);
        Assert.True(GeometryArgs.TryReadPoint("{\"X\":7,\"Y\":8}", "point", out var c, out _));
        Assert.Equal(0, c.Z);
        Assert.True(GeometryArgs.TryReadPoint(JValue.CreateString("[1.5, 2.5, 3.5]"), "point", out var d, out _));
        Assert.Equal(2.5, d.Y);
    }

    [Fact]
    public void Point_RejectsWhatIsNotAPoint()
    {
        Assert.False(GeometryArgs.TryReadPoint(null, "point", out _, out var none));
        Assert.Contains("point", none);
        Assert.False(GeometryArgs.TryReadPoint(new JArray(1, 2), "from", out _, out var two));
        Assert.StartsWith("from:", two);
        Assert.False(GeometryArgs.TryReadPoint(JObject.Parse("{\"x\":1}"), "to", out _, out _));
    }

    [Fact]
    public void Box_MinMaxOrFlat()
    {
        Assert.True(GeometryArgs.TryReadBox(JObject.Parse("{\"min\":{\"x\":10,\"y\":0,\"z\":0},\"max\":{\"x\":0,\"y\":5,\"z\":1}}"), "box", out var a, out _));
        Assert.Equal(0, a.Min.X);
        Assert.Equal(10, a.Max.X);
        Assert.True(GeometryArgs.TryReadBox("{\"minX\":0,\"minY\":0,\"minZ\":0,\"maxX\":1,\"maxY\":2,\"maxZ\":3}", "box", out var b, out _));
        Assert.Equal(3, b.Max.Z);
        Assert.False(GeometryArgs.TryReadBox(JObject.Parse("{\"min\":{\"x\":0,\"y\":0,\"z\":0}}"), "box", out _, out var half));
        Assert.Contains("both min and max", half);
        Assert.False(GeometryArgs.TryReadBox(new JArray(1, 2, 3), "box", out _, out _));
    }

    [Fact]
    public void IsGiven_TreatsEmptyValuesAsAbsent()
    {
        var args = new Dictionary<string, object?>
        {
            ["a"] = null, ["b"] = "", ["c"] = new JObject(), ["d"] = JValue.CreateNull(), ["e"] = new JArray(1, 2, 3), ["f"] = "{}"
        };
        Assert.False(GeometryArgs.IsGiven(args, "a"));
        Assert.False(GeometryArgs.IsGiven(args, "b"));
        Assert.False(GeometryArgs.IsGiven(args, "c"));
        Assert.False(GeometryArgs.IsGiven(args, "d"));
        Assert.True(GeometryArgs.IsGiven(args, "e"));
        Assert.False(GeometryArgs.IsGiven(args, "f"));
        Assert.False(GeometryArgs.IsGiven(args, "missing"));
    }

    [Fact]
    public void Legacy_DefaultsToTrue()
    {
        Assert.True(GeometryArgs.Legacy(new Dictionary<string, object?>()));
        Assert.False(GeometryArgs.Legacy(new Dictionary<string, object?> { ["legacy"] = false }));
        Assert.False(GeometryArgs.Legacy(new Dictionary<string, object?> { ["legacy"] = new JValue(false) }));
        Assert.True(GeometryArgs.Legacy(new Dictionary<string, object?> { ["legacy"] = new JValue(true) }));
    }

    [Fact]
    public void UnscopedQueriesAreRefusedInTheGuardsShape()
    {
        var refused = GeometryQueryGuard.Validate("r1", hasCategoryFilter: false, hasElementIds: false, hasSpatialScope: false, "needs ids", "Pass elementIds");
        Assert.NotNull(refused);
        Assert.False(refused!.Success);
        Assert.Equal("geometry_query_too_broad", refused.Status);
        Assert.Equal("r1", refused.RequestId);
        var data = JObject.FromObject(refused.Data!);
        Assert.Equal(ToolErrorCodes.GeometryQueryTooBroad, (string?)data["error"]);
        Assert.Equal("Pass elementIds", (string?)data["suggestedActions"]![0]);
        Assert.Equal(QueryLimits.Default.MaxGeometryElements, (int)data["limits"]!["maxElementIds"]!);

        Assert.Null(GeometryQueryGuard.Validate("r2", false, true, false, "x"));
        Assert.Null(GeometryQueryGuard.Validate("r3", false, false, true, "x"));
        Assert.Null(GeometryQueryGuard.Validate("r4", true, false, false, "x"));
    }

    [Fact]
    public void RowCapsFollowTheCategoryRule()
    {
        var warnings = new List<string>();
        Assert.Equal(200, GeometryQueryGuard.ResolveMaxResults(0, hasCategoryFilter: false, warnings, out var broad));
        Assert.False(broad);
        Assert.Equal(150, GeometryQueryGuard.ResolveMaxResults(150, false, warnings, out broad));
        Assert.False(broad);
        GeometryQueryGuard.ResolveMaxResults(500, false, warnings, out broad);
        Assert.True(broad);
        Assert.Equal(500, GeometryQueryGuard.ResolveMaxResults(500, true, warnings, out broad));
        Assert.False(broad);
        Assert.Equal(2000, GeometryQueryGuard.ResolveMaxResults(9999, true, warnings, out _));
        Assert.Contains(warnings, w => w.Contains("exceeds the cap"));
        Assert.Equal(100, GeometryQueryGuard.MaxElementIds);
    }
}
