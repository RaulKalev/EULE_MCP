using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.ViewFilters;
using RevitMCP.Core.Models;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#134: the Revit-free side of the view filter tools: rule trees, operator and value checks, requests, matching, routing, order.</summary>
public class ViewFilterTests
{
    private static ViewFilterNode? Parse(string json, out List<ViewFilterIssue> issues)
    {
        issues = new List<ViewFilterIssue>();
        return ViewFilterRuleParser.Parse(JToken.Parse(json), issues);
    }

    private static ViewFilterRuleNode Rule(string json)
    {
        var node = Parse(json, out var issues);
        Assert.Empty(issues);
        return Assert.IsType<ViewFilterRuleNode>(node);
    }

    private static Dictionary<string, object?> Args(string json) =>
        JObject.Parse(json).Properties().ToDictionary(p => p.Name, p => (object?)p.Value);

    // ── Rule trees ───────────────────────────────────────────────────────────

    [Fact]
    public void NestedAndOrTree_KeepsItsStructure()
    {
        var node = Parse("""
            { "and": [
                { "parameter": "Comments", "operator": "contains", "value": "EN" },
                { "or": [
                    { "parameter": "Mark", "operator": "beginsWith", "value": "A" },
                    { "and": [
                        { "parameter": "Level", "operator": "equals", "value": "1. korrus" },
                        { "parameter": "Height", "operator": ">=", "value": 2400 }
                    ] }
                ] }
            ] }
            """, out var issues);

        Assert.Empty(issues);
        var and = Assert.IsType<ViewFilterGroupNode>(node);
        Assert.False(and.IsOr);
        Assert.Equal(2, and.Children.Count);
        var or = Assert.IsType<ViewFilterGroupNode>(and.Children[1]);
        Assert.True(or.IsOr);
        var inner = Assert.IsType<ViewFilterGroupNode>(or.Children[1]);
        Assert.False(inner.IsOr);
        Assert.Equal(ViewFilterOperators.GreaterOrEqual, ((ViewFilterRuleNode)inner.Children[1]).Operator);
        Assert.Equal(4, ViewFilterRuleParser.CountRules(node));
        Assert.Equal("rules.and[1].or[1].and[0]", inner.Children[0].Path);
    }

    [Fact]
    public void SingleChildGroups_Collapse_SoRevitNeverGetsAOneChildLogicalFilter()
    {
        var node = Parse("""{ "and": [ { "or": [ { "parameter": "Mark", "operator": "hasValue" } ] } ] }""", out var issues);
        Assert.Empty(issues);
        Assert.IsType<ViewFilterRuleNode>(node);
    }

    [Fact]
    public void EmptyGroups_MeanNoRules()
    {
        Assert.Null(Parse("""{ "and": [ { "or": [] } ] }""", out var issues));
        Assert.Empty(issues);
        Assert.Null(Parse("[]", out issues));
        Assert.Empty(issues);
    }

    [Fact]
    public void BareArray_IsAnAndGroup()
    {
        var group = Assert.IsType<ViewFilterGroupNode>(Parse("""
            [ { "parameter": "A", "operator": "hasValue" }, { "parameter": "B", "operator": "hasValue" } ]
            """, out _));
        Assert.False(group.IsOr);
    }

    [Theory]
    [InlineData("""{ "and": [], "or": [] }""")]
    [InlineData("""{ "and": [], "parameter": "x" }""")]
    [InlineData("""{ "operator": "equals" }""")]
    [InlineData("""{ "and": "x" }""")]
    [InlineData("""{ "parameter": { "a": 1 }, "operator": "equals" }""")]
    [InlineData("12")]
    public void MalformedNodes_AreIssues_NotExceptions(string json)
    {
        Parse(json, out var issues);
        Assert.Contains(issues, i => i.Code == ViewFilterCodes.InvalidSpec);
    }

    [Fact]
    public void UnknownOperator_IsReportedWithTheOperators()
    {
        Parse("""{ "parameter": "Mark", "operator": "resembles", "value": "x" }""", out var issues);
        var issue = Assert.Single(issues);
        Assert.Equal(ViewFilterCodes.OperatorUnknown, issue.Code);
        Assert.Contains("beginsWith", issue.Reason);
    }

