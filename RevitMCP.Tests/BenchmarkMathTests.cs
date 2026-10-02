using System.Text.Json.Nodes;
using RevitMCP.Benchmark;
using Xunit;

namespace RevitMCP.Tests;

public class BenchmarkMathTests
{
    [Fact]
    public void EstimateTokens_IsCharactersOverFourRoundedUp()
    {
        Assert.Equal(0, BenchmarkMath.EstimateTokens(""));
        Assert.Equal(1, BenchmarkMath.EstimateTokens("abc"));
        Assert.Equal(3, BenchmarkMath.EstimateTokens("123456789"));
    }

    [Fact]
    public void CountLiveElements_CountsElementObjects_NotGraphNodes()
    {
        const string live = """{"data":{"elements":[{"ElementId":1},{"ElementId":2,"Sub":{"elementId":3}}]}}""";
        Assert.Equal(3, BenchmarkMath.CountLiveElements("revit_get_elements_info", live));
        Assert.Equal(0, BenchmarkMath.CountLiveElements("revit_graph_query", live));
        Assert.Equal(0, BenchmarkMath.CountLiveElements("revit_get_elements_info", "not json"));
    }

    [Fact]
    public void Select_ResolvesPathsCaseInsensitively()
    {
        var root = JsonNode.Parse("""{"data":{"items":[{"id":"10","extra":{"panelName":"JK1"}},{"id":"11"},{"id":"12"}]}}""");
        Assert.Equal("10", BenchmarkMath.Select(root, "$.Data.Items[0].id")!.GetValue<string>());
        Assert.Equal("JK1", BenchmarkMath.Select(root, "$.data.items[0].extra.panelName")!.GetValue<string>());
        Assert.Equal(3, ((JsonArray)BenchmarkMath.Select(root, "$.data.items[*].id")!).Count);
        Assert.Equal(2, ((JsonArray)BenchmarkMath.Select(root, "$.data.items[:2].id")!).Count);
        Assert.Null(BenchmarkMath.Select(root, "$.data.items[5].id"));
        Assert.Null(BenchmarkMath.Select(root, "$.data.missing"));
        Assert.Null(BenchmarkMath.Select(JsonNode.Parse("""{"items":[]}"""), "$.items[*].id"));
    }

    [Fact]
    public void Fill_KeepsTypesForWholeValues_AndInlinesInsideStrings()
    {
        var bindings = new Dictionary<string, JsonNode?>
        {
            ["ids"] = BenchmarkMath.AsIdArray(new JsonArray("1", "2")),
            ["level"] = JsonValue.Create("Esimene \"korrus\""),
            ["n"] = JsonValue.Create(5)
        };
        var filled = BenchmarkMath.Fill("""{"elementIds":"{{ids}}","filter":"level={{level}}","pageSize":"{{n}}"}""", bindings, out var missing);
        Assert.Null(missing);
        var obj = JsonNode.Parse(filled!)!.AsObject();
        Assert.Equal(2, obj["elementIds"]!.AsArray().Count);
        Assert.Equal(1L, obj["elementIds"]![0]!.GetValue<long>());
        Assert.Equal("level=Esimene \"korrus\"", obj["filter"]!.GetValue<string>());
        Assert.Equal(5, obj["pageSize"]!.GetValue<int>());
    }

    [Fact]
    public void Fill_ReportsMissingBindings()
    {
        Assert.Null(BenchmarkMath.Fill("""{"id":"{{roomId}}"}""", new Dictionary<string, JsonNode?>(), out var missing));
        Assert.Equal("roomId", missing);
    }

    [Fact]
    public void Report_SeparatesSchemaAndResultCost()
    {
        var run = new BenchmarkRun
        {
            Label = "t",
            Model = "m",
            Schemas = { new SchemaMeasurement { Profile = "full", ToolCount = 228, SchemaBytes = 272323, SchemaTokens = 68081 } },
            Scenarios =
            {
                new ScenarioResult
                {
                    Id = "s", Title = "S",
                    Variants =
                    {
                        new VariantResult { Name = "graph", Profile = "full", SchemaTokens = 68081,
                            Steps = { new StepMeasurement { Tool = "revit_graph_query", Success = true, ResultBytes = 400, ResultTokens = 100, ElapsedMs = 5 } } },
                        new VariantResult { Name = "x", Profile = "core", Skipped = "profile not selected" }
                    }
                },
                new ScenarioResult { Id = "p", Title = "P", Skipped = "the model has no electrical panels" }
            }
        };
        var md = BenchmarkReport.ToMarkdown(run);
        Assert.Contains("| `full` | 228 | 272,323 | 68,081 |", md);
        Assert.Contains("| graph | `full` | 1 | 400 | 100 | 68,081 | 68,181 | 0 | 5 ms | 0 |", md);
        Assert.Contains("skipped: profile not selected", md);
        Assert.Contains("Skipped: the model has no electrical panels", md);
    }
}
