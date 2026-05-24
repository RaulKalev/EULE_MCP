using Newtonsoft.Json;

namespace RevitMCP.Addin.Standards;

// ─── Config models (stored in %ProgramData%\RKTools\MCP\Config\StandardsSources.json) ───

/// <summary>
/// Root config file that lists all company standards sources.
/// </summary>
public class StandardsSourceConfig
{
    [JsonProperty("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0";

    [JsonProperty("sources")]
    public List<StandardsSource> Sources { get; set; } = new();
}

/// <summary>
/// A single standards source — a folder or file containing company standards documents.
/// </summary>
public class StandardsSource
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("description")]
    public string? Description { get; set; }

    /// <summary>Absolute path to a directory or a single file (.md, .txt, .json).</summary>
    [JsonProperty("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>File extensions to index. Defaults to ["*.md","*.txt","*.json"] if empty.</summary>
    [JsonProperty("filePatterns")]
    public List<string> FilePatterns { get; set; } = new();

    /// <summary>Optional discipline tag (e.g. "Electrical", "Mechanical") for boosted results.</summary>
    [JsonProperty("discipline")]
    public string? Discipline { get; set; }

    /// <summary>Whether this source is active and should be indexed.</summary>
    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;
}

// ─── Index models (stored as JSON in %AppData%\RKTools\RevitMCP\StandardsIndex) ───

/// <summary>
/// A single indexed chunk of text from a standards document.
/// </summary>
public class StandardsChunk
{
    [JsonProperty("chunkId")]
    public string ChunkId { get; set; } = string.Empty;

    [JsonProperty("sourceId")]
    public string SourceId { get; set; } = string.Empty;

    [JsonProperty("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonProperty("relPath")]
    public string RelPath { get; set; } = string.Empty;

    [JsonProperty("heading")]
    public string? Heading { get; set; }

    [JsonProperty("text")]
    public string Text { get; set; } = string.Empty;

    [JsonProperty("fileLastModified")]
    public DateTime FileLastModified { get; set; }

    [JsonProperty("indexedAt")]
    public DateTime IndexedAt { get; set; }
}

/// <summary>
/// Index metadata file stored alongside the chunks — tracks when each source was indexed
/// so we can skip re-indexing unchanged files.
/// </summary>
public class StandardsIndexMeta
{
    [JsonProperty("sourceId")]
    public string SourceId { get; set; } = string.Empty;

    [JsonProperty("indexedAt")]
    public DateTime IndexedAt { get; set; }

    /// <summary>Key = filePath, Value = last modified UTC at index time.</summary>
    [JsonProperty("fileStamps")]
    public Dictionary<string, DateTime> FileStamps { get; set; } = new();
}

// ─── Search result models ───

/// <summary>
/// A single search result returned from <see cref="StandardsSearchService"/>.
/// </summary>
public class StandardsSearchResult
{
    [JsonProperty("rank")]
    public int Rank { get; set; }

    [JsonProperty("score")]
    public double Score { get; set; }

    [JsonProperty("sourceId")]
    public string SourceId { get; set; } = string.Empty;

    [JsonProperty("sourceName")]
    public string SourceName { get; set; } = string.Empty;

    [JsonProperty("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonProperty("relPath")]
    public string RelPath { get; set; } = string.Empty;

    [JsonProperty("heading")]
    public string? Heading { get; set; }

    /// <summary>Relevant snippet of the chunk text (up to ~400 chars).</summary>
    [JsonProperty("snippet")]
    public string Snippet { get; set; } = string.Empty;

    [JsonProperty("fileLastModified")]
    public DateTime FileLastModified { get; set; }
}
