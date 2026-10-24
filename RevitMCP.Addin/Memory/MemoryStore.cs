using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace RevitMCP.Addin.Memory;

/// <summary>An entry as read from disk, with the file it came from.</summary>
public sealed class StoredMemoryEntry
{
    public string Path { get; set; } = string.Empty;
    public DateTime LastWriteUtc { get; set; }
    public long Length { get; set; }
    public MemoryEntry Entry { get; set; } = new();
}

/// <summary>Where a write landed.</summary>
public sealed class MemoryWriteLocation
{
    public string Path { get; set; } = string.Empty;

    /// <summary>The share was unreachable: the entry waits in the local mirror and is copied when it returns.</summary>
    public bool Pending { get; set; }
}

/// <summary>
/// One tier folder on disk: one <c>&lt;id&gt;.md</c> per entry, written to a temp file in the same folder and
/// renamed into place, so readers never see half an entry and no lock is ever taken. Usage counters live in
/// per-user tally files under <c>.counters\</c>, so recall by many people never rewrites a shared entry.
///
/// A store with a mirror (the project tier on a share) also keeps a local copy of every entry it reads; when the
/// share is unreachable it reads that copy and parks writes in <c>.pending\</c>, which <see cref="ReplayPending"/>
/// copies back — entries are independent files, so replay is a plain copy. Pure — no Revit API.
/// </summary>
public sealed class MemoryStore
{
    public const string CountersFolderName = ".counters";
    public const string PendingFolderName = ".pending";
    public const string PendingCountersFolderName = ".pending-counters";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> TallyLocks = new(StringComparer.OrdinalIgnoreCase);
    private const string TempExtension = ".tmp";

    private readonly string? _probe;

    /// <param name="folder">The tier folder.</param>
    /// <param name="mirrorFolder">Local mirror for a folder on a share; null for a local folder.</param>
    /// <param name="reachabilityProbe">
    /// A folder that exists whenever the share is reachable (the project root). The tier folder itself may not
    /// exist yet. Null: the tier folder's parent.
    /// </param>
    public MemoryStore(string folder, string? mirrorFolder = null, string? reachabilityProbe = null)
    {
        Folder = folder;
        MirrorFolder = mirrorFolder;
        _probe = reachabilityProbe;
    }

    public string Folder { get; }
    public string? MirrorFolder { get; }

