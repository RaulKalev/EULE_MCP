using Newtonsoft.Json;

namespace RevitMCP.Core.Instances;

/// <summary>
/// File-based registry of running Revit instances that host a RevitMCP pipe server.
/// Each Revit process writes an "instance-{pid}.json" registration file on connector start
/// and removes it on stop. An optional "active-instance.json" marker records the instance
/// the user explicitly selected, so the bridge can route requests to it first.
///
/// All operations are best-effort: registry failures must never crash the add-in or bridge,
/// so every file operation is guarded.
/// </summary>
public class RevitInstanceRegistry
{
    private const string InstanceFilePrefix = "instance-";
    private const string ActiveMarkerFileName = "active-instance.json";

    /// <summary>Contents of the active-instance marker file.</summary>
    private class ActiveInstanceMarker
    {
        public int ProcessId { get; set; }
    }

    /// <summary>Default registry location: %LOCALAPPDATA%\RevitMCP\Instances.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitMCP", "Instances");

    private readonly string _directory;

    public RevitInstanceRegistry(string? directory = null)
    {
        _directory = string.IsNullOrEmpty(directory) ? DefaultDirectory : directory!;
    }

    public string Directory => _directory;

    private string InstanceFilePath(int processId) => Path.Combine(_directory, $"{InstanceFilePrefix}{processId}.json");
    private string ActiveMarkerPath => Path.Combine(_directory, ActiveMarkerFileName);

    /// <summary>Registers (or refreshes) an instance. Returns false if the write failed.</summary>
    public bool Register(RevitInstanceInfo info)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            info.UpdatedUtc = DateTime.UtcNow;
            File.WriteAllText(InstanceFilePath(info.ProcessId), JsonConvert.SerializeObject(info, Formatting.Indented));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes the registration for a process. Also clears the active marker if it points to it.</summary>
    public void Unregister(int processId)
    {
        try
        {
            var path = InstanceFilePath(processId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best effort */ }

        try
        {
            if (GetActiveProcessId() == processId && File.Exists(ActiveMarkerPath))
                File.Delete(ActiveMarkerPath);
        }
        catch { /* best effort */ }
    }

    /// <summary>Marks a process as the user-selected active instance.</summary>
    public bool SetActive(int processId)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            File.WriteAllText(ActiveMarkerPath, JsonConvert.SerializeObject(new ActiveInstanceMarker { ProcessId = processId }));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns the process id of the user-selected active instance, or null when none is set.</summary>
    public int? GetActiveProcessId()
    {
        try
        {
            if (!File.Exists(ActiveMarkerPath)) return null;
            var json = File.ReadAllText(ActiveMarkerPath);
            var marker = JsonConvert.DeserializeObject<ActiveInstanceMarker>(json);
            return marker?.ProcessId;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Lists all registered instances. Corrupt or unreadable entries are skipped.</summary>
    public List<RevitInstanceInfo> List()
    {
        var result = new List<RevitInstanceInfo>();
        try
        {
            if (!System.IO.Directory.Exists(_directory)) return result;

            foreach (var file in System.IO.Directory.GetFiles(_directory, $"{InstanceFilePrefix}*.json"))
            {
                try
                {
                    var info = JsonConvert.DeserializeObject<RevitInstanceInfo>(File.ReadAllText(file));
                    if (info != null && info.ProcessId > 0 && !string.IsNullOrEmpty(info.PipeName))
                        result.Add(info);
                }
                catch { /* skip corrupt entry */ }
            }
        }
        catch { /* best effort */ }

        return result;
    }

    /// <summary>
    /// Orders instances by routing preference:
    /// 1. The user-selected active instance (if present in the list).
    /// 2. Higher Revit version first (2026 before 2024 — newer versions take priority).
    /// 3. Most recently registered first.
    /// </summary>
    public static List<RevitInstanceInfo> OrderByPreference(IEnumerable<RevitInstanceInfo> instances, int? activeProcessId)
    {
        return instances
            .OrderByDescending(i => activeProcessId.HasValue && i.ProcessId == activeProcessId.Value)
            .ThenByDescending(i => ParseVersion(i.RevitVersion))
            .ThenByDescending(i => i.UpdatedUtc)
            .ToList();
    }

    private static int ParseVersion(string? version) =>
        int.TryParse(version, out var v) ? v : 0;
}