    [Fact]
    public void TooDeepNesting_IsAnIssue()
    {
        // Built as a token: Newtonsoft's reader stops at 64 levels, which the pipe's JSON meets first.
        JToken token = JObject.Parse("""{ "parameter": "A", "operator": "hasValue" }""");
        for (var i = 0; i < ViewFilterRuleParser.MaxDepth + 2; i++) token = new JObject { ["and"] = new JArray(token) };

        var issues = new List<ViewFilterIssue>();
        ViewFilterRuleParser.Parse(token, issues);

        Assert.Contains(issues, i => i.Code == ViewFilterCodes.InvalidSpec && i.Reason.Contains("nested"));
    }

    [Fact]
    public void Tree_RoundTripsThroughJson()
    {
        var original = Parse("""
            { "or": [
                { "parameter": "Comments", "operator": "notContains", "value": "x" },
                { "parameter": "Length", "operator": "less", "value": 12.5, "units": "internal", "tolerance": 0.01, "onlyCategory": "OST_Walls" }
            ] }
            """, out _);

        var again = Parse(ViewFilterRuleParser.ToJson(original)!.ToString(), out var issues);

        Assert.Empty(issues);
        Assert.Equal(ViewFilterRuleParser.Describe(original), ViewFilterRuleParser.Describe(again));
        var rule = (ViewFilterRuleNode)((ViewFilterGroupNode)again!).Children[1];
        Assert.Equal(ViewFilterUnits.Internal, rule.Units);
        Assert.Equal(0.01, rule.Tolerance);
        Assert.Equal("OST_Walls", rule.OnlyCategory);
    }

    [Fact]
    public void Describe_ShowsNesting()
    {
        var node = Parse("""
            { "and": [ { "parameter": "A", "operator": "hasValue" },
                       { "or": [ { "parameter": "B", "operator": "equals", "value": 1 }, { "parameter": "C", "operator": "hasNoValue" } ] } ] }
            """, out _);
        Assert.Equal("A hasValue AND (B equals 1 OR C hasNoValue)", ViewFilterRuleParser.Describe(node));
        Assert.Equal("no rules", ViewFilterRuleParser.Describe(null));
    }

    [Fact]
    public void BadUnitsAndTolerance_AreIssues()
    {
        Parse("""{ "parameter": "A", "operator": "equals", "value": 1, "units": "furlongs", "tolerance": -1 }""", out var issues);
        Assert.Equal(2, issues.Count);
    }

    // ── Operators and storage types ──────────────────────────────────────────

    [Theory]
    [InlineData("=", "equals")]
    [InlineData("!=", "notEquals")]
    [InlineData(">=", "greaterOrEqual")]
    [InlineData("does not contain", "notContains")]
    [InlineData("Begins With", "beginsWith")]
    [InlineData("is greater than or equal to", "greaterOrEqual")]
    [InlineData("HAS_VALUE", "hasValue")]
    public void Operator_Spellings(string spelling, string canonical) =>
        Assert.Equal(canonical, ViewFilterOperators.Normalize(spelling));

    [Theory]
    [InlineData("contains", FilterValueKind.Text, true)]
    [InlineData("greater", FilterValueKind.Text, true)]
    [InlineData("contains", FilterValueKind.Number, false)]
    [InlineData("contains", FilterValueKind.Integer, false)]
    [InlineData("beginsWith", FilterValueKind.ElementId, false)]
    [InlineData("equals", FilterValueKind.ElementId, true)]
    [InlineData("greater", FilterValueKind.YesNo, false)]
    [InlineData("equals", FilterValueKind.YesNo, true)]
    [InlineData("hasValue", FilterValueKind.Number, true)]
    [InlineData("associatedWithGlobalParameter", FilterValueKind.Number, true)]
    [InlineData("associatedWithGlobalParameter", FilterValueKind.Text, false)]
    [InlineData("equals", FilterValueKind.Unknown, false)]
    public void Operators_MatchTheParameterKind(string op, FilterValueKind kind, bool allowed) =>
        Assert.Equal(allowed, ViewFilterOperators.IsAllowed(op, kind));