    public bool IsReachable()
    {
        try
        {
            if (Directory.Exists(Folder)) return true;
            var probe = _probe ?? System.IO.Path.GetDirectoryName(Folder);
            return !string.IsNullOrEmpty(probe) && Directory.Exists(probe);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The folder entries are read from right now: the tier folder, or the mirror while the share is away.</summary>
    public string ReadFolder() => MirrorFolder != null && !IsReachable() ? MirrorFolder : Folder;

    public static string FileName(string id) => id + MemoryEntryParser.FileExtension;

    /// <summary>Last write time of the read folder; changes when an entry is added, replaced or removed.</summary>
    public DateTime FolderStamp()
    {
        try
        {
            var folder = ReadFolder();
            return Directory.Exists(folder) ? Directory.GetLastWriteTimeUtc(folder) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Reads every entry file of <paramref name="folder"/>, reusing <paramref name="known"/> entries whose file did
    /// not change. Files that do not parse are reported in <paramref name="errors"/> and skipped.
    /// </summary>
    public List<StoredMemoryEntry> ReadAll(string folder, IReadOnlyDictionary<string, StoredMemoryEntry>? known, List<string> errors)
    {
        var result = new List<StoredMemoryEntry>();
        if (!Directory.Exists(folder)) return result;
        var mirrorThis = MirrorFolder != null && !string.Equals(folder, MirrorFolder, StringComparison.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(folder, "*" + MemoryEntryParser.FileExtension, SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new FileInfo(path);
                if (known != null && known.TryGetValue(path, out var cached) &&
                    cached.LastWriteUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
                {
                    result.Add(cached);
                    continue;
                }

                var text = File.ReadAllText(path, Encoding.UTF8);
                var entry = MemoryEntryParser.Parse(text, out var error);
                if (entry == null)
                {
                    errors.Add($"{info.Name}: {error}");
                    continue;
                }
                result.Add(new StoredMemoryEntry { Path = path, LastWriteUtc = info.LastWriteTimeUtc, Length = info.Length, Entry = entry });
                if (mirrorThis) MirrorCopy(info.Name, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{System.IO.Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>Writes (or replaces) an entry atomically. Falls back to the mirror when the share is unreachable.</summary>
    public MemoryWriteLocation Write(MemoryEntry entry)
    {
        var text = MemoryEntryParser.Serialize(entry);
        var name = FileName(entry.Id);
        if (MirrorFolder != null && !IsReachable())
        {
            var mirrorPath = AtomicWrite(MirrorFolder, name, text);
            AtomicWrite(System.IO.Path.Combine(MirrorFolder, PendingFolderName), name, text);
            return new MemoryWriteLocation { Path = mirrorPath, Pending = true };
        }

        var path = AtomicWrite(Folder, name, text);
        if (MirrorFolder != null) MirrorCopy(name, text);
        return new MemoryWriteLocation { Path = path };
    }

    /// <summary>
    /// Copies entries written while the share was away back to it. An entry the share already holds with a newer
    /// <c>updated</c> is kept as it is there. Returns the number copied; never throws.
    /// </summary>
    public int ReplayPending(List<string>? errors = null)
    {
        if (MirrorFolder == null || !IsReachable()) return 0;
        var pending = System.IO.Path.Combine(MirrorFolder, PendingFolderName);
        var copied = 0;
        try
        {
            ReplayPendingTallies(errors);
            if (!Directory.Exists(pending)) return 0;
            foreach (var path in Directory.EnumerateFiles(pending, "*" + MemoryEntryParser.FileExtension))
            {
                try
                {
                    var text = File.ReadAllText(path, Encoding.UTF8);
                    var entry = MemoryEntryParser.Parse(text, out _);
                    var target = System.IO.Path.Combine(Folder, System.IO.Path.GetFileName(path));
                    var onShare = File.Exists(target) ? MemoryEntryParser.Parse(File.ReadAllText(target, Encoding.UTF8), out _) : null;
                    if (entry != null && (onShare == null || onShare.Updated <= entry.Updated))
                    {
                        AtomicWrite(Folder, System.IO.Path.GetFileName(path), text);
                        copied++;
                    }
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors?.Add($"Replay of {System.IO.Path.GetFileName(path)} failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors?.Add("Replay failed: " + ex.Message);
        }
        return copied;
    }

    public int PendingCount()
    {
        try
        {
            if (MirrorFolder == null) return 0;
            var pending = System.IO.Path.Combine(MirrorFolder, PendingFolderName);
            return Directory.Exists(pending) ? Directory.GetFiles(pending, "*" + MemoryEntryParser.FileExtension).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    // ── Usage tallies: one file per user, only ever written by that user ────────────────────────────────

    /// <summary>The sum of every user's tallies, by entry id. Unreadable tally files are skipped.</summary>
    public Dictionary<string, MemoryCounters> ReadTallies()
    {
        var totals = new Dictionary<string, MemoryCounters>(StringComparer.OrdinalIgnoreCase);
        var offline = MirrorFolder != null && !IsReachable();
        var folder = System.IO.Path.Combine(ReadFolder(), CountersFolderName);
        try
        {
            if (Directory.Exists(folder))
                foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
                {
                    var text = SafeRead(path);
                    if (text == null) continue;
                    AddInto(totals, ParseTallies(text));
                    // Keep everyone's counts in the mirror, so the lifecycle stays right while the share is away.
                    if (!offline && MirrorFolder != null) MirrorTally(System.IO.Path.GetFileName(path), text);
                }
            if (offline)
            {
                var pending = System.IO.Path.Combine(MirrorFolder!, PendingCountersFolderName);
                if (Directory.Exists(pending))
                    foreach (var path in Directory.EnumerateFiles(pending, "*.json"))
                        AddInto(totals, ParseTallies(SafeRead(path) ?? string.Empty));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return totals;
    }

    /// <summary>Counts each id as shown once in the user's own tally file. Never throws; false when it could not write.</summary>
    public bool AddShown(string user, IEnumerable<string> ids, DateTimeOffset? at = null) =>
        AddTallies(user, ids.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id, _ => new MemoryCounters { Shown = 1, LastShown = at ?? DateTimeOffset.UtcNow }, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Adds counter deltas (shown, used, worked, contradicted, last used) to the user's own tally file — the only
    /// file this user writes for usage, so nobody else's counts can be lost. While the share is away the deltas wait
    /// in the mirror's <c>.pending-counters\</c> and are merged in by <see cref="ReplayPending"/>. Never throws;
    /// false when it could not write.
    /// </summary>
    public bool AddTallies(string user, IReadOnlyDictionary<string, MemoryCounters> deltas)
    {
        if (deltas.Count == 0) return true;
        var name = MemoryPolicy.Slug(user);
        if (name.Length == 0) name = "_unknown";
        var folder = MirrorFolder != null && !IsReachable()
            ? System.IO.Path.Combine(MirrorFolder, PendingCountersFolderName)
            : System.IO.Path.Combine(Folder, CountersFolderName);
        return MergeTallies(folder, name + ".json", deltas);
    }

    /// <summary>Read-modify-write of one tally file, serialised per file within this process (recall and the observer race).</summary>
    private static bool MergeTallies(string folder, string fileName, IReadOnlyDictionary<string, MemoryCounters> deltas)
    {
        var path = System.IO.Path.Combine(folder, fileName);
        lock (TallyLocks.GetOrAdd(System.IO.Path.GetFullPath(path), _ => new object()))
        {
            try
            {
                var tallies = File.Exists(path) ? ParseTallies(SafeRead(path) ?? string.Empty) : new Dictionary<string, MemoryCounters>(StringComparer.OrdinalIgnoreCase);
                foreach (var delta in deltas)
                {
                    if (!tallies.TryGetValue(delta.Key, out var c)) tallies[delta.Key] = c = new MemoryCounters();
                    c.Add(delta.Value);
                }
                var json = JsonConvert.SerializeObject(tallies.ToDictionary(
                    p => p.Key,
                    p => new TallyDto
                    {
                        Shown = p.Value.Shown,
                        Used = p.Value.Used,
                        Worked = p.Value.Worked,
                        Contradicted = p.Value.Contradicted,
                        LastUsed = p.Value.LastUsed,
                        LastShown = p.Value.LastShown
                    }));
                AtomicWrite(folder, fileName, json);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return false;
            }
        }
    }

    /// <summary>Merges tallies counted while the share was away into the users' tally files on the share.</summary>
    private void ReplayPendingTallies(List<string>? errors)
    {
        var pending = System.IO.Path.Combine(MirrorFolder!, PendingCountersFolderName);
        if (!Directory.Exists(pending)) return;
        foreach (var path in Directory.EnumerateFiles(pending, "*.json"))
        {
            var text = SafeRead(path);
            if (text == null) continue;
            if (MergeTallies(System.IO.Path.Combine(Folder, CountersFolderName), System.IO.Path.GetFileName(path), ParseTallies(text)))
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors?.Add("Replay of usage counts: " + ex.Message); }
            }
            else errors?.Add($"Replay of usage counts in {System.IO.Path.GetFileName(path)} failed.");
        }
    }

    private void MirrorTally(string name, string text)
    {
        try
        {
            var target = System.IO.Path.Combine(MirrorFolder!, CountersFolderName, name);
            if (File.Exists(target) && SafeRead(target) == text) return;
            AtomicWrite(System.IO.Path.Combine(MirrorFolder!, CountersFolderName), name, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void AddInto(Dictionary<string, MemoryCounters> totals, Dictionary<string, MemoryCounters> tallies)
    {
        foreach (var pair in tallies)
        {
            if (!totals.TryGetValue(pair.Key, out var sum)) totals[pair.Key] = sum = new MemoryCounters();
            sum.Add(pair.Value);
        }
    }

    private static string? SafeRead(string path)
    {
        try { return File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static Dictionary<string, MemoryCounters> ParseTallies(string text)
    {
        var result = new Dictionary<string, MemoryCounters>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var parsed = JsonConvert.DeserializeObject<Dictionary<string, TallyDto>>(text);
            if (parsed == null) return result;
            foreach (var pair in parsed)
            {
                if (pair.Value == null) continue;
                result[pair.Key] = new MemoryCounters
                {
                    Shown = Math.Max(0, pair.Value.Shown),
                    Used = Math.Max(0, pair.Value.Used),
                    Worked = Math.Max(0, pair.Value.Worked),
                    Contradicted = Math.Max(0, pair.Value.Contradicted),
                    LastUsed = pair.Value.LastUsed,
                    LastShown = pair.Value.LastShown
                };
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }

    private sealed class TallyDto
    {
        [JsonProperty("shown")] public int Shown { get; set; }
        [JsonProperty("used")] public int Used { get; set; }
        [JsonProperty("worked")] public int Worked { get; set; }
        [JsonProperty("contradicted")] public int Contradicted { get; set; }
        [JsonProperty("lastUsed", NullValueHandling = NullValueHandling.Ignore)] public DateTimeOffset? LastUsed { get; set; }
        [JsonProperty("lastShown", NullValueHandling = NullValueHandling.Ignore)] public DateTimeOffset? LastShown { get; set; }
    }

    // ── Files ────────────────────────────────────────────────────────────────────────────────────────────

    private void MirrorCopy(string name, string text)
    {
        if (MirrorFolder == null) return;
        try
        {
            var target = System.IO.Path.Combine(MirrorFolder, name);
            if (File.Exists(target) && File.ReadAllText(target, Encoding.UTF8) == text) return;
            AtomicWrite(MirrorFolder, name, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The mirror is a convenience; the share stays the source of truth.
        }
    }

    /// <summary>
    /// Writes to a temp file in the target folder, then renames it over the target. A share that refuses the rename
    /// for a moment (another machine reading the file) gets a few retries; a share without Replace support gets a copy.
    /// The temp file never stays behind.
    /// </summary>
    public static string AtomicWrite(string folder, string name, string text)
    {
        Directory.CreateDirectory(folder);
        var target = System.IO.Path.Combine(folder, name);
        var temp = System.IO.Path.Combine(folder, "." + name + "." + Guid.NewGuid().ToString("N") + TempExtension);
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(target)) File.Replace(temp, target, null);
                    else File.Move(temp, target);
                    return target;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 3 && File.Exists(temp))
                {
                    Thread.Sleep(50 * (attempt + 1));
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(temp))
                {
                    File.Copy(temp, target, true);
                    return target;
                }
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
