using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Setup.Services;

/// <summary>
/// Pure text-in/text-out merge helpers for the MCP config files of each supported agent.
/// No file IO here so the logic is unit-testable (linked into RevitMCP.Tests).
/// </summary>
public static class McpConfigMerger
{
    public const string ServerName = "revit-mcp";

    /// <summary>
    /// Merges the revit-mcp entry into a JSON config that carries a top-level
    /// "mcpServers" object. Used for both Claude Code (~/.claude.json) and
    /// Antigravity (~/.gemini/config/mcp_config.json). Existing content — other
    /// servers and unrelated settings — is preserved. Empty or missing input is
    /// treated as an empty object.
    /// </summary>
    public static string MergeJsonMcpServers(string? existingJson, string bridgePath, string clientName)
    {
        JObject root;
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            root = new JObject();
        }
        else
        {
            root = JObject.Parse(existingJson); // throws on invalid JSON — caller surfaces the error
        }

        if (root["mcpServers"] is not JObject servers)
        {
            servers = new JObject();
            root["mcpServers"] = servers;
        }

        servers[ServerName] = new JObject
        {
            ["command"] = bridgePath,
            ["args"] = new JArray("--client", clientName)
        };

        return root.ToString(Formatting.Indented);
    }

    /// <summary>
    /// Merges the [mcp_servers.revit-mcp] section into Codex's config.toml.
    /// If the section already exists it is replaced in place (up to the next
    /// section header); otherwise it is appended. All other content is preserved
    /// byte-for-byte. Empty or missing input yields just the new section.
    /// </summary>
    public static string MergeCodexToml(string? existingToml, string bridgePath, string clientName)
    {
        var section = BuildCodexSection(bridgePath, clientName);
        if (string.IsNullOrWhiteSpace(existingToml))
            return section + Environment.NewLine;

        var lines = existingToml!.Replace("\r\n", "\n").Split('\n');
        var header = $"[mcp_servers.{ServerName}]";

        int start = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == header) { start = i; break; }
        }

        if (start < 0)
        {
            var trimmed = existingToml.TrimEnd('\r', '\n');
            return trimmed + Environment.NewLine + Environment.NewLine + section + Environment.NewLine;
        }

        // Replace lines from the header up to (excluding) the next section header.
        int end = lines.Length;
        for (int i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("[")) { end = i; break; }
        }

        var result = new List<string>();
        result.AddRange(lines.Take(start));
        result.AddRange(section.Replace("\r\n", "\n").Split('\n'));
        result.AddRange(lines.Skip(end));
        return string.Join(Environment.NewLine, result);
    }

    private static string BuildCodexSection(string bridgePath, string clientName)
    {
        var escaped = bridgePath.Replace("\\", "\\\\");
        return $"[mcp_servers.{ServerName}]" + Environment.NewLine +
               $"command = \"{escaped}\"" + Environment.NewLine +
               $"args = [\"--client\", \"{clientName}\"]";
    }
}
