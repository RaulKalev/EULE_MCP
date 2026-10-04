using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Annotation;
using Xunit;

namespace RevitMCP.Tests;

public class TextNoteEditPlannerTests
{
    private static TextNoteEditOptions Options(
        string mode,
        string? text = null,
        string? find = null,
        string? replace = null,
        string? prefix = null,
        string? suffix = null,
        bool matchCase = false,
        bool wholeWord = false,
        object? map = null,
        double widthMm = 0,
        bool autoWidth = false)
    {
        var options = TextNoteEditPlanner.ParseOptions(mode, text, find, replace, prefix, suffix, matchCase, wholeWord,
            map, widthMm, autoWidth, out var error);
        Assert.True(options != null, error);
        return options!;
    }

    private static string? Error(
        string mode,
        string? text = null,
        string? find = null,
        string? suffix = null,
        string? prefix = null,
        object? map = null,
        double widthMm = 0,
        bool autoWidth = false)
    {
        var options = TextNoteEditPlanner.ParseOptions(mode, text, find, null, prefix, suffix, false, false, map, widthMm,
            autoWidth, out var error);
        Assert.Null(options);
        return error;
    }

    [Theory]
    [InlineData("set", TextNoteEditMode.Set)]
    [InlineData("findReplace", TextNoteEditMode.FindReplace)]
    [InlineData("find_replace", TextNoteEditMode.FindReplace)]
    [InlineData("APPEND", TextNoteEditMode.Append)]
    [InlineData("prepend", TextNoteEditMode.Prepend)]
    [InlineData("map", TextNoteEditMode.Map)]
    [InlineData("", TextNoteEditMode.None)]
    public void NormalizeMode_AcceptsKnownModes(string raw, TextNoteEditMode expected) =>
        Assert.Equal(expected, TextNoteEditPlanner.NormalizeMode(raw));

    [Fact]
    public void NormalizeMode_RejectsUnknown() => Assert.Null(TextNoteEditPlanner.NormalizeMode("translate"));

    [Fact]
    public void Validation_RejectsMissingModeArguments()
    {
        Assert.Contains("text", Error("set", text: "  "));
        Assert.Contains("find", Error("findReplace"));
        Assert.Contains("suffix", Error("append"));
        Assert.Contains("prefix", Error("prepend"));
        Assert.Contains("map", Error("map"));
        Assert.Contains("mode", Error(""));
        Assert.Contains("Unknown mode", Error("translate"));
    }

    [Fact]
    public void Validation_RejectsConflictingWidthOptions()
    {
        Assert.Contains("not both", Error("set", text: "A", widthMm: 50, autoWidth: true));
        Assert.Contains("widthMm", Error("set", text: "A", widthMm: -1));
    }

    [Fact]
    public void Validation_WidthOnlyIsAllowedWithoutMode()
    {
        var options = Options("", widthMm: 40);
        Assert.Equal(TextNoteEditMode.None, options.Mode);
        var plan = TextNoteEditPlanner.Plan("Hello\r", options);
        Assert.False(plan.TextChanged);
    }

    [Fact]
    public void Set_ReplacesContentButKeepsTrailingParagraphBreak()
    {
        var plan = TextNoteEditPlanner.Plan("Old text\r", Options("set", text: "New\nline"));
        Assert.Equal("New\rline\r", plan.NewText);
        Assert.True(plan.TextChanged);
        Assert.Single(plan.Splices);
    }

    [Fact]
    public void Set_SameTextIsUnchanged()
    {
        var plan = TextNoteEditPlanner.Plan("Same\r", Options("set", text: "Same"));
        Assert.False(plan.TextChanged);
        Assert.Equal("text already as requested", plan.UnchangedReason);
        Assert.Empty(plan.Splices);
    }

    [Fact]
    public void Append_InsertsBeforeTrailingBreak()
    {
        var plan = TextNoteEditPlanner.Plan("Arvutivõrgu ühendused\r",
            Options("append", suffix: " // Data network connections"));
        Assert.Equal("Arvutivõrgu ühendused // Data network connections\r", plan.NewText);
    }

    [Fact]
    public void Prepend_InsertsAtStart()
    {
        var plan = TextNoteEditPlanner.Plan("Kapp\r", Options("prepend", prefix: "EN: "));
        Assert.Equal("EN: Kapp\r", plan.NewText);
    }

    [Fact]
    public void FindReplace_IsCaseInsensitiveByDefault()
    {
        var plan = TextNoteEditPlanner.Plan("Cable CABLE cable\r", Options("findReplace", find: "cable", replace: "wire"));
        Assert.Equal("wire wire wire\r", plan.NewText);
        Assert.Equal(3, plan.Splices.Count);
    }

    [Fact]
    public void FindReplace_MatchCase()
    {
        var plan = TextNoteEditPlanner.Plan("Cable CABLE cable\r",
            Options("findReplace", find: "cable", replace: "wire", matchCase: true));
        Assert.Equal("Cable CABLE wire\r", plan.NewText);
    }

    [Fact]
    public void FindReplace_WholeWord()
    {
        var plan = TextNoteEditPlanner.Plan("cat catalog cat_x (cat)\r",
            Options("findReplace", find: "cat", replace: "dog", wholeWord: true));
        Assert.Equal("dog catalog cat_x (dog)\r", plan.NewText);
    }

