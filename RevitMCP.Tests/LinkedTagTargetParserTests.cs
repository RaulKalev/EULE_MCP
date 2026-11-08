using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Tagging;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#151: the linked targets revit_place_tags accepts.</summary>
public class LinkedTagTargetParserTests
{
    private static (List<LinkedTagTargetId> Ids, List<string> Errors) Parse(string json)
    {
        var args = JObject.Parse(json).Properties().ToDictionary(p => p.Name, p => (object?)p.Value);
        var errors = new List<string>();
        return (LinkedTagTargetParser.Parse(args, errors), errors);
    }

    [Fact]
    public void LinkInstanceWithLinkedElementIds()
    {
        var (ids, errors) = Parse("{\"linkInstanceId\":900,\"linkedElementIds\":[11,12,11]}");
        Assert.Empty(errors);
        Assert.Equal(new[] { new LinkedTagTargetId(900, 11), new LinkedTagTargetId(900, 12) }, ids);
    }

    [Fact]
    public void GraphIdsAndObjectsMix()
    {
        var (ids, errors) = Parse("{\"linkedElements\":[\"link:900:11\",\"LINK:901:5\",{\"linkInstanceId\":902,\"elementId\":7}]}");
        Assert.Empty(errors);
        Assert.Equal(new[] { "link:900:11", "link:901:5", "link:902:7" }, ids.Select(i => i.Key));
    }

    [Fact]
    public void ObjectsFallBackToTheTopLevelLinkInstance()
    {
        var (ids, errors) = Parse("{\"linkInstanceId\":900,\"linkedElements\":[{\"elementId\":3}]}");
        Assert.Empty(errors);
        Assert.Equal(new LinkedTagTargetId(900, 3), Assert.Single(ids));
    }

    [Fact]
    public void NothingGivenIsNoError()
    {
        var (ids, errors) = Parse("{\"linkInstanceId\":0,\"linkedElementIds\":[],\"linkedElements\":[]}");
        Assert.Empty(ids);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("{\"linkedElementIds\":[11]}", "needs linkInstanceId")]
    [InlineData("{\"linkedElements\":[\"900:11\"]}", "is not a linked id")]
    [InlineData("{\"linkedElements\":[\"link:abc:11\"]}", "is not a linked id")]
    [InlineData("{\"linkedElements\":[{\"elementId\":3}]}", "needs linkInstanceId and elementId")]
    [InlineData("{\"linkedElements\":[5]}", "must be")]
    [InlineData("{\"linkInstanceId\":900,\"linkedElementIds\":[\"x\"]}", "is not an element id")]
    public void Problems(string json, string expected)
    {
        var (_, errors) = Parse(json);
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void KeysRoundTrip()
    {
        Assert.True(LinkedTagTargetParser.TryParseKey(" link:12345:678 ", out var id));
        Assert.Equal(12345, id.LinkInstanceId);
        Assert.Equal(678, id.ElementId);
        Assert.Equal("link:12345:678", id.Key);
        Assert.False(LinkedTagTargetParser.TryParseKey("link:0:5", out _));
        Assert.False(LinkedTagTargetParser.TryParseKey("host:5", out _));
    }

    [Fact]
    public void BridgeAdvertisesTheLinkedArguments()
    {
        foreach (var tool in new[] { "revit_place_tags", "revit_preview_place_tags" })
        {
            var source = string.Concat(Directory.GetFiles(Path.Combine(ToolSourceScan.Root(), "RevitMCP.Bridge"), "RevitMcpTools*.cs").Select(File.ReadAllText));
            Assert.Contains("args[\"linkedElements\"]", source);
            Assert.Contains("args[\"linkInstanceId\"]", source);
            Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        }
    }
}
