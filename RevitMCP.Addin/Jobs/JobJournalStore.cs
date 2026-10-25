using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Checkpoints;

namespace RevitMCP.Addin.Jobs;

/// <summary>
/// Job journals (#100): <c>&lt;folder&gt;\&lt;modelId&gt;\&lt;jobId&gt;.json</c>, the whole job with per-step results
/// (each result's data trimmed to 64 KB) and bound variables, rewritten after every state change. A job that was
/// running, waiting or paused when Revit stopped is loaded as Suspended; nothing resumes by itself. At most 50
/// journals per model: the oldest finished ones go first. Thread-safe; call it off the Revit API thread. Pure.
/// </summary>
public sealed class JobJournalStore
{
    public const int MaxPerModel = 50;
    public const int MaxResultBytes = 64 * 1024;
    public const string RestartReason = "Revit was restarted";

    private readonly string _folder;
    private readonly object _gate = new();

    public JobJournalStore(string folder) => _folder = folder;

    public string Folder => _folder;

    public void Save(Job job)
    {
        if (!JobIds.IsValid(job.Id) || !CheckpointIds.IsValidModelId(job.ModelId)) return;
        string json;
        lock (job) json = JsonConvert.SerializeObject(job, Formatting.None);
        lock (_gate)
        {
            var dir = Path.Combine(_folder, job.ModelId);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, job.Id + ".json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            Prune(dir, job.Id);
        }
    }

    /// <summary>
    /// Every journal on disk. Jobs that were running, waiting for an approval or paused become Suspended (and are
    /// saved so); finished ones are returned as they were.
    /// </summary>
    public List<Job> LoadAll()
    {
        var jobs = new List<Job>();
        lock (_gate)
        {
            if (!Directory.Exists(_folder)) return jobs;
            foreach (var dir in Directory.GetDirectories(_folder))
            {
                if (!CheckpointIds.IsValidModelId(Path.GetFileName(dir))) continue;
                foreach (var file in Directory.GetFiles(dir, JobIds.Prefix + "*.json"))
                {
                    var job = Read(file);
                    if (job == null) continue;
                    if (job.State is JobState.Running or JobState.WaitingApproval or JobState.Paused or JobState.Proposed)
                    {
                        job.State = JobState.Suspended;
                        job.StopReason = RestartReason;
                        foreach (var step in job.Steps.Where(s => s.State is JobStepState.Running or JobStepState.WaitingApproval))
                            step.State = JobStepState.Pending;
                        job.PlanId = null;
                        try { File.WriteAllText(file, JsonConvert.SerializeObject(job, Formatting.None), new UTF8Encoding(false)); } catch { }
                    }
                    jobs.Add(job);
                }
            }
        }
        return jobs.OrderByDescending(j => j.CreatedAt).ToList();
    }

    public bool Remove(string modelId, string jobId)
    {
        if (!JobIds.IsValid(jobId) || !CheckpointIds.IsValidModelId(modelId)) return false;
        lock (_gate)
        {
            var path = Path.Combine(_folder, modelId, jobId + ".json");
            if (!File.Exists(path)) return false;
            try { File.Delete(path); return true; } catch { return false; }
        }
    }

    /// <summary>
    /// A step result for the journal: success, status, message, warnings and data — data replaced by a stub
    /// when its JSON is larger than <see cref="MaxResultBytes"/>.
    /// </summary>
    public static JObject Trim(JObject result)
    {
        var data = result["data"];
        if (data == null) return result;
        var bytes = Encoding.UTF8.GetByteCount(data.ToString(Formatting.None));
        if (bytes <= MaxResultBytes) return result;
        var copy = (JObject)result.DeepClone();
        copy["data"] = new JObject { ["truncated"] = true, ["bytes"] = bytes };
        return copy;
    }

    private static Job? Read(string file)
    {
        try
        {
            var job = JsonConvert.DeserializeObject<Job>(File.ReadAllText(file, Encoding.UTF8));
            return job != null && JobIds.IsValid(job.Id) && job.Id == Path.GetFileNameWithoutExtension(file) ? job : null;
        }
        catch
        {
            return null;
        }
    }

    private static void Prune(string dir, string keepId)
    {
        var files = Directory.GetFiles(dir, JobIds.Prefix + "*.json");
        if (files.Length <= MaxPerModel) return;
        var jobs = files.Select(f => (File: f, Job: Read(f))).Where(x => x.Job != null).ToList();
        var removable = jobs
            .Where(x => x.Job!.Id != keepId && x.Job.IsTerminal)
            .OrderBy(x => x.Job!.FinishedAt ?? x.Job.CreatedAt)
            .Take(files.Length - MaxPerModel);
        foreach (var x in removable)
        {
            try { File.Delete(x.File); } catch { }
        }
    }
}