    [Theory]
    [InlineData("equals", "notEquals")]
    [InlineData("contains", "notContains")]
    [InlineData("beginsWith", "notBeginsWith")]
    [InlineData("endsWith", "notEndsWith")]
    [InlineData("greater", "lessOrEqual")]
    [InlineData("hasValue", "hasNoValue")]
    public void Inverse_IsItsOwnInverse(string op, string inverse)
    {
        Assert.Equal(inverse, ViewFilterOperators.Inverse(op));
        Assert.Equal(op, ViewFilterOperators.Inverse(inverse));
    }

    [Fact]
    public void Validator_ConvertsValuesPerKind()
    {
        Assert.Null(ViewFilterRuleValidator.Check(Rule("""{ "parameter": "A", "operator": "equals", "value": 12 }"""), FilterValueKind.Text, out var text));
        Assert.Equal("12", text.Text);

        Assert.Null(ViewFilterRuleValidator.Check(Rule("""{ "parameter": "A", "operator": ">", "value": "2,5" }"""), FilterValueKind.Number, out var number));
        Assert.Equal(2.5, number.Number);

        Assert.Null(ViewFilterRuleValidator.Check(Rule("""{ "parameter": "A", "operator": "=", "value": 7 }"""), FilterValueKind.Integer, out var integer));
        Assert.Equal(7, integer.Integer);

        Assert.Null(ViewFilterRuleValidator.Check(Rule("""{ "parameter": "A", "operator": "=", "value": "yes" }"""), FilterValueKind.YesNo, out var yes));
        Assert.Equal(1, yes.Integer);
        Assert.Null(ViewFilterRuleValidator.Check(Rule("""{ "parameter": "A", "operator": "=", "value": false }"""), FilterValueKind.YesNo, out var no));
        Assert.Equal(0, no.Integer);

        Assert.Null(ViewFilterRuleValidator.Check(Rule("""{ "parameter": "A", "operator": "=", "value": 311 }"""), FilterValueKind.ElementId, out var id));
        Assert.Equal(311, id.Id);
        Assert.Null(ViewFilterRuleValidator.Check(Rule("""{ "parameter": "A", "operator": "=", "value": "1. korrus" }"""), FilterValueKind.ElementId, out var name));
        Assert.Equal("1. korrus", name.Name);
    }

    [Theory]
    [InlineData("""{ "parameter": "A", "operator": "=", "value": "abc" }""", FilterValueKind.Number, ViewFilterCodes.RuleValueInvalid)]
    [InlineData("""{ "parameter": "A", "operator": "=", "value": 1.5 }""", FilterValueKind.Integer, ViewFilterCodes.RuleValueInvalid)]
    [InlineData("""{ "parameter": "A", "operator": "=", "value": 99999999999 }""", FilterValueKind.Integer, ViewFilterCodes.RuleValueInvalid)]
    [InlineData("""{ "parameter": "A", "operator": "=", "value": 2 }""", FilterValueKind.YesNo, ViewFilterCodes.RuleValueInvalid)]
    [InlineData("""{ "parameter": "A", "operator": "=", "value": true }""", FilterValueKind.Text, ViewFilterCodes.RuleValueInvalid)]
    [InlineData("""{ "parameter": "A", "operator": "=", "value": true }""", FilterValueKind.ElementId, ViewFilterCodes.RuleValueInvalid)]
    [InlineData("""{ "parameter": "A", "operator": "=" }""", FilterValueKind.Text, ViewFilterCodes.RuleValueMissing)]
    [InlineData("""{ "parameter": "A", "operator": "contains", "value": 1 }""", FilterValueKind.Number, ViewFilterCodes.OperatorNotValid)]
    [InlineData("""{ "parameter": "A", "operator": "=", "value": 1 }""", FilterValueKind.Unknown, ViewFilterCodes.ParameterTypeUnknown)]
    public void Validator_RefusesWhatRevitWouldNotTake(string json, FilterValueKind kind, string code)
    {
        var issue = ViewFilterRuleValidator.Check(Rule(json), kind, out _);
        Assert.Equal(code, issue?.Code);
    }

