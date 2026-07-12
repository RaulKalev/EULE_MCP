using Newtonsoft.Json.Linq;
using RevitMCP.Setup.Services;
using Xunit;

namespace RevitMCP.Tests;

public class McpConfigMergerTests
{
    private const string Bridge = @"C:\Dropbox\EULE-MCP\Bridge\RevitMCP.Bridge.exe";

    // ── JSON (Claude Code ~/.claude.json, Antigravity mcp_config.json) ────────

    [Fact]
    public void MergeJson_EmptyInput_CreatesServerEntry()
    {
        var result = McpConfigMerger.MergeJsonMcpServers(null, Bridge, "AntigravityCLI");

        var root = JObject.Parse(result);
        Assert.Equal(Bridge, root["mcpServers"]!["revit-mcp"]!["command"]!.ToString());
        Assert.Equal(new[] { "--client", "AntigravityCLI" },
            root["mcpServers"]!["revit-mcp"]!["args"]!.Select(t => t.ToString()).ToArray());
    }

    [Fact]
    public void MergeJson_WhitespaceInput_TreatedAsEmpty()
    {
        var result = McpConfigMerger.MergeJsonMcpServers("   ", Bridge, "Codex");
        Assert.NotNull(JObject.Parse(result)["mcpServers"]!["revit-mcp"]);
    }

    [Fact]
    public void MergeJson_PreservesOtherServersAndSettings()
    {
        var existing = """
            {
              "security": { "auth": { "selectedType": "oauth-personal" } },
              "mcpServers": {
                "other": { "serverUrl": "https://example.com/mcp" }
              }
            }
            """;

        var result = McpConfigMerger.MergeJsonMcpServers(existing, Bridge, "AntigravityCLI");

        var root = JObject.Parse(result);
        Assert.Equal("oauth-personal", root["security"]!["auth"]!["selectedType"]!.ToString());
        Assert.Equal("https://example.com/mcp", root["mcpServers"]!["other"]!["serverUrl"]!.ToString());
        Assert.Equal(Bridge, root["mcpServers"]!["revit-mcp"]!["command"]!.ToString());
    }

    [Fact]
    public void MergeJson_ReplacesExistingRevitMcpEntry()
    {
        var existing = """
            { "mcpServers": { "revit-mcp": { "command": "C:\\old\\bridge.exe", "args": ["--client", "GeminiCLI"] } } }
            """;

        var result = McpConfigMerger.MergeJsonMcpServers(existing, Bridge, "AntigravityCLI");

        var entry = JObject.Parse(result)["mcpServers"]!["revit-mcp"]!;
        Assert.Equal(Bridge, entry["command"]!.ToString());
        Assert.Equal("AntigravityCLI", entry["args"]![1]!.ToString());
    }

    [Fact]
    public void MergeJson_InvalidJson_Throws()
    {
        Assert.ThrowsAny<Exception>(() =>
            McpConfigMerger.MergeJsonMcpServers("{ not json", Bridge, "Codex"));
    }

    // ── TOML (Codex ~/.codex/config.toml) ─────────────────────────────────────

    [Fact]
    public void MergeToml_EmptyInput_CreatesSection()
    {
        var result = McpConfigMerger.MergeCodexToml(null, Bridge, "Codex");

        Assert.Contains("[mcp_servers.revit-mcp]", result);
        Assert.Contains(@"command = ""C:\\Dropbox\\EULE-MCP\\Bridge\\RevitMCP.Bridge.exe""", result);
        Assert.Contains(@"args = [""--client"", ""Codex""]", result);
    }

    [Fact]
    public void MergeToml_AppendsToExistingConfig_PreservingContent()
    {
        var existing = "model = \"o4\"\n\n[mcp_servers.other]\ncommand = \"other.exe\"\n";

        var result = McpConfigMerger.MergeCodexToml(existing, Bridge, "Codex");

        Assert.Contains("model = \"o4\"", result);
        Assert.Contains("[mcp_servers.other]", result);
        Assert.Contains("command = \"other.exe\"", result);
        Assert.Contains("[mcp_servers.revit-mcp]", result);
    }

    [Fact]
    public void MergeToml_ReplacesExistingSection_InPlace()
    {
        var existing =
            "[mcp_servers.revit-mcp]\n" +
            "command = \"C:\\\\old\\\\bridge.exe\"\n" +
            "args = [\"--client\", \"Old\"]\n" +
            "\n" +
            "[mcp_servers.other]\n" +
            "command = \"other.exe\"\n";

        var result = McpConfigMerger.MergeCodexToml(existing, Bridge, "Codex");

        Assert.DoesNotContain("old\\\\bridge.exe", result);
        Assert.Contains(@"C:\\Dropbox\\EULE-MCP\\Bridge\\RevitMCP.Bridge.exe", result);
        Assert.Contains("[mcp_servers.other]", result);
        Assert.Contains("command = \"other.exe\"", result);
        // Only one revit-mcp section after the merge.
        Assert.Equal(2, result.Split("[mcp_servers.revit-mcp]").Length);
    }

    [Fact]
    public void MergeToml_SectionAtEndOfFile_Replaced()
    {
        var existing = "model = \"o4\"\n\n[mcp_servers.revit-mcp]\ncommand = \"C:\\\\old\\\\bridge.exe\"\n";

        var result = McpConfigMerger.MergeCodexToml(existing, Bridge, "Codex");

        Assert.Contains("model = \"o4\"", result);
        Assert.DoesNotContain("old", result);
        Assert.Contains(@"C:\\Dropbox\\EULE-MCP\\Bridge\\RevitMCP.Bridge.exe", result);
    }
}
