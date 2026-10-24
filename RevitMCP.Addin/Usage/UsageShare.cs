using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace RevitMCP.Addin.Usage;

/// <summary>
/// The shared copy of the activity log (docs/usage-reporting.md, "Additions"): every line the logger writes locally is
/// appended a second time to <c>&lt;sharedFolder&gt;\&lt;user&gt;\&lt;yyyy-MM-dd&gt;.&lt;machine&gt;.jsonl</c>. One file per
/// user, day and machine, append-only, written only by that machine — no write conflicts, no sync step (the machine
/// suffix keeps one person working on two workstations from appending to the same file over SMB).
///
/// Best-effort: a failed append never reaches the tool call; the day is remembered in
/// <c>.usage-pending.json</c> next to the local logs, and <see cref="CatchUp"/> — at the next start, or after the next
/// append that succeeds — copies the local day file over the shared one, which makes it complete again. Pure — no
/// Revit API.
/// </summary>
public sealed class UsageShare
{
    public const string PendingFileName = ".usage-pending.json";
    public const string Extension = ".jsonl";

    private readonly string _localLogDir;
    private readonly object _gate = new();
    private HashSet<string>? _pending;

    public UsageShare(string localLogDir) => _localLogDir = localLogDir;

    /// <summary>The shared file of one user, day and machine.</summary>
    public static string SharedPath(string sharedFolder, string user, string machine, DateTime day) =>
        Path.Combine(sharedFolder, Segment(user, "_unknown"), day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "." + Segment(machine, "_machine") + Extension);

    /// <summary>A user or machine name as one safe path segment.</summary>
    public static string Segment(string? value, string fallback)
    {
        var sb = new StringBuilder();
        foreach (var c in (value ?? string.Empty).Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        var s = sb.ToString().Trim('.', ' ');
        return s.Length == 0 ? fallback : s.Length > 64 ? s.Substring(0, 64) : s;
    }

    /// <summary>
    /// Appends one line. Returns false (and remembers the day for the catch-up) when the share refused it; never throws.
    /// After a success, days left over from an earlier failure are caught up.
    /// </summary>
    public bool Append(string sharedFolder, string user, string machine, DateTime day, string line)
    {
        try
        {
            var path = SharedPath(sharedFolder, user, machine, day);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            MarkPending(day);
            return false;
        }
        if (HasPending()) CatchUp(sharedFolder, user, machine);
        return true;
    }

    /// <summary>
    /// Copies the local log of every day whose append failed over the shared file (the local file holds every line of
    /// that day). Returns the number of days caught up; never throws.
    /// </summary>
    public int CatchUp(string sharedFolder, string user, string machine)
    {
        var done = 0;
        foreach (var key in PendingDays())
        {
            if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) { Unmark(key); continue; }
            var local = Path.Combine(_localLogDir, key + Extension);
            try
            {
                if (File.Exists(local))
                {
                    var target = SharedPath(sharedFolder, user, machine, day);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.Copy(local, temp, true);
                    if (File.Exists(target)) File.Replace(temp, target, null);
                    else File.Move(temp, target);
                }
                Unmark(key);
                done++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Still unreachable: try again next time.
            }
        }
        return done;
    }

    public IReadOnlyList<string> PendingDays()
    {
        lock (_gate) return Load().OrderBy(d => d, StringComparer.Ordinal).ToList();
    }

    private bool HasPending()
    {
        lock (_gate) return Load().Count > 0;
    }

    private void MarkPending(DateTime day)
    {
        lock (_gate)
        {
            if (Load().Add(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))) Save();
        }
    }

    private void Unmark(string key)
    {
        lock (_gate)
        {
            if (Load().Remove(key)) Save();
        }
    }

    private HashSet<string> Load()
    {
        if (_pending != null) return _pending;
        _pending = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(_localLogDir, PendingFileName);
            if (File.Exists(path))
                foreach (var d in JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path, Encoding.UTF8)) ?? new List<string>())
                    _pending.Add(d);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return _pending;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_localLogDir);
            File.WriteAllText(Path.Combine(_localLogDir, PendingFileName), JsonConvert.SerializeObject(_pending!.OrderBy(d => d).ToList()), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
