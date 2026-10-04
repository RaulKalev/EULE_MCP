using RevitMCP.Addin.Query;
using Xunit;

namespace RevitMCP.Tests;

public class ParameterResolutionTests
{
    private static ParameterCandidate User(string name, string storage = "Integer", bool type = false) =>
        new() { Name = name, StorageType = storage, IsTypeParameter = type };

    private static ParameterCandidate BuiltIn(string name, string builtIn, string storage = "Double") =>
        new() { Name = name, BuiltInParameter = builtIn, StorageType = storage };

    private static ParameterCandidate Shared(string name, string guid) =>
        new() { Name = name, Guid = guid, StorageType = "String" };

    // The socket families from #86: a user "Offset" next to the built-in host offset / elevation.
    private static List<ParameterCandidate> SocketParameters(bool withUserOffset = true)
    {
        var list = new List<ParameterCandidate>
        {
            BuiltIn("Offset from Host", "INSTANCE_FREE_HOST_OFFSET_PARAM"),
            BuiltIn("Elevation from Level", "INSTANCE_ELEVATION_PARAM"),
            BuiltIn("Comments", "ALL_MODEL_INSTANCE_COMMENTS", "String"),
        };
        if (withUserOffset) list.Add(User("Offset"));
        return list;
    }

    [Fact]
    public void ExactNameBeatsPartialBuiltInMatch()
    {
        var r = ParameterResolution.Resolve(SocketParameters(), new ParameterTarget { Name = "Offset" });

        Assert.NotNull(r.Selected);
        Assert.Equal("Offset", r.Selected!.Name);
        Assert.False(r.Selected.IsBuiltIn);
        Assert.Equal("exact", r.MatchedBy);
    }

    [Fact]
    public void ExactMatchIsOrderIndependent()
    {
        var reversed = SocketParameters();
        reversed.Reverse();

        var r = ParameterResolution.Resolve(reversed, new ParameterTarget { Name = "offset" });

        Assert.Equal("Offset", r.Selected!.Name);
    }

    [Fact]
    public void MissingExactWithExactMatchFlagWritesNothing()
    {
        var r = ParameterResolution.Resolve(SocketParameters(withUserOffset: false),
            new ParameterTarget { Name = "Offset", ExactMatch = true });

        Assert.Null(r.Selected);
        Assert.Contains("not found", r.Problem);
    }

    [Fact]
    public void SinglePartialMatchIsUsedAndFlagged()
    {
        var r = ParameterResolution.Resolve(SocketParameters(withUserOffset: false),
            new ParameterTarget { Name = "Host Offset" });

        // "Host Offset" is not contained in "Offset from Host" — nothing matches.
        Assert.Null(r.Selected);

        r = ParameterResolution.Resolve(SocketParameters(withUserOffset: false),
            new ParameterTarget { Name = "from Host" });
        Assert.Equal("Offset from Host", r.Selected!.Name);
        Assert.Equal("partial", r.MatchedBy);
    }

    [Fact]
    public void SeveralPartialMatchesAreAmbiguous()
    {
        var parameters = new List<ParameterCandidate>
        {
            BuiltIn("Offset from Host", "INSTANCE_FREE_HOST_OFFSET_PARAM"),
            User("Symbol Offset"),
        };

        var r = ParameterResolution.Resolve(parameters, new ParameterTarget { Name = "Offset" });

        Assert.Null(r.Selected);
        Assert.True(r.IsAmbiguous);
        Assert.Equal(2, r.Candidates.Count);
        Assert.Contains("ambiguous", r.Problem);
        Assert.Contains("INSTANCE_FREE_HOST_OFFSET_PARAM", r.Problem);
    }

    [Fact]
    public void DuplicateExactNamesPreferUserOverBuiltIn()
    {
        var parameters = new List<ParameterCandidate>
        {
            BuiltIn("Offset", "SOME_BUILTIN_OFFSET"),
            User("Offset"),
        };

        var r = ParameterResolution.Resolve(parameters, new ParameterTarget { Name = "Offset" });

        Assert.False(r.Selected!.IsBuiltIn);
    }

    [Fact]
    public void DuplicateExactNamesPreferInstanceOverType()
    {
        var parameters = new List<ParameterCandidate> { User("Code", type: true), User("Code") };

        var r = ParameterResolution.Resolve(parameters, new ParameterTarget { Name = "Code" });

        Assert.False(r.Selected!.IsTypeParameter);
    }

    [Fact]
    public void UnbreakableExactTieIsAmbiguous()
    {
        var parameters = new List<ParameterCandidate> { Shared("Code", "a"), Shared("Code", "b") };

        var r = ParameterResolution.Resolve(parameters, new ParameterTarget { Name = "Code" });

        Assert.Null(r.Selected);
        Assert.True(r.IsAmbiguous);
    }

    [Fact]
    public void BuiltInSelectorIgnoresName()
    {
        var r = ParameterResolution.Resolve(SocketParameters(),
            new ParameterTarget { Name = "Offset", BuiltInParameter = "instance_elevation_param" });

        Assert.Equal("Elevation from Level", r.Selected!.Name);
        Assert.Equal("builtIn", r.MatchedBy);
    }

    [Fact]
    public void GuidSelectorMatchesWithOrWithoutBraces()
    {
        var parameters = new List<ParameterCandidate>
        {
            Shared("Code", "6f1a2b3c-0000-0000-0000-000000000001"),
            Shared("Code 2", "6f1a2b3c-0000-0000-0000-000000000002"),
        };

        var r = ParameterResolution.Resolve(parameters,
            new ParameterTarget { Guid = "{6F1A2B3C-0000-0000-0000-000000000002}" });

        Assert.Equal("Code 2", r.Selected!.Name);
        Assert.Equal("guid", r.MatchedBy);
    }

    [Fact]
    public void EmptySelectorFails()
    {
        var r = ParameterResolution.Resolve(SocketParameters(), new ParameterTarget());

        Assert.Null(r.Selected);
        Assert.NotNull(r.Problem);
    }
}
