using RevitMCP.Village;
using Xunit;

namespace RevitMCP.Tests;

public class VillageInstanceRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rkmcp_village_reg_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void RegisterListUnregister_PrunesDeadProcessesAndSkipsCorruptFiles()
    {
        var registry = new VillageInstanceRegistry(_dir);
        Assert.True(registry.Register(new VillageInstanceRecord { ProcessId = 100, RevitVersion = "2026", ProjectName = "A", Url = "http://127.0.0.1:47800/" }));
        Assert.True(registry.Register(new VillageInstanceRecord { ProcessId = 200, RevitVersion = "2024", ProjectName = new string('x', 500), Url = "http://127.0.0.1:47801/" }));
        Assert.True(registry.Register(new VillageInstanceRecord { ProcessId = 300, RevitVersion = "2024", ProjectName = "dead", Url = "http://127.0.0.1:47802/" }));
        File.WriteAllText(Path.Combine(_dir, "village-999.json"), "{ not json");
        File.WriteAllText(Path.Combine(_dir, "village-998.json"), "{\"ProcessId\":998}"); // no url

        var list = registry.List(pid => pid != 300, currentProcessId: 200);
        Assert.Equal(2, list.Count);
        Assert.True(list[0].IsCurrent);
        Assert.Equal(200, list[0].ProcessId);
        Assert.Equal(VillageSchema.MaxStringLength, list[0].ProjectName.Length);
        Assert.Equal(100, list[1].ProcessId);
        Assert.False(File.Exists(Path.Combine(_dir, "village-300.json")));

        registry.Unregister(100);
        registry.Unregister(12345); // unknown: no throw
        Assert.Single(registry.List(_ => true, 200));
    }

    [Fact]
    public void List_OnMissingDirectory_IsEmpty()
    {
        var registry = new VillageInstanceRegistry(Path.Combine(_dir, "nope"));
        Assert.Empty(registry.List(_ => true, 1));
    }
}
