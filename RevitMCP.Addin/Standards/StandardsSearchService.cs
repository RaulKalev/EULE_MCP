using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Standards;

/// <summary>
/// Keyword-based search over indexed <see cref="StandardsChunk"/> objects.
/// Works fully offline — no embeddings or cloud calls.
///
/// Scoring:
///   +3  per query token found in heading
///   +1  per query token found in text body
///   ×1.5 boost if chunk file is in a source whose discipline matches a discipline hint
///   Results sorted by total score descending.
/// </summary>
public class StandardsSearchService
{
    private readonly StandardsIndexer _indexer;
    private readonly StandardsSourceService _sourceService;

    public StandardsSearchService(StandardsIndexer indexer, StandardsSourceService sourceService)
    {
        _indexer       = indexer;
        _sourceService = sourceService;
    }

    /// <summary>
    /// Searches all indexed sources (or a filtered subset) for the given query.
    /// </summary>
    /// <param name="query">Free-text search query.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="sourceIds">Restrict search to specific source IDs; null = all.</param>
    /// <param name="disciplineHint">Boost results from sources matching this discipline.</param>
    public List<StandardsSearchResult> Search(
        string query,
        int maxResults = 10,
        IEnumerable<string>? sourceIds = null,
        string? disciplineHint = null)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<StandardsSearchResult>();

        var tokens = Tokenize(query);
        if (tokens.Count == 0) return new List<StandardsSearchResult>();

        var chunks  = _indexer.LoadAllChunks(sourceIds);
        var sources = _sourceService.Load().Sources.ToDictionary(s => s.Id);

        var scored = new List<(double Score, StandardsChunk Chunk, StandardsSource? Source)>();

        foreach (var chunk in chunks)
        {
            sources.TryGetValue(chunk.SourceId, out var source);

            var score = ScoreChunk(chunk, tokens, disciplineHint, source);
            if (score > 0)
                scored.Add((score, chunk, source));
        }

        var results = scored
            .OrderByDescending(x => x.Score)
            .Take(maxResults)
            .Select((x, i) => ToResult(x.Chunk, x.Source, x.Score, i + 1, query, tokens))
            .ToList();

        return results;
    }

    // -------------------------------------------------------------------------

    private static double ScoreChunk(StandardsChunk chunk, List<string> tokens, string? disciplineHint, StandardsSource? source)
    {
        var headingText = (chunk.Heading ?? string.Empty).ToLowerInvariant();
        var bodyText    = chunk.Text.ToLowerInvariant();
        double score = 0;

        foreach (var token in tokens)
        {
            if (headingText.Contains(token)) score += 3;
            if (bodyText.Contains(token))    score += 1;
        }

        if (score == 0) return 0;

        // Discipline boost
        if (!string.IsNullOrWhiteSpace(disciplineHint) &&
            !string.IsNullOrWhiteSpace(source?.Discipline) &&
            source!.Discipline!.Equals(disciplineHint, StringComparison.OrdinalIgnoreCase))
        {
            score *= 1.5;
        }

        return score;
    }

    private static StandardsSearchResult ToResult(
        StandardsChunk chunk,
        StandardsSource? source,
        double score,
        int rank,
        string query,
        List<string> tokens)
    {
        return new StandardsSearchResult
        {
            Rank             = rank,
            Score            = Math.Round(score, 2),
            SourceId         = chunk.SourceId,
            SourceName       = source?.Name ?? chunk.SourceId,
            FilePath         = chunk.FilePath,
            RelPath          = chunk.RelPath,
            Heading          = chunk.Heading,
            Snippet          = BuildSnippet(chunk.Text, tokens, maxLength: 400),
            FileLastModified = chunk.FileLastModified
        };
    }

    private static string BuildSnippet(string text, List<string> tokens, int maxLength)
    {
        if (text.Length <= maxLength) return text.Trim();

        // Try to start snippet near first token occurrence
        var lower = text.ToLowerInvariant();
        var bestPos = text.Length;
        foreach (var token in tokens)
        {
            var pos = lower.IndexOf(token, StringComparison.Ordinal);
            if (pos >= 0 && pos < bestPos) bestPos = pos;
        }

        var start = Math.Max(0, bestPos - 80);
        var snippet = text.Substring(start, Math.Min(maxLength, text.Length - start)).Trim();
        if (start > 0) snippet = "…" + snippet;
        if (start + maxLength < text.Length) snippet += "…";
        return snippet;
    }

    /// <summary>
    /// Tokenizes query: lowercase, strip punctuation, split on whitespace.
    /// Also expands common Estonian-English abbreviations/synonyms.
    /// </summary>
    internal static List<string> Tokenize(string query)
    {
        var clean  = Regex.Replace(query.ToLowerInvariant(), @"[^\w\s\-]", " ");
        var tokens = clean.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                          .Where(t => t.Length >= 2)
                          .ToList();

        // Expand abbreviations
        var expanded = new List<string>(tokens);
        foreach (var t in tokens)
        {
            switch (t)
            {
                case "e":   case "el":  case "elec": expanded.Add("electrical"); break;
                case "mec": case "mech":              expanded.Add("mechanical"); break;
                case "str": case "struct":            expanded.Add("structural");  break;
                case "hvac":                          expanded.Add("mechanical"); break;
                case "pipe": case "pip":              expanded.Add("piping");      break;
            }
        }

        return expanded.Distinct().ToList();
    }
}
