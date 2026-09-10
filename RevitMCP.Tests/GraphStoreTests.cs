using RevitMCP.Addin.Graph;
using Xunit;

namespace RevitMCP.Tests;

public class GraphStoreTests
{
    private static string NewDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rkmcp_{label}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteGraph(string path, string modelName)
    {
        using var db = GraphDatabase.CreateNew(path);
        db.WriteGraph(
            new[] { new GraphNode { Id = "1", Kind = "level", Name = "L1" } },
            Array.Empty<GraphEdge>(),
            new Dictionary<string, string> { [GraphSchema.MetaKeys.ModelName] = modelName });
    }

    [Fact]
    public void Publish_MovesTempIntoPlace_AndReplacesExisting()
    {
        var local = NewDir("local");
        var shared = NewDir("shared");
        try
        {
            var store = new GraphStore(local);
            var target = Path.Combine(shared, "P", "Model.graph.db");

            var temp1 = store.CreateBuildTempPath();
            Assert.StartsWith(store.TempRoot, temp1);
            WriteGraph(temp1, "first");
            store.Publish(temp1, target);
            Assert.True(File.Exists(target));
            Assert.False(File.Exists(temp1));
            using (var ro = GraphDatabase.OpenReadOnly(target))
                Assert.Equal("first", ro.ReadMeta().ModelName);

            var temp2 = store.CreateBuildTempPath();
            WriteGraph(temp2, "second");
            store.Publish(temp2, target);
            using (var ro = GraphDatabase.OpenReadOnly(target))
                Assert.Equal("second", ro.ReadMeta().ModelName);

            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, "*.tmp-*"));
        }
        finally
        {
            Directory.Delete(local, true);
            Directory.Delete(shared, true);
        }
    }

    [Fact]
    public void Publish_MissingTemp_Throws()
    {
        var local = NewDir("local");
        try
        {
            var store = new GraphStore(local);
            Assert.Throws<FileNotFoundException>(() =>
                store.Publish(Path.Combine(local, "nope.graph.db"), Path.Combine(local, "out.graph.db")));
        }
        finally { Directory.Delete(local, true); }
    }

    [Fact]
    public void OpenForRead_LocalFile_IsUsedDirectly()
    {
        var local = NewDir("local");
        try
        {
            var store = new GraphStore(local);
            var path = Path.Combine(local, "P", "Model.graph.db");
            WriteGraph(path, "local");

            var handle = store.OpenForRead(path);
            Assert.False(handle.UsedLocalCache);
            Assert.False(handle.CopiedThisTime);
            Assert.Equal(Path.GetFullPath(path), handle.ReadPath);
            Assert.True(store.IsUnderLocalRoot(path));
        }
        finally { Directory.Delete(local, true); }
    }

    [Fact]
    public void OpenForRead_SharedFile_GoesThroughCache_AndRefreshesOnChange()
    {
        var local = NewDir("local");
        var shared = NewDir("shared");
        try
        {
            var store = new GraphStore(local);
            var path = Path.Combine(shared, "P", "Model.graph.db");
            WriteGraph(path, "v1");
            Assert.False(store.IsUnderLocalRoot(path));

            var first = store.OpenForRead(path);
            Assert.True(first.UsedLocalCache);
            Assert.True(first.CopiedThisTime);
            Assert.StartsWith(store.CacheRoot, first.ReadPath);
            Assert.Equal(GraphStore.CachePathFor(path, store.CacheRoot), first.ReadPath);
            using (var ro = GraphDatabase.OpenReadOnly(first.ReadPath))
                Assert.Equal("v1", ro.ReadMeta().ModelName);

            var second = store.OpenForRead(path);
            Assert.True(second.UsedLocalCache);
            Assert.False(second.CopiedThisTime);

            // Replace the shared file (new build) → cache is refreshed on the next read
            var temp = store.CreateBuildTempPath();
            WriteGraph(temp, "v2");
            File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddMinutes(1));
            store.Publish(temp, path);

            var third = store.OpenForRead(path);
            Assert.True(third.CopiedThisTime);
            using (var ro = GraphDatabase.OpenReadOnly(third.ReadPath))
                Assert.Equal("v2", ro.ReadMeta().ModelName);
        }
        finally
        {
            Directory.Delete(local, true);
            Directory.Delete(shared, true);
        }
    }

    [Fact]
    public void OpenForRead_MissingFile_Throws()
    {
        var local = NewDir("local");
        try
        {
            var store = new GraphStore(local);
            Assert.Throws<FileNotFoundException>(() => store.OpenForRead(Path.Combine(local, "missing.graph.db")));
        }
        finally { Directory.Delete(local, true); }
    }

    [Fact]
    public void CachePathFor_IsDeterministic_AndCaseInsensitive()
    {
        var a = GraphStore.CachePathFor(Path.Combine(Path.GetTempPath(), "Shared", "M.graph.db"), "/cache");
        var b = GraphStore.CachePathFor(Path.Combine(Path.GetTempPath(), "shared", "M.graph.db"), "/cache");
        Assert.Equal(a, b);
        Assert.EndsWith("M.graph.db", a);
        Assert.NotEqual(a, GraphStore.CachePathFor(Path.Combine(Path.GetTempPath(), "Other", "M.graph.db"), "/cache"));
    }
}
