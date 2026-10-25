using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Documents;
using Xunit;

namespace RevitMCP.Tests;

public class LocalFileNamingTests
{
    [Theory]
    [InlineData(@"\\srv\projects\P125\P125_EL.rvt", "raul.kalev", "P125_EL_raul.kalev_mcp.rvt")]
    [InlineData(@"C:\c\Plan A (v2).rvt", "Raul Kalev", "Plan_A__v2_Raul_Kalev_mcp.rvt")]
    [InlineData(@"C:\c\x.rvt", "", "x_mcp.rvt")]
    [InlineData(@"C:\c\x.rvt", @"..\evil", "x_evil_mcp.rvt")]
    public void NameHoldsCentralAndUser(string central, string user, string expected)
        => Assert.Equal(expected, LocalFileNaming.FileName(central, user));

    [Fact]
    public void ExistingFileGetsATimestamp()
    {
        var now = new DateTime(2026, 10, 25, 14, 30, 5);
        Assert.Equal(@"C:\l\x_u_mcp.rvt", LocalFileNaming.PathIn(@"C:\l", @"C:\c\x.rvt", "u", _ => false, now));
        Assert.Equal(@"C:\l\x_u_mcp_20261025-143005.rvt", LocalFileNaming.PathIn(@"C:\l", @"C:\c\x.rvt", "u", _ => true, now));
    }

    [Fact]
    public void LongNamesAreCut()
        => Assert.True(LocalFileNaming.FileName(@"C:\" + new string('a', 300) + ".rvt", "u").Length <= LocalFileNaming.MaxStemLength + 4);
}

public class OpenOptionsBuilderTests
{
    private static OpenRequest Parse(object args)
        => OpenOptionsBuilder.Parse(JObject.FromObject(args).Properties().ToDictionary(p => p.Name, p => (object?)p.Value));

    [Fact]
    public void DefaultsAreANewLocalOfACentralWithAllWorksetsNotActivated()
    {
        var r = Parse(new { path = @"\\srv\P125.rvt" });
        Assert.True(r.IsValid);
        Assert.Equal(OpenMode.Auto, r.Mode);
        Assert.Equal(WorksetChoice.All, r.Worksets);
        Assert.False(r.Activate);
        Assert.Equal(OpenMode.NewLocal, OpenOptionsBuilder.Effective(r.Mode, isWorkshared: true, isCentral: true, out _));
        Assert.Equal(OpenMode.Plain, OpenOptionsBuilder.Effective(r.Mode, isWorkshared: false, isCentral: false, out _));
    }

    [Theory]
    [InlineData("editable", WorksetChoice.Editable)]
    [InlineData("lastViewed", WorksetChoice.LastViewed)]
    [InlineData("closeAll", WorksetChoice.CloseAll)]
    [InlineData("EL, Shared Levels", WorksetChoice.Named)]
    public void WorksetChoices(string value, WorksetChoice expected) => Assert.Equal(expected, Parse(new { path = "C:/a.rvt", worksets = value }).Worksets);

    [Fact]
    public void WorksetNamesAsArrayAndTheirIds()
    {
        var r = Parse(new { path = "C:/a.rvt", worksets = new[] { "EL", "Nope" } });
        var available = new List<(int, string, string)> { (1, "EL", ""), (2, "ARH", "maria"), (3, "Shared Levels", "") };

        var ids = OpenOptionsBuilder.WorksetsToOpen(r, available, "raul", out var unknown);

        Assert.Equal(new[] { 1 }, ids);
        Assert.Equal(new[] { "Nope" }, unknown);
        var editable = OpenOptionsBuilder.WorksetsToOpen(Parse(new { path = "C:/a.rvt", worksets = "editable" }), available, "raul", out _);
        Assert.Equal(new[] { 1, 3 }, editable);
    }

    [Fact]
    public void RefusesServerPathsAndBadModes()
    {
        Assert.Contains(Parse(new { path = "RSN://srv/P125.rvt" }).Errors, e => e.Contains("Revit Server"));
        Assert.Contains(Parse(new { path = "C:/a.ifc" }).Errors, e => e.Contains(".rvt"));
        Assert.Contains(Parse(new { path = "C:/a.rvt", mode = "copy" }).Errors, e => e.Contains("mode"));
        Assert.Contains(Parse(new { }).Errors, e => e.Contains("path is required"));
        OpenOptionsBuilder.Effective(OpenMode.NewLocal, isWorkshared: true, isCentral: false, out var error);
        Assert.Contains("local copy", error);
    }
}

public class WorkerDocumentMatchingTests
{
    [Fact]
    public void TheAgentsLocalIsFoundByTitleWhileTheCentralPathIsAmbiguous()
    {
        var docs = new List<OpenDocumentInfo>
        {
            new() { Title = "P125_EL_raul", PathName = @"C:\Users\raul\P125_EL_raul.rvt", CentralPath = @"\\srv\P125_EL.rvt", IsActive = true },
            new() { Title = "P125_EL_raul_mcp", PathName = @"C:\Users\raul\AppData\Local\RevitMCP\Locals\P125_EL_raul_mcp.rvt", CentralPath = @"\\srv\P125_EL.rvt" }
        };

        Assert.Equal(1, DocumentTargetMatcher.Match(docs, "P125_EL_raul_mcp").Index);
        Assert.Equal(1, DocumentTargetMatcher.Match(docs, @"C:\Users\raul\AppData\Local\RevitMCP\Locals\P125_EL_raul_mcp.rvt").Index);
        Assert.Contains("matches 2 open documents", DocumentTargetMatcher.Match(docs, @"\\srv\P125_EL.rvt").Error);
    }
}
