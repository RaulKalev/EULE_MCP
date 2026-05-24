using System.IO;
using Newtonsoft.Json;

namespace RevitMCP.Addin.Standards;

/// <summary>
/// Indexes standards sources into a local JSON cache under
/// %AppData%\RKTools\RevitMCP\StandardsIndex.
/// Each source gets its own subdirectory with chunks.json + meta.json.
/// Skips re-indexing files whose last-modified timestamp hasn't changed.
/// </summary>
public class StandardsIndexer
{
    private static readonly string IndexBase = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RKTools", "RevitMCP", "StandardsIndex");

    private static readonly string[] DefaultFilePatterns = ["*.md", "*.txt", "*.json"];

    private static readonly JsonSerializerSettings _json = new() { Formatting = Formatting.None };

    // -------------------------------------------------------------------------

    /// <summary>
    /// (Re-)indexes a single source, skipping unchanged files unless <paramref name="force"/> is true.
    /// Returns statistics.
    /// </summary>
    public StandardsIndexResult IndexSource(StandardsSource source, bool force = false)
    {
        var result = new StandardsIndexResult { SourceId = source.Id };

        if (!Directory.Exists(source.Path) && !File.Exists(source.Path))
        {
            result.Error = $"Path does not exist: {source.Path}";
            return result;
        }

        var sourceDir = GetSourceDir(source.Id);
        Directory.CreateDirectory(sourceDir);

        var metaPath   = System.IO.Path.Combine(sourceDir, "meta.json");
        var chunksPath = System.IO.Path.Combine(sourceDir, "chunks.json");

        var meta = LoadMeta(metaPath) ?? new StandardsIndexMeta { SourceId = source.Id };
        var existingChunks = LoadChunks(chunksPath);

        var patterns = source.FilePatterns.Count > 0
            ? source.FilePatterns
            : new List<string>(DefaultFilePatterns);

        var files = GetFiles(source.Path, patterns);
        result.FilesFound = files.Count;

        var allChunks = new List<StandardsChunk>();

        foreach (var filePath in files)
        {
            var modified = File.GetLastWriteTimeUtc(filePath);

            // Skip unchanged files unless forced
            if (!force && meta.FileStamps.TryGetValue(filePath, out var prevStamp) && prevStamp == modified)
            {
                // Preserve existing chunks for this file
                allChunks.AddRange(existingChunks.Where(c => c.FilePath == filePath));
                result.FilesSkipped++;
                continue;
            }

            var rootPath = Directory.Exists(source.Path) ? source.Path : null;
            var chunks   = StandardsChunker.ChunkFile(filePath, source.Id, rootPath);
            allChunks.AddRange(chunks);
            meta.FileStamps[filePath] = modified;
            result.FilesIndexed++;
            result.ChunksCreated += chunks.Count;
        }

        meta.IndexedAt = DateTime.UtcNow;

        // Write index
        File.WriteAllText(chunksPath,
            JsonConvert.SerializeObject(allChunks, _json),
            System.Text.Encoding.UTF8);
        File.WriteAllText(metaPath,
            JsonConvert.SerializeObject(meta, new JsonSerializerSettings { Formatting = Formatting.Indented }),
            System.Text.Encoding.UTF8);

        result.TotalChunks = allChunks.Count;
        return result;
    }

    /// <summary>
    /// Loads all chunks for the given source IDs (or all indexed sources if empty).
    /// </summary>
    public List<StandardsChunk> LoadAllChunks(IEnumerable<string>? sourceIds = null)
    {
        var chunks = new List<StandardsChunk>();
        var dirs   = Directory.Exists(IndexBase) ? Directory.GetDirectories(IndexBase) : [];

        foreach (var dir in dirs)
        {
            var sourceId = System.IO.Path.GetFileName(dir);
            if (sourceIds != null && !sourceIds.Contains(sourceId)) continue;

            var path = System.IO.Path.Combine(dir, "chunks.json");
            chunks.AddRange(LoadChunks(path));
        }
        return chunks;
    }

    /// <summary>
    /// Returns the index meta for a source, or null if not indexed.
    /// </summary>
    public StandardsIndexMeta? GetMeta(string sourceId)
    {
        var path = System.IO.Path.Combine(GetSourceDir(sourceId), "meta.json");
        return LoadMeta(path);
    }

    public string GetIndexDirectory() => IndexBase;

    // -------------------------------------------------------------------------

    private static string GetSourceDir(string sourceId)
    {
        var safe = string.Concat(sourceId.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return System.IO.Path.Combine(IndexBase, safe);
    }

    private static List<StandardsChunk> LoadChunks(string path)
    {
        if (!File.Exists(path)) return new List<StandardsChunk>();
        try
        {
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<List<StandardsChunk>>(json) ?? new List<StandardsChunk>();
        }
        catch { return new List<StandardsChunk>(); }
    }

    private static StandardsIndexMeta? LoadMeta(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<StandardsIndexMeta>(json);
        }
        catch { return null; }
    }

    private static List<string> GetFiles(string path, List<string> patterns)
    {
        if (File.Exists(path))
            return [path];

        var files = new List<string>();
        foreach (var pattern in patterns)
        {
            try
            {
                files.AddRange(Directory.GetFiles(path, pattern, SearchOption.AllDirectories));
            }
            catch { /* skip inaccessible dirs */ }
        }
        return files.Distinct().ToList();
    }
}

/// <summary>Result statistics from an index operation.</summary>
public class StandardsIndexResult
{
    public string SourceId     { get; set; } = string.Empty;
    public string? Error       { get; set; }
    public int FilesFound      { get; set; }
    public int FilesIndexed    { get; set; }
    public int FilesSkipped    { get; set; }
    public int ChunksCreated   { get; set; }
    public int TotalChunks     { get; set; }
}
