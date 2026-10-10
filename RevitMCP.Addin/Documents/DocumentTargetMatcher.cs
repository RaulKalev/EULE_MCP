namespace RevitMCP.Addin.Documents;

/// <summary>An open project document as seen by the matcher (no Revit types, unit-testable).</summary>
public sealed class OpenDocumentInfo
{
    public string Title { get; set; } = string.Empty;
    public string PathName { get; set; } = string.Empty;
    public string CentralPath { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

/// <summary>
/// Resolves the optional <c>document</c> tool argument (#90) — a title or a path — to one of the open
/// project documents. Pure; linked and family documents are filtered out before this runs.
/// Match order: full path (local or central), exact title, title without extension. A selector that
/// matches several documents is an error rather than a guess.
/// </summary>
public static class DocumentTargetMatcher
{
    public sealed class Result
    {
        public int Index { get; set; } = -1;
        public string? Error { get; set; }
        public bool Success => Error == null && Index >= 0;
    }

    public static Result Match(IReadOnlyList<OpenDocumentInfo> documents, string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            for (var i = 0; i < documents.Count; i++)
                if (documents[i].IsActive) return new Result { Index = i };
            return new Result { Error = "No active project document. Pass document=<title or path> (see revit_list_open_documents)." };
        }

        var wanted = selector!.Trim();
        var normalizedPath = NormalizePath(wanted);

        var byPath = Indexes(documents, d =>
            (d.PathName.Length > 0 && NormalizePath(d.PathName) == normalizedPath) ||
            (d.CentralPath.Length > 0 && NormalizePath(d.CentralPath) == normalizedPath));
        if (byPath.Count > 0) return Pick(documents, byPath, wanted);

        var byTitle = Indexes(documents, d => string.Equals(d.Title, wanted, StringComparison.OrdinalIgnoreCase));
        if (byTitle.Count > 0) return Pick(documents, byTitle, wanted);

        var stem = StripRevitExtension(wanted);
        var byStem = Indexes(documents, d =>
            string.Equals(StripRevitExtension(d.Title), stem, StringComparison.OrdinalIgnoreCase) ||
            (d.PathName.Length > 0 && string.Equals(StripRevitExtension(FileName(d.PathName)), stem, StringComparison.OrdinalIgnoreCase)));
        if (byStem.Count > 0) return Pick(documents, byStem, wanted);

        var titles = string.Join(", ", documents.Select(d => $"'{d.Title}'"));
        return new Result { Error = $"No open project document matches '{wanted}'. Open documents: {(titles.Length == 0 ? "none" : titles)}." };
    }

    private static Result Pick(IReadOnlyList<OpenDocumentInfo> documents, List<int> matches, string wanted)
    {
        if (matches.Count == 1) return new Result { Index = matches[0] };
        var names = string.Join(", ", matches.Select(i => $"'{documents[i].Title}' ({documents[i].PathName})"));
        return new Result { Error = $"'{wanted}' matches {matches.Count} open documents: {names}. Pass the full path instead." };
    }

    private static List<int> Indexes(IReadOnlyList<OpenDocumentInfo> documents, Func<OpenDocumentInfo, bool> predicate)
    {
        var result = new List<int>();
        for (var i = 0; i < documents.Count; i++)
            if (predicate(documents[i])) result.Add(i);
        return result;
    }

    public static string NormalizePath(string path) =>
        path.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();

    private static string FileName(string path)
    {
        var normalized = path.Replace('/', '\\');
        var slash = normalized.LastIndexOf('\\');
        return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
    }

    private static string StripRevitExtension(string name) =>
        name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
}
