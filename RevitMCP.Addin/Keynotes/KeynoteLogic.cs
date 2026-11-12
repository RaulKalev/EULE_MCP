namespace RevitMCP.Addin.Keynotes;

/// <summary>One keynote: key, text and the key of its parent (empty at the top level). Pure DTO.</summary>
public sealed class KeynoteEntryInfo
{
    public string Key { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string ParentKey { get; set; } = string.Empty;
}

/// <summary>A keynote whose text or parent differs between two tables.</summary>
public sealed class KeynoteChange
{
    public string Key { get; set; } = string.Empty;
    public string FromText { get; set; } = string.Empty;
    public string ToText { get; set; } = string.Empty;
    public string FromParent { get; set; } = string.Empty;
    public string ToParent { get; set; } = string.Empty;
}

public sealed class KeynoteDiff
{
    public List<KeynoteEntryInfo> Added { get; } = new();
    public List<KeynoteEntryInfo> Removed { get; } = new();
    public List<KeynoteChange> Changed { get; } = new();
    public int Unchanged { get; set; }
    public bool HasChanges => Added.Count + Removed.Count + Changed.Count > 0;
}

/// <summary>A keynote file parsed by the connector, for the preview.</summary>
public sealed class KeynoteFileParse
{
    public List<KeynoteEntryInfo> Entries { get; } = new();
    /// <summary>Lines the connector could not read, with their line number.</summary>
    public List<string> Problems { get; } = new();
}

/// <summary>Which elements carry one keynote key (their Keynote parameter).</summary>
public sealed class KeynoteUsage
{
    public string Key { get; set; } = string.Empty;
    public int Types { get; set; }
    public int Materials { get; set; }
    public int Elements { get; set; }
    public List<long> SampleIds { get; } = new();
    public int Total => Types + Materials + Elements;
}

/// <summary>
/// Pure keynote logic for revit_get_keynotes and revit_reload_keynote_table: search and paging, parsing a keynote
/// file (key TAB text TAB parent key), comparing two tables, counting usage. No Revit types; unit tested.
/// </summary>
public static class KeynoteLogic
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 1000;
    public const int MaxListedChanges = 50;
    public const int SampleIdsPerKey = 5;

    /// <summary>
    /// The entries matching <paramref name="search"/> (in the key or the text, any case) and <paramref name="parent"/>
    /// (direct children of that key; "" or "root" = the top level; null = any), in key order.
    /// </summary>
    public static List<KeynoteEntryInfo> Filter(IEnumerable<KeynoteEntryInfo> entries, string? search, string? parent)
    {
        var query = entries;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search!.Trim();
            query = query.Where(e => e.Key.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0
                                     || e.Text.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        if (parent != null)
        {
            var p = parent.Trim();
            if (p.Equals("root", StringComparison.OrdinalIgnoreCase)) p = string.Empty;
            query = query.Where(e => e.ParentKey.Equals(p, StringComparison.OrdinalIgnoreCase));
        }
        return query.OrderBy(e => e.Key, KeyComparer.Instance).ToList();
    }

    /// <summary>How many direct children each key has.</summary>
    public static Dictionary<string, int> ChildCounts(IEnumerable<KeynoteEntryInfo> entries) =>
        entries.Where(e => e.ParentKey.Length > 0)
            .GroupBy(e => e.ParentKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

    public static (int Offset, int Limit) Page(int offset, int limit) =>
        (Math.Max(0, offset), limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit));

    /// <summary>
    /// Reads a keynote file as Revit writes it: one entry per line, key TAB text, then TAB parent key for a child.
    /// Blank lines are skipped; so are lines starting with '#', which the connector treats as comments. A line
    /// without a TAB, an empty key or a key listed twice is a problem; a parent key the file does not define is one too.
    /// </summary>
    public static KeynoteFileParse ParseFile(string content)
    {
        var result = new KeynoteFileParse();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (i == 0 && line.Length > 0 && line[0] == '﻿') line = line.Substring(1);
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
            var fields = line.Split('\t');
            if (fields.Length < 2)
            {
                result.Problems.Add($"line {i + 1}: no TAB between key and text.");
                continue;
            }
            var key = fields[0].Trim();
            if (key.Length == 0)
            {
                result.Problems.Add($"line {i + 1}: the key is empty.");
                continue;
            }
            if (!seen.Add(key))
            {
                result.Problems.Add($"line {i + 1}: the key '{key}' is listed twice.");
                continue;
            }
            result.Entries.Add(new KeynoteEntryInfo
            {
                Key = key,
                Text = fields[1].Trim(),
                ParentKey = fields.Length > 2 ? fields[2].Trim() : string.Empty
            });
        }
        foreach (var e in result.Entries.Where(e => e.ParentKey.Length > 0 && !seen.Contains(e.ParentKey)))
            result.Problems.Add($"key '{e.Key}': its parent '{e.ParentKey}' is not in the file.");
        return result;
    }

