using RevitMCP.Addin.Village;
using Xunit;

namespace RevitMCP.Tests;

public class VillageOptionsTests
{
    [Fact]
    public void Defaults_AreOptInLoopbackAndBounded()
    {
        var o = VillageOptions.FromConfig(null, null);
        Assert.False(o.Enabled);
        Assert.Equal("127.0.0.1", o.Host);
        Assert.Equal(VillageOptions.DefaultPort, o.Port);
        Assert.True(o.PortFallback);
        Assert.Equal(2000, o.QueueSize);
        Assert.Equal(200, o.MaxEventsPerSecond);
        Assert.Equal(1500, o.AggregationWindowMs);
        Assert.Equal(200, o.RecentActivityLimit);
        Assert.Equal(500, o.HistoryLimit);
        Assert.Equal(60, o.GraphRefreshSeconds);
        Assert.Equal(300, o.AgentIdleSeconds);
        Assert.Equal(1.0, o.AnimationSpeed);
        Assert.Equal(12, o.MaxBuildings);
        Assert.Equal(6, o.MaxEffects);
        Assert.False(o.DiagnosticLogging);
        Assert.Empty(o.ToolAreas);
        Assert.Null(o.ThemesJson);
        Assert.Equal("http://127.0.0.1:47800/", o.BaseUrl);
    }

    [Fact]
    public void FromConfig_UserWinsOverCompanyPerKey()
    {
        var company = VillageOptions.ParseConfig("{\"village\":{\"enabled\":true,\"port\":48000,\"queueSize\":500,\"toolAreas\":{\"x_tool\":\"sheets\",\"y_tool\":\"views\"},\"themes\":{\"company\":true}}}");
        var user = VillageOptions.ParseConfig("{\"village\":{\"port\":\"48123\",\"animationSpeed\":\"2.5\",\"toolAreas\":{\"x_tool\":\"lighting\"}}}");

        var o = VillageOptions.FromConfig(user, company);
        Assert.True(o.Enabled);          // from company
        Assert.Equal(48123, o.Port);     // user overrides, string parsed
        Assert.Equal(500, o.QueueSize);  // from company
        Assert.Equal(2.5, o.AnimationSpeed);
        Assert.Equal("lighting", o.ToolAreas["x_tool"]);
        Assert.Equal("views", o.ToolAreas["y_tool"]);
        Assert.Contains("company", o.ThemesJson);
    }

    [Fact]
    public void FromConfig_ClampsAndIgnoresGarbage()
    {
        var user = VillageOptions.ParseConfig("{\"village\":{\"port\":80,\"queueSize\":999999,\"maxEventsPerSecond\":-5," +
                                              "\"aggregationWindowMs\":\"soon\",\"animationSpeed\":\"NaN\",\"enabled\":\"yes please\"," +
                                              "\"host\":\"0.0.0.0\",\"agentIdleSeconds\":1,\"maxBuildings\":40,\"toolAreas\":\"not an object\"}}");

        var o = VillageOptions.FromConfig(user, null);
        Assert.Equal(1024, o.Port);
        Assert.Equal(20000, o.QueueSize);
        Assert.Equal(10, o.MaxEventsPerSecond);
        Assert.Equal(1500, o.AggregationWindowMs);
        Assert.Equal(1.0, o.AnimationSpeed);
        Assert.False(o.Enabled);
        Assert.Equal("127.0.0.1", o.Host);
        Assert.Equal(30, o.AgentIdleSeconds);
        Assert.Equal(12, o.MaxBuildings);
        Assert.Empty(o.ToolAreas);
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("localhost", "localhost")]
    [InlineData("LOCALHOST", "localhost")]
    [InlineData("::1", "::1")]
    [InlineData("0.0.0.0", "127.0.0.1")]
    [InlineData("192.168.1.10", "127.0.0.1")]
    [InlineData("*", "127.0.0.1")]
    [InlineData("+", "127.0.0.1")]
    [InlineData("", "127.0.0.1")]
    [InlineData(null, "127.0.0.1")]
    public void CoerceHost_OnlyAllowsLoopback(string? input, string expected)
    {
        Assert.Equal(expected, VillageOptions.CoerceHost(input));
    }

    [Fact]
    public void ParseConfig_ReturnsNullForNonObjects()
    {
        Assert.Null(VillageOptions.ParseConfig(null));
        Assert.Null(VillageOptions.ParseConfig(""));
        Assert.Null(VillageOptions.ParseConfig("[1,2]"));
        Assert.Null(VillageOptions.ParseConfig("{not json"));
        Assert.NotNull(VillageOptions.ParseConfig("{}"));
        Assert.False(VillageOptions.FromConfig(VillageOptions.ParseConfig("{\"village\":\"nope\"}"), null).Enabled);
    }
}
