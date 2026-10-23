using System.Globalization;
using System.Text;

namespace RevitMCP.Addin.Memory;

/// <summary>
/// Reads and writes the entry file format: YAML-style front matter between <c>---</c> lines, then
/// <c>value:</c> and <c>reason:</c> as text. Only the subset the store writes is understood (scalars,
/// double-quoted strings, inline <c>[a, b]</c> lists and the inline <c>counters</c> map), so files edited by
/// hand stay readable as long as they keep that shape. Unknown keys are ignored. Pure — no Revit API.
/// </summary>
public static class MemoryEntryParser
{
    public const string FileExtension = ".md";
    private const string Fence = "---";

    public static string Serialize(MemoryEntry e)
    {
        var sb = new StringBuilder();
        sb.Append(Fence).Append('\n');
        Line(sb, "id", e.Id);
        Line(sb, "key", Quote(e.Key));
        Line(sb, "scope", e.Scope);
        Line(sb, "project", QuoteOrNull(e.Project));
        Line(sb, "stage", QuoteOrNull(e.Stage));
        Line(sb, "model", QuoteOrNull(e.Model));
        Line(sb, "modelVersion", QuoteOrNull(e.ModelVersion));
        Line(sb, "source", e.Source);
        Line(sb, "author", Quote(e.Author));
        Line(sb, "authors", List(e.Authors));
        Line(sb, "client", QuoteOrNull(e.Client));
        Line(sb, "created", Date(e.Created));
        Line(sb, "updated", Date(e.Updated));
        Line(sb, "state", e.State);
        Line(sb, "supersedes", string.IsNullOrEmpty(e.Supersedes) ? "null" : e.Supersedes!);
        Line(sb, "tags", List(e.Tags));
        Line(sb, "flags", List(e.Flags));
        var c = e.Counters;
        Line(sb, "counters", string.Format(CultureInfo.InvariantCulture,
            "{{ shown: {0}, used: {1}, worked: {2}, contradicted: {3}, remembered: {4} }}",
            c.Shown, c.Used, c.Worked, c.Contradicted, c.Remembered));
        // Written only when set, so plain notes keep the short shape of the design document.
        if (e.Requires.Count > 0) Line(sb, "requires", List(e.Requires));
        if (e.Match != null) Line(sb, "match", List(new[] { e.Match.Tool, e.Match.Argument, e.Match.Value }));
        if (!string.IsNullOrEmpty(e.Connector)) Line(sb, "connector", Quote(e.Connector!));
        sb.Append(Fence).Append('\n');
        sb.Append("value: ").Append(OneLine(e.Value)).Append('\n');
        sb.Append("reason: ").Append(OneLine(e.Reason)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Parses an entry file. Returns null with <paramref name="error"/> when it is not a valid entry.</summary>
    public static MemoryEntry? Parse(string text, out string? error)
    {
        error = null;
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;
        while (i < lines.Length && lines[i].Trim().Length == 0) i++;
        if (i >= lines.Length || lines[i].Trim() != Fence)
        {
            error = "The file does not start with front matter (---).";
            return null;
        }
        i++;

        var e = new MemoryEntry();
        var closed = false;
        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim() == Fence) { closed = true; i++; break; }
            var colon = line.IndexOf(':');
            if (colon <= 0 || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
            var name = line.Substring(0, colon).Trim();
            var raw = StripComment(line.Substring(colon + 1)).Trim();
            Apply(e, name, raw);
        }
        if (!closed)
        {
            error = "The front matter is not closed (---).";
            return null;
        }

        string? field = null;
        var value = new StringBuilder();
        var reason = new StringBuilder();
        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("value:", StringComparison.Ordinal)) { field = "value"; Append(value, line.Substring(6)); }
            else if (line.StartsWith("reason:", StringComparison.Ordinal)) { field = "reason"; Append(reason, line.Substring(7)); }
            else if (field != null && line.Length > 0 && char.IsWhiteSpace(line[0])) Append(field == "value" ? value : reason, line);
            else if (line.Trim().Length > 0) field = null;
        }
        e.Value = value.ToString();
        e.Reason = reason.ToString();

