using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Styles;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Style usage counting and delete planning of revit_get_style_usage and revit_delete_styles, and Object Styles kinds.</summary>
public class StyleUsagePlannerTests
{
    private static readonly List<StyleInfo> Lines = new()
    {
        new() { Kind = StyleKinds.LineStyle, Id = -2000831, Name = "<Thin Lines>", BuiltIn = true },
        new() { Kind = StyleKinds.LineStyle, Id = 101, Name = "EL_Kaabel" },
        new() { Kind = StyleKinds.LineStyle, Id = 102, Name = "EL_Vana" },
        new() { Kind = StyleKinds.LineStyle, Id = 103, Name = "EL_Tühi" }
    };

    private static readonly List<StyleInfo> Types = new()
    {
        new() { Kind = StyleKinds.FilledRegionType, Id = 201, Name = "Solid Black" },
        new() { Kind = StyleKinds.FilledRegionType, Id = 202, Name = "EL_Viirutus" }
    };

    private static readonly List<StyleUse> Uses = new()
    {
        new() { ElementId = 1, StyleId = 102, StyleKind = StyleKinds.LineStyle, Use = "DetailCurve", ViewId = 10, ViewName = "Plan 1" },
        new() { ElementId = 2, StyleId = 102, StyleKind = StyleKinds.LineStyle, Use = "DetailCurve", ViewId = 10, ViewName = "Plan 1" },
        new() { ElementId = 3, StyleId = 102, StyleKind = StyleKinds.LineStyle, Use = "ModelCurve" },
        new() { ElementId = 4, StyleId = 102, StyleKind = StyleKinds.LineStyle, Use = StyleUseKinds.RegionBoundary, ViewId = 11, ViewName = "Plan 2", RegionId = 50 },
        new() { ElementId = 5, StyleId = 101, StyleKind = StyleKinds.LineStyle, Use = "DetailCurve", ViewId = 11, ViewName = "Plan 2" },
        new() { ElementId = 50, StyleId = 202, StyleKind = StyleKinds.FilledRegionType, Use = StyleUseKinds.FilledRegion, ViewId = 11, ViewName = "Plan 2" },
        // An element id equal to a style id of the other kind must not be counted for it.
        new() { ElementId = 6, StyleId = 201, StyleKind = StyleKinds.LineStyle, Use = "DetailCurve", ViewId = 10 }
    };

    // ── Finding and close names ───────────────────────────────────────────────

    [Fact]
    public void Finds_by_name_ignoring_case_and_by_id()
    {
        Assert.Equal(102, StyleUsagePlanner.Find("el_vana", Lines)!.Id);
        Assert.Equal(101, StyleUsagePlanner.Find("101", Lines)!.Id);
        Assert.Null(StyleUsagePlanner.Find("Puudub", Lines));
    }

    [Fact]
    public void Suggests_close_names()
    {
        Assert.Equal("EL_Kaabel", StyleUsagePlanner.CloseNames("EL_Kabel", Lines.Select(s => s.Name)).First());
        Assert.Contains("EL_Vana", StyleUsagePlanner.CloseNames("Vana", Lines.Select(s => s.Name)));
        Assert.Empty(StyleUsagePlanner.CloseNames("Täiesti muu", Lines.Select(s => s.Name)));
        Assert.Contains("Did you mean 'EL_Kaabel'?", StyleUsagePlanner.DidYouMean("EL_Kabel", new[] { "EL_Kaabel" }));
    }

    // ── Usage ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Summarises_usage_by_use_and_by_view()
    {
        var usage = StyleUsagePlanner.Summarise(Lines[2], Uses, 50);

        Assert.Equal(4, usage.Count);
        Assert.Equal(2, usage.ByUse["DetailCurve"]);
        Assert.Equal(1, usage.ByUse["ModelCurve"]);
        Assert.Equal(1, usage.ByUse[StyleUseKinds.RegionBoundary]);
        var plan1 = usage.ByView.First();                         // the busiest view first
        Assert.Equal(10, plan1.ViewId);
        Assert.Equal(new long[] { 1, 2 }, plan1.ElementIds);
        var model = usage.ByView.Single(v => v.ViewId == null);
        Assert.Equal("(model)", model.ViewName);
        Assert.False(usage.IdsTruncated);
    }

