using System.IO;
using Newtonsoft.Json;

namespace RevitMCP.Addin.Village;

/// <summary>One Revit process's village, as written to the local instance folder.</summary>
public sealed class VillageInstanceRecord
{
    public int ProcessId { get; set; }
    public string RevitVersion { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// File-based list of running villages (one per Revit process) so a viewer can link to the
/// others. Mirrors the connector's instance registry pattern: best-effort, never throws,
/// entries of dead processes are pruned on read. Separate from the connector registry so the
/// village never has to touch a Core type. No Revit API dependency.
/// </summary>
public sealed class VillageInstanceRegistry
{
    private const string FilePrefix = "village-";

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RKTools", "RevitMCP", "Village", "instances");

    private readonly string _directory;

    public VillageInstanceRegistry(string? directory = null)
    {
        _directory = string.IsNullOrEmpty(directory) ? DefaultDirectory : directory!;
    }

    public string Directory => _directory;

    private string PathFor(int processId) => Path.Combine(_directory, FilePrefix + processId + ".json");

    public bool Register(VillageInstanceRecord record)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            record.UpdatedUtc = DateTime.UtcNow;
            File.WriteAllText(PathFor(record.ProcessId), JsonConvert.SerializeObject(record, Formatting.Indented));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Unregister(int processId)
    {
        try
        {
            var path = PathFor(processId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best effort */ }
    }

    /// <summary>Lists registered villages whose process is still alive (per <paramref name="isAlive"/>), pruning the rest.</summary>
    public List<VillageInstanceLink> List(Func<int, bool> isAlive, int currentProcessId)
    {
        var result = new List<VillageInstanceLink>();
        try
        {
            if (!System.IO.Directory.Exists(_directory)) return result;
            foreach (var file in System.IO.Directory.GetFiles(_directory, FilePrefix + "*.json"))
            {
                VillageInstanceRecord? record;
                try { record = JsonConvert.DeserializeObject<VillageInstanceRecord>(File.ReadAllText(file)); }
                catch { continue; }
                if (record == null || record.ProcessId <= 0 || string.IsNullOrEmpty(record.Url)) continue;

                bool alive;
                try { alive = record.ProcessId == currentProcessId || isAlive(record.ProcessId); }
                catch { alive = true; }
                if (!alive)
                {
                    try { File.Delete(file); } catch { }
                    continue;
                }

                result.Add(new VillageInstanceLink
                {
                    ProcessId = record.ProcessId,
                    RevitVersion = record.RevitVersion,
                    ProjectName = VillageEventSerializer.Truncate(record.ProjectName),
                    Url = record.Url,
                    IsCurrent = record.ProcessId == currentProcessId
                });
            }
        }
        catch { /* best effort */ }

        return result.OrderByDescending(l => l.IsCurrent).ThenBy(l => l.ProcessId).ToList();
    }
}
