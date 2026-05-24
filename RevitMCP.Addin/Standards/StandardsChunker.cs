using System.IO;
using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Standards;

/// <summary>
/// Splits a text document into searchable <see cref="StandardsChunk"/> objects.
/// Supported formats: Markdown (.md), plain text (.txt), JSON (.json).
/// Chunks are created by heading boundaries (Markdown) or paragraph boundaries (plain text).
/// JSON files are treated as a single chunk per top-level property or root object.
/// </summary>
public static class StandardsChunker
{
    private const int MaxChunkLength = 1200;
    private const int MinChunkLength = 40;

    private static readonly Regex _mdHeading =
        new(@"^#{1,3}\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Reads a file and returns chunks.  Returns empty list on unrecognised format or read error.
    /// </summary>
    public static List<StandardsChunk> ChunkFile(string filePath, string sourceId, string? rootPath = null)
    {
        var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        string text;
        try { text = File.ReadAllText(filePath, System.Text.Encoding.UTF8); }
        catch { return new List<StandardsChunk>(); }

        var modified = File.GetLastWriteTimeUtc(filePath);
        var relPath  = rootPath != null && filePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
            ? filePath[rootPath.Length..].TrimStart('\\', '/')
            : System.IO.Path.GetFileName(filePath);

        return ext switch
        {
            ".md"  => ChunkMarkdown(text, filePath, relPath, sourceId, modified),
            ".txt" => ChunkPlainText(text, filePath, relPath, sourceId, modified),
            ".json" => ChunkJson(text, filePath, relPath, sourceId, modified),
            _       => new List<StandardsChunk>()
        };
    }

    private static List<StandardsChunk> ChunkMarkdown(string text, string filePath, string relPath, string sourceId, DateTime modified)
    {
        var chunks = new List<StandardsChunk>();
        var matches = _mdHeading.Matches(text);

        if (matches.Count == 0)
        {
            // No headings — treat as single chunk
            return SingleChunk(text, null, filePath, relPath, sourceId, modified);
        }

        // Add preamble before first heading (if any)
        if (matches[0].Index > MinChunkLength)
        {
            var preamble = text[..matches[0].Index].Trim();
            if (preamble.Length >= MinChunkLength)
                chunks.Add(MakeChunk(preamble, null, filePath, relPath, sourceId, modified, chunks.Count));
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var heading = matches[i].Groups[1].Value.Trim();
            var start   = matches[i].Index + matches[i].Length;
            var end     = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var body    = text[start..end].Trim();

            if (body.Length < MinChunkLength && i + 1 < matches.Count)
            {
                // Too short — merge with heading text
                body = heading + "\n" + body;
            }

            foreach (var subChunk in SplitLong(body, heading, filePath, relPath, sourceId, modified, chunks.Count))
                chunks.Add(subChunk);
        }

        return chunks;
    }

    private static List<StandardsChunk> ChunkPlainText(string text, string filePath, string relPath, string sourceId, DateTime modified)
    {
        // Split on blank lines (paragraphs)
        var paragraphs = Regex.Split(text, @"\n\s*\n")
            .Select(p => p.Trim())
            .Where(p => p.Length >= MinChunkLength)
            .ToList();

        if (paragraphs.Count == 0)
            return SingleChunk(text, null, filePath, relPath, sourceId, modified);

        var chunks = new List<StandardsChunk>();
        var buffer = string.Empty;

        foreach (var para in paragraphs)
        {
            if ((buffer + "\n\n" + para).Length > MaxChunkLength && buffer.Length > 0)
            {
                chunks.Add(MakeChunk(buffer, null, filePath, relPath, sourceId, modified, chunks.Count));
                buffer = para;
            }
            else
            {
                buffer = buffer.Length == 0 ? para : buffer + "\n\n" + para;
            }
        }
        if (buffer.Length >= MinChunkLength)
            chunks.Add(MakeChunk(buffer, null, filePath, relPath, sourceId, modified, chunks.Count));

        return chunks;
    }

    private static List<StandardsChunk> ChunkJson(string text, string filePath, string relPath, string sourceId, DateTime modified)
    {
        // Treat entire JSON as a single chunk (good for config/lookup tables ≤ MaxChunkLength)
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return new List<StandardsChunk>();
        return SingleChunk(trimmed.Length > MaxChunkLength * 3 ? trimmed[..(MaxChunkLength * 3)] : trimmed,
            null, filePath, relPath, sourceId, modified);
    }

    private static List<StandardsChunk> SingleChunk(string text, string? heading, string filePath, string relPath, string sourceId, DateTime modified)
    {
        var body = text.Trim();
        if (body.Length < MinChunkLength) return new List<StandardsChunk>();
        return new List<StandardsChunk> { MakeChunk(body, heading, filePath, relPath, sourceId, modified, 0) };
    }

    private static IEnumerable<StandardsChunk> SplitLong(string body, string? heading, string filePath, string relPath, string sourceId, DateTime modified, int startIdx)
    {
        if (body.Length <= MaxChunkLength)
        {
            yield return MakeChunk(body, heading, filePath, relPath, sourceId, modified, startIdx);
            yield break;
        }

        var parts = SplitAtSentences(body, MaxChunkLength);
        foreach (var part in parts)
        {
            if (part.Trim().Length >= MinChunkLength)
                yield return MakeChunk(part.Trim(), heading, filePath, relPath, sourceId, modified, startIdx++);
        }
    }

    private static List<string> SplitAtSentences(string text, int maxLen)
    {
        var results = new List<string>();
        var start   = 0;
        while (start < text.Length)
        {
            if (start + maxLen >= text.Length)
            {
                results.Add(text[start..]);
                break;
            }
            // Find last sentence boundary within maxLen window
            var window = text.Substring(start, maxLen);
            var lastDot = window.LastIndexOfAny(new[] { '.', '!', '?', '\n' });
            var cut = lastDot > 0 ? start + lastDot + 1 : start + maxLen;
            results.Add(text[start..cut].Trim());
            start = cut;
        }
        return results;
    }

    private static StandardsChunk MakeChunk(string text, string? heading, string filePath, string relPath, string sourceId, DateTime modified, int idx)
    {
        var chunkId = $"{sourceId}::{relPath}::{idx}";
        return new StandardsChunk
        {
            ChunkId          = chunkId,
            SourceId         = sourceId,
            FilePath         = filePath,
            RelPath          = relPath,
            Heading          = heading,
            Text             = text,
            FileLastModified = modified,
            IndexedAt        = DateTime.UtcNow
        };
    }
}
