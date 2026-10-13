using RevitMCP.Core.Configuration;
using Xunit;

namespace RevitMCP.Tests;

public class BuildInfoTests
{
    [Theory]
    [InlineData("1.0.0+bd4b933dc73fbcf2f8dbefd8c61e30fcf73f7f09", "bd4b933")]
    [InlineData("1.0.0+BD4B933", "bd4b933")]
    [InlineData("1.0.0", null)]
    [InlineData("1.0.0+local", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void CommitOf_ReadsTheSourceRevision(string? informationalVersion, string? expected) =>
        Assert.Equal(expected, BuildInfo.CommitOf(informationalVersion));

    [Fact]
    public void PackageCommits_ReadsEveryLine()
    {
        var text = "13.10.2026 22:51:41,41 (4a16f34)\r\n13.10.2026 23:00:34 (04ea7ad) Addin\\2024 only: Revit 2024 startup fix; the rest is 4a16f34";
        Assert.Equal(["4a16f34", "04ea7ad"], BuildInfo.PackageCommits(text));
        Assert.Empty(BuildInfo.PackageCommits("13.10.2026 (unknown)"));
        Assert.Empty(BuildInfo.PackageCommits(null));
    }

    [Fact]
    public void MatchesPackage_IsUnknownWithoutBothSides()
    {
        Assert.True(BuildInfo.MatchesPackage("04ea7ad", ["4a16f34", "04ea7ad"]));
        Assert.False(BuildInfo.MatchesPackage("3d3325f", ["4a16f34"]));
        Assert.Null(BuildInfo.MatchesPackage(null, ["4a16f34"]));
        Assert.Null(BuildInfo.MatchesPackage("3d3325f", []));
    }

    [Fact]
    public void Describe_OutsideAPackage_SaysSo()
    {
        var info = BuildInfo.Describe(typeof(BuildInfo).Assembly, levelsUpToPackage: 1, restartHint: "Restart.");
        Assert.Null(info["matchesPackage"]);
        Assert.Contains("development build", (string)info["note"]!);
    }
}
