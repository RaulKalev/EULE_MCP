using System.Text.RegularExpressions;
using Newtonsoft.Json;
using RevitMCP.Addin.Geometry;
using RevitMCP.Addin.Graph;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Query;
using RevitMCP.Addin.ViewElements;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#136: the pure parts of the view argument on the element reads.</summary>
public class ViewElementReadsTests
{
    private static ViewTargetCandidate View(long id, string name, string type = "FloorPlan", bool template = false) =>
        new() { Id = id, Name = name, ViewType = type, IsTemplate = template };

    private static ViewTargetCandidate Sheet(long id, string number, string name, bool placeholder = false) =>
        new() { Id = id, Name = name, ViewType = "DrawingSheet", IsSheet = true, SheetNumber = number, IsPlaceholderSheet = placeholder };

    private static readonly ViewTargetCandidate[] Views =
    {
        View(1000, "1. korrus"),
        View(1001, "2. korrus"),
        View(1002, "Lõige A", "Section"),
        View(1003, "Mall plaan", "FloorPlan", template: true),
        View(1004, "Uksed", "Schedule"),
        View(1005, "Sama nimi", "FloorPlan"),
        View(1006, "Sama nimi", "CeilingPlan"),
        View(1007, "101"),
        Sheet(2000, "EN-101", "ATS plaan"),
        Sheet(2001, "EN-102", "Side plaan"),
        Sheet(2002, "EN-999", "Tulevane", placeholder: true)
    };

    // ── resolving a view reference ────────────────────────────────────────────

    [Theory]
    [InlineData("active")]
    [InlineData(" ACTIVE ")]
    public void ActiveKeyword_IsRecognisedIgnoringCase(string reference) =>
        Assert.True(ViewTargetMatcher.IsActiveKeyword(reference));

    [Theory]
    [InlineData("1000", 1000, "id")]
    [InlineData("1. KORRUS", 1000, "name")]
    [InlineData("  Lõige A ", 1002, "name")]
    [InlineData("2000", 2000, "id")]
    [InlineData("en-102", 2001, "sheetNumber")]
    [InlineData("ATS plaan", 2000, "name")]
    [InlineData("EN-101 - ATS plaan", 2000, "sheetLabel")]
    public void Match_FindsViewsAndSheets(string reference, long id, string matchedBy)
    {
        var match = ViewTargetMatcher.Match(Views, reference);
        Assert.Equal(ViewTargetOutcome.Resolved, match.Outcome);
        Assert.Equal(id, match.Target!.Id);
        Assert.Equal(matchedBy, match.MatchedBy);
        Assert.Null(match.Status);
    }

    [Fact]
    public void Match_SameNameTwice_IsAmbiguousWithBothCandidates()
    {
        var match = ViewTargetMatcher.Match(Views, "sama nimi");
        Assert.Equal(ViewTargetOutcome.Ambiguous, match.Outcome);
        Assert.Equal("view_ambiguous", match.Status);
        Assert.Equal(new long[] { 1005, 1006 }, match.Candidates.Select(c => c.Id).OrderBy(i => i));
        Assert.Null(match.Target);
    }

    [Fact]
    public void Match_Unknown_ListsClosestNames_AndNeverPicksOne()
    {
        var match = ViewTargetMatcher.Match(Views, "korrus");
        Assert.Equal(ViewTargetOutcome.NotFound, match.Outcome);
        Assert.Equal("view_not_found", match.Status);
        Assert.Null(match.Target);
        Assert.Contains(match.Candidates, c => c.Id == 1000);
        Assert.Contains(match.Candidates, c => c.Id == 1001);
    }

    [Fact]
    public void Match_Unknown_NeverOffersATemplate()
    {
        var match = ViewTargetMatcher.Match(Views, "Mall");
        Assert.Equal(ViewTargetOutcome.NotFound, match.Outcome);
        Assert.DoesNotContain(match.Candidates, c => c.IsTemplate);
    }

    [Fact]
    public void Match_NothingClose_HasNoCandidates()
    {
        var match = ViewTargetMatcher.Match(Views, "xyzzy qqqqqqqq");
        Assert.Equal(ViewTargetOutcome.NotFound, match.Outcome);
        Assert.Empty(match.Candidates);
    }

    [Fact]
    public void Match_Blank_IsNotFound() =>
        Assert.Equal(ViewTargetOutcome.NotFound, ViewTargetMatcher.Match(Views, "   ").Outcome);

