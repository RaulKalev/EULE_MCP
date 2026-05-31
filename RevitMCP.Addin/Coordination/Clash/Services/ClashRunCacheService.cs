using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RevitMCP.Addin.Coordination.Clash.DTOs;

namespace RevitMCP.Addin.Coordination.Clash.Services;

/// <summary>
/// Persists the last clash run result to %AppData%\RKTools\RevitMCP\LastClashRun.json.
/// Maintains an in-memory cursor for next/previous navigation.
/// Uses System.Text.Json — no Revit API dependency, safe for unit testing.
/// </summary>
public class ClashRunCacheService
{
    private static readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RKTools", "RevitMCP", "LastClashRun.json");

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static int _cursor = 0;

    public void Save(ClashRunResultDto run)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(run, _jsonOpts));
        _cursor = 0;
    }

    public ClashRunResultDto? Load()
    {
        if (!File.Exists(_filePath)) return null;
        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<ClashRunResultDto>(json, _jsonOpts);
    }

    public bool HasCache() => File.Exists(_filePath);

    /// <summary>Advances cursor and returns the clash at that position.</summary>
    public (ClashResultDto? Clash, int Index, int Total) GetNext(ClashRunResultDto run)
    {
        if (run.Clashes.Count == 0) return (null, 0, 0);
        _cursor = (_cursor + 1) % run.Clashes.Count;
        return (run.Clashes[_cursor], _cursor + 1, run.Clashes.Count);
    }

    /// <summary>Moves cursor back and returns the clash at that position.</summary>
    public (ClashResultDto? Clash, int Index, int Total) GetPrevious(ClashRunResultDto run)
    {
        if (run.Clashes.Count == 0) return (null, 0, 0);
        _cursor = (_cursor - 1 + run.Clashes.Count) % run.Clashes.Count;
        return (run.Clashes[_cursor], _cursor + 1, run.Clashes.Count);
    }

    /// <summary>Returns the clash at the current cursor without changing position.</summary>
    public (ClashResultDto? Clash, int Index, int Total) GetCurrent(ClashRunResultDto run)
    {
        if (run.Clashes.Count == 0) return (null, 0, 0);
        var idx = Math.Max(0, Math.Min(_cursor, run.Clashes.Count - 1));
        return (run.Clashes[idx], idx + 1, run.Clashes.Count);
    }

    /// <summary>Resets the cursor, used when a new run is saved.</summary>
    public static void ResetCursor() => _cursor = 0;
}
