using System.Globalization;
using Newtonsoft.Json;

namespace RevitMCP.Village;

/// <summary>One glTF model the viewer may use in place of a procedurally drawn sprite.</summary>
public sealed class VillageModelEntry
{
    /// <summary>Relative path under the models folder, always forward-slashed: <c>landmarks/town_hall.glb</c>.</summary>
    [JsonProperty("path")] public string Path { get; set; } = string.Empty;

    /// <summary><c>landmarks</c>, <c>warehouses</c>, <c>scenery</c> or <c>characters</c>.</summary>
    [JsonProperty("kind")] public string Kind { get; set; } = string.Empty;

    /// <summary>File name without the extension — the id the viewer matches against.</summary>
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;

    [JsonProperty("bytes")] public long Bytes { get; set; }
}

/// <summary>What the viewer is told about the model folder.</summary>
public sealed class VillageModelIndex
{
    [JsonProperty("enabled")] public bool Enabled { get; set; }
    [JsonProperty("folder_configured")] public bool FolderConfigured { get; set; }
    /// <summary>Present so the UI can tell you where to drop files. Never a user name or a document path.</summary>
    [JsonProperty("folder")] public string? Folder { get; set; }
    [JsonProperty("models")] public List<VillageModelEntry> Models { get; set; } = new();
    [JsonProperty("error")] public string? Error { get; set; }
    [JsonProperty("note")] public string Note { get; set; } =
        "Models are read only. Anything without a model falls back to the drawn sprite.";
}

/// <summary>
/// Reads the optional glTF model folder. Pure path handling and directory enumeration — no Revit,
/// no network, and nothing is ever written. The folder is served read-only over the loopback
/// listener, so every path the viewer can ask for is validated here first.
/// </summary>
public sealed class VillageModelLibrary
{
    /// <summary>Only binary glTF. One extension keeps the served surface trivial to reason about.</summary>
    public const string Extension = ".glb";

    /// <summary>Sub-folders that mean something to the viewer; anything else is ignored.</summary>
    public static readonly string[] Kinds = { "landmarks", "warehouses", "scenery", "characters" };

    /// <summary>
    /// Files may also sit loose in the root. An exported asset pack is usually one flat folder, and
    /// making people sort 30 files into sub-folders to see anything is a poor first five minutes.
    /// The viewer matches on the file name either way.
    /// </summary>
    public const string RootKind = "root";

    /// <summary>A single model larger than this is skipped: the viewer has to load it over loopback.</summary>
    public const long MaxBytes = 32L * 1024 * 1024;

    /// <summary>Upper bound on entries, so a stray folder cannot produce an enormous index.</summary>
    public const int MaxEntries = 400;

    private readonly string? _root;

    public VillageModelLibrary(string? folder)
    {
        _root = Normalize(folder);
    }

    /// <summary>Absolute models folder, or null when none is configured or it does not exist.</summary>
    public string? Root => _root;

    private static string? Normalize(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            var full = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder!.Trim()));
            return System.IO.Directory.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Everything the viewer may load. Never throws.</summary>
    public VillageModelIndex Index(bool folderConfigured)
    {
        var index = new VillageModelIndex { FolderConfigured = folderConfigured, Folder = _root };
        if (_root == null)
        {
            index.Error = folderConfigured
                ? "The configured village.modelsFolder does not exist yet."
                : null;
            return index;
        }

        try
        {
            foreach (var kind in new[] { RootKind }.Concat(Kinds))
            {
                var dir = kind == RootKind ? _root : System.IO.Path.Combine(_root, kind);
                if (!System.IO.Directory.Exists(dir)) continue;
                foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*" + Extension, System.IO.SearchOption.TopDirectoryOnly))
                {
                    if (index.Models.Count >= MaxEntries) break;
                    long length;
                    try { length = new System.IO.FileInfo(file).Length; } catch { continue; }
                    if (length <= 0 || length > MaxBytes) continue;
                    var name = System.IO.Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(name) || !IsSafeName(name)) continue;
                    index.Models.Add(new VillageModelEntry
                    {
                        Path = (kind == RootKind ? string.Empty : kind + "/") + name + Extension,
                        Kind = kind,
                        Name = name,
                        Bytes = length
                    });
                }
            }
            index.Models.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            index.Enabled = index.Models.Count > 0;
        }
        catch (Exception ex)
        {
            index.Error = "The models folder could not be read: " + ex.GetType().Name;
        }
        return index;
    }

    /// <summary>
    /// Maps a request path such as <c>landmarks/town_hall.glb</c> to a file, or null. Rejects
    /// anything that is not exactly &lt;known kind&gt;/&lt;safe name&gt;.glb, so no request can
    /// escape the folder however it is spelled or encoded.
    /// </summary>
    public string? Resolve(string? relative)
    {
        if (_root == null || string.IsNullOrWhiteSpace(relative)) return null;

        var value = relative!.Replace('\\', '/').Trim();
        if (value.Length == 0 || value.Length > 200) return null;
        if (value.IndexOf("..", StringComparison.Ordinal) >= 0) return null;
        if (value.IndexOf(':') >= 0 || value.IndexOf('%') >= 0 || value[0] == '/') return null;

        var parts = value.Split('/');
        if (parts.Length > 2) return null;
        if (parts.Length == 2 && Array.IndexOf(Kinds, parts[0]) < 0) return null;
        var folder = parts.Length == 2 ? parts[0] : string.Empty;

        var file = parts[parts.Length - 1];
        if (!file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return null;
        var name = file.Substring(0, file.Length - Extension.Length);
        if (!IsSafeName(name)) return null;

        try
        {
            var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(_root, folder, name + Extension));
            // Belt and braces: the resolved path must still sit under the root.
            var root = _root.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? _root : _root + System.IO.Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            if (!System.IO.File.Exists(candidate)) return null;
            return new System.IO.FileInfo(candidate).Length is > 0 and <= MaxBytes ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Letters, digits, underscore and hyphen only — the slug shape warehouse ids already use.</summary>
    public static bool IsSafeName(string name)
    {
        if (name.Length == 0 || name.Length > 120) return false;
        foreach (var c in name)
        {
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// File name the viewer looks for, for a landmark or warehouse id. Warehouse ids carry a
    /// <c>wh_</c> prefix that the file name drops, so the file is named after the category.
    /// </summary>
    public static string FileNameFor(string kind, string id)
    {
        var name = id ?? string.Empty;
        if (string.Equals(kind, "warehouses", StringComparison.Ordinal) && name.StartsWith("wh_", StringComparison.Ordinal))
            name = name.Substring(3);
        return kind + "/" + name + Extension;
    }

    /// <summary>Candidate folders in priority order: the configured one, then next to the add-in.</summary>
    public static string? FirstExisting(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var normalized = Normalize(candidate);
            if (normalized != null) return normalized;
        }
        return null;
    }

    public static string Describe(long bytes) =>
        bytes >= 1024 * 1024
            ? (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
            : Math.Max(1, bytes / 1024) + " KB";
}