    [Theory]
    [InlineData("1003")]
    [InlineData("Mall plaan")]
    public void Match_Template_HasItsOwnStatus(string reference)
    {
        var match = ViewTargetMatcher.Match(Views, reference);
        Assert.Equal(ViewTargetOutcome.Template, match.Outcome);
        Assert.Equal("view_is_template", match.Status);
    }

    [Fact]
    public void Match_ATemplateWithTheSameNameAsAView_PicksTheView()
    {
        var views = new[] { View(1, "Plaan", template: true), View(2, "Plaan") };
        var match = ViewTargetMatcher.Match(views, "Plaan");
        Assert.Equal(ViewTargetOutcome.Resolved, match.Outcome);
        Assert.Equal(2, match.Target!.Id);
    }

    [Theory]
    [InlineData("Uksed")]
    [InlineData("1004")]
    [InlineData("EN-999")]
    public void Match_ScheduleOrPlaceholderSheet_IsNotCollectable(string reference)
    {
        var match = ViewTargetMatcher.Match(Views, reference);
        Assert.Equal(ViewTargetOutcome.NotCollectable, match.Outcome);
        Assert.Equal("view_not_collectable", match.Status);
    }

    [Fact]
    public void Match_AnIdThatIsAlsoAName_UsesTheIdAndSaysSo()
    {
        var views = new[] { View(101, "Plaan"), View(7, "101") };
        var match = ViewTargetMatcher.Match(views, "101");
        Assert.Equal(101, match.Target!.Id);
        Assert.Equal("id", match.MatchedBy);
        Assert.Contains("id was used", match.Note);
    }

    [Fact]
    public void Match_ANumericNameThatIsNoId_MatchesTheName()
    {
        var match = ViewTargetMatcher.Match(Views, "101");
        Assert.Equal(1007, match.Target!.Id);
        Assert.Equal("name", match.MatchedBy);
        Assert.Null(match.Note);
    }

    [Fact]
    public void Validate_AppliesToTheActiveViewToo()
    {
        Assert.Equal(ViewTargetOutcome.Resolved, ViewTargetMatcher.Validate(View(1, "P"), "active").Outcome);
        Assert.Equal(ViewTargetOutcome.Resolved, ViewTargetMatcher.Validate(Sheet(2, "A", "S"), "active").Outcome);
        Assert.Equal(ViewTargetOutcome.NotCollectable, ViewTargetMatcher.Validate(View(3, "S", "Schedule"), "active").Outcome);
    }

    [Theory]
    [InlineData("FloorPlan", true)]
    [InlineData("CeilingPlan", true)]
    [InlineData("ThreeD", true)]
    [InlineData("Section", true)]
    [InlineData("Elevation", true)]
    [InlineData("DraftingView", true)]
    [InlineData("Legend", true)]
    [InlineData("DrawingSheet", true)]
    [InlineData("Schedule", false)]
    [InlineData("PanelSchedule", false)]
    [InlineData("ProjectBrowser", false)]
    [InlineData("Rendering", false)]
    [InlineData("Internal", false)]
    [InlineData("", false)]
    public void CollectableViewTypes(string viewType, bool collectable) =>
        Assert.Equal(collectable, ViewElementRules.IsCollectableViewType(viewType));

    // ── scope ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, ViewElementScope.All)]
    [InlineData("", ViewElementScope.All)]
    [InlineData("all", ViewElementScope.All)]
    [InlineData("Model", ViewElementScope.Model)]
    [InlineData(" ANNOTATION ", ViewElementScope.Annotation)]
    public void Scope_Parses(string? text, ViewElementScope expected)
    {
        Assert.True(ViewElementRules.TryParseScope(text, out var scope));
        Assert.Equal(expected, scope);
    }

    [Fact]
    public void Scope_Unknown_IsRefused() =>
        Assert.False(ViewElementRules.TryParseScope("details", out _));

