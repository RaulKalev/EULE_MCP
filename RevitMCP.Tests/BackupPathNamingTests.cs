using RevitMCP.Addin.FileSystem;
using Xunit;

namespace RevitMCP.Tests;

public class BackupPathNamingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "backup-naming-" + Guid.NewGuid().ToString("N"));

    public BackupPathNamingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void SameSecond_GetsANumberedSuffix_InsteadOfColliding()
    {
        var at = new DateTime(2026, 10, 3, 1, 15, 3);
        var first = BackupPathNaming.Unique(_dir, "config", "backup", at, ".json");
        Assert.EndsWith("config_backup_2026-10-03_011503.json", first);
        File.WriteAllText(first, "{}");

        var second = BackupPathNaming.Unique(_dir, "config", "backup", at, ".json");
        Assert.EndsWith("config_backup_2026-10-03_011503_2.json", second);
        File.WriteAllText(second, "{}");

        Assert.EndsWith("config_backup_2026-10-03_011503_3.json", BackupPathNaming.Unique(_dir, "config", "backup", at, ".json"));
    }
}
