using RevitMCP.Addin.Graph;
using Xunit;

namespace RevitMCP.Tests;

public class GraphFreshnessTests
{
    private static GraphMeta Meta(string version = "central:42:guid", long elements = 100, string model = "Model", int? schema = null)
    {
        var meta = new GraphMeta();
        meta.Values[GraphSchema.MetaKeys.CentralVersion] = version;
        meta.Values[GraphSchema.MetaKeys.ElementCount] = elements.ToString();
        meta.Values[GraphSchema.MetaKeys.ModelName] = model;
        meta.Values[GraphSchema.MetaKeys.SchemaVersion] = (schema ?? GraphSchema.SchemaVersion).ToString();
        meta.Values[GraphSchema.MetaKeys.BuiltAt] = "2026-01-01T00:00:00Z";
        return meta;
    }

    private static ModelVersionSignal Current(string version = "central:42:guid", long elements = 100, string model = "Model") =>
        new() { Value = version, ElementCount = elements, ModelName = model, Source = "BasicFileInfo" };

    [Fact]
    public void NoMeta_IsStale()
    {
        var r = GraphFreshness.Evaluate(null, Current());
        Assert.True(r.Stale);
        Assert.Contains("revit_graph_build", r.Reason);
        Assert.Equal(100, r.CurrentElementCount);
    }

    [Fact]
    public void MatchingVersionAndCount_IsFresh()
    {
        var r = GraphFreshness.Evaluate(Meta(), Current());
        Assert.False(r.Stale);
        Assert.Contains("match", r.Reason);
        Assert.Equal("2026-01-01T00:00:00Z", r.BuiltAt);
        Assert.Equal("central:42:guid", r.StoredVersion);
    }

    [Fact]
    public void VersionChanged_IsStale()
    {
        var r = GraphFreshness.Evaluate(Meta(), Current(version: "central:43:guid"));
        Assert.True(r.Stale);
        Assert.Contains("central:42:guid", r.Reason);
        Assert.Contains("central:43:guid", r.Reason);
    }

    [Fact]
    public void ElementCountChanged_IsStale_EvenWhenVersionMatches()
    {
        var r = GraphFreshness.Evaluate(Meta(elements: 100), Current(elements: 101));
        Assert.True(r.Stale);
        Assert.Contains("100", r.Reason);
        Assert.Contains("101", r.Reason);
    }

    [Fact]
    public void SchemaMismatch_IsStale()
    {
        var r = GraphFreshness.Evaluate(Meta(schema: GraphSchema.SchemaVersion + 1), Current());
        Assert.True(r.Stale);
        Assert.Contains("schema", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DifferentModelName_IsStale()
    {
        var r = GraphFreshness.Evaluate(Meta(model: "Other"), Current(model: "Model"));
        Assert.True(r.Stale);
        Assert.Contains("Other", r.Reason);
    }

    [Fact]
    public void UnknownVersion_FallsBackToElementCount()
    {
        var fresh = GraphFreshness.Evaluate(Meta(version: ""), Current(version: ""));
        Assert.False(fresh.Stale);
        Assert.Contains("unavailable", fresh.Reason);

        var stale = GraphFreshness.Evaluate(Meta(version: ""), Current(version: "", elements: 5));
        Assert.True(stale.Stale);
    }
}
