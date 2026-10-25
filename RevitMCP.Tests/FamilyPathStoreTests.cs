using RevitMCP.Addin.Families;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#102: the per-model record of where families were loaded from (revit_reload_family's default path).</summary>
public class FamilyPathStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rmcp-fps-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch { }
    }

    [Fact]
    public void RemembersAndReadsBack_CaseInsensitively()
    {
        var store = new FamilyPathStore(_folder);
        var when = new DateTime(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc);
        Assert.Null(store.Remember("model-a", new[] { ("Socket", @"C:\Lib\Socket.rfa"), ("Switch", @"C:\Lib\Switch.rfa") }, when));

        var entry = new FamilyPathStore(_folder).Get("model-a", "socket");
        Assert.NotNull(entry);
        Assert.Equal(@"C:\Lib\Socket.rfa", entry!.Path);
        Assert.Equal(when, entry.LoadedUtc);
        Assert.Equal(2, store.All("model-a").Count);
        Assert.True(File.Exists(Path.Combine(_folder, "model-a.json")));
    }

    [Fact]
    public void ModelsAreSeparate()
    {
        var store = new FamilyPathStore(_folder);
        store.Remember("model-a", new[] { ("Socket", @"C:\A\Socket.rfa") });
        store.Remember("model-b", new[] { ("Socket", @"C:\B\Socket.rfa") });
        Assert.Equal(@"C:\A\Socket.rfa", store.Get("model-a", "Socket")!.Path);
        Assert.Equal(@"C:\B\Socket.rfa", store.Get("model-b", "Socket")!.Path);
        Assert.Null(store.Get("model-c", "Socket"));
    }

    [Fact]
    public void ALaterLoadReplacesThePath()
    {
        var store = new FamilyPathStore(_folder);
        store.Remember("m", new[] { ("Socket", @"C:\Old\Socket.rfa") });
        store.Remember("m", new[] { ("Socket", @"C:\New\Socket.rfa") });
        Assert.Equal(@"C:\New\Socket.rfa", store.Get("m", "Socket")!.Path);
        Assert.Single(store.All("m"));
    }

    [Fact]
    public void ForgetRemovesEntries()
    {
        var store = new FamilyPathStore(_folder);
        store.Remember("m", new[] { ("A", @"C:\A.rfa"), ("B", @"C:\B.rfa") });
        Assert.Equal(1, store.Forget("m", new[] { "a", "missing" }));
        Assert.Null(store.Get("m", "A"));
        Assert.NotNull(store.Get("m", "B"));
    }

    [Fact]
    public void ADamagedFile_ReadsAsEmpty_AndIsRepairedOnTheNextWrite()
    {
        Directory.CreateDirectory(_folder);
        var store = new FamilyPathStore(_folder);
        File.WriteAllText(store.FileOf("m"), "{ not json");
        Assert.Null(store.Get("m", "A"));
        Assert.Null(store.Remember("m", new[] { ("A", @"C:\A.rfa") }));
        Assert.Equal(@"C:\A.rfa", store.Get("m", "A")!.Path);
    }

    [Theory]
    [InlineData(@"..\..\evil", "evil")]
    [InlineData("a/b:c", "a_b_c")]
    [InlineData("", "unknown-model")]
    [InlineData("proj-123_x", "proj-123_x")]
    public void ModelIdsNeverLeaveTheFolder(string modelId, string expected)
    {
        Assert.Equal(expected, FamilyPathStore.SafeFileName(modelId));
        var file = new FamilyPathStore(_folder).FileOf(modelId);
        Assert.Equal(Path.GetFullPath(_folder), Path.GetDirectoryName(Path.GetFullPath(file)));
    }

    [Fact]
    public void EmptyNamesAndPathsAreIgnored()
    {
        var store = new FamilyPathStore(_folder);
        Assert.Null(store.Remember("m", new[] { ("", @"C:\A.rfa"), ("B", "") }));
        Assert.False(File.Exists(store.FileOf("m")));
    }
}
