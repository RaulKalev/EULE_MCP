using System.Text.Json.Nodes;
using RevitMCP.Addin.Graph;
using Xunit;

namespace RevitMCP.Tests;

public class GraphPathResolverTests
{
    private static readonly string LocalRoot = Path.Combine(Path.GetTempPath(), "rkmcp_graph_local");

    [Fact]
    public void SanitizeSegment_ReplacesInvalidCharacters_AndFallsBack()
    {
        Assert.Equal("1626_Office", GraphPathResolver.SanitizeSegment("1626/Office", "x"));
        Assert.Equal("A_B", GraphPathResolver.SanitizeSegment(@"A\B", "x"));
        Assert.Equal("Model", GraphPathResolver.SanitizeSegment("  Model.  ", "x"));
        Assert.Equal("fallback", GraphPathResolver.SanitizeSegment("", "fallback"));
        Assert.Equal("fallback", GraphPathResolver.SanitizeSegment("???", "fallback"));
        Assert.Equal("fallback", GraphPathResolver.SanitizeSegment(null, "fallback"));
        Assert.True(GraphPathResolver.SanitizeSegment(new string('a', 300), "x").Length <= 100);
    }

    [Fact]
    public void Resolve_UsesLocalFallback_WhenNothingConfigured()
    {
        var loc = GraphPathResolver.Resolve(null, null, null, null, "1626", "Model", LocalRoot);
        Assert.Equal(GraphPathResolver.SourceLocalFallback, loc.RootSource);
        Assert.Equal(Path.GetFullPath(LocalRoot), loc.Root);
        Assert.Equal(Path.Combine(Path.GetFullPath(LocalRoot), "1626", "Model.graph.db"), loc.DatabasePath);
        Assert.Equal("1626", loc.ProjectSegment);
        Assert.Equal("Model", loc.ModelSegment);
    }

    [Fact]
    public void Resolve_UsesConfiguredSharedFolder_WithSource()
    {
        var shared = Path.Combine(Path.GetTempPath(), "rkmcp_shared");
        var loc = GraphPathResolver.Resolve(null, null, shared, GraphPathResolver.SourceCompanyConfig, "", "Model", LocalRoot);
        Assert.Equal(GraphPathResolver.SourceCompanyConfig, loc.RootSource);
        Assert.Equal(Path.Combine(Path.GetFullPath(shared), "_unfiled", "Model.graph.db"), loc.DatabasePath);
    }

    [Fact]
    public void Resolve_ArgumentOverridesConfig_AndDbPathOverridesEverything()
    {
        var shared = Path.Combine(Path.GetTempPath(), "rkmcp_shared_cfg");
        var argFolder = Path.Combine(Path.GetTempPath(), "rkmcp_shared_arg");

        var byArg = GraphPathResolver.Resolve(null, argFolder, shared, GraphPathResolver.SourceUserConfig, "P", "M", LocalRoot);
        Assert.Equal(GraphPathResolver.SourceArgument, byArg.RootSource);
        Assert.StartsWith(Path.GetFullPath(argFolder), byArg.DatabasePath);

        var explicitDb = Path.Combine(Path.GetTempPath(), "somewhere", "custom.graph.db");
        var byPath = GraphPathResolver.Resolve(explicitDb, argFolder, shared, null, "P", "M", LocalRoot);
        Assert.Equal(GraphPathResolver.SourceExplicitPath, byPath.RootSource);
        Assert.Equal(Path.GetFullPath(explicitDb), byPath.DatabasePath);
    }

    [Fact]
    public void Resolve_UntitledModel_UsesPlaceholders()
    {
        var loc = GraphPathResolver.Resolve(null, null, null, null, null, null, LocalRoot);
        Assert.EndsWith(Path.Combine("_unfiled", "_untitled.graph.db"), loc.DatabasePath);
    }

    [Fact]
    public void ExtractSharedFolder_ReadsNestedKey()
    {
        Assert.Null(GraphPathResolver.ExtractSharedFolder(null));
        Assert.Null(GraphPathResolver.ExtractSharedFolder(new JsonObject()));
        Assert.Null(GraphPathResolver.ExtractSharedFolder(JsonNode.Parse("{\"graph\":{}}")!.AsObject()));
        Assert.Null(GraphPathResolver.ExtractSharedFolder(JsonNode.Parse("{\"graph\":{\"sharedFolder\":\"  \"}}")!.AsObject()));
        Assert.Equal(@"\\server\graphs",
            GraphPathResolver.ExtractSharedFolder(JsonNode.Parse("{\"graph\":{\"sharedFolder\":\" \\\\\\\\server\\\\graphs \"}}")!.AsObject()));
    }

    [Fact]
    public void LoadConfiguredSharedFolder_PrefersUserOverCompany_AndHandlesMissingFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rkmcp_graphcfg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var user = Path.Combine(dir, "user.config.json");
        var company = Path.Combine(dir, "company.config.json");
        try
        {
            Assert.Equal((null, null), GraphPathResolver.LoadConfiguredSharedFolder(user, company));

            File.WriteAllText(company, "{\"graph\":{\"sharedFolder\":\"C:/company\"}}");
            var fromCompany = GraphPathResolver.LoadConfiguredSharedFolder(user, company);
            Assert.Equal("C:/company", fromCompany.Folder);
            Assert.Equal(GraphPathResolver.SourceCompanyConfig, fromCompany.Source);

            File.WriteAllText(user, "{\"graph\":{\"sharedFolder\":\"C:/user\"}}");
            var fromUser = GraphPathResolver.LoadConfiguredSharedFolder(user, company);
            Assert.Equal("C:/user", fromUser.Folder);
            Assert.Equal(GraphPathResolver.SourceUserConfig, fromUser.Source);

            File.WriteAllText(user, "not json");
            Assert.Equal("C:/company", GraphPathResolver.LoadConfiguredSharedFolder(user, company).Folder);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