    [Fact]
    public void FindReplace_NotFoundIsUnchanged()
    {
        var plan = TextNoteEditPlanner.Plan("Hello\r", Options("findReplace", find: "xyz", replace: "a"));
        Assert.False(plan.TextChanged);
        Assert.Equal("find text not present", plan.UnchangedReason);
    }

    [Fact]
    public void FindReplace_EmptyReplacementRemovesText()
    {
        var plan = TextNoteEditPlanner.Plan("A - B\r", Options("findReplace", find: " - ", replace: ""));
        Assert.Equal("AB\r", plan.NewText);
    }

    [Fact]
    public void FindReplace_ResultEmptyIsSkipped()
    {
        var plan = TextNoteEditPlanner.Plan("Remove\r", Options("findReplace", find: "Remove", replace: ""));
        Assert.NotNull(plan.SkipReason);
    }

    [Fact]
    public void FindReplace_MatchesAcrossLineBreaksGivenAsNewline()
    {
        var plan = TextNoteEditPlanner.Plan("Line1\rLine2\r", Options("findReplace", find: "1\n", replace: "1 / "));
        Assert.Equal("Line1 / Line2\r", plan.NewText);
    }

    [Fact]
    public void FindReplace_NonOverlapping()
    {
        Assert.Equal(new[] { 0, 2 }, TextNoteEditPlanner.FindAll("aaaa", "aa", false, false));
    }

    [Fact]
    public void Map_MatchesWholeTrimmedText()
    {
        var map = new JObject
        {
            ["Arvutivõrgu ühendused"] = "Arvutivõrgu ühendused // Data network connections",
            ["Toide"] = "Toide // Power"
        };
        var options = Options("map", map: map);

        var hit = TextNoteEditPlanner.Plan("  arvutivõrgu ühendused \r", options);
        Assert.Equal("  Arvutivõrgu ühendused // Data network connections \r", hit.NewText);

        var miss = TextNoteEditPlanner.Plan("Toide ja maandus\r", options);
        Assert.False(miss.TextChanged);
        Assert.Equal("text not in map", miss.UnchangedReason);
    }

    [Fact]
    public void Map_MatchCaseIsRespected()
    {
        var options = Options("map", map: "{\"Toide\": \"Toide // Power\"}", matchCase: true);
        Assert.False(TextNoteEditPlanner.Plan("toide\r", options).TextChanged);
        Assert.True(TextNoteEditPlanner.Plan("Toide\r", options).TextChanged);
    }

    [Fact]
    public void Map_AcceptsJsonStringAndMultiLineKeys()
    {
        var options = Options("map", map: "{\"Rida 1\\nRida 2\": \"Row 1\\nRow 2\"}");
        var plan = TextNoteEditPlanner.Plan("Rida 1\rRida 2\r", options);
        Assert.Equal("Row 1\rRow 2\r", plan.NewText);
    }

    [Fact]
    public void Map_RejectsInvalidInput()
    {
        Assert.Contains("JSON", Error("map", map: "{not json"));
        Assert.Contains("JSON object", Error("map", map: "[1,2]"));
        Assert.Contains("empty", Error("map", map: "{\"A\": \"\"}"));
        Assert.Contains("more than once", Error("map", map: "{\"A\": \"x\", \"a \": \"y\"}"));
        Assert.Contains("string", Error("map", map: "{\"A\": 5}"));
    }

    [Fact]
    public void MatchesFilter_IsCaseInsensitiveSubstring()
    {
        Assert.True(TextNoteEditPlanner.MatchesFilter("Arvutivõrgu ühendused", "VÕRGU"));
        Assert.True(TextNoteEditPlanner.MatchesFilter("anything", ""));
        Assert.False(TextNoteEditPlanner.MatchesFilter("Toide", "kaabel"));
    }

    [Fact]
    public void ApplySplices_MatchesPlannedText()
    {
        var plan = TextNoteEditPlanner.Plan("a-b-c\r", Options("findReplace", find: "-", replace: " / "));
        Assert.Equal(plan.NewText, TextNoteEditPlanner.ApplySplices(plan.OldText, plan.Splices));
        Assert.Equal("a / b / c\r", plan.NewText);
    }

    [Fact]
    public void LongestLineLength_SplitsOnBreaks()
    {
        Assert.Equal(11, TextNoteEditPlanner.LongestLineLength("short\rmuch longer\nx\r"));
        Assert.Equal(0, TextNoteEditPlanner.LongestLineLength(""));
    }

    [Fact]
    public void EstimateWidthMm_ScalesWithTextSizeAndFactor()
    {
        // 9 chars + 1 slack, 2.5 mm text, factor 1 → 10 × 2.5 × 0.62 = 15.5 mm.
        Assert.Equal(15.5, TextNoteEditPlanner.EstimateWidthMm("123456789", 2.5, 1.0));
        Assert.Equal(31.0, TextNoteEditPlanner.EstimateWidthMm("123456789", 5.0, 1.0));
        Assert.True(TextNoteEditPlanner.EstimateWidthMm("123456789", 2.5, 0.8) < 15.5);
        Assert.Equal(0, TextNoteEditPlanner.EstimateWidthMm("", 2.5, 1.0));
        Assert.Equal(0, TextNoteEditPlanner.EstimateWidthMm("abc", 0, 1.0));
    }
}
