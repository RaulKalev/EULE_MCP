using RevitMCP.Addin.Village;
using Xunit;

namespace RevitMCP.Tests;

public class VillageThemeScorerTests
{
    private static VillageThemeInput Input(string modelId, params (string Category, long Count)[] categories)
    {
        var input = new VillageThemeInput { ModelId = modelId };
        foreach (var (category, count) in categories)
            input.ElementsByCategory.Add(new KeyValuePair<string, long>(category, count));
        return input;
    }

    [Fact]
    public void FireAlarmHeavyProject_GetsTheFireAlarmTheme()
    {
        var input = Input("model-a", ("Fire Alarm Devices", 400), ("Lighting Fixtures", 60), ("Walls", 900), ("Electrical Fixtures", 40));
        var r = new VillageThemeScorer().Score(input);

        Assert.Equal("fire_alarm", r.Theme);
        Assert.Equal("fire_alarm", r.Dominant);
        Assert.Equal("Fire alarm", r.Label);
        Assert.Equal("#b8432f", r.Primary);
        Assert.Equal("fire_alarm", r.Scores[0].System);
        Assert.True(r.Scores[0].Dominant);
        Assert.Equal(400, r.Scores[0].CategoryScore);
        Assert.Equal(0.8, r.Scores[0].Share);
        Assert.Contains(r.Scores[0].Categories, c => c.Name == "Fire Alarm Devices" && c.Count == 400);
        Assert.Equal(new[] { "fire_alarm", "lighting" }, r.VisibleSystems);   // electrical: 40/500 = 8% < 10%
        Assert.Equal(500, r.SampleCount);                                        // walls are not classified
        Assert.Contains("dominant", r.Reason);
        Assert.Equal("default", r.ConfigSource);
    }

    [Fact]
    public void BalancedProject_IsMixed()
    {
        var input = Input("model-b", ("Fire Alarm Devices", 100), ("Security Devices", 90), ("Lighting Fixtures", 110), ("Data Devices", 80));
        var r = new VillageThemeScorer().Score(input);

        Assert.Equal("mixed", r.Theme);
        Assert.Null(r.Dominant);
        Assert.Equal(4, r.VisibleSystems.Count);
        Assert.All(r.Scores, s => Assert.False(s.Dominant));
        Assert.Equal("#6c8e5a", r.Primary);
    }

    [Fact]
    public void SmallSample_StaysNeutral()
    {
        var input = Input("model-c", ("Fire Alarm Devices", 12));
        var r = new VillageThemeScorer().Score(input);
        Assert.Equal("neutral", r.Theme);
        Assert.Contains("minimum 20", r.Reason);
        Assert.Equal(1.0, r.Scores[0].Share);
        Assert.False(r.Scores[0].Dominant);
    }

    [Fact]
    public void UnknownCategoriesOnly_StaysNeutral()
    {
        var input = Input("model-d", ("Walls", 5000), ("Doors", 300), ("Generic Models", 200));
        var r = new VillageThemeScorer().Score(input);
        Assert.Equal("neutral", r.Theme);
        Assert.Equal(0, r.TotalScore);
        Assert.Empty(r.VisibleSystems);
    }

    [Fact]
    public void NamesAlone_CannotDominate()
    {
        // Thousands of instances named like fire-alarm devices but categorised as generic models.
        var input = Input("model-e", ("Lighting Fixtures", 40), ("Generic Models", 5000));
        input.Types.Add(new VillageTypeSample { Name = "ATS: Suitsuandur", InstanceCount = 3000 });
        input.Types.Add(new VillageTypeSample { Name = "Sireen: Wall", InstanceCount = 2000 });

        var r = new VillageThemeScorer().Score(input);
        var fire = r.Scores.Single(s => s.System == "fire_alarm");

        Assert.Equal(5000, fire.NameScore);
        Assert.Equal(10, fire.NameContribution);   // capped at category evidence (0) + minSystemCount
        Assert.Equal(0, fire.CategoryScore);
        Assert.False(fire.Dominant);
        Assert.NotEqual("fire_alarm", r.Theme);
        Assert.Equal("lighting", r.Theme);          // 40 category-classified lighting fixtures dominate the 50-point total
        Assert.Equal(2, fire.MatchedTypes);
        Assert.Contains(fire.Keywords, k => k.Name == "ATS" && k.Count == 3000);
    }