    [Theory]
    // A wall or a device.
    [InlineData(false, "Model", ViewElementScope.Model, true)]
    [InlineData(false, "Model", ViewElementScope.Annotation, false)]
    // Tags, text: annotation category and view-specific.
    [InlineData(true, "Annotation", ViewElementScope.Annotation, true)]
    [InlineData(true, "Annotation", ViewElementScope.Model, false)]
    // Detail lines, filled regions, detail components: a model category, drawn in one view.
    [InlineData(true, "Model", ViewElementScope.Annotation, true)]
    [InlineData(true, "Model", ViewElementScope.Model, false)]
    // Levels and grids: an annotation category, seen in many views.
    [InlineData(false, "Annotation", ViewElementScope.Annotation, true)]
    // Analytical elements are model.
    [InlineData(false, "AnalyticalModel", ViewElementScope.Model, true)]
    [InlineData(true, "Annotation", ViewElementScope.All, true)]
    [InlineData(false, "Model", ViewElementScope.All, true)]
    public void Scope_ClassifiesElements(bool viewSpecific, string categoryType, ViewElementScope scope, bool inScope) =>
        Assert.Equal(inScope, ViewElementRules.InScope(scope, viewSpecific, categoryType));

    // ── which elements count ──────────────────────────────────────────────────

    private static ViewElementFacts Element(string category = "OST_Walls", string type = "Model", string className = "Wall", string name = "W") =>
        new() { HasCategory = true, BuiltInCategory = category, CategoryType = type, ClassName = className, Name = name };

    [Theory]
    [InlineData("OST_Walls", "Model")]
    [InlineData("OST_FireAlarmDevices", "Model")]
    [InlineData("OST_TextNotes", "Annotation")]
    [InlineData("OST_Lines", "Model")]
    [InlineData("OST_DetailComponents", "Model")]
    [InlineData("OST_Grids", "Annotation")]
    [InlineData("OST_IOSModelGroups", "Model")]
    [InlineData("OST_IOSDetailGroups", "Model")]
    [InlineData("", "Model")]
    public void RealElements_AreKept(string category, string type) =>
        Assert.Null(ViewElementRules.ExclusionReason(Element(category, type), "Plaan"));

    [Theory]
    [InlineData("OST_SketchLines")]
    [InlineData("OST_StairsSketchRunLines")]
    [InlineData("OST_CLines")]
    [InlineData("OST_WeakDims")]
    [InlineData("OST_SectionBox")]
    [InlineData("OST_CropBoundary")]
    [InlineData("OST_IOSSketchGrid")]
    [InlineData("OST_IOSDatumPlane")]
    [InlineData("OST_Cameras")]
    public void HelperCategories_AreLeftOut(string category) =>
        Assert.Equal(ViewElementRules.ReasonHelper, ViewElementRules.ExclusionReason(Element(category), "Plaan"));

    [Theory]
    [InlineData("Sketch")]
    [InlineData("SketchPlane")]
    public void SketchClasses_AreLeftOut_EvenWithoutACategory(string className)
    {
        var facts = new ViewElementFacts { ClassName = className };
        Assert.Equal(ViewElementRules.ReasonHelper, ViewElementRules.ExclusionReason(facts, "Plaan"));
    }

    [Fact]
    public void AnElementWithNoCategory_IsLeftOut() =>
        Assert.Equal(ViewElementRules.ReasonNoCategory,
            ViewElementRules.ExclusionReason(new ViewElementFacts { ClassName = "Element" }, "Plaan"));

    [Fact]
    public void AnInternalCategory_IsLeftOut() =>
        Assert.Equal(ViewElementRules.ReasonInternal,
            ViewElementRules.ExclusionReason(Element("OST_Something", "Internal"), "Plaan"));

    [Fact]
    public void TheViewItself_IsLeftOut()
    {
        var facts = Element("OST_Views", "Annotation", "ViewPlan");
        facts.IsView = true;
        Assert.Equal(ViewElementRules.ReasonViewElement, ViewElementRules.ExclusionReason(facts, "Plaan"));
    }

    [Fact]
    public void TheViewsOwnCropViewer_IsLeftOut_ButOtherViewMarkersStay()
    {
        var own = Element("OST_Viewers", "Annotation", "Element", "Plaan");
        var marker = Element("OST_Viewers", "Annotation", "Element", "Lõige A");
        Assert.Equal(ViewElementRules.ReasonViewElement, ViewElementRules.ExclusionReason(own, "Plaan"));
        Assert.Null(ViewElementRules.ExclusionReason(marker, "Plaan"));
    }

    [Fact]
    public void TheLinesOfASketch_AreLeftOut_ThoughTheyLookLikeDetailLines()
    {
        var line = Element("OST_Lines", "Model", "DetailLine");
        line.IsSketchMember = true;
        Assert.Equal(ViewElementRules.ReasonSketchMember, ViewElementRules.ExclusionReason(line, "Plaan"));
    }