    /// <summary>What changes from <paramref name="before"/> to <paramref name="after"/>, by key (case-sensitive, as Revit keys are).</summary>
    public static KeynoteDiff Compare(IEnumerable<KeynoteEntryInfo> before, IEnumerable<KeynoteEntryInfo> after)
    {
        var diff = new KeynoteDiff();
        var old = new Dictionary<string, KeynoteEntryInfo>(StringComparer.Ordinal);
        foreach (var e in before) old[e.Key] = e;
        var now = new Dictionary<string, KeynoteEntryInfo>(StringComparer.Ordinal);
        foreach (var e in after) now[e.Key] = e;

        foreach (var e in now.Values.OrderBy(e => e.Key, KeyComparer.Instance))
        {
            if (!old.TryGetValue(e.Key, out var was)) { diff.Added.Add(e); continue; }
            if (!string.Equals(was.Text, e.Text, StringComparison.Ordinal) || !string.Equals(was.ParentKey, e.ParentKey, StringComparison.Ordinal))
                diff.Changed.Add(new KeynoteChange { Key = e.Key, FromText = was.Text, ToText = e.Text, FromParent = was.ParentKey, ToParent = e.ParentKey });
            else diff.Unchanged++;
        }
        foreach (var e in old.Values.Where(e => !now.ContainsKey(e.Key)).OrderBy(e => e.Key, KeyComparer.Instance))
            diff.Removed.Add(e);
        return diff;
    }

    /// <summary>The diff as a compact object: counts and at most <see cref="MaxListedChanges"/> of each list.</summary>
    public static object Describe(KeynoteDiff diff) => new
    {
        added = diff.Added.Count,
        removed = diff.Removed.Count,
        changed = diff.Changed.Count,
        unchanged = diff.Unchanged,
        addedKeys = diff.Added.Take(MaxListedChanges).Select(e => new { key = e.Key, text = e.Text, parent = NullIfEmpty(e.ParentKey) }).ToList(),
        removedKeys = diff.Removed.Take(MaxListedChanges).Select(e => new { key = e.Key, text = e.Text }).ToList(),
        changedKeys = diff.Changed.Take(MaxListedChanges).Select(c => new
        {
            key = c.Key,
            fromText = c.FromText,
            toText = c.ToText,
            fromParent = c.FromParent == c.ToParent ? null : c.FromParent,
            toParent = c.FromParent == c.ToParent ? null : c.ToParent
        }).ToList(),
        truncated = diff.Added.Count > MaxListedChanges || diff.Removed.Count > MaxListedChanges || diff.Changed.Count > MaxListedChanges
    };

    /// <summary>
    /// Counts the carriers of each key. <paramref name="carriers"/>: (key value, kind "type" | "material" | "element", id).
    /// Keys used in the model but not in <paramref name="tableKeys"/> come back in <paramref name="unknown"/>.
    /// </summary>
    public static Dictionary<string, KeynoteUsage> CountUsage(IEnumerable<(string Key, string Kind, long Id)> carriers,
        IEnumerable<string> tableKeys, out List<KeynoteUsage> unknown)
    {
        var known = new HashSet<string>(tableKeys, StringComparer.Ordinal);
        var all = new Dictionary<string, KeynoteUsage>(StringComparer.Ordinal);
        foreach (var (rawKey, kind, id) in carriers)
        {
            var key = (rawKey ?? string.Empty).Trim();
            if (key.Length == 0) continue;
            if (!all.TryGetValue(key, out var usage)) all[key] = usage = new KeynoteUsage { Key = key };
            switch (kind)
            {
                case "type": usage.Types++; break;
                case "material": usage.Materials++; break;
                default: usage.Elements++; break;
            }
            if (usage.SampleIds.Count < SampleIdsPerKey) usage.SampleIds.Add(id);
        }
        unknown = all.Values.Where(u => !known.Contains(u.Key)).OrderBy(u => u.Key, KeyComparer.Instance).ToList();
        return all;
    }

    public static string ApprovalSummary(string document, string? newPath, string? currentPath)
    {
        var where = document.Length > 0 ? $"'{document}'" : "the active document";
        var from = !string.IsNullOrWhiteSpace(newPath)
            ? $"from '{newPath}'{(string.IsNullOrWhiteSpace(currentPath) ? string.Empty : $" (it now points to '{currentPath}')")}"
            : string.IsNullOrWhiteSpace(currentPath) ? "from its current file" : $"from its current file '{currentPath}'";
        return $"Reload the keynote table of {where} {from}. Keynote tags then show the file's texts; keys the file no longer " +
               "has leave their tags without text. One transaction, one undo.";
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    /// <summary>Orders keys so that "A.2" comes before "A.10": digit runs compare as numbers.</summary>
    public sealed class KeyComparer : IComparer<string>
    {
        public static readonly KeyComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= string.Empty;
            y ??= string.Empty;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    var si = i; while (i < x.Length && char.IsDigit(x[i])) i++;
                    var sj = j; while (j < y.Length && char.IsDigit(y[j])) j++;
                    var a = x.Substring(si, i - si).TrimStart('0');
                    var b = y.Substring(sj, j - sj).TrimStart('0');
                    if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                    var c = string.CompareOrdinal(a, b);
                    if (c != 0) return c;
                }
                else
                {
                    var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            var rest = (x.Length - i).CompareTo(y.Length - j);
            return rest != 0 ? rest : string.CompareOrdinal(x, y);
        }
    }
}
