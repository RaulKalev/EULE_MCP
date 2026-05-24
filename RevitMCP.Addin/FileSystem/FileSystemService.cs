using System.IO;

namespace RevitMCP.Addin.FileSystem;

public class FileSystemService
{
    private readonly FilePathPolicy _policy;

    public FileSystemService() : this(new FilePathPolicy()) { }
    public FileSystemService(FilePathPolicy policy) => _policy = policy;

    public FileReadResult ReadText(string filePath, int maxBytes = 0)
    {
        if (maxBytes <= 0) maxBytes = _policy.MaxReadBytes;

        var readError = _policy.ValidateRead(filePath);
        if (readError != null) return FileReadResult.Fail(readError);

        var (normalizedPath, normError) = FilePathPolicy.NormalizePath(filePath);
        if (normError != null) return FileReadResult.Fail(normError);

        if (!File.Exists(normalizedPath!))
            return FileReadResult.Fail($"File not found: {normalizedPath}");

        var info = new FileInfo(normalizedPath!);
        if (info.Length > maxBytes)
            return FileReadResult.Fail(
                $"File size {info.Length:N0} bytes exceeds the {maxBytes:N0}-byte read limit.");

        try
        {
            var content = File.ReadAllText(normalizedPath!, System.Text.Encoding.UTF8);
            return new FileReadResult
            {
                Success = true,
                FilePath = normalizedPath!,
                Exists = true,
                SizeBytes = info.Length,
                Content = content
            };
        }
        catch (Exception ex)
        {
            return FileReadResult.Fail($"Read failed: {ex.Message}");
        }
    }

    public FileWriteResult WriteText(string filePath, string content, bool overwrite, bool createDirectories)
    {
        var writeError = _policy.ValidateWrite(filePath);
        if (writeError != null) return FileWriteResult.Fail(writeError);

        var (normalizedPath, normError) = FilePathPolicy.NormalizePath(filePath);
        if (normError != null) return FileWriteResult.Fail(normError);

        var existed = File.Exists(normalizedPath!);
        if (existed && !overwrite)
            return FileWriteResult.Fail(
                $"File already exists at '{normalizedPath}'. Set overwrite=true to replace it.");

        var dir = Path.GetDirectoryName(normalizedPath!);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            if (!createDirectories)
                return FileWriteResult.Fail(
                    $"Directory '{dir}' does not exist. Set createDirectories=true to create it.");
            Directory.CreateDirectory(dir);
        }

        try
        {
            File.WriteAllText(normalizedPath!, content, System.Text.Encoding.UTF8);
            var info = new FileInfo(normalizedPath!);
            return new FileWriteResult
            {
                Success = true,
                FilePath = normalizedPath!,
                SizeBytes = info.Length,
                WasOverwritten = existed
            };
        }
        catch (Exception ex)
        {
            return FileWriteResult.Fail($"Write failed: {ex.Message}");
        }
    }

    public FileListResult ListDirectory(string folderPath, string searchPattern, bool recursive, int maxResults)
    {
        var readError = _policy.ValidateRead(folderPath);
        if (readError != null) return FileListResult.Fail(readError);

        var (normalizedPath, normError) = FilePathPolicy.NormalizePath(folderPath);
        if (normError != null) return FileListResult.Fail(normError);

        if (!Directory.Exists(normalizedPath!))
            return FileListResult.Fail($"Directory not found: {normalizedPath}");

        if (string.IsNullOrWhiteSpace(searchPattern)) searchPattern = "*";
        if (maxResults <= 0) maxResults = 500;

        try
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var entries = new List<FileSystemEntryDto>();
            var truncated = false;

            foreach (var entry in Directory.EnumerateFileSystemEntries(normalizedPath!, searchPattern, option))
            {
                if (entries.Count >= maxResults) { truncated = true; break; }

                var isDir = Directory.Exists(entry);
                FileSystemInfo info = isDir ? new DirectoryInfo(entry) : new FileInfo(entry);

                entries.Add(new FileSystemEntryDto
                {
                    Name = info.Name,
                    FullPath = entry,
                    Extension = isDir ? string.Empty : Path.GetExtension(entry),
                    SizeBytes = isDir ? 0L : ((FileInfo)info).Length,
                    ModifiedAt = info.LastWriteTimeUtc,
                    Type = isDir ? "folder" : "file"
                });
            }

            return new FileListResult
            {
                Success = true,
                FolderPath = normalizedPath!,
                Entries = entries,
                Truncated = truncated,
                MaxResults = maxResults
            };
        }
        catch (Exception ex)
        {
            return FileListResult.Fail($"Directory listing failed: {ex.Message}");
        }
    }
}

public class FileReadResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public long SizeBytes { get; set; }
    public string Content { get; set; } = string.Empty;
    public static FileReadResult Fail(string error) => new() { Success = false, Error = error };
}

public class FileWriteResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool WasOverwritten { get; set; }
    public static FileWriteResult Fail(string error) => new() { Success = false, Error = error };
}

public class FileListResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public List<FileSystemEntryDto> Entries { get; set; } = new();
    public bool Truncated { get; set; }
    public int MaxResults { get; set; }
    public static FileListResult Fail(string error) => new() { Success = false, Error = error };
}

public class FileSystemEntryDto
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string Type { get; set; } = string.Empty;
}
