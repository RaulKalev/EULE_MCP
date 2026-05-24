using RevitMCP.Addin.FileSystem;
using Xunit;

namespace RevitMCP.Tests;

public class FileSystemServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string TempFile(string content = "hello")
    {
        var path = Path.Combine(Path.GetTempPath(), $"rkmcp_fs_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content);
        return path;
    }

    private static FileSystemService CreateService()
    {
        // Use a permissive policy that allows the temp directory
        var policy = new FilePathPolicy();
        return new FileSystemService(policy);
    }

    // ── Inspect ───────────────────────────────────────────────────────────────

    [Fact]
    public void Inspect_ExistingFile_ReturnsFileDetails()
    {
        var path = TempFile("test content");
        try
        {
            var svc = CreateService();
            var result = svc.Inspect(path, includeHash: false);

            Assert.True(result.Success);
            Assert.True(result.Exists);
            Assert.Equal("file", result.Type);
            Assert.Equal(".txt", result.Extension);
            Assert.True(result.SizeBytes > 0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Inspect_MissingFile_ReturnsExistsFalse()
    {
        var svc = CreateService();
        var fakePath = Path.Combine(Path.GetTempPath(), "does_not_exist_ever_12345.txt");
        var result = svc.Inspect(fakePath);

        Assert.True(result.Success);
        Assert.False(result.Exists);
    }

    [Fact]
    public void Inspect_WithHash_ReturnsHashString()
    {
        var path = TempFile("hash me");
        try
        {
            var svc = CreateService();
            var result = svc.Inspect(path, includeHash: true);

            Assert.True(result.Success);
            Assert.NotNull(result.HashSha256);
            Assert.Equal(64, result.HashSha256!.Length); // SHA-256 hex = 64 chars
        }
        finally { File.Delete(path); }
    }

    // ── CopyFile ──────────────────────────────────────────────────────────────

    [Fact]
    public void CopyFile_ValidCopy_Succeeds()
    {
        var src = TempFile("copy me");
        var dst = Path.Combine(Path.GetTempPath(), $"rkmcp_copy_dst_{Guid.NewGuid():N}.txt");
        try
        {
            var svc = CreateService();
            var result = svc.CopyFile(src, dst, overwrite: false, createDirectories: false, preserveTimestamps: false);

            Assert.True(result.Success);
            Assert.True(File.Exists(dst));
            Assert.Equal(File.ReadAllText(src), File.ReadAllText(dst));
            Assert.False(result.Overwritten);
        }
        finally
        {
            if (File.Exists(src)) File.Delete(src);
            if (File.Exists(dst)) File.Delete(dst);
        }
    }

    [Fact]
    public void CopyFile_DestinationExists_NoOverwrite_ReturnsError()
    {
        var src = TempFile("source");
        var dst = TempFile("existing");
        try
        {
            var svc = CreateService();
            var result = svc.CopyFile(src, dst, overwrite: false, createDirectories: false, preserveTimestamps: false);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.Contains("overwrite=true", result.Error!);
        }
        finally
        {
            if (File.Exists(src)) File.Delete(src);
            if (File.Exists(dst)) File.Delete(dst);
        }
    }

    [Fact]
    public void CopyFile_SourceNotFound_ReturnsError()
    {
        var dst = Path.Combine(Path.GetTempPath(), $"rkmcp_dst_{Guid.NewGuid():N}.txt");
        var svc = CreateService();
        var result = svc.CopyFile(
            @"C:\nonexistent_source.txt", dst,
            overwrite: false, createDirectories: false, preserveTimestamps: false);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    // ── BackupFile ────────────────────────────────────────────────────────────

    [Fact]
    public void BackupFile_ValidFile_CreatesTimestampedCopy()
    {
        var src = TempFile("backup me");
        var backupDir = Path.GetTempPath();
        try
        {
            var svc = CreateService();
            var result = svc.BackupFile(src, backupDir, suffix: "test", preserveTimestamps: false);

            Assert.True(result.Success);
            Assert.True(File.Exists(result.BackupPath));
            Assert.Contains("_test_", result.BackupPath);
        }
        finally
        {
            if (File.Exists(src)) File.Delete(src);
            if (File.Exists(src)) File.Delete(src); // belt-and-suspenders
            // Clean up backup
            var stem = Path.GetFileNameWithoutExtension(src);
            foreach (var f in Directory.GetFiles(backupDir, $"{stem}_test_*.txt"))
                try { File.Delete(f); } catch { }
        }
    }

    [Fact]
    public void BackupFile_SourceNotFound_ReturnsError()
    {
        var svc = CreateService();
        var result = svc.BackupFile(
            @"C:\nonexistent_file.txt", Path.GetTempPath(), "bak", false);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    // ── WriteText with backupBeforeOverwrite ──────────────────────────────────

    [Fact]
    public void WriteText_OverwriteWithBackup_CreatesBackupBeforeOverwriting()
    {
        var path = TempFile("original content");
        try
        {
            var svc = CreateService();
            var result = svc.WriteText(path, "new content", overwrite: true, createDirectories: false, backupBeforeOverwrite: true);

            Assert.True(result.Success);
            Assert.True(result.WasOverwritten);
            Assert.NotNull(result.BackupPath);
            Assert.True(File.Exists(result.BackupPath!));
            Assert.Equal("original content", File.ReadAllText(result.BackupPath!));
            Assert.Equal("new content", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var dir = Path.GetDirectoryName(path)!;
            var stem = Path.GetFileNameWithoutExtension(path);
            foreach (var f in Directory.GetFiles(dir, $"{stem}_backup_*.txt"))
                try { File.Delete(f); } catch { }
        }
    }

    [Fact]
    public void WriteText_NewFile_NoBackupPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rkmcp_new_{Guid.NewGuid():N}.txt");
        try
        {
            var svc = CreateService();
            var result = svc.WriteText(path, "hello", overwrite: false, createDirectories: false);

            Assert.True(result.Success);
            Assert.False(result.WasOverwritten);
            Assert.Null(result.BackupPath);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── ListDirectory ─────────────────────────────────────────────────────────

    [Fact]
    public void ListDirectory_ValidFolder_ReturnsEntries()
    {
        var dir = Directory.CreateTempSubdirectory("rkmcp_ls_").FullName;
        File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(dir, "b.txt"), "b");
        try
        {
            var svc = CreateService();
            var result = svc.ListDirectory(dir, "*", recursive: false, maxResults: 100);

            Assert.True(result.Success);
            Assert.Equal(2, result.Entries.Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ListDirectory_MaxResults_Truncates()
    {
        var dir = Directory.CreateTempSubdirectory("rkmcp_ls_trunc_").FullName;
        for (int i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(dir, $"f{i}.txt"), "x");
        try
        {
            var svc = CreateService();
            var result = svc.ListDirectory(dir, "*.txt", recursive: false, maxResults: 3);

            Assert.True(result.Success);
            Assert.True(result.Truncated);
            Assert.Equal(3, result.Entries.Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