    // ── large views ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(199, false)]
    [InlineData(200, true)]
    [InlineData(5000, true)]
    public void Guard_StartsAt200(int total, bool applies) =>
        Assert.Equal(applies, ViewLargeReadGuard.Applies(new ViewReadShape { Total = total }));

    [Fact]
    public void Guard_AnythingThatNarrowsOrPages_GoesThrough()
    {
        Assert.False(ViewLargeReadGuard.Applies(new ViewReadShape { Total = 900, HasCategory = true }));
        Assert.False(ViewLargeReadGuard.Applies(new ViewReadShape { Total = 900, HasFilters = true }));
        Assert.False(ViewLargeReadGuard.Applies(new ViewReadShape { Total = 900, HasElementIds = true }));
        Assert.False(ViewLargeReadGuard.Applies(new ViewReadShape { Total = 900, UseSelection = true }));
        Assert.False(ViewLargeReadGuard.Applies(new ViewReadShape { Total = 900, SummaryOnly = true }));
        Assert.False(ViewLargeReadGuard.Applies(new ViewReadShape { Total = 900, RequestedPageSize = 50 }));
        Assert.False(ViewLargeReadGuard.Applies(new ViewReadShape { Total = 900, RequestedPage = 1 }));
    }

    [Fact]
    public void Guard_Next_NamesTheLargestCategoriesAndThePagedRead()
    {
        var counts = Enumerable.Range(1, 8).Select(i => new KeyValuePair<string, int>("Cat" + i, i * 10));
        var next = ViewLargeReadGuard.Next("revit_get_elements_info", 42, null, "all", counts);

        Assert.Equal(ViewLargeReadGuard.MaxCategoryCalls + 1, next.Count);
        Assert.Equal("Cat8", next[0].Args["category"]);
        Assert.Equal("42", next[0].Args["view"]);
        Assert.All(next, call => Assert.Equal("revit_get_elements_info", call.Tool));
        Assert.All(next, call => Assert.False(call.Args.ContainsKey("scope")));
        Assert.All(next, call => Assert.False(call.Args.ContainsKey("document")));

        var last = next[^1];
        Assert.Equal(ViewLargeReadGuard.PagedReadSize, last.Args["pageSize"]);
        Assert.Equal(0, last.Args["page"]);
    }

    [Fact]
    public void Guard_Next_CarriesScopeAndDocument()
    {
        var next = ViewLargeReadGuard.Next("revit_find_elements_by_parameter", 42, "Mudel.rvt", "annotation",
            new[] { new KeyValuePair<string, int>("Text Notes", 300) });
        Assert.All(next, call =>
        {
            Assert.Equal("annotation", call.Args["scope"]);
            Assert.Equal("Mudel.rvt", call.Args["document"]);
        });
    }

    // ── paging across a sheet's viewports ─────────────────────────────────────

    [Fact]
    public void Paging_WalksThroughTheSourcesAsOneList()
    {
        var counts = new[] { 3, 5, 0, 4 };
        Assert.Equal(
            new[] { (0, 0, 3), (1, 0, 2) },
            ViewResultPaging.Slice(counts, 0, 5).Select(s => (s.Source, s.Skip, s.Take)));
        Assert.Equal(
            new[] { (1, 2, 3), (3, 0, 2) },
            ViewResultPaging.Slice(counts, 1, 5).Select(s => (s.Source, s.Skip, s.Take)));
        Assert.Equal(
            new[] { (3, 2, 2) },
            ViewResultPaging.Slice(counts, 2, 5).Select(s => (s.Source, s.Skip, s.Take)));
        Assert.Empty(ViewResultPaging.Slice(counts, 3, 5));
    }

    [Fact]
    public void Paging_EveryElementIsOnExactlyOnePage()
    {
        var counts = new[] { 7, 0, 13, 1, 22 };
        var seen = new List<(int Source, int Index)>();
        for (var page = 0; page < 20; page++)
            foreach (var slice in ViewResultPaging.Slice(counts, page, 6))
                for (var i = 0; i < slice.Take; i++)
                    seen.Add((slice.Source, slice.Skip + i));

        Assert.Equal(counts.Sum(), seen.Count);
        Assert.Equal(counts.Sum(), seen.Distinct().Count());
    }

