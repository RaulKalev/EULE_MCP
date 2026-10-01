using Newtonsoft.Json;
using RevitMCP.Village;
using Xunit;

namespace RevitMCP.Tests;

public class VillageWarehouseContentsTests
{
    private static VillageContentRow Row(string type, string level, string workset, long count) =>
        new() { Type = type, Level = level, Workset = workset, Count = count };

    [Fact]
    public void Build_MergesDuplicates_AndOrdersLargestFirst()
    {
        var contents = VillageWarehouseContents.Build(new[]
        {
            Row("A", "L1", "W", 2), Row("B", "L1", "W", 5), Row(" A ", "L1", "W", 3), Row("C", "L2", "W", 0)
        });

        Assert.Equal(10, contents.Total);
        Assert.Equal(0, contents.Omitted);
        Assert.Equal(new[] { "A", "B" }, contents.Rows.Select(r => r.Type).ToArray().OrderBy(t => t).ToArray());
        Assert.Equal(5, contents.Rows[0].Count);
        Assert.Equal(5, contents.Rows[1].Count);
    }

    [Fact]
    public void Build_PastTheCap_CountsTheRestAsOmitted()
    {
        var rows = Enumerable.Range(1, 10).Select(i => Row("T" + i, "L1", "W", i)).ToList();

        var contents = VillageWarehouseContents.Build(rows, cap: 3);

        Assert.Equal(new long[] { 10, 9, 8 }, contents.Rows.Select(r => r.Count).ToArray());
        Assert.Equal(55, contents.Total);
        Assert.Equal(55 - 27, contents.Omitted);
    }

    [Fact]
    public void Build_WithNothing_IsEmptyNotNull()
    {
        var contents = VillageWarehouseContents.Build(null);
        Assert.Empty(contents.Rows);
        Assert.Equal(0, contents.Total);
    }

    [Fact]
    public void Build_TruncatesVeryLongNames()
    {
        var contents = VillageWarehouseContents.Build(new[] { Row(new string('x', 500), "", "", 1) });
        Assert.Equal(VillageWarehouseContents.MaxNameLength, contents.Rows[0].Type.Length);
    }

    [Fact]
    public void Serialized_CarriesOnlyTypeLevelWorksetAndCounts()
    {
        var json = JsonConvert.SerializeObject(VillageWarehouseContents.Build(new[] { Row("Smoke Detector: ATS-O", "Level 1", "E-Fire", 4) }));

        var rows = Newtonsoft.Json.Linq.JObject.Parse(json)["rows"]!.First!;
        Assert.Equal(new[] { "count", "level", "type", "workset" }, rows.Children<Newtonsoft.Json.Linq.JProperty>().Select(p => p.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task Server_ServesContentsAsReadOnlyJson()
    {
        var options = new VillageOptions { Port = 0, PortFallback = false };
        using var server = new VillageSseServer(options, () => "{}", () => "<p>ok</p>", contentsJsonProvider: () => "{\"wh_x\":{\"rows\":[],\"total\":0,\"omitted\":0}}");
        Assert.True(server.Start());

        using var http = new System.Net.Http.HttpClient();
        var body = await http.GetStringAsync(server.BaseUrl + "contents");
        Assert.Contains("wh_x", body);

        var post = await http.PostAsync(server.BaseUrl + "contents", new System.Net.Http.StringContent(""));
        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }
}
