using System.IO;
using System.Security.Cryptography;

namespace RevitMCP.Addin.FileSystem;

public class FileSystemService
{
    private const long MaxHashBytes = 100 * 1024 * 1024; // 100 MB

    private readonly FilePathPolicy _policy;

    public FileSystemService() : this(new FilePathPolicy()) { }
    public FileSystemService(FilePathPolicy policy) => _policy = policy;

    public FileInspectResult Inspect(string path, bool includeHash = false)
    {
        var readError = _policy.ValidateRead(path);
        if (readError != null) return FileInspectResult.Fail(readError);

        var (normalized, normError) = FilePathPolicy.NormalizePath(path);
        if (normError != null) return FileInspectResult.Fail(normError);

        bool isFile   = File.Exists(normalized!);
        bool isFolder = !isFile && Directory.Exists(normalized!);

        if (!isFile && !isFolder)
            return new FileInspectResult { Success = true, Exists = false, Path = normalized! };

        try
        {
            FileSystemInfo info = isFile
                ? new FileInfo(normalized!)
                : new DirectoryInfo(normalized!);

            string? hash = null;
            if (includeHash && isFile)
            {
                var fi = (FileInfo)info;
                if (fi.Length <= MaxHashBytes)
                {
                    using var sha = SHA256.Create();
                    using var stream = File.OpenRead(normalized!);
                    hash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
                }
                else
                {
                    hash = $"skipped — file size {fi.Length:N0} bytes exceeds {MaxHashBytes:N0}-byte hash limit";
                }
            }

            var attrs = Enum.GetValues<FileAttributes>()
                .Where(a => info.Attributes.HasFlag(a))
                .Select(a => a.ToString())
                .ToList();

            return new FileInspectResult
            {
                Success       = true,
                Exists        = true,
                Path          = normalized!,
                Type          = isFile ? "file" : "folder",
                Extension     = isFile ? Path.GetExtension(normalized!) : string.Empty,
                SizeBytes     = isFile ? ((FileInfo)info).Length : 0L,
                CreatedAtUtc  = info.CreationTimeUtc,
                ModifiedAtUtc = info.LastWriteTimeUtc,
                IsReadOnly    = isFile && ((FileInfo)info).IsReadOnly,
                Attributes    = attrs,
                HashSha256    = hash
            };
        }
        catch (Exception ex)
        {
            return FileInspectResult.Fail($"Inspect failed: {ex.Message}");
        }
    }

    public FileCopyResult CopyFile(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, bool preserveTimestamps)
    {
        var readError = _policy.ValidateRead(sourcePath);
        if (readError != null) return FileCopyResult.Fail(readError);

        var writeError = _policy.ValidateWrite(destinationPath);
        if (writeError != null) return FileCopyResult.Fail(writeError);

        var (src, srcErr) = FilePathPolicy.NormalizePath(sourcePath);
        if (srcErr != null) return FileCopyResult.Fail(srcErr);

        var (dst, dstErr) = FilePathPolicy.NormalizePath(destinationPath);
        if (dstErr != null) return FileCopyResult.Fail(dstErr);

        if (Directory.Exists(src!))
            return FileCopyResult.Fail("Folder copy is not supported — provide a file path.");

        if (!File.Exists(src!))
            return FileCopyResult.Fail($"Source file not found: {src}");

        if (File.Exists(dst!) && !overwrite)
            return FileCopyResult.Fail(
                $"Destination already exists: '{dst}'. Set overwrite=true to replace it.");

        var dir = Path.GetDirectoryName(dst!);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            if (!createDirectories)
                return FileCopyResult.Fail(
                    $"Destination directory '{dir}' does not exist. Set createDirectories=true.");
            Directory.CreateDirectory(dir);
        }

