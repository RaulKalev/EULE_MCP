using System.IO;

namespace RevitMCP.Setup.Services;

public enum RegistrationState
{
    NotRegistered,
    Registered,
    /// <summary>revit-mcp is registered but points at a different bridge path (stale install).</summary>
    PathMismatch
}

/// <summary>One supported AI agent (CLI + optional desktop client).</summary>
public interface IAgent
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>Value passed to the bridge as --client so Revit logs identify the caller.</summary>
    string ClientName { get; }
    string? DesktopName { get; }

    bool IsCliInstalled();
    bool IsDesktopInstalled();
    RegistrationState GetRegistration(string bridgePath);
    Task RegisterAsync(string bridgePath, Action<string> log);
    Task InstallCliAsync(Action<string> log);
    Task InstallDesktopAsync(Action<string> log);
}

public static class AgentCatalog
{
    public static IReadOnlyList<IAgent> All { get; } =
    [
        new ClaudeCodeAgent(),
        new CodexAgent(),
        new AntigravityAgent()
    ];
}

/// <summary>Claude Code CLI + Claude Desktop. User-scope MCP servers live in ~/.claude.json.</summary>
public class ClaudeCodeAgent : IAgent
{
    public string Id => "claude";
    public string DisplayName => "Claude Code";
    public string ClientName => "Claude Code";
    public string? DesktopName => "Claude Desktop";

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    public bool IsCliInstalled() => ProcessRunner.ExistsOnPath("claude");

    public bool IsDesktopInstalled() => Directory.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude"));

    public RegistrationState GetRegistration(string bridgePath) =>
        JsonRegistrationHelper.Check(ConfigPath, bridgePath);

    public async Task RegisterAsync(string bridgePath, Action<string> log)
    {
        // Prefer the official CLI — it owns ~/.claude.json and handles concurrent sessions.
        if (IsCliInstalled())
        {
            var cmd = $"claude mcp add --scope user {McpConfigMerger.ServerName} -- \"{bridgePath}\" --client \"{ClientName}\"";
            log("> " + cmd);
            var result = await ProcessRunner.RunAsync(cmd, log);
            if (result.ExitCode == 0) return;
            log("claude mcp add failed — falling back to editing ~/.claude.json directly.");
        }

        JsonRegistrationHelper.Merge(ConfigPath, bridgePath, ClientName);
        log($"Registered {McpConfigMerger.ServerName} in {ConfigPath}");
    }

    public async Task InstallCliAsync(Action<string> log)
    {
        if (ProcessRunner.ExistsOnPath("npm"))
        {
            log("> npm install -g @anthropic-ai/claude-code");
            var result = await ProcessRunner.RunAsync("npm install -g @anthropic-ai/claude-code", log);
            if (result.ExitCode == 0) return;
            log("npm install failed — trying the native installer.");
        }

        log("> irm https://claude.ai/install.ps1 | iex");
        await ProcessRunner.RunAsync(
            "powershell -NoProfile -ExecutionPolicy Bypass -Command \"irm https://claude.ai/install.ps1 | iex\"", log);
    }

    public async Task InstallDesktopAsync(Action<string> log)
    {
        if (ProcessRunner.ExistsOnPath("winget"))
        {
            log("> winget install -e --id Anthropic.Claude");
            var result = await ProcessRunner.RunAsync(
                "winget install -e --id Anthropic.Claude --accept-source-agreements --accept-package-agreements", log);
            if (result.ExitCode == 0) return;
            log("winget install failed — opening the download page instead.");
        }
        ProcessRunner.OpenUrl("https://claude.ai/download");
    }
}

/// <summary>OpenAI Codex CLI. MCP servers live in ~/.codex/config.toml. No desktop client.</summary>
public class CodexAgent : IAgent
{
    public string Id => "codex";
    public string DisplayName => "Codex CLI";
    public string ClientName => "Codex";
    public string? DesktopName => null;

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");

    public bool IsCliInstalled() => ProcessRunner.ExistsOnPath("codex");
    public bool IsDesktopInstalled() => false;

