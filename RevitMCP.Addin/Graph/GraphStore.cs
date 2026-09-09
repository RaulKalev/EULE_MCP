using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RevitMCP.Addin.Graph;

/// <summary>Where a read-only query actually reads from.</summary>
public sealed class GraphReadHandle
{
    /// <summary>The published (possibly shared) database path.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Local path the reader should open. Equals <see cref="SourcePath"/> when it is already local.</summary>
    public string ReadPath { get; set; } = string.Empty;
    public bool UsedLocalCache { get; set; }
    public bool CopiedThisTime { get; set; }
}

/// <summary>
/// File-level safety around the graph database:
/// builds write to a local temp file and are then moved into place atomically
/// (temp → same-directory .tmp → rename/replace), and reads of a non-local file go through a
/// local cache copy so a synced or network folder is never held open. No Revit API dependency.
/// </summary>
public sealed class GraphStore
{
    private const int ReplaceRetries = 6;
    private const int ReplaceRetryDelayMs = 250;

    public GraphStore(string? localRoot = null)
    {
        LocalRoot = Path.GetFullPath(localRoot ?? GraphPathResolver.DefaultLocalRoot());
    }

    public string LocalRoot { get; }
    public string TempRoot => Path.Combine(LocalRoot, "tmp");
    public string CacheRoot => Path.Combine(LocalRoot, "cache");

    /// <summary>Unique local temp path for a build in progress.</summary>
    public string CreateBuildTempPath()
    {
        Directory.CreateDirectory(TempRoot);
        return Path.Combine(TempRoot, $"build-{Guid.NewGuid():N}{GraphSchema.FileExtension}");
    }

    /// <summary>
    /// Publishes a finished temp database to its final location. The temp file is first copied
    /// next to the target (so the final step is a same-volume rename), then swapped in with
    /// File.Replace / File.Move. A reader never sees a half-written file.
    /// </summary>
    public void Publish(string tempPath, string targetPath)
    {
        if (!File.Exists(tempPath))
            throw new FileNotFoundException("Temp graph database not found.", tempPath);

        var targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

        var staging = targetPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.Copy(tempPath, staging, overwrite: true);
            SwapIntoPlace(staging, targetPath);
        }
        finally
        {
            TryDelete(staging);
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Returns a local path that is safe to open read-only. Files under <see cref="LocalRoot"/>
    /// are used directly; anything else is copied into the cache (only when size or timestamp changed).
    /// </summary>
    public GraphReadHandle OpenForRead(string sourcePath)
    {
        var full = Path.GetFullPath(sourcePath);
        if (!File.Exists(full))
            throw new FileNotFoundException("Graph database not found.", full);

        var handle = new GraphReadHandle { SourcePath = full, ReadPath = full };
        if (IsUnderLocalRoot(full))
            return handle;

        var cachePath = CachePathFor(full, CacheRoot);
        handle.UsedLocalCache = true;
        handle.ReadPath = cachePath;

        var source = new FileInfo(full);
        var cached = new FileInfo(cachePath);
        var upToDate = cached.Exists &&
                       cached.Length == source.Length &&
                       cached.LastWriteTimeUtc == source.LastWriteTimeUtc;
        if (upToDate)
            return handle;

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var staging = cachePath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            CopyWithRetry(full, staging);
            File.SetLastWriteTimeUtc(staging, source.LastWriteTimeUtc);
            SwapIntoPlace(staging, cachePath);
            handle.CopiedThisTime = true;
        }
        finally
        {
            TryDelete(staging);
        }
        return handle;
    }

    public bool IsUnderLocalRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var root = LocalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Deterministic cache location for a source path: <c>&lt;cacheRoot&gt;\&lt;hash&gt;\&lt;file name&gt;</c>.</summary>
    public static string CachePathFor(string sourcePath, string cacheRoot)
    {
        var normalized = Path.GetFullPath(sourcePath).ToLowerInvariant();
        string hash;
        using (var sha = SHA1.Create())
        {
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
            var sb = new StringBuilder(16);
            for (var i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
            hash = sb.ToString();
        }
        return Path.Combine(cacheRoot, hash, Path.GetFileName(sourcePath));
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static void SwapIntoPlace(string staging, string target)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < ReplaceRetries; attempt++)
        {
            try
            {
                if (File.Exists(target))
                    File.Replace(staging, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
                else
                    File.Move(staging, target);
                return;
            }
            catch (IOException ex)
            {
                // Synced folders (Dropbox/OneDrive) and other readers can hold the file briefly.
                last = ex;
                Thread.Sleep(ReplaceRetryDelayMs * (attempt + 1));
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
                Thread.Sleep(ReplaceRetryDelayMs * (attempt + 1));
            }
        }
        throw new IOException($"Could not replace '{target}' after {ReplaceRetries} attempts: {last?.Message}", last);
    }

    private static void CopyWithRetry(string source, string destination)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < ReplaceRetries; attempt++)
        {
            try
            {
                using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                src.CopyTo(dst);
                return;
            }
            catch (IOException ex)
            {
                last = ex;
                Thread.Sleep(ReplaceRetryDelayMs * (attempt + 1));
            }
        }
        throw new IOException($"Could not copy '{source}' to the local cache after {ReplaceRetries} attempts: {last?.Message}", last);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
