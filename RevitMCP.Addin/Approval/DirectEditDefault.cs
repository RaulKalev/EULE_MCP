using System.IO;
using System.Text.Json.Nodes;
using RevitMCP.Addin.Configuration;

namespace RevitMCP.Addin.Approval;

/// <summary>
/// Reads <c>approval.directEditByDefault</c> from the per-user config (%APPDATA%\RKTools\MCP\user.config.json).
/// When true the connector starts in Direct Edit, so unattended batches (#90: reload links, remove links, save
/// across several projects) keep running after an add-in reload or a Revit restart. Only the user's own config
/// counts — a company or project config cannot switch approvals off. Destructive tools still need approval.
/// </summary>
public static class DirectEditDefault
{
    public const string Key = "directEditByDefault";

    public static bool Read()
    {
        try
        {
            var (path, _) = ConfigPathResolver.Resolve(ConfigPathResolver.ScopeUser);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            var (config, _) = new JsonConfigService().Read(path!, createIfMissing: false);
            return IsEnabled(config);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True only for an explicit boolean <c>true</c> at <c>approval.directEditByDefault</c>.</summary>
    public static bool IsEnabled(JsonObject? config)
    {
        if (config?["approval"] is not JsonObject approval) return false;
        if (approval[Key] is not JsonValue value) return false;
        return value.TryGetValue<bool>(out var enabled) && enabled;
    }
}