    public RegistrationState GetRegistration(string bridgePath)
    {
        try
        {
            if (!File.Exists(ConfigPath)) return RegistrationState.NotRegistered;
            var toml = File.ReadAllText(ConfigPath);
            if (!toml.Contains($"[mcp_servers.{McpConfigMerger.ServerName}]")) return RegistrationState.NotRegistered;
            return toml.Contains(bridgePath.Replace("\\", "\\\\"))
                ? RegistrationState.Registered
                : RegistrationState.PathMismatch;
        }
        catch
        {
            return RegistrationState.NotRegistered;
        }
    }

    public Task RegisterAsync(string bridgePath, Action<string> log)
    {
        var existing = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;
        var merged = McpConfigMerger.MergeCodexToml(existing, bridgePath, ClientName);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, merged);
        log($"Registered {McpConfigMerger.ServerName} in {ConfigPath}");
        return Task.CompletedTask;
    }

    public async Task InstallCliAsync(Action<string> log)
    {
        if (ProcessRunner.ExistsOnPath("npm"))
        {
            log("> npm install -g @openai/codex");
            var result = await ProcessRunner.RunAsync("npm install -g @openai/codex", log);
            if (result.ExitCode == 0) return;
            log("npm install failed — opening the install docs instead.");
        }
        ProcessRunner.OpenUrl("https://developers.openai.com/codex/cli/");
    }

    public Task InstallDesktopAsync(Action<string> log) => Task.CompletedTask;
}

/// <summary>
/// Google Antigravity (CLI `agy` + IDE). Both read the shared global MCP config at
/// ~/.gemini/config/mcp_config.json, so one registration covers CLI and IDE.
/// </summary>
public class AntigravityAgent : IAgent
{
    public string Id => "antigravity";
    public string DisplayName => "Antigravity CLI";
    public string ClientName => "AntigravityCLI";
    public string? DesktopName => "Antigravity IDE";

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "config", "mcp_config.json");

    public bool IsCliInstalled() =>
        ProcessRunner.ExistsOnPath("agy") ||
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "bin", "agy.exe"));

    public bool IsDesktopInstalled() => File.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Antigravity", "Antigravity.exe"));

    public RegistrationState GetRegistration(string bridgePath) =>
        JsonRegistrationHelper.Check(ConfigPath, bridgePath);

    public Task RegisterAsync(string bridgePath, Action<string> log)
    {
        JsonRegistrationHelper.Merge(ConfigPath, bridgePath, ClientName);
        log($"Registered {McpConfigMerger.ServerName} in {ConfigPath}");
        return Task.CompletedTask;
    }

    public async Task InstallCliAsync(Action<string> log)
    {
        log("> irm https://antigravity.google/cli/install.ps1 | iex");
        await ProcessRunner.RunAsync(
            "powershell -NoProfile -ExecutionPolicy Bypass -Command \"irm https://antigravity.google/cli/install.ps1 | iex\"", log);
    }

    public Task InstallDesktopAsync(Action<string> log)
    {
        ProcessRunner.OpenUrl("https://antigravity.google/download");
        return Task.CompletedTask;
    }
}

/// <summary>Shared read/merge logic for JSON configs with a top-level mcpServers object.</summary>
internal static class JsonRegistrationHelper
{
    public static RegistrationState Check(string configPath, string bridgePath)
    {
        try
        {
            if (!File.Exists(configPath)) return RegistrationState.NotRegistered;
            var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(configPath));
            var entry = root["mcpServers"]?[McpConfigMerger.ServerName];
            if (entry == null) return RegistrationState.NotRegistered;
            var command = entry["command"]?.ToString();
            return string.Equals(command, bridgePath, StringComparison.OrdinalIgnoreCase)
                ? RegistrationState.Registered
                : RegistrationState.PathMismatch;
        }
        catch
        {
            return RegistrationState.NotRegistered;
        }
    }

    public static void Merge(string configPath, string bridgePath, string clientName)
    {
        var existing = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
        var merged = McpConfigMerger.MergeJsonMcpServers(existing, bridgePath, clientName);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, merged);
    }
}
