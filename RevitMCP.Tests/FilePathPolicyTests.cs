using RevitMCP.Addin.FileSystem;
using Xunit;

namespace RevitMCP.Tests;

public class FilePathPolicyTests
{
    // ── NormalizePath ─────────────────────────────────────────────────────────

    [Fact]
    public void NormalizePath_EmptyString_ReturnsError()
    {
        var (path, error) = FilePathPolicy.NormalizePath("");
        Assert.Null(path);
        Assert.NotNull(error);
    }

    [Fact]
    public void NormalizePath_Whitespace_ReturnsError()
    {
        var (path, error) = FilePathPolicy.NormalizePath("   ");
        Assert.Null(path);
        Assert.NotNull(error);
    }

    [Fact]
    public void NormalizePath_ValidAbsolutePath_ReturnsFull()
    {
        var (path, error) = FilePathPolicy.NormalizePath(@"C:\Windows\Temp\test.txt");
        Assert.Null(error);
        Assert.NotNull(path);
        Assert.StartsWith("C:\\", path!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizePath_ExpandsEnvironmentVariables()
    {
        var (path, error) = FilePathPolicy.NormalizePath(@"%TEMP%\test.txt");
        Assert.Null(error);
        Assert.NotNull(path);
        Assert.DoesNotContain("%TEMP%", path!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizePath_PathTraversalDotDot_ReturnsError()
    {
        var (path, error) = FilePathPolicy.NormalizePath(@"C:\Projects\..\Windows\System32\important.dll");
        Assert.Null(path);
        Assert.NotNull(error);
        Assert.Contains("..", error!);
    }

    // ── AllowedRoots ──────────────────────────────────────────────────────────

    [Fact]
    public void ValidateRead_PathInsideAllowedRoot_ReturnsNull()
    {
        var config = new FileAccessPolicyConfig
        {
            AllowedReadRoots = [@"C:\Projects"],
            AllowedWriteRoots = [],
            MaxReadBytesDefault = 1_048_576
        };
        var policy = new FilePathPolicy(config);
        var error = policy.ValidateRead(@"C:\Projects\Example\file.txt");
        Assert.Null(error);
    }

    [Fact]
    public void ValidateRead_PathOutsideAllowedRoots_ReturnsError()
    {
        var config = new FileAccessPolicyConfig
        {
            AllowedReadRoots = [@"C:\Projects"],
            AllowedWriteRoots = [],
            MaxReadBytesDefault = 1_048_576
        };
        var policy = new FilePathPolicy(config);
        var error = policy.ValidateRead(@"C:\Windows\System32\notepad.exe");
        Assert.NotNull(error);
        Assert.Contains("outside all allowed roots", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateWrite_PathInsideAllowedWriteRoot_ReturnsNull()
    {
        var tempRoot = Path.GetTempPath();
        var config = new FileAccessPolicyConfig
        {
            AllowedReadRoots = [],
            AllowedWriteRoots = [tempRoot],
            MaxReadBytesDefault = 1_048_576
        };
        var policy = new FilePathPolicy(config);
        var error = policy.ValidateWrite(Path.Combine(tempRoot, "output.txt"));
        Assert.Null(error);
    }

    [Fact]
    public void ValidateWrite_PathOutsideAllowedWriteRoots_ReturnsError()
    {
        var config = new FileAccessPolicyConfig
        {
            AllowedReadRoots = [],
            AllowedWriteRoots = [@"C:\Projects\Restricted"],
            MaxReadBytesDefault = 1_048_576
        };
        var policy = new FilePathPolicy(config);
        var error = policy.ValidateWrite(@"C:\Windows\Temp\hacked.txt");
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateRead_EnvironmentVariableRoot_Resolved()
    {
        var config = new FileAccessPolicyConfig
        {
            AllowedReadRoots = [@"%USERPROFILE%"],
            AllowedWriteRoots = [],
            MaxReadBytesDefault = 1_048_576
        };
        var policy = new FilePathPolicy(config);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var error = policy.ValidateRead(Path.Combine(userProfile, "Documents", "test.txt"));
        Assert.Null(error);
    }

    // ── FileSystemService integration ─────────────────────────────────────────

    [Fact]
    public void ReadText_FileNotFound_ReturnsFail()
    {
        var policy = new FileAccessPolicyConfig
        {
            AllowedReadRoots = [Path.GetTempPath()],
            AllowedWriteRoots = [Path.GetTempPath()],
            MaxReadBytesDefault = 1_048_576
        };
        var svc = new FileSystemService(new FilePathPolicy(policy));
        var result = svc.ReadText(Path.Combine(Path.GetTempPath(), "does_not_exist_xyz.txt"));
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteAndReadText_RoundTrip_Succeeds()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"rkmcp_test_{Guid.NewGuid():N}.txt");
        try
        {
            var policy = new FileAccessPolicyConfig
            {
                AllowedReadRoots = [Path.GetTempPath()],
                AllowedWriteRoots = [Path.GetTempPath()],
                MaxReadBytesDefault = 1_048_576
            };
            var svc = new FileSystemService(new FilePathPolicy(policy));

            var writeResult = svc.WriteText(tempFile, "Hello World", overwrite: false, createDirectories: false);
            Assert.True(writeResult.Success);
            Assert.False(writeResult.WasOverwritten);

            var readResult = svc.ReadText(tempFile);
            Assert.True(readResult.Success);
            Assert.Equal("Hello World", readResult.Content);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void WriteText_OverwriteFalse_WhenFileExists_ReturnsFail()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"rkmcp_test_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tempFile, "existing");
            var policy = new FileAccessPolicyConfig
            {
                AllowedReadRoots = [Path.GetTempPath()],
                AllowedWriteRoots = [Path.GetTempPath()],
                MaxReadBytesDefault = 1_048_576
            };
            var svc = new FileSystemService(new FilePathPolicy(policy));
            var result = svc.WriteText(tempFile, "new content", overwrite: false, createDirectories: false);
            Assert.False(result.Success);
            Assert.Contains("overwrite", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ReadText_ExceedsMaxBytes_ReturnsFail()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"rkmcp_test_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tempFile, new string('X', 200));
            var policy = new FileAccessPolicyConfig
            {
                AllowedReadRoots = [Path.GetTempPath()],
                AllowedWriteRoots = [Path.GetTempPath()],
                MaxReadBytesDefault = 100
            };
            var svc = new FileSystemService(new FilePathPolicy(policy));
            var result = svc.ReadText(tempFile, maxBytes: 100);
            Assert.False(result.Success);
            Assert.Contains("limit", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ListDirectory_ValidFolder_ReturnsEntries()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rkmcp_listdir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var file1 = Path.Combine(tempDir, "a.txt");
        var file2 = Path.Combine(tempDir, "b.xlsx");
        try
        {
            File.WriteAllText(file1, "a");
            File.WriteAllText(file2, "b");

            var policy = new FileAccessPolicyConfig
            {
                AllowedReadRoots = [Path.GetTempPath()],
                AllowedWriteRoots = [],
                MaxReadBytesDefault = 1_048_576
            };
            var svc = new FileSystemService(new FilePathPolicy(policy));
            var result = svc.ListDirectory(tempDir, "*", recursive: false, maxResults: 100);
            Assert.True(result.Success);
            Assert.Equal(2, result.Entries.Count);
        }
        finally
        {
            if (File.Exists(file1)) File.Delete(file1);
            if (File.Exists(file2)) File.Delete(file2);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir);
        }
    }

    // ── AllowNetworkPaths ────────────────────────────────────────────────────

    [Fact]
    public void ValidateRead_NetworkPath_WhenNetworkDisabled_ReturnsError()
    {
        var config = new FileAccessPolicyConfig
        {
            AllowedReadRoots = [@"\\server\share"],
            AllowedWriteRoots = [],
            AllowNetworkPaths = false,
            MaxReadBytesDefault = 1_048_576
        };
        var policy = new FilePathPolicy(config);
        var error = policy.ValidateRead(@"\\server\share\file.txt");
        Assert.NotNull(error);
        Assert.Contains("Network paths", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateWrite_NetworkPath_WhenNetworkDisabled_ReturnsError()
    {
        var config = new FileAccessPolicyConfig
        {
            AllowedReadRoots = [],
            AllowedWriteRoots = [@"\\server\share"],
            AllowNetworkPaths = false,
            MaxReadBytesDefault = 1_048_576
        };
        var policy = new FilePathPolicy(config);
        var error = policy.ValidateWrite(@"\\server\share\output.xlsx");
        Assert.NotNull(error);
        Assert.Contains("Network paths", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── AllowProgramDataWrites ────────────────────────────────────────────────

    [Fact]
    public void ValidateWrite_ProgramDataPath_WhenDisabled_ReturnsError()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var config = new FileAccessPolicyConfig
        {
            AllowedReadRoots = [],
            AllowedWriteRoots = [programData],
            AllowNetworkPaths = false,
            AllowProgramDataWrites = false,
            MaxReadBytesDefault = 1_048_576
        };
        var policy = new FilePathPolicy(config);
        var error = policy.ValidateWrite(Path.Combine(programData, "SomeApp", "config.json"));
        Assert.NotNull(error);
        Assert.Contains("ProgramData", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateWrite_ProgramDataPath_WhenEnabled_AllowedByRoot_ReturnsNull()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var config = new FileAccessPolicyConfig
        {
            AllowedReadRoots = [],
            AllowedWriteRoots = [programData],
            AllowNetworkPaths = false,
            AllowProgramDataWrites = true,
            MaxReadBytesDefault = 1_048_576
        };
        var policy = new FilePathPolicy(config);
        var error = policy.ValidateWrite(Path.Combine(programData, "SomeApp", "config.json"));
        Assert.Null(error);
    }

    // ── Excel path policy enforcement ─────────────────────────────────────────

    [Fact]
    public void ValidateRead_ExcelFile_OutsideAllowedRoots_ReturnsError()
    {
        var config = new FileAccessPolicyConfig
        {
            AllowedReadRoots = [@"C:\Projects"],
            AllowedWriteRoots = [],
            AllowNetworkPaths = false,
            MaxReadBytesDefault = 1_048_576
        };
        var policy = new FilePathPolicy(config);
        var error = policy.ValidateRead(@"C:\Users\Public\file.xlsx");
        Assert.NotNull(error);
        Assert.Contains("outside all allowed roots", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateWrite_ExcelFile_InsideAllowedWriteRoot_ReturnsNull()
    {
        var tempRoot = Path.GetTempPath();
        var config = new FileAccessPolicyConfig
        {
            AllowedReadRoots = [],
            AllowedWriteRoots = [tempRoot],
            AllowNetworkPaths = false,
            AllowProgramDataWrites = false,
            MaxReadBytesDefault = 1_048_576
        };
        var policy = new FilePathPolicy(config);
        var error = policy.ValidateWrite(Path.Combine(tempRoot, "report.xlsx"));
        Assert.Null(error);
    }
}
