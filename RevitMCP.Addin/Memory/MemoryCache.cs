using System.IO;

namespace RevitMCP.Addin.Memory;

/// <summary>A consistent view of one tier at the moment it was read.</summary>
public sealed class MemorySnapshot
{
    public string Folder { get; set; } = string.Empty;

    /// <summary>Read from the local mirror because the share was unreachable.</summary>
    public bool FromMirror { get; set; }
    public List<MemoryEntry> Entries { get; set; } = new();

    /// <summary>Each entry exactly as its file says (by id): no usage tallies, stored state. What a rewrite starts from.</summary>
    public Dictionary<string, MemoryEntry> FileEntries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// The hot in-memory copy of one tier folder. A <see cref="FileSystemWatcher"/> marks it dirty, and every read also
/// compares the folder's last-write time (watchers miss events on network shares), so an entry written on another
/// machine shows up on the next read with no index and no rebuild. Only files that changed are parsed again.
/// Usage tallies are summed in on every read. Thread-safe. Pure — no Revit API.
/// </summary>
public sealed class MemoryCache : IDisposable
{
    private readonly object _gate = new();
    private readonly bool _watch;
    private Dictionary<string, StoredMemoryEntry> _files = new(StringComparer.OrdinalIgnoreCase);
    private string _readFolder = string.Empty;
    private DateTime _stamp = DateTime.MinValue;
    private bool _dirty = true;
    private List<string> _errors = new();
    private FileSystemWatcher? _watcher;

    public MemoryCache(MemoryStore store, bool watch = true)
    {
        Store = store;
        _watch = watch;
    }

    public MemoryStore Store { get; }

    /// <summary>Forces the next read to look at the folder again (after this process wrote to it).</summary>
    public void Invalidate()
    {
        lock (_gate) _dirty = true;
    }

    public MemorySnapshot Read()
    {
        lock (_gate)
        {
            var replayErrors = new List<string>();
            if (Store.ReplayPending(replayErrors) > 0) _dirty = true;

            var folder = Store.ReadFolder();
            var stamp = Store.FolderStamp();
            if (!string.Equals(folder, _readFolder, StringComparison.OrdinalIgnoreCase))
            {
                _readFolder = folder;
                _files = new Dictionary<string, StoredMemoryEntry>(StringComparer.OrdinalIgnoreCase);
                _dirty = true;
                EnsureWatcher(folder);
            }
            else if (_watcher == null)
            {
                EnsureWatcher(folder);
            }

            if (_dirty || stamp != _stamp)
            {
                var errors = new List<string>();
                var read = Store.ReadAll(folder, _files, errors);
                _files = read.ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);
                _errors = errors;
                _stamp = stamp;
                _dirty = false;
            }

            var tallies = Store.ReadTallies();
            var entries = new List<MemoryEntry>(_files.Count);
            var fileEntries = new Dictionary<string, MemoryEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var stored in _files.Values)
            {
                var entry = stored.Entry.Clone();
                fileEntries[entry.Id] = stored.Entry.Clone();
                if (tallies.TryGetValue(entry.Id, out var extra)) entry.Counters.Add(extra);
                entries.Add(entry);
            }

            return new MemorySnapshot
            {
                Folder = folder,
                FromMirror = Store.MirrorFolder != null && string.Equals(folder, Store.MirrorFolder, StringComparison.OrdinalIgnoreCase),
                Entries = entries,
                FileEntries = fileEntries,
                Errors = _errors.Concat(replayErrors).ToList()
            };
        }
    }

    private void EnsureWatcher(string folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (!_watch || !Directory.Exists(folder)) return;
        try
        {
            var watcher = new FileSystemWatcher(folder, "*" + MemoryEntryParser.FileExtension)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            FileSystemEventHandler onChange = (_, _) => Invalidate();
            watcher.Changed += onChange;
            watcher.Created += onChange;
            watcher.Deleted += onChange;
            watcher.Renamed += (_, _) => Invalidate();
            watcher.Error += (_, _) => Invalidate();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // The last-write comparison on every read still keeps the copy fresh.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }
}