    [Fact]
    public void Caps_the_ids_across_views_but_not_the_counts()
    {
        var usage = StyleUsagePlanner.Summarise(Lines[2], Uses, 2);
        Assert.Equal(4, usage.Count);
        Assert.Equal(2, usage.ByView.Sum(v => v.ElementIds.Count));
        Assert.Equal(4, usage.ByView.Sum(v => v.Count));
        Assert.True(usage.IdsTruncated);
    }

    [Fact]
    public void Counts_a_style_only_for_its_own_kind()
    {
        Assert.Equal(0, StyleUsagePlanner.Summarise(Types[0], Uses, 10).Count);   // id 201 is used as a line style id only
        Assert.Equal(1, StyleUsagePlanner.Summarise(Types[1], Uses, 10).Count);
    }

    // ── Delete planning ───────────────────────────────────────────────────────

    private static StyleDeletePlan Plan(string json)
    {
        var errors = new List<string>();
        var request = StyleRequestParser.ParseDelete(JObject.Parse(json).Properties().ToDictionary(p => p.Name, p => (object?)p.Value), errors);
        Assert.Empty(errors);
        return StyleUsagePlanner.PlanDelete(request, Lines, Types, Uses);
    }

    [Fact]
    public void Deletes_an_unused_style()
    {
        var plan = Plan("{\"lineStyles\":[\"EL_Tühi\"]}");
        Assert.True(plan.Ready);
        Assert.Empty(plan.Reassign);
        Assert.Equal(StyleDeleteStatus.Delete, plan.Items.Single().Status);
    }

    [Fact]
    public void Refuses_a_style_in_use_without_a_replacement_and_with_it_the_whole_request()
    {
        var plan = Plan("{\"lineStyles\":[\"EL_Tühi\",\"EL_Vana\"]}");
        Assert.False(plan.Ready);
        var refused = plan.Items.Single(i => i.Style!.Name == "EL_Vana");
        Assert.Equal(StyleDeleteCodes.InUse, refused.Code);
        Assert.Equal(4, refused.Uses);
        Assert.Contains("replaceWith.lineStyle", refused.Reason);
        Assert.Empty(plan.Reassign);
    }

    [Fact]
    public void Moves_the_users_to_the_replacement_first()
    {
        var plan = Plan("{\"lineStyles\":[\"EL_Vana\"],\"filledRegionTypes\":[\"EL_Viirutus\"],\"replaceWith\":{\"lineStyle\":\"<Thin Lines>\",\"filledRegionType\":\"Solid Black\"}}");
        Assert.True(plan.Ready, string.Join(" ", plan.Problems));
        Assert.Equal("<Thin Lines>", plan.LineReplacement!.Name);   // a built-in style may be the replacement
        Assert.Equal(new long[] { 1, 2, 3, 4 }, plan.Reassign.Where(u => u.StyleKind == StyleKinds.LineStyle).Select(u => u.ElementId).OrderBy(i => i));
        Assert.Equal(50, plan.Reassign.Single(u => u.StyleKind == StyleKinds.FilledRegionType).ElementId);
        Assert.All(plan.Items, i => Assert.NotNull(i.ReassignTo));
        Assert.Contains("moved to '<Thin Lines>'", StyleUsagePlanner.Describe(plan));
    }

    [Theory]
    [InlineData("{\"lineStyles\":[\"<Thin Lines>\"]}", StyleDeleteCodes.BuiltIn)]
    [InlineData("{\"lineStyles\":[\"EL_Puudub\"]}", StyleDeleteCodes.NotFound)]
    [InlineData("{\"lineStyles\":[\"EL_Tühi\",\"103\"]}", StyleDeleteCodes.ListedTwice)]
    [InlineData("{\"lineStyles\":[\"EL_Vana\",\"EL_Kaabel\"],\"replaceWith\":{\"lineStyle\":\"EL_Kaabel\"}}", StyleDeleteCodes.ReplacementDeleted)]
    [InlineData("{\"filledRegionTypes\":[\"Solid Black\",\"EL_Viirutus\"],\"replaceWith\":{\"filledRegionType\":\"Solid Black\"}}", StyleDeleteCodes.ReplacementDeleted)]
    [InlineData("{\"filledRegionTypes\":[\"Solid Black\"]}", null)]
    public void Refuses(string json, string? code)
    {
        var plan = Plan(json);
        if (code == null)
        {
            Assert.True(plan.Ready);    // Solid Black is unused and EL_Viirutus remains
            return;
        }
        Assert.False(plan.Ready);
        Assert.Contains(plan.Items, i => i.Code == code);
    }

