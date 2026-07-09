using RevitMCP.Core.Configuration;
using RevitMCP.Core.Instances;
using Xunit;

namespace RevitMCP.Tests;

public class RevitInstanceRegistryTests : IDisposable
{
    private readonly string _dir;
    private readonly RevitInstanceRegistry _registry;

    public RevitInstanceRegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "RevitMCP.Tests", Guid.NewGuid().ToString("N"));
        _registry = new RevitInstanceRegistry(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static RevitInstanceInfo Instance(int pid, string version, string? title = null) => new()
    {
        ProcessId = pid,
        RevitVersion = version,
        PipeName = RevitMcpDefaults.BuildPipeName(version, pid),
        DocumentTitle = title
    };

    [Fact]
    public void BuildPipeName_IsUniquePerVersionAndProcess()
    {
        Assert.Equal("RKTools.RevitMCP.2026.12345", RevitMcpDefaults.BuildPipeName("2026", 12345));
        Assert.NotEqual(
            RevitMcpDefaults.BuildPipeName("2024", 100),
            RevitMcpDefaults.BuildPipeName("2026", 100));
        Assert.NotEqual(
            RevitMcpDefaults.BuildPipeName("2026", 100),
            RevitMcpDefaults.BuildPipeName("2026", 200));
    }

    [Fact]
    public void Register_ThenList_RoundTrips()
    {
        Assert.True(_registry.Register(Instance(100, "2026", "Hospital.rvt")));

        var listed = Assert.Single(_registry.List());
        Assert.Equal(100, listed.ProcessId);
        Assert.Equal("2026", listed.RevitVersion);
        Assert.Equal("RKTools.RevitMCP.2026.100", listed.PipeName);
        Assert.Equal("Hospital.rvt", listed.DocumentTitle);
        Assert.True(listed.UpdatedUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Register_SamePid_OverwritesExistingEntry()
    {
        _registry.Register(Instance(100, "2026", "Old.rvt"));
        _registry.Register(Instance(100, "2026", "New.rvt"));

        var listed = Assert.Single(_registry.List());
        Assert.Equal("New.rvt", listed.DocumentTitle);
    }

    [Fact]
    public void Unregister_RemovesEntry_AndClearsActiveMarkerForThatPid()
    {
        _registry.Register(Instance(100, "2026"));
        _registry.Register(Instance(200, "2024"));
        _registry.SetActive(100);

        _registry.Unregister(100);

        var listed = Assert.Single(_registry.List());
        Assert.Equal(200, listed.ProcessId);
        Assert.Null(_registry.GetActiveProcessId());
    }

    [Fact]
    public void Unregister_OtherPidActive_KeepsActiveMarker()
    {
        _registry.Register(Instance(100, "2026"));
        _registry.Register(Instance(200, "2024"));
        _registry.SetActive(200);

        _registry.Unregister(100);

        Assert.Equal(200, _registry.GetActiveProcessId());
    }

    [Fact]
    public void GetActiveProcessId_WithoutMarker_ReturnsNull()
    {
        Assert.Null(_registry.GetActiveProcessId());
    }

    [Fact]
    public void SetActive_ThenGetActiveProcessId_RoundTrips()
    {
        Assert.True(_registry.SetActive(4242));
        Assert.Equal(4242, _registry.GetActiveProcessId());
    }

    [Fact]
    public void List_SkipsCorruptAndForeignFiles()
    {
        _registry.Register(Instance(100, "2026"));
        File.WriteAllText(Path.Combine(_dir, "instance-999.json"), "{ not valid json");
        File.WriteAllText(Path.Combine(_dir, "unrelated.txt"), "noise");

        var listed = Assert.Single(_registry.List());
        Assert.Equal(100, listed.ProcessId);
    }

    [Fact]
    public void List_MissingDirectory_ReturnsEmpty()
    {
        var registry = new RevitInstanceRegistry(Path.Combine(_dir, "does-not-exist"));
        Assert.Empty(registry.List());
    }

    [Fact]
    public void OrderByPreference_PrefersHigherRevitVersion()
    {
        var ordered = RevitInstanceRegistry.OrderByPreference(
            [Instance(1, "2024"), Instance(2, "2026")],
            activeProcessId: null);

        Assert.Equal("2026", ordered[0].RevitVersion);
        Assert.Equal("2024", ordered[1].RevitVersion);
    }

    [Fact]
    public void OrderByPreference_ActiveInstanceWins_EvenOverNewerVersion()
    {
        var ordered = RevitInstanceRegistry.OrderByPreference(
            [Instance(1, "2024"), Instance(2, "2026")],
            activeProcessId: 1);

        Assert.Equal(1, ordered[0].ProcessId);
        Assert.Equal(2, ordered[1].ProcessId);
    }

    [Fact]
    public void OrderByPreference_SameVersion_PrefersMostRecentlyRegistered()
    {
        var older = Instance(1, "2026");
        older.UpdatedUtc = DateTime.UtcNow.AddMinutes(-10);
        var newer = Instance(2, "2026");
        newer.UpdatedUtc = DateTime.UtcNow;

        var ordered = RevitInstanceRegistry.OrderByPreference([older, newer], activeProcessId: null);

        Assert.Equal(2, ordered[0].ProcessId);
    }

    [Fact]
    public void OrderByPreference_ActiveMarkerForMissingInstance_FallsBackToVersionOrder()
    {
        var ordered = RevitInstanceRegistry.OrderByPreference(
            [Instance(1, "2024"), Instance(2, "2026")],
            activeProcessId: 999);

        Assert.Equal("2026", ordered[0].RevitVersion);
    }

    [Fact]
    public void OrderByPreference_NonNumericVersion_DoesNotThrow()
    {
        var ordered = RevitInstanceRegistry.OrderByPreference(
            [Instance(1, "unknown"), Instance(2, "2026")],
            activeProcessId: null);

        Assert.Equal("2026", ordered[0].RevitVersion);
    }
}
