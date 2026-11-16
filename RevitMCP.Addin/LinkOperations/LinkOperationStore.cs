using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace RevitMCP.Addin.LinkOperations;

/// <summary>
/// The durable record of link operations (#163): one JSON file per operation in
/// <c>%LOCALAPPDATA%\RevitMCP\LinkOperations</c>, replaced atomically on every change. The bridge reads the same
/// folder, so status never needs the Revit API thread. A cancel request from the bridge is a separate empty
/// <c>&lt;id&gt;.cancel</c> file, so the bridge never rewrites a record the add-in is changing. Thread-safe within a
/// process; all changes go through <see cref="Update"/> (read, change, write under one lock). Pure (System.IO).
/// </summary>
public sealed class LinkOperationStore
{
    public const int MaxFinishedKept = 200;
    private const string CancelExtension = ".cancel";

    private readonly string _folder;
    private readonly object _gate = new();

    public LinkOperationStore(string folder) => _folder = folder;

    public string Folder => _folder;

    public static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMCP", "LinkOperations");

    private string PathOf(string id) => Path.Combine(_folder, id + ".json");
    private string CancelPathOf(string id) => Path.Combine(_folder, id + CancelExtension);

    /// <summary>Writes a new operation. False when the id is invalid or already exists.</summary>
    public bool Create(LinkOperation op)
    {
        if (!LinkOperationIds.IsValid(op.Id)) return false;
        lock (_gate)
        {
            if (File.Exists(PathOf(op.Id))) return false;
            Write(op);
            Prune();
            return true;
        }
    }

    /// <summary>Reads, changes and writes one operation. The mutator returns false to leave it unchanged. Null when it does not exist.</summary>
    public LinkOperation? Update(string id, Func<LinkOperation, bool> mutate)
    {
        if (!LinkOperationIds.IsValid(id)) return null;
        lock (_gate)
        {
            var op = ReadFile(PathOf(id));
            if (op == null) return null;
            if (mutate(op)) Write(op);
            return op;
        }
    }

    public LinkOperation? Get(string? id)
    {
        if (!LinkOperationIds.IsValid(id)) return null;
        lock (_gate) return ReadFile(PathOf(id!));
    }

    /// <summary>Every readable record, newest first. Unreadable files are skipped.</summary>
    public List<LinkOperation> LoadAll()
    {
        lock (_gate)
        {
            var list = new List<LinkOperation>();
            if (!Directory.Exists(_folder)) return list;
            foreach (var file in Directory.GetFiles(_folder, LinkOperationIds.Prefix + "*.json"))
            {
                var op = ReadFile(file);
                if (op != null) list.Add(op);
            }
            return list.OrderByDescending(o => o.CreatedAt).ToList();
        }
    }

    /// <summary>The operation that was created for an approval request (so a caller that only has the request id finds it).</summary>
    public List<LinkOperation> FindByRequest(string requestId) =>
        string.IsNullOrWhiteSpace(requestId) ? new List<LinkOperation>() : LoadAll().Where(o => o.RequestId == requestId).OrderBy(o => o.CreatedAt).ToList();

    public bool Remove(string id)
    {
        if (!LinkOperationIds.IsValid(id)) return false;
        lock (_gate)
        {
            var path = PathOf(id);
            if (!File.Exists(path)) return false;
            try
            {
                File.Delete(path);
                if (File.Exists(CancelPathOf(id))) File.Delete(CancelPathOf(id));
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Records a cancel request for an operation that exists and is not finished. Returns false otherwise. Safe to call
    /// from another process: it only creates an empty file next to the record.
    /// </summary>
    public bool RequestCancel(string id)
    {
        if (!LinkOperationIds.IsValid(id)) return false;
        var op = Get(id);
        if (op == null || LinkOperationStates.IsTerminal(op.State)) return false;
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(CancelPathOf(id), DateTimeOffset.UtcNow.ToString("o"), new UTF8Encoding(false));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public bool IsCancelRequested(string id) => LinkOperationIds.IsValid(id) && File.Exists(CancelPathOf(id));

    private void Write(LinkOperation op)
    {
        Directory.CreateDirectory(_folder);
        var json = JsonConvert.SerializeObject(op, Formatting.Indented);
        var path = PathOf(op.Id);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null);   // atomic: a reader sees the old or the new file, never none
        else File.Move(tmp, path);
    }

    private static LinkOperation? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var op = JsonConvert.DeserializeObject<LinkOperation>(File.ReadAllText(path, Encoding.UTF8));
            return op != null && LinkOperationIds.IsValid(op.Id) && op.Id == Path.GetFileNameWithoutExtension(path) ? op : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Keeps the newest <see cref="MaxFinishedKept"/> finished records; open and outcome_unknown ones are never pruned.</summary>
    private void Prune()
    {
        try
        {
            var finished = Directory.GetFiles(_folder, LinkOperationIds.Prefix + "*.json")
                .Select(f => (File: f, Op: ReadFile(f)))
                .Where(x => x.Op != null && LinkOperationStates.IsTerminal(x.Op.State))
                .OrderByDescending(x => x.Op!.CreatedAt)
                .Skip(MaxFinishedKept)
                .ToList();
            foreach (var old in finished)
            {
                File.Delete(old.File);
                var marker = Path.ChangeExtension(old.File, CancelExtension);
                if (File.Exists(marker)) File.Delete(marker);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pruning is housekeeping.
        }
    }
}