    [Fact]
    public void Names_AddEvidenceWhenCategoriesAgree()
    {
        var input = Input("model-f", ("Fire Alarm Devices", 30), ("Lighting Fixtures", 30));
        input.Types.Add(new VillageTypeSample { Name = "EN_ATS-Häiresireen", InstanceCount = 60 });

        var r = new VillageThemeScorer().Score(input);
        var fire = r.Scores.Single(s => s.System == "fire_alarm");
        Assert.Equal(30, fire.NameContribution);   // 60 × 0.5, under the cap of 30 + 10
        Assert.Equal(60, fire.Score);
        Assert.Equal("fire_alarm", r.Theme);       // 60 / 90 = 67%
    }

    [Theory]
    [InlineData("EN_ATS-Häiresireen", "ATS")]
    [InlineData("Formats: Sheet A1", null)]              // "ats" inside "formats" is not a token
    [InlineData("Fire Alarm Sounder 24V", "fire alarm")]
    [InlineData("Suitsuandur Bosch", "suitsuandur")]
    [InlineData("Basic Wall", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void KeywordMatching_IsTokenAwareForShortKeywords(string? name, string? expected)
    {
        var keywords = VillageThemeConfig.Default().FindSystem("fire_alarm")!.Keywords;
        Assert.Equal(expected, VillageThemeScorer.FirstMatchingKeyword(name, keywords));
    }

    [Fact]
    public void KeywordMatching_ShortKeywordsDoNotMatchInsideWords()
    {
        var it = VillageThemeConfig.Default().FindSystem("it_av")!.Keywords;
        Assert.Null(VillageThemeScorer.FirstMatchingKeyword("Inside Outside Wall", it));   // "side" is a token keyword
        Assert.Equal("side", VillageThemeScorer.FirstMatchingKeyword("EN_SIDE_Pistik", it));
        Assert.Equal("data", VillageThemeScorer.FirstMatchingKeyword("TH_DataDevices_RJ45Keystone", it)); // camelCase split
        Assert.Equal("rack", VillageThemeScorer.FirstMatchingKeyword("Rack AV 19in", it));  // configured order wins
        Assert.Equal("AV", VillageThemeScorer.FirstMatchingKeyword("Amplifier AV 2ch", it));
        Assert.Null(VillageThemeScorer.FirstMatchingKeyword("Pavement", it));
        Assert.Null(VillageThemeScorer.FirstMatchingKeyword("Database Cabinet", it));      // "data" is not a token here

        var tokens = VillageThemeScorer.Tokenize("TH_DataDevices_RJ45Keystone");
        Assert.Contains("data", tokens);
        Assert.Contains("devices", tokens);
        Assert.Contains("datadevices", tokens);
        Assert.Contains("rj45keystone", tokens);
        Assert.DoesNotContain("rj45", tokens);
    }

    [Fact]
    public void Scoring_IsDeterministic()
    {
        var input = Input("model-g", ("Security Devices", 300), ("Data Devices", 120), ("Lighting Fixtures", 90));
        input.Types.Add(new VillageTypeSample { Name = "EN_VVS-Kaamera", InstanceCount = 80 });
        var scorer = new VillageThemeScorer();
        var a = Newtonsoft.Json.JsonConvert.SerializeObject(scorer.Score(input));
        var b = Newtonsoft.Json.JsonConvert.SerializeObject(scorer.Score(input));
        Assert.Equal(a, b);
        Assert.Contains("\"theme\":\"security\"", a);
        Assert.Contains("\"config_source\":\"default\"", a);
        Assert.Contains("\"thresholds\":{", a);
    }

    [Fact]
    public void Identity_IsStableAndDistinctPerModel()
    {
        var a1 = VillageThemeScorer.IdentityFor("abcdef0123456789");
        var a2 = VillageThemeScorer.IdentityFor("ABCDEF0123456789");
        var b = VillageThemeScorer.IdentityFor("0123456789abcdef");
        Assert.Equal(a1.Hue, a2.Hue);
        Assert.Equal(a1.Seed, a2.Seed);
        Assert.NotEqual(a1.Seed, b.Seed);
        Assert.InRange(a1.Hue, 0, 359);
        Assert.InRange(a1.Pattern, 0, 3);
        Assert.Equal(200, VillageThemeScorer.IdentityFor(VillageModelId.NoDocument).Hue);
        Assert.Equal(0, VillageThemeScorer.IdentityFor(null).Seed);
    }

    [Fact]
    public void Config_OverridesThresholdsAndSystemLists()
    {
        var json = "{\"minSampleCount\":5,\"minSystemCount\":5,\"dominantShare\":0.6,\"visibleShare\":0.2,\"nameWeight\":\"1.0\"," +
                   "\"systems\":{\"fire_alarm\":{\"categories\":[\"Tulekahjusignalisatsiooni seadmed\"],\"keywords\":[\"ATS\"],\"primary\":\"#ff0000\",\"accent\":\"bad\",\"label\":\"ATS\"}," +
                   "\"moon_base\":{\"categories\":[\"Rockets\"]},\"lighting\":\"nope\"}}";
        var config = VillageThemeConfig.FromJson(json);

        Assert.Equal("config", config.Source);
        Assert.Equal(5, config.MinSampleCount);
        Assert.Equal(5, config.MinSystemCount);
        Assert.Equal(0.6, config.DominantShare);
        Assert.Equal(0.2, config.VisibleShare);
        Assert.Equal(1.0, config.NameWeight);
        Assert.Equal(5, config.Systems.Count);
        var fire = config.FindSystem("fire_alarm")!;
        Assert.Equal(new[] { "Tulekahjusignalisatsiooni seadmed" }, fire.Categories);
        Assert.Equal(new[] { "ATS" }, fire.Keywords);
        Assert.Equal("#ff0000", fire.Primary);
        Assert.Equal("#ff8a75", fire.Accent);    // invalid colour ignored
        Assert.Equal("ATS", fire.Label);
        Assert.Contains("Lighting Fixtures", config.FindSystem("lighting")!.Categories); // untouched

        var r = new VillageThemeScorer(config).Score(Input("m", ("Tulekahjusignalisatsiooni seadmed", 8), ("Lighting Fixtures", 2)));
        Assert.Equal("fire_alarm", r.Theme);
        Assert.Equal("config", r.ConfigSource);
        Assert.Equal(0.6, r.Thresholds["dominant_share"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"minSampleCount\":-100,\"dominantShare\":9,\"visibleShare\":0}")]
    public void Config_FallsBackOrClampsOnGarbage(string? json)
    {
        var config = VillageThemeConfig.FromJson(json);
        Assert.Equal(5, config.Systems.Count);
        Assert.InRange(config.MinSampleCount, 1, 100000);
        Assert.InRange(config.DominantShare, 0.2, 1.0);
        Assert.InRange(config.VisibleShare, 0.01, 1.0);
    }

    [Fact]
    public void Defaults_CoverEveryElvSystemWithColoursAndEvidence()
    {
        var config = VillageThemeConfig.Default();
        Assert.Equal(new[] { "fire_alarm", "security", "lighting", "it_av", "electrical" }, config.Systems.Select(s => s.Id).ToArray());
        Assert.All(config.Systems, s =>
        {
            Assert.NotEmpty(s.Categories);
            Assert.NotEmpty(s.Keywords);
            Assert.StartsWith("#", s.Primary);
            Assert.StartsWith("#", s.Accent);
            Assert.False(string.IsNullOrEmpty(s.Label));
        });
    }
}
