using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RevitMCP.Addin.Coordination.Clash.DTOs;

namespace RevitMCP.Addin.Coordination.Clash.Services;

/// <summary>
/// Persists the last clash run of one document (<c>scope</c>, its model id) to
/// %AppData%\RKTools\RevitMCP\ClashRuns\&lt;scope&gt;.json, and keeps a next/previous cursor per document, client and
/// run. Before M24 there was one global file and one static cursor for every session and document, and "next" after
/// a new run skipped clash 1 (the cursor started on it and advanced before reading).
/// Uses System.Text.Json — no Revit API dependency, safe for unit testing.
/// </summary>
public class ClashRunCacheService
{
    private static readonly string DefaultFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RKTools", "RevitMCP", "ClashRuns");

    /// <summary>Redirects the files (tests).</summary>
    public static string? FolderOverride { get; set; }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Cursor per scope|client: the run it belongs to and the 0-based position (-1 = before the first).</summary>
    private static readonly Dictionary<string, (string RunId, int Position)> Cursors = new(StringComparer.Ordinal);
    private static readonly object SyncRoot = new();

    private readonly string _scope;

    /// <param name="scope">The document's model id; empty when no document is known (one shared "none" run).</param>
    public ClashRunCacheService(string scope = "")
    {
        _scope = string.IsNullOrWhiteSpace(scope) ? "none" : scope;
    }

    private string FilePath => Path.Combine(FolderOverride ?? DefaultFolder, SafeFileName(_scope) + ".json");

    public void Save(ClashRunResultDto run)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(run, _jsonOpts));
    }

    public ClashRunResultDto? Load()
    {
        if (!File.Exists(FilePath)) return null;
        var json = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize<ClashRunResultDto>(json, _jsonOpts);
    }

    public bool HasCache() => File.Exists(FilePath);

    /// <summary>Advances the client's cursor and returns the clash there; the first call after a run gives clash 1.</summary>
    public (ClashResultDto? Clash, int Index, int Total) GetNext(ClashRunResultDto run, string client = "") =>
        Move(run, client, position => (position + 1) % run.Clashes.Count);

    /// <summary>Moves the client's cursor back; the first call after a run gives the last clash.</summary>
    public (ClashResultDto? Clash, int Index, int Total) GetPrevious(ClashRunResultDto run, string client = "") =>
        Move(run, client, position => position < 0 ? run.Clashes.Count - 1 : (position - 1 + run.Clashes.Count) % run.Clashes.Count);

    /// <summary>The clash at the client's cursor (clash 1 before any move).</summary>
    public (ClashResultDto? Clash, int Index, int Total) GetCurrent(ClashRunResultDto run, string client = "")
    {
        if (run.Clashes.Count == 0) return (null, 0, 0);
        var idx = Math.Max(0, Math.Min(Position(run, client), run.Clashes.Count - 1));
        return (run.Clashes[idx], idx + 1, run.Clashes.Count);
    }

    /// <summary>Forgets every cursor (tests).</summary>
    public static void ResetCursor()
    {
        lock (SyncRoot) Cursors.Clear();
    }

    private (ClashResultDto? Clash, int Index, int Total) Move(ClashRunResultDto run, string client, Func<int, int> step)
    {
        if (run.Clashes.Count == 0) return (null, 0, 0);
        lock (SyncRoot)
        {
            var next = step(Position(run, client));
            Cursors[Key(client)] = (run.RunId, next);
            return (run.Clashes[next], next + 1, run.Clashes.Count);
        }
    }

    /// <summary>The client's position in this run; -1 when it has not moved in it (a new run starts before clash 1).</summary>
    private int Position(ClashRunResultDto run, string client)
    {
        lock (SyncRoot)
            return Cursors.TryGetValue(Key(client), out var cursor) && cursor.RunId == run.RunId &&
                   cursor.Position < run.Clashes.Count ? cursor.Position : -1;
    }

    private string Key(string client) => _scope + "|" + (client ?? string.Empty);

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