    [Fact]
    public void Validator_NoValueOperatorsNeedNoValue()
    {
        Assert.Null(ViewFilterRuleValidator.Check(Rule("""{ "parameter": "A", "operator": "hasNoValue" }"""), FilterValueKind.Number, out _));
        Assert.Null(ViewFilterRuleValidator.Check(Rule("""{ "parameter": "A", "operator": "sharedParameterApplicable" }"""), FilterValueKind.Unknown, out _));
    }

    [Fact]
    public void Validator_OperatorMessageListsWhatIsAllowed()
    {
        var issue = ViewFilterRuleValidator.Check(Rule("""{ "parameter": "A", "operator": "contains", "value": 1 }"""), FilterValueKind.YesNo, out _);
        Assert.Contains("hasNoValue", issue!.Reason);
    }

    // ── Create requests ──────────────────────────────────────────────────────

    [Fact]
    public void Create_ParsesRuleAndSelectionFilters()
    {
        var request = ViewFilterRequestParser.ParseCreate(Args("""
            { "ifNameExists": "skip", "atomic": true, "filters": [
                { "name": "EN kaablid", "categories": ["Cable Trays", -2008130], "rules": { "parameter": "Comments", "operator": "contains", "value": "EN" } },
                { "name": "Valik", "elementIds": [5, 6, 5] },
                { "name": "Kõik seinad", "categories": ["OST_Walls"] }
            ] }
            """), out var error);

        Assert.Null(error);
        Assert.True(request!.SkipExisting);
        Assert.True(request.Atomic);
        Assert.Equal(new[] { "Cable Trays", "-2008130" }, request.Filters[0].Categories);
        Assert.IsType<ViewFilterRuleNode>(request.Filters[0].Rules);
        Assert.Equal(ViewFilterKinds.Selection, request.Filters[1].Kind);
        Assert.Equal(new long[] { 5, 6 }, request.Filters[1].ElementIds);
        Assert.Null(request.Filters[2].Rules);
        Assert.All(request.Filters, f => Assert.Empty(f.Issues));
    }

    [Theory]
    [InlineData("""{ "name": "A", "rules": [] }""", ViewFilterCodes.NoCategories)]
    [InlineData("""{ "categories": ["Walls"] }""", ViewFilterCodes.InvalidFilterName)]
    [InlineData("""{ "name": "A:B", "categories": ["Walls"] }""", ViewFilterCodes.InvalidFilterName)]
    [InlineData("""{ "name": "A", "kind": "selection", "categories": ["Walls"] }""", ViewFilterCodes.InvalidSpec)]
    [InlineData("""{ "name": "A", "kind": "rule", "categories": ["Walls"], "elementIds": [1] }""", ViewFilterCodes.InvalidSpec)]
    [InlineData("""{ "name": "A", "kind": "other" }""", ViewFilterCodes.InvalidSpec)]
    [InlineData("""{ "name": "A", "elementIds": ["x"] }""", ViewFilterCodes.InvalidSpec)]
    public void Create_SpecProblems_AreIssuesOnTheSpec(string spec, string code)
    {
        var request = ViewFilterRequestParser.ParseCreate(Args($$"""{ "filters": [ {{spec}} ] }"""), out _);
        Assert.Contains(request!.Filters[0].Issues, i => i.Code == code);
    }

    [Fact]
    public void Create_DuplicateNameInTheRequest_MarksTheSecond()
    {
        var request = ViewFilterRequestParser.ParseCreate(Args("""
            { "filters": [ { "name": "A", "categories": ["Walls"] }, { "name": "a", "categories": ["Doors"] } ] }
            """), out _);
        Assert.Empty(request!.Filters[0].Issues);
        Assert.Equal(ViewFilterCodes.DuplicateNameInRequest, request.Filters[1].Issues.Single().Code);
    }