    [Theory]
    [InlineData(10, 0, 5, true)]
    [InlineData(10, 1, 5, false)]
    [InlineData(11, 1, 5, true)]
    [InlineData(0, 0, 5, false)]
    public void Paging_HasMore(int total, int page, int pageSize, bool hasMore) =>
        Assert.Equal(hasMore, ViewResultPaging.HasMore(total, page, pageSize));

    [Fact]
    public void Paging_NoPageSize_GivesNothing() =>
        Assert.Empty(ViewResultPaging.Slice(new[] { 5 }, 0, 0));

    // ── crop ──────────────────────────────────────────────────────────────────

    private static readonly (double X, double Y)[] Square = { (0, 0), (10, 0), (10, 10), (0, 10) };
    private static readonly (double X, double Y)[] LShape = { (0, 0), (10, 0), (10, 5), (5, 5), (5, 10), (0, 10) };

    [Theory]
    [InlineData(2, 2, 3, 3, true)]      // inside
    [InlineData(-5, -5, 50, 50, true)]  // around the whole crop
    [InlineData(-1, 4, 1, 6, true)]     // across one edge
    [InlineData(4, -5, 6, 50, true)]    // through it, no corner of either inside the other
    [InlineData(10, 10, 12, 12, true)]  // touching a corner stays
    [InlineData(11, 11, 12, 12, false)] // outside
    [InlineData(-3, 2, -1, 3, false)]   // outside on one side
    public void Crop_Rectangle(double minX, double minY, double maxX, double maxY, bool overlaps) =>
        Assert.Equal(overlaps, ViewCropMath.BoxOverlapsPolygon(minX, minY, maxX, maxY, Square));

    [Theory]
    [InlineData(1, 1, 2, 2, true)]
    [InlineData(7, 7, 9, 9, false)]     // in the notch of the L, inside its bounding rectangle
    [InlineData(4, 4, 9, 9, true)]
    public void Crop_NonRectangularShape(double minX, double minY, double maxX, double maxY, bool overlaps) =>
        Assert.Equal(overlaps, ViewCropMath.BoxOverlapsPolygon(minX, minY, maxX, maxY, LShape));

    [Fact]
    public void Crop_SplitRegion_AnyLoopWillDo()
    {
        var loops = new IReadOnlyList<(double X, double Y)>[]
        {
            Square,
            new (double X, double Y)[] { (100, 0), (110, 0), (110, 10), (100, 10) }
        };
        Assert.True(ViewCropMath.BoxOverlapsCrop(101, 1, 102, 2, loops));
        Assert.False(ViewCropMath.BoxOverlapsCrop(50, 1, 52, 2, loops));
    }

    // ── payloads ──────────────────────────────────────────────────────────────

    private static ElementInfoDto Dto() => new()
    {
        ElementId = 123,
        UniqueId = "u-1",
        Category = "Lines",
        Type = "",
        Name = "Detail Lines",
        LineStyle = "<Thin Lines>",
        Parameters = new Dictionary<string, ParameterValueDto>()
    };

    private static void AddViewFields(ElementInfoDto dto)
    {
        dto.Location = new ViewElementLocationDto
        {
            Kind = "curve",
            Start = PointMmDto.FromMm(0, 0, 0),
            End = PointMmDto.FromMm(1000, 0, 0),
            LengthMm = 1000,
            CurveType = "Line"
        };
        dto.BoundingBox = BoxMmDto.FromMm(new Vec3(0, 0, 0), new Vec3(1000, 1, 1));
        dto.OwnerView = new ViewElementRefDto { Id = 9, Name = "7-01" };
        dto.PartOfGroup = false;
    }

    private static readonly string[] ViewFieldNames =
    {
        "Location", "BoundingBox", "Host", "TaggedElementIds", "OwnerView", "PartOfGroup", "GroupId", "Room"
    };

    [Fact]
    public void WithoutView_TheFullPayloadIsByteForByteTheOldOne()
    {
        const string before =
            "{\"ElementId\":123,\"UniqueId\":\"u-1\",\"Category\":\"Lines\",\"Family\":\"\",\"Type\":\"\",\"TypeElementId\":null," +
            "\"Name\":\"Detail Lines\",\"Level\":\"\",\"LineStyle\":\"<Thin Lines>\",\"Parameters\":{}}";
        Assert.Equal(before, JsonConvert.SerializeObject(Dto()));
    }