    [Fact]
    public void Refuses_to_delete_every_filled_region_type()
    {
        var types = new List<StyleInfo> { Types[0] };
        var errors = new List<string>();
        var request = StyleRequestParser.ParseDelete(new Dictionary<string, object?> { ["filledRegionTypes"] = JToken.Parse("[\"Solid Black\"]") }, errors);
        var plan = StyleUsagePlanner.PlanDelete(request, Lines, types, Uses);
        Assert.False(plan.Ready);
        Assert.Equal(StyleDeleteCodes.LastFilledRegionType, plan.Items.Single().Code);
    }

    [Fact]
    public void A_missing_replacement_is_a_problem_with_close_names()
    {
        var plan = Plan("{\"lineStyles\":[\"EL_Vana\"],\"replaceWith\":{\"lineStyle\":\"EL_Kabel\"}}");
        Assert.False(plan.Ready);
        Assert.Contains(plan.Problems, p => p.StartsWith(StyleDeleteCodes.ReplacementNotFound) && p.Contains("'EL_Kaabel'"));
    }

    [Fact]
    public void An_empty_request_is_a_problem_and_an_unused_replacement_a_warning()
    {
        Assert.False(Plan("{}").Ready);
        var plan = Plan("{\"lineStyles\":[\"EL_Tühi\"],\"replaceWith\":{\"filledRegionType\":\"Solid Black\"}}");
        Assert.True(plan.Ready);
        Assert.Contains(plan.Warnings, w => w.Contains("no filled region type is deleted"));
    }

    // ── Arguments ─────────────────────────────────────────────────────────────

    [Fact]
    public void Reads_references_as_names_or_ids()
    {
        var errors = new List<string>();
        Assert.Equal(new[] { "A", "12" }, StyleRequestParser.References(new Dictionary<string, object?> { ["x"] = JToken.Parse("[\"A\", 12, \"\", null]") }, "x", errors));
        Assert.Equal(new[] { "Ühe nimi" }, StyleRequestParser.References(new Dictionary<string, object?> { ["x"] = "Ühe nimi" }, "x", errors));
        Assert.Empty(errors);
        StyleRequestParser.References(new Dictionary<string, object?> { ["x"] = JToken.Parse("[{\"a\":1}]") }, "x", errors);
        Assert.Contains(errors, e => e.Contains("x[0] must be a name or an id"));

        var bad = new List<string>();
        StyleRequestParser.ParseDelete(new Dictionary<string, object?> { ["replaceWith"] = JToken.Parse("[1]") }, bad);
        Assert.Contains(bad, e => e.Contains("replaceWith must be an object"));
    }

    // ── Object Styles kinds ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Model", -2000011L, "OST_Walls", ObjectStyleKinds.Model)]
    [InlineData("Annotation", -2000280L, "OST_TitleBlocks", ObjectStyleKinds.Annotation)]
    [InlineData("Model", 123456L, null, ObjectStyleKinds.Imported)]
    [InlineData("Model", -2000064L, "OST_ImportObjectStyles", ObjectStyleKinds.Imported)]
    [InlineData("Internal", -2000999L, "OST_Something", null)]
    [InlineData("AnalyticalModel", -2009000L, "OST_Analytical", null)]
    public void Classifies_object_style_categories(string type, long id, string? builtIn, string? kind)
        => Assert.Equal(kind, ObjectStyleKinds.Classify(type, id, builtIn));

    [Theory]
    [InlineData(null, "")]
    [InlineData("Annotations", ObjectStyleKinds.Annotation)]
    [InlineData("import", ObjectStyleKinds.Imported)]
    [InlineData("model", ObjectStyleKinds.Model)]
    [InlineData("analytical", null)]
    public void Parses_the_kind_filter(string? text, string? expected)
        => Assert.Equal(expected, ObjectStyleKinds.ParseFilter(text));
}