        if (string.IsNullOrWhiteSpace(e.Id)) error = "The entry has no id.";
        else if (string.IsNullOrWhiteSpace(e.Key)) error = "The entry has no key.";
        else if (string.IsNullOrWhiteSpace(e.Value)) error = "The entry has no value.";
        return error == null ? e : null;
    }

    private static void Apply(MemoryEntry e, string name, string raw)
    {
        switch (name)
        {
            case "id": e.Id = Scalar(raw) ?? string.Empty; break;
            case "key": e.Key = Scalar(raw) ?? string.Empty; break;
            case "scope": e.Scope = (Scalar(raw) ?? MemoryScopes.Project).ToLowerInvariant(); break;
            case "project": e.Project = Scalar(raw); break;
            case "stage": e.Stage = Scalar(raw); break;
            case "model": e.Model = Scalar(raw); break;
            case "modelVersion": e.ModelVersion = Scalar(raw); break;
            case "source": e.Source = (Scalar(raw) ?? MemorySources.Agent).ToLowerInvariant(); break;
            case "author": e.Author = Scalar(raw) ?? string.Empty; break;
            case "authors": e.Authors = ParseList(raw); break;
            case "client": e.Client = Scalar(raw); break;
            case "created": e.Created = ParseDate(raw); break;
            case "updated": e.Updated = ParseDate(raw); break;
            case "state":
                var state = (Scalar(raw) ?? MemoryStates.Candidate).ToLowerInvariant();
                e.State = MemoryStates.IsKnown(state) ? state : MemoryStates.Candidate;
                break;
            case "supersedes": e.Supersedes = Scalar(raw); break;
            case "tags": e.Tags = ParseList(raw); break;
            case "flags": e.Flags = ParseList(raw); break;
            case "counters": e.Counters = ParseCounters(raw); break;
            case "requires": e.Requires = ParseList(raw); break;
            case "match":
                var m = ParseList(raw);
                e.Match = m.Count == 3 ? new MemoryMatch { Tool = m[0], Argument = m[1], Value = m[2] } : null;
                break;
            case "connector": e.Connector = Scalar(raw); break;
        }
    }

    private static void Line(StringBuilder sb, string name, string value) => sb.Append(name).Append(": ").Append(value).Append('\n');

    private static string Date(DateTimeOffset d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string raw)
    {
        var s = Scalar(raw);
        return s != null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d
            : DateTimeOffset.MinValue;
    }

    /// <summary>Text is stored on one line: line breaks become spaces, runs of whitespace one space.</summary>
    public static string OneLine(string? text)
    {
        var sb = new StringBuilder();
        var space = false;
        foreach (var c in (text ?? string.Empty).Trim())
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return;
        if (sb.Length > 0) sb.Append(' ');
        sb.Append(t);
    }

    private static string QuoteOrNull(string? s) => s == null ? "null" : Quote(s);

    /// <summary>Double-quoted with JSON-style escapes, so any text survives the round trip.</summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Quote)) + "]";

    /// <summary>A scalar: a quoted string, <c>null</c>/<c>~</c>/empty as null, anything else as written.</summary>
    private static string? Scalar(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0 || s == "null" || s == "~") return null;
        if (s[0] == '"')
        {
            var pos = 0;
            return ReadQuoted(s, ref pos);
        }
        if (s[0] == '\'' && s.Length >= 2 && s[s.Length - 1] == '\'') return s.Substring(1, s.Length - 2).Replace("''", "'");
        return s;
    }

    private static string ReadQuoted(string s, ref int pos)
    {
        var sb = new StringBuilder();
        pos++; // opening quote
        while (pos < s.Length)
        {
            var c = s[pos++];
            if (c == '"') break;
            if (c != '\\' || pos >= s.Length) { sb.Append(c); continue; }
            var esc = s[pos++];
            switch (esc)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u' when pos + 4 <= s.Length &&
                              int.TryParse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    sb.Append((char)code);
                    pos += 4;
                    break;
                default: sb.Append(esc); break;
            }
        }
        return sb.ToString();
    }

    private static List<string> ParseList(string raw)
    {
        var list = new List<string>();
        var s = raw.Trim();
        if (s.Length < 2 || s[0] != '[' || s[s.Length - 1] != ']') return list;
        s = s.Substring(1, s.Length - 2);
        var pos = 0;
        while (pos < s.Length)
        {
            while (pos < s.Length && (char.IsWhiteSpace(s[pos]) || s[pos] == ',')) pos++;
            if (pos >= s.Length) break;
            string item;
            if (s[pos] == '"') item = ReadQuoted(s, ref pos);
            else
            {
                var end = s.IndexOf(',', pos);
                if (end < 0) end = s.Length;
                item = s.Substring(pos, end - pos).Trim();
                pos = end;
            }
            if (item.Length > 0) list.Add(item);
        }
        return list;
    }

    private static MemoryCounters ParseCounters(string raw)
    {
        var counters = new MemoryCounters();
        var s = raw.Trim().TrimStart('{').TrimEnd('}');
        foreach (var part in s.Split(','))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0) continue;
            if (!int.TryParse(part.Substring(colon + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0) continue;
            switch (part.Substring(0, colon).Trim())
            {
                case "shown": counters.Shown = n; break;
                case "used": counters.Used = n; break;
                case "worked": counters.Worked = n; break;
                case "contradicted": counters.Contradicted = n; break;
                case "remembered": counters.Remembered = n; break;
            }
        }
        return counters;
    }

    /// <summary>Drops a trailing <c># comment</c> that is outside double quotes.</summary>
    private static string StripComment(string s)
    {
        var inQuotes = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && inQuotes) { i++; continue; }
            if (c == '"') inQuotes = !inQuotes;
            else if (c == '#' && !inQuotes && (i == 0 || char.IsWhiteSpace(s[i - 1]))) return s.Substring(0, i);
        }
        return s;
    }
}
