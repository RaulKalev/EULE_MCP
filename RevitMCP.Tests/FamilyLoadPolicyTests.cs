using RevitMCP.Addin.Families;
using RevitMCP.Addin.FileSystem;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#102: ifExists parsing, the load callbacks' answers and the family file selection.</summary>
public class FamilyLoadPolicyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rmcp-fam-" + Guid.NewGuid().ToString("N"));

    public FamilyLoadPolicyTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch { }
    }

    private FilePathPolicy Policy(bool allowNetwork = false) => new(new FileAccessPolicyConfig
    {
        AllowedReadRoots = { _folder },
        AllowNetworkPaths = allowNetwork
    });

    private string Rfa(string name, int bytes = 16)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Theory]
    [InlineData("", FamilyIfExists.Skip)]
    [InlineData("skip", FamilyIfExists.Skip)]
    [InlineData("overwrite", FamilyIfExists.Overwrite)]
    [InlineData("overwriteParameters", FamilyIfExists.OverwriteParameters)]
    [InlineData("overwrite_parameters", FamilyIfExists.OverwriteParameters)]
    [InlineData("OverwriteAll", FamilyIfExists.OverwriteAll)]
    public void ParsesIfExists(string value, FamilyIfExists expected)
    {
        Assert.True(FamilyLoadPolicy.TryParseIfExists(value, FamilyIfExists.Skip, out var policy, out var error));
        Assert.Null(error);
        Assert.Equal(expected, policy);
    }

    [Fact]
    public void RejectsAnUnknownIfExists()
    {
        Assert.False(FamilyLoadPolicy.TryParseIfExists("replace", FamilyIfExists.Skip, out _, out var error));
        Assert.Contains("overwriteParameters", error);
    }

    [Fact]
    public void EmptyIfExistsUsesTheFallback()
    {
        Assert.True(FamilyLoadPolicy.TryParseIfExists(null, FamilyIfExists.Overwrite, out var policy, out _));
        Assert.Equal(FamilyIfExists.Overwrite, policy);
    }

    [Fact]
    public void DecisionsFollowThePolicy()
    {
        Assert.False(FamilyLoadPolicy.Decide(FamilyIfExists.Skip, shared: false).Load);

        var overwrite = FamilyLoadPolicy.Decide(FamilyIfExists.Overwrite, shared: false);
        Assert.True(overwrite.Load);
        Assert.False(overwrite.OverwriteParameterValues);

        var parameters = FamilyLoadPolicy.Decide(FamilyIfExists.OverwriteParameters, shared: true);
        Assert.True(parameters.OverwriteParameterValues);
        Assert.False(parameters.SharedFromFile);   // shared nested families keep the project's version

        var all = FamilyLoadPolicy.Decide(FamilyIfExists.OverwriteAll, shared: true);
        Assert.True(all.Load);
        Assert.True(all.OverwriteParameterValues);
        Assert.True(all.SharedFromFile);
    }

    [Theory]
    [InlineData(false, FamilyIfExists.Skip, "willLoad")]
    [InlineData(true, FamilyIfExists.Skip, "willSkip")]
    [InlineData(true, FamilyIfExists.OverwriteParameters, "willOverwrite")]
    public void PlannedStatus(bool loaded, FamilyIfExists policy, string expected) =>
        Assert.Equal(expected, FamilyLoadPolicy.PlannedStatus(loaded, policy));

    [Fact]
    public void FolderAndPattern_SelectRfaFiles_AndDropBackups()
    {
        Rfa("Socket.rfa");
        Rfa("Socket.0003.rfa");
        Rfa("Switch.rfa");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "x");

        var selection = FamilyLoadPolicy.SelectFiles(Array.Empty<string>(), _folder, null, recursive: false, Policy());

        Assert.Null(selection.Error);
        Assert.Equal(new[] { "Socket", "Switch" }, selection.Valid.Select(f => f.FamilyName));
        Assert.Contains(selection.Files, f => f.Status == "backup");
        Assert.DoesNotContain(selection.Files, f => f.Path.EndsWith("notes.txt"));
    }

    [Fact]
    public void Paths_AreValidated_OneByOne()
    {
        var good = Rfa("Good.rfa");
        var empty = Rfa("Empty.rfa", 0);
        var txt = Path.Combine(_folder, "Readme.txt");
        File.WriteAllText(txt, "x");
        var missing = Path.Combine(_folder, "Missing.rfa");

        var selection = FamilyLoadPolicy.SelectFiles(new[] { good, empty, txt, missing, good }, null, null, false, Policy());

        Assert.Null(selection.Error);
        Assert.Single(selection.Valid);
        Assert.Equal(4, selection.Files.Count);   // the repeated path is loaded once
        Assert.Contains(selection.Files, f => f.Path == empty && f.Reason!.Contains("empty"));
        Assert.Contains(selection.Files, f => f.Path == txt && f.Reason!.Contains(".rfa"));
        Assert.Contains(selection.Files, f => f.Path == missing && f.Reason!.Contains("does not exist"));
        Assert.Equal(3, selection.Warnings.Count);
    }

    [Fact]
    public void TwoFilesOfTheSameFamilyName_LoadOnlyTheFirst()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "b"));
        var first = Rfa("Lamp.rfa");
        var second = Path.Combine(_folder, "b", "Lamp.rfa");
        File.WriteAllBytes(second, new byte[8]);

        var selection = FamilyLoadPolicy.SelectFiles(new[] { first, second }, null, null, false, Policy());

        Assert.Equal(first, Assert.Single(selection.Valid).Path);
        Assert.Equal("duplicate", selection.Files[1].Status);
    }

    [Fact]
    public void PathsOutsideTheReadPolicy_AreRefused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "rmcp-outside-" + Guid.NewGuid().ToString("N") + ".rfa");
        var selection = FamilyLoadPolicy.SelectFiles(new[] { outside }, null, null, false, Policy());
        Assert.Empty(selection.Valid);
        Assert.Contains("outside all allowed roots", selection.Files[0].Reason);
    }

    [Fact]
    public void NetworkPaths_NeedAllowNetworkPaths()
    {
        var selection = FamilyLoadPolicy.SelectFiles(new[] { @"\\server\share\Lamp.rfa" }, null, null, false, Policy());
        Assert.Contains("Network paths", selection.Files[0].Reason);
    }

    [Fact]
    public void TraversalIsRefused()
    {
        var selection = FamilyLoadPolicy.SelectFiles(new[] { Path.Combine(_folder, "..", "x.rfa") }, null, null, false, Policy());
        Assert.Contains("traversal", selection.Files[0].Reason);
    }

    [Fact]
    public void MoreThanFiftyFiles_IsAnError()
    {
        for (var i = 0; i < FamilyLoadPolicy.MaxFilesPerCall + 1; i++) Rfa($"F{i:00}.rfa");
        var selection = FamilyLoadPolicy.SelectFiles(Array.Empty<string>(), _folder, "*.rfa", false, Policy());
        Assert.Contains("at most 50", selection.Error);
    }

    [Fact]
    public void NothingNamed_IsAnError()
    {
        Assert.NotNull(FamilyLoadPolicy.SelectFiles(Array.Empty<string>(), null, null, false, Policy()).Error);
        Assert.Contains("does not exist", FamilyLoadPolicy.SelectFiles(Array.Empty<string>(), Path.Combine(_folder, "nope"), null, false, Policy()).Error);
        Assert.Contains("No file in folder", FamilyLoadPolicy.SelectFiles(Array.Empty<string>(), _folder, "*.rfa", false, Policy()).Error);
    }

    [Fact]
    public void Recursive_FindsSubfolders()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "sub"));
        File.WriteAllBytes(Path.Combine(_folder, "sub", "Deep.rfa"), new byte[4]);
        Assert.Empty(FamilyLoadPolicy.SelectFiles(Array.Empty<string>(), _folder, null, false, Policy()).Valid);
        Assert.Single(FamilyLoadPolicy.SelectFiles(Array.Empty<string>(), _folder, null, true, Policy()).Valid);
    }
}