    [Theory]
    [InlineData("{}", "'filters' is required")]
    [InlineData("""{ "filters": [] }""", "'filters' is empty")]
    [InlineData("""{ "filters": [ {} ], "ifNameExists": "rename" }""", "ifNameExists")]
    public void Create_BadRequests_GiveAnError(string json, string expected)
    {
        Assert.Null(ViewFilterRequestParser.ParseCreate(Args(json), out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Create_RefusesTooManyFilters()
    {
        var filters = new JArray(Enumerable.Range(0, ViewFilterRequestParser.MaxFilters + 1).Select(i => new JObject { ["name"] = $"F{i}", ["categories"] = new JArray("Walls") }));
        Assert.Null(ViewFilterRequestParser.ParseCreate(new Dictionary<string, object?> { ["filters"] = filters }, out var error));
        Assert.Contains("limit", error);
    }

    // ── Edit requests ────────────────────────────────────────────────────────

    [Fact]
    public void Edit_ParsesEveryField()
    {
        var request = ViewFilterRequestParser.ParseEdit(Args("""
            { "edits": [
                { "filter": "A", "name": "B", "categories": ["Walls"], "rules": { "parameter": "Mark", "operator": "hasValue" } },
                { "filter": 42, "clearRules": true },
                { "filter": "S", "addElementIds": [1], "removeElementIds": [2] }
            ] }
            """), out var error);

        Assert.Null(error);
        Assert.All(request!.Edits, e => Assert.Empty(e.Issues));
        Assert.Equal("B", request.Edits[0].NewName);
        Assert.True(request.Edits[0].HasRules);
        Assert.Equal("42", request.Edits[1].Filter);
        Assert.True(request.Edits[1].ClearRules);
        Assert.True(request.Edits[2].TouchesMembers);
    }

    [Theory]
    [InlineData("""{ "name": "B" }""", ViewFilterCodes.InvalidSpec)]
    [InlineData("""{ "filter": "A" }""", ViewFilterCodes.NothingToDo)]
    [InlineData("""{ "filter": "A", "name": "B?" }""", ViewFilterCodes.InvalidFilterName)]
    [InlineData("""{ "filter": "A", "categories": [] }""", ViewFilterCodes.NoCategories)]
    [InlineData("""{ "filter": "A", "rules": { "or": [] } }""", ViewFilterCodes.InvalidSpec)]
    [InlineData("""{ "filter": "A", "clearRules": true, "rules": { "parameter": "M", "operator": "hasValue" } }""", ViewFilterCodes.InvalidSpec)]
    [InlineData("""{ "filter": "A", "elementIds": [1], "addElementIds": [2] }""", ViewFilterCodes.InvalidSpec)]
    [InlineData("""{ "filter": "A", "elementIds": [1], "clearRules": true }""", ViewFilterCodes.InvalidSpec)]
    public void Edit_SpecProblems_AreIssues(string edit, string code)
    {
        var request = ViewFilterRequestParser.ParseEdit(Args($$"""{ "edits": [ {{edit}} ] }"""), out _);
        Assert.Contains(request!.Edits[0].Issues, i => i.Code == code);
    }

    // ── Apply requests and graphics ──────────────────────────────────────────

    [Fact]
    public void Apply_AddWithoutGraphics_HasNoSettings()
    {
        var request = ViewFilterRequestParser.ParseApply(Args("""{ "views": ["EN 1. korrus", 77], "add": ["EN kaablid", 9] }"""), out var error);

        Assert.Null(error);
        Assert.Equal(new[] { "EN 1. korrus", "77" }, request!.Views);
        Assert.All(request.Add, a =>
        {
            Assert.False(a.HasSettings);
            Assert.Null(a.Graphics);
            Assert.Empty(a.Issues);
        });
    }

    [Fact]
    public void Apply_ReadsEveryOptionalSetting()
    {
        var request = ViewFilterRequestParser.ParseApply(Args("""
            { "views": ["V"], "remove": ["R"], "order": ["A", "B"], "add": [ {
                "filter": "A", "enabled": false, "visible": true, "halftone": true, "transparency": 40,
                "projectionLines": { "color": "#FF8000", "weight": 3, "pattern": "Dash" },
                "cutLines": { "color": [1, 2, 3], "pattern": "solid" },
                "surfaceForeground": { "color": { "r": 9, "g": 8, "b": 7 }, "pattern": "Solid fill", "visible": false },
                "surfaceBackground": { "pattern": "none" },
                "cutForeground": { "visible": true },
                "cutBackground": { "color": "000000" }
            } ] }
            """), out var error);

        Assert.Null(error);
        var add = request!.Add.Single();
        Assert.Empty(add.Issues);
        Assert.False(add.Enabled);
        Assert.True(add.Visible);
        var graphics = add.Graphics!;
        Assert.True(graphics.Halftone);
        Assert.Equal(40, graphics.Transparency);
        Assert.Equal("#FF8000", graphics.ProjectionLines!.Color!.ToHex());
        Assert.Equal(3, graphics.ProjectionLines.Weight);
        Assert.Equal("Dash", graphics.ProjectionLines.Pattern);
        Assert.Equal("#010203", graphics.CutLines!.Color!.ToHex());
        Assert.Equal("#090807", graphics.SurfaceForeground!.Color!.ToHex());
        Assert.False(graphics.SurfaceForeground.Visible);
        Assert.Equal("none", graphics.SurfaceBackground!.Pattern);
        Assert.True(graphics.CutForeground!.Visible);
        Assert.Equal("#000000", graphics.CutBackground!.Color!.ToHex());
        Assert.Equal(new[] { "R" }, request.Remove);
        Assert.Equal(new[] { "A", "B" }, request.Order);
    }

    [Theory]
    [InlineData("""{ "filter": "A", "transparency": 101 }""")]
    [InlineData("""{ "filter": "A", "transparency": 1.5 }""")]
    [InlineData("""{ "filter": "A", "halftone": 1 }""")]
    [InlineData("""{ "filter": "A", "projectionLines": { "weight": 17 } }""")]
    [InlineData("""{ "filter": "A", "projectionLines": { "weight": 0 } }""")]
    [InlineData("""{ "filter": "A", "projectionLines": "red" }""")]
    [InlineData("""{ "filter": "A", "cutLines": { "color": "red" } }""")]
    [InlineData("""{ "filter": "A", "cutLines": { "color": [1, 2, 300] } }""")]
    [InlineData("""{ "filter": "A", "surfaceForeground": { "visible": "no" } }""")]
    [InlineData("""{ "filter": "A", "cutBackground": 5 }""")]
    public void Apply_BadGraphics_AreReportedAsGraphicsInvalid(string add)
    {
        var request = ViewFilterRequestParser.ParseApply(Args($$"""{ "views": ["V"], "add": [ {{add}} ] }"""), out _);
        Assert.Contains(request!.Add[0].Issues, i => i.Code == ViewFilterCodes.GraphicsInvalid);
    }

    [Theory]
    [InlineData("""{ "add": ["A"] }""", "'views' is required")]
    [InlineData("""{ "views": [], "add": ["A"] }""", "'views' is empty")]
    [InlineData("""{ "views": ["V"] }""", "Nothing to do")]
    [InlineData("""{ "views": ["V"], "add": [], "remove": [], "order": [] }""", "Nothing to do")]
    [InlineData("""{ "views": ["V"], "add": "A" }""", "'add' must be an array")]
    public void Apply_BadRequests_GiveAnError(string json, string expected)
    {
        Assert.Null(ViewFilterRequestParser.ParseApply(Args(json), out var error));
        Assert.Contains(expected, error);
    }

    // ── Copy requests ────────────────────────────────────────────────────────

    [Fact]
    public void Copy_Defaults()
    {
        var request = ViewFilterRequestParser.ParseCopy(Args("""{ "sourceView": "V" }"""), out var error);
        Assert.Null(error);
        Assert.Equal(ViewFilterIfExists.Reuse, request!.IfExists);
        Assert.True(request.CopySettings);
        Assert.False(request.Atomic);
    }

    [Fact]
    public void Copy_NeedsASource_AndAKnownIfExists()
    {
        Assert.Null(ViewFilterRequestParser.ParseCopy(Args("""{ "targetViews": ["T"] }"""), out var error));
        Assert.Contains("sourceView", error);
        Assert.Null(ViewFilterRequestParser.ParseCopy(Args("""{ "sourceView": "V", "ifExists": "merge" }"""), out error));
        Assert.Contains("ifExists", error);
    }

    [Fact]
    public void Delete_DropsDuplicateReferences()
    {
        Assert.Equal(new[] { "A", "5" }, ViewFilterRequestParser.ParseDelete(Args("""{ "filters": ["A", "a", 5] }"""), out _));
        Assert.Null(ViewFilterRequestParser.ParseDelete(Args("""{ "filters": [{}] }"""), out var error));
        Assert.NotNull(error);
    }

    // ── Reference matching ───────────────────────────────────────────────────

    private static readonly List<NamedRef> Parameters = new()
    {
        new NamedRef(-1001, "Comments", "ALL_MODEL_INSTANCE_COMMENTS"),
        new NamedRef(-1002, "Mark", "ALL_MODEL_MARK"),
        new NamedRef(200, "EN kood", null, "6f1a7f0e-1111-2222-3333-444455556666"),
        new NamedRef(201, "Duplicate"),
        new NamedRef(202, "duplicate")
    };

    [Theory]
    [InlineData("-1001", -1001)]
    [InlineData("ALL_MODEL_MARK", -1002)]
    [InlineData("all_model_mark", -1002)]
    [InlineData("comments", -1001)]
    [InlineData("6F1A7F0E-1111-2222-3333-444455556666", 200)]
    [InlineData(" EN kood ", 200)]
    public void Match_ByIdGuidBuiltInOrName(string reference, long id)
    {
        var match = ViewFilterRefMatcher.Match(reference, Parameters);
        Assert.Equal(RefMatchKind.Found, match.Kind);
        Assert.Equal(id, match.Found!.Id);
    }

    [Fact]
    public void Match_SameNameTwice_IsAmbiguous_NotAGuess()
    {
        var match = ViewFilterRefMatcher.Match("Duplicate", Parameters);
        Assert.Equal(RefMatchKind.Ambiguous, match.Kind);
        Assert.Null(match.Found);
        Assert.Contains("id 202", ViewFilterRefMatcher.List(match.Candidates));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Nope")]
    [InlineData("9999")]
    public void Match_NotFound(string reference) =>
        Assert.Equal(RefMatchKind.NotFound, ViewFilterRefMatcher.Match(reference, Parameters).Kind);

    [Fact]
    public void Match_NumericName_FallsBackToTheName()
    {
        var items = new List<NamedRef> { new(5, "101") };
        Assert.Equal(5, ViewFilterRefMatcher.Match("101", items).Found!.Id);
    }

    // ── Copy matching ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("A", "reuse", "create")]
    [InlineData("Existing", "reuse", "reuse")]
    [InlineData("existing", "overwrite", "overwrite")]
    [InlineData("Existing", "skip", "skip")]
    [InlineData("Existing", "fail", "fail")]
    [InlineData("A", "fail", "create")]
    public void Copy_MatchesByName(string source, string ifExists, string action) =>
        Assert.Equal(action, ViewFilterCopyMatcher.Decide(source, new[] { "Existing" }, ifExists, sameDocument: false));

    [Fact]
    public void Copy_InTheSameDocument_AlwaysReuses() =>
        Assert.Equal(ViewFilterCopyActions.Reuse, ViewFilterCopyMatcher.Decide("A", new[] { "A" }, ViewFilterIfExists.Fail, sameDocument: true));

    // ── Routing ──────────────────────────────────────────────────────────────

    [Fact]
    public void Route_ViewWithoutTemplate_GoesOnTheView()
    {
        var route = ViewFilterRouting.Route(new ViewRouteInput { ViewId = 1, ViewName = "V" });
        Assert.Equal(1, route.TargetId);
        Assert.False(route.RoutedToTemplate);
        Assert.False(route.TargetIsTemplate);
    }

    [Fact]
    public void Route_TemplateControllingFilters_TakesTheChange()
    {
        var route = ViewFilterRouting.Route(new ViewRouteInput { ViewId = 1, ViewName = "V", TemplateId = 2, TemplateName = "T", TemplateControlsFilters = true });
        Assert.Equal(2, route.TargetId);
        Assert.Equal("T", route.TargetName);
        Assert.True(route.RoutedToTemplate);
        Assert.True(route.TargetIsTemplate);
    }

    [Fact]
    public void Route_TemplateNotControllingFilters_LeavesItOnTheView()
    {
        var route = ViewFilterRouting.Route(new ViewRouteInput { ViewId = 1, ViewName = "V", TemplateId = 2, TemplateName = "T", TemplateControlsFilters = false });
        Assert.Equal(1, route.TargetId);
        Assert.False(route.RoutedToTemplate);
    }

    [Fact]
    public void Route_TemplateAsTarget_IsItsOwnTarget()
    {
        var route = ViewFilterRouting.Route(new ViewRouteInput { ViewId = 2, ViewName = "T", IsTemplate = true });
        Assert.Equal(2, route.TargetId);
        Assert.True(route.TargetIsTemplate);
        Assert.False(route.RoutedToTemplate);
    }

    [Theory]
    [InlineData(new long[] { -5, -6 }, new long[] { }, true)]
    [InlineData(new long[] { -5, -6 }, new long[] { -5 }, false)]
    [InlineData(new long[] { -6 }, new long[] { }, false)]
    public void TemplateControlsFilters_ReadsBothLists(long[] all, long[] nonControlled, bool expected) =>
        Assert.Equal(expected, ViewFilterRouting.TemplateControlsFilters(all, nonControlled, -5));

    // ── Order ────────────────────────────────────────────────────────────────

    [Fact]
    public void Order_NamedFirst_RestAsTheyWere()
    {
        var order = ViewFilterOrderPlanner.Merge(new long[] { 1, 2, 3, 4 }, new long[] { 3, 1 }, out var missing);
        Assert.Equal(new long[] { 3, 1, 2, 4 }, order);
        Assert.Empty(missing);
    }

    [Fact]
    public void Order_ReportsFiltersTheViewDoesNotHave()
    {
        var order = ViewFilterOrderPlanner.Merge(new long[] { 1, 2 }, new long[] { 9, 2, 2 }, out var missing);
        Assert.Equal(new long[] { 2, 1 }, order);
        Assert.Equal(new long[] { 9 }, missing);
    }

    [Fact]
    public void Order_AfterChanges_AppendsAndRemoves()
    {
        Assert.Equal(new long[] { 1, 3, 4 }, ViewFilterOrderPlanner.AfterChanges(new long[] { 1, 2, 3 }, new long[] { 4, 3 }, new long[] { 2 }));
    }

    // ── Approval summaries ───────────────────────────────────────────────────

    private static string? Summary(string tool, string json) =>
        ViewFilterApprovalSummaries.TryBuild(new McpToolRequest { ToolName = tool, Arguments = Args(json) });

    [Fact]
    public void Summaries_SayWhatHappens()
    {
        Assert.Contains("'EN' (Walls: 1 rule(s))", Summary("revit_create_view_filters",
            """{ "filters": [ { "name": "EN", "categories": ["Walls"], "rules": { "parameter": "M", "operator": "hasValue" } } ] }"""));
        Assert.Contains("rename to 'B'", Summary("revit_edit_view_filters", """{ "edits": [ { "filter": "A", "name": "B" } ] }"""));
        Assert.StartsWith("DESTRUCTIVE", Summary("revit_delete_view_filters", """{ "filters": ["A"] }"""));
        Assert.Contains("(no graphics)", Summary("revit_apply_view_filters", """{ "views": ["V"], "add": ["A"] }"""));
        Assert.Contains("from document 'Other'", Summary("revit_copy_view_filters", """{ "sourceView": "V", "sourceDocument": "Other" }"""));
        Assert.Null(Summary("revit_something_else", "{}"));
    }

    [Fact]
    public void Summaries_OfInvalidRequests_SaySo()
    {
        Assert.Contains("invalid", Summary("revit_create_view_filters", "{}"));
        Assert.StartsWith("DESTRUCTIVE", Summary("revit_delete_view_filters", "{}"));
    }
}