    [Fact]
    public void WithoutView_TheCompactPayloadIsByteForByteTheOldOne()
    {
        const string before = "[{\"ElementId\":123,\"Category\":\"Lines\",\"Name\":\"Detail Lines\"}]";
        Assert.Equal(before, JsonConvert.SerializeObject(ElementQueryResponseFormatter.FormatElements(new[] { Dto() }, compact: true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithView_TheFieldsAppearInBothShapes(bool compact)
    {
        var dto = Dto();
        AddViewFields(dto);
        var json = JsonConvert.SerializeObject(ElementQueryResponseFormatter.FormatElements(new[] { dto }, compact));

        Assert.Contains("\"Location\":{\"Kind\":\"curve\",\"Start\":{\"x\":0.0,\"y\":0.0,\"z\":0.0},\"End\":{\"x\":1000.0,\"y\":0.0,\"z\":0.0},\"LengthMm\":1000.0,\"CurveType\":\"Line\"}", json);
        Assert.Contains("\"BoundingBox\":{\"min\":", json);
        Assert.Contains("\"OwnerView\":{\"Id\":9,\"Name\":\"7-01\"}", json);
        Assert.Contains("\"PartOfGroup\":false", json);
        Assert.Contains("\"LineStyle\":\"<Thin Lines>\"", json);
        // Not known or not asked for: left out, not null.
        foreach (var missing in new[] { "Host", "TaggedElementIds", "GroupId", "Room" })
            Assert.DoesNotContain($"\"{missing}\"", json);
    }

    [Fact]
    public void WithoutView_NoViewFieldIsSerialized()
    {
        var full = JsonConvert.SerializeObject(Dto());
        var compact = JsonConvert.SerializeObject(ElementQueryResponseFormatter.FormatElements(new[] { Dto() }, compact: true));
        foreach (var name in ViewFieldNames)
        {
            Assert.DoesNotContain($"\"{name}\"", full);
            Assert.DoesNotContain($"\"{name}\"", compact);
        }
    }

    // ── graph-first gate ──────────────────────────────────────────────────────

    [Fact]
    public void Gate_NeverAnswersACallWithView()
    {
        var read = new GraphRouting.BroadRead { Tool = "revit_get_elements_info", Category = "Walls" };
        Assert.True(GraphRouting.IsGateCandidate(read));
        read.HasView = true;
        Assert.False(GraphRouting.IsGateCandidate(read));
    }

    [Fact]
    public void GateAndHint_LookAtTheViewArgument()
    {
        var source = File.ReadAllText(Path.Combine(ToolSourceScan.Root(), "RevitMCP.Addin", "Tools", "Graph", "GraphRoutingSupport.cs"));
        Assert.Contains("HasView = !string.IsNullOrWhiteSpace(ToolArguments.GetString(args, \"view\"))", source);
        Assert.Contains("ToolArguments.GetString(request.Arguments, \"view\"))) return;", source);
    }

    [Theory]
    [InlineData("CountElementsTool.cs", "public Task<McpToolResult> ExecuteAsync", "var collector = new FilteredElementCollector(doc)")]
    [InlineData("GetElementsInfoTool.cs", "public Task<McpToolResult> ExecuteAsync", "Provide useSelection=true")]
    [InlineData("FindElementsByParameterTool.cs", "public Task<McpToolResult> ExecuteAsync", "BuildNoNarrowingGuidance(target.Document)")]
    public void Tools_HandAViewCallOverBeforeTheirOldPathAndTheirNarrowingGuard(string file, string start, string oldPath)
    {
        var source = File.ReadAllText(Path.Combine(ToolSourceScan.Root(), "RevitMCP.Addin", "Tools", file));
        var handOver = source.IndexOf("ViewElementReads.HasView(request)", StringComparison.Ordinal);
        Assert.True(handOver > source.IndexOf(start, StringComparison.Ordinal));
        Assert.True(handOver < source.IndexOf(oldPath, StringComparison.Ordinal));
    }

    [Fact]
    public void Bridge_SendsTheViewArgumentsOnlyWithView()
    {
        var source = File.ReadAllText(Path.Combine(ToolSourceScan.Root(), "RevitMCP.Bridge", "RevitMcpTools.ViewElements.cs"));
        Assert.Matches(new Regex(@"if \(string\.IsNullOrWhiteSpace\(view\)\) return;\s+args\[""view""\]"), source);
    }
}
