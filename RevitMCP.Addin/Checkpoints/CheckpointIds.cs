using System.Globalization;
using System.Security.Cryptography;

namespace RevitMCP.Addin.Checkpoints;

/// <summary>Checkpoint ids ("cp-" + 6 hex) and the folder names they live under. Pure — no Revit API.</summary>
public static class CheckpointIds
{
    public const string Prefix = "cp-";
    public const int MaxFolderNameLength = 48;

    public static string NewId()
    {
        var bytes = new byte[3];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return Prefix + string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    /// <summary>"cp-" followed by lowercase hex: an id is a file name.</summary>
    public static bool IsValid(string? id)
    {
        if (id == null || id.Length != Prefix.Length + 6 || !id.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        for (var i = Prefix.Length; i < id.Length; i++)
        {
            var c = id[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
        return true;
    }

    /// <summary>Lowercase letters, digits and hyphens (the model ids VillageModelId produces).</summary>
    public static bool IsValidModelId(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId) || modelId!.Length > MaxFolderNameLength || modelId[0] == '-') return false;
        foreach (var c in modelId)
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return false;
        return true;
    }
}