        try
        {
            var srcInfo = new FileInfo(src!);
            bool overwritten = File.Exists(dst!);
            File.Copy(src!, dst!, overwrite);

            if (preserveTimestamps)
            {
                File.SetCreationTimeUtc(dst!, srcInfo.CreationTimeUtc);
                File.SetLastWriteTimeUtc(dst!, srcInfo.LastWriteTimeUtc);
            }

            return new FileCopyResult
            {
                Success              = true,
                SourcePath           = src!,
                DestinationPath      = dst!,
                SizeBytes            = srcInfo.Length,
                Overwritten          = overwritten,
                PreservedTimestamps  = preserveTimestamps
            };
        }
        catch (Exception ex)
        {
            return FileCopyResult.Fail($"Copy failed: {ex.Message}");
        }
    }

    public FileBackupResult BackupFile(string filePath, string backupDirectory,
        string suffix, bool preserveTimestamps)
    {
        var readError = _policy.ValidateRead(filePath);
        if (readError != null) return FileBackupResult.Fail(readError);

        var (normalized, normErr) = FilePathPolicy.NormalizePath(filePath);
        if (normErr != null) return FileBackupResult.Fail(normErr);

        if (!File.Exists(normalized!))
            return FileBackupResult.Fail($"File not found: {normalized}");

        string targetDir = string.IsNullOrWhiteSpace(backupDirectory)
            ? Path.GetDirectoryName(normalized!)!
            : backupDirectory;

        var writeError = _policy.ValidateWrite(targetDir);
        if (writeError != null) return FileBackupResult.Fail(writeError);

        if (!string.IsNullOrWhiteSpace(backupDirectory))
        {
            var (normDir, dirErr) = FilePathPolicy.NormalizePath(backupDirectory);
            if (dirErr != null) return FileBackupResult.Fail(dirErr);
            targetDir = normDir!;
        }

        if (!Directory.Exists(targetDir))
            Directory.CreateDirectory(targetDir);

        if (string.IsNullOrWhiteSpace(suffix)) suffix = "backup";
        var stem      = Path.GetFileNameWithoutExtension(normalized!);
        var ext       = Path.GetExtension(normalized!);
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HHmmss");
        var backupName = $"{stem}_{suffix}_{timestamp}{ext}";
        var backupPath = Path.Combine(targetDir, backupName);

        if (File.Exists(backupPath))
            return FileBackupResult.Fail(
                $"Backup file already exists: {backupPath}. Try again in 1 second.");

        try
        {
            var srcInfo = new FileInfo(normalized!);
            File.Copy(normalized!, backupPath);
            if (preserveTimestamps)
            {
                File.SetCreationTimeUtc(backupPath, srcInfo.CreationTimeUtc);
                File.SetLastWriteTimeUtc(backupPath, srcInfo.LastWriteTimeUtc);
            }

            return new FileBackupResult
            {
                Success             = true,
                SourcePath          = normalized!,
                BackupPath          = backupPath,
                SizeBytes           = srcInfo.Length,
                PreservedTimestamps = preserveTimestamps
            };
        }
        catch (Exception ex)
        {
            return FileBackupResult.Fail($"Backup failed: {ex.Message}");
        }
    }

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

    public FileWriteResult WriteText(string filePath, string content, bool overwrite, bool createDirectories, bool backupBeforeOverwrite = false)
    {
        var writeError = _policy.ValidateWrite(filePath);
        if (writeError != null) return FileWriteResult.Fail(writeError);

        var (normalizedPath, normError) = FilePathPolicy.NormalizePath(filePath);
        if (normError != null) return FileWriteResult.Fail(normError);

        var existed = File.Exists(normalizedPath!);
        if (existed && !overwrite)
            return FileWriteResult.Fail(
                $"File already exists at '{normalizedPath}'. Set overwrite=true to replace it.");

        string? backupPath = null;
        if (existed && overwrite && backupBeforeOverwrite)
        {
            var backupResult = BackupFile(normalizedPath!, string.Empty, "backup", false);
            if (!backupResult.Success)
                return FileWriteResult.Fail($"Pre-write backup failed: {backupResult.Error}");
            backupPath = backupResult.BackupPath;
        }

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
                Success        = true,
                FilePath       = normalizedPath!,
                SizeBytes      = info.Length,
                WasOverwritten = existed,
                BackupPath     = backupPath
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
    public string? BackupPath { get; set; }
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

public class FileInspectResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string Path { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ModifiedAtUtc { get; set; }
    public bool IsReadOnly { get; set; }
    public List<string> Attributes { get; set; } = new();
    public string? HashSha256 { get; set; }
    public static FileInspectResult Fail(string error) => new() { Success = false, Error = error };
}

public class FileCopyResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool Overwritten { get; set; }
    public bool PreservedTimestamps { get; set; }
    public static FileCopyResult Fail(string error) => new() { Success = false, Error = error };
}

public class FileBackupResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool PreservedTimestamps { get; set; }
    public static FileBackupResult Fail(string error) => new() { Success = false, Error = error };
}
