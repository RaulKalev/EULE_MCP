using System.Text.Json.Nodes;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Services;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#90: resolving the 'document' argument, pairing chat-provided files with links, long-running timeouts.</summary>
public class MultiDocumentTests
{
    private static readonly List<OpenDocumentInfo> Documents =
    [
        new() { Title = "ITM_EN", PathName = @"C:\Projects\ITM\ITM_EN.rvt", IsActive = true },
        new() { Title = "Kool_EN_local", PathName = @"C:\Local\Kool_EN_local.rvt", CentralPath = @"\\server\central\Kool_EN.rvt" },
        new() { Title = "Lasteaed_EN.rvt", PathName = @"D:\Lasteaed\Lasteaed_EN.rvt" }
    ];

    [Fact]
    public void EmptySelector_ResolvesToTheActiveDocument()
    {
        var result = DocumentTargetMatcher.Match(Documents, "");
        Assert.True(result.Success);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public void EmptySelector_WithoutActiveDocument_IsAnError()
    {
        var result = DocumentTargetMatcher.Match([new OpenDocumentInfo { Title = "A" }], null);
        Assert.False(result.Success);
    }

    [Theory]
    [InlineData(@"c:/local/kool_en_local.rvt", 1)]
    [InlineData(@"\\server\central\Kool_EN.rvt", 1)]
    [InlineData("lasteaed_en.rvt", 2)]
    [InlineData("Lasteaed_EN", 2)]
    [InlineData("ITM_EN.rvt", 0)]
    public void Selector_MatchesByPathTitleOrStem(string selector, int expected)
    {
        var result = DocumentTargetMatcher.Match(Documents, selector);
        Assert.True(result.Success, result.Error);
        Assert.Equal(expected, result.Index);
    }

    [Fact]
    public void Selector_MatchingTwoDocuments_IsAnErrorNotAGuess()
    {
        var docs = new List<OpenDocumentInfo>
        {
            new() { Title = "Model", PathName = @"C:\A\Model.rvt" },
            new() { Title = "Model", PathName = @"C:\B\Model.rvt" }
        };
        var result = DocumentTargetMatcher.Match(docs, "Model");
        Assert.False(result.Success);
        Assert.Contains("2 open documents", result.Error);
        Assert.True(DocumentTargetMatcher.Match(docs, @"C:\B\Model.rvt").Index == 1);
    }

    [Fact]
    public void UnknownSelector_ListsTheOpenDocuments()
    {
        var result = DocumentTargetMatcher.Match(Documents, "Nope");
        Assert.False(result.Success);
        Assert.Contains("ITM_EN", result.Error);
    }

    // ── Link classification ────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\x\plan.dwg", LinkKind.Dwg)]
    [InlineData(@"C:\x\ARH.IFC", LinkKind.Ifc)]
    [InlineData(@"C:\x\KONS.rvt", LinkKind.Rvt)]
    public void FileKind_ComesFromTheExtension(string path, LinkKind kind) =>
        Assert.Equal(kind, LinkFileMatcher.KindFromFilePath(path));

    [Theory]
    [InlineData(@"C:\x\ARH.ifc.RVT")]
    [InlineData(@"C:\x\plan.dxf")]
    public void CacheFilesAndOtherFormats_AreNotReloadSources(string path) =>
        Assert.Null(LinkFileMatcher.KindFromFilePath(path));

    [Fact]
    public void RevitLinkTypes_AreClassifiedAsIfcOrRvt()
    {
        Assert.Equal(LinkKind.Ifc, LinkFileMatcher.ClassifyRevitLink("ARH.ifc", @"C:\x\ARH.ifc.RVT"));
        Assert.Equal(LinkKind.Ifc, LinkFileMatcher.ClassifyRevitLink("ARH.ifc", ""));
        Assert.Equal(LinkKind.Rvt, LinkFileMatcher.ClassifyRevitLink("KONS.rvt", @"C:\x\KONS.rvt"));
        Assert.Equal(LinkKind.Dwg, LinkFileMatcher.ClassifyCadLink("plan.dwg", ""));
        Assert.Equal(LinkKind.OtherCad, LinkFileMatcher.ClassifyCadLink("plan.dgn", @"C:\x\plan.dgn"));
    }

    // ── Pairing files with links ───────────────────────────────────────────────

    private static readonly List<LinkCandidate> Links =
    [
        new() { TypeId = 10, Name = "ITM_asendiplaan_2026-08-01.dwg", Kind = LinkKind.Dwg, Path = @"C:\old\ITM_asendiplaan_2026-08-01.dwg" },
        new() { TypeId = 20, Name = "ARH_2026-08-01.ifc", Kind = LinkKind.Ifc, Path = @"C:\old\ARH_2026-08-01.ifc" },
        new() { TypeId = 30, Name = "KONS_2026-08-01.ifc", Kind = LinkKind.Ifc, Path = @"C:\old\KONS_2026-08-01.ifc" }
    ];

    [Fact]
    public void OneDwgLinkAndOneDwgFile_PairDirectly()
    {
        var pairs = LinkFileMatcher.Match(Links, [new LinkReloadRequest { Path = @"C:\new\asukoht.dwg" }]);
        Assert.Equal(LinkReloadPair.Matched, pairs[0].Status);
        Assert.Equal(10, pairs[0].Link!.TypeId);
    }

    [Fact]
    public void TwoIfcFiles_PairByName_IgnoringDates()
    {
        var pairs = LinkFileMatcher.Match(Links,
        [
            new LinkReloadRequest { Path = @"C:\new\KONS_2026-10-01.ifc" },
            new LinkReloadRequest { Path = @"C:\new\ARH_2026-10-01.ifc" }
        ]);
        Assert.All(pairs, p => Assert.Equal(LinkReloadPair.Matched, p.Status));
        Assert.Equal(30, pairs[0].Link!.TypeId);
        Assert.Equal(20, pairs[1].Link!.TypeId);
    }

    [Fact]
    public void TwoIfcFilesWithUnrelatedNames_AreAmbiguous_WithAProposal()
    {
        var pairs = LinkFileMatcher.Match(Links,
        [
            new LinkReloadRequest { Path = @"C:\new\model_a.ifc" },
            new LinkReloadRequest { Path = @"C:\new\model_b.ifc" }
        ]);
        Assert.All(pairs, p => Assert.Equal(LinkReloadPair.Ambiguous, p.Status));
        Assert.All(pairs, p => Assert.NotNull(p.Link));
        Assert.NotEqual(pairs[0].Link!.TypeId, pairs[1].Link!.TypeId);
    }

    [Fact]
    public void NamingTheLink_ResolvesAnAmbiguity()
    {
        var pairs = LinkFileMatcher.Match(Links,
        [
            new LinkReloadRequest { Path = @"C:\new\model_a.ifc", Link = "KONS_2026-08-01.ifc" },
            new LinkReloadRequest { Path = @"C:\new\model_b.ifc" }
        ]);
        Assert.Equal(LinkReloadPair.Matched, pairs[0].Status);
        Assert.Equal(30, pairs[0].Link!.TypeId);
        // The remaining file has only one unclaimed IFC link left.
        Assert.Equal(LinkReloadPair.Matched, pairs[1].Status);
        Assert.Equal(20, pairs[1].Link!.TypeId);
    }

    [Fact]
    public void NamingTheLink_ByTypeId_Works()
    {
        var pairs = LinkFileMatcher.Match(Links, [new LinkReloadRequest { Path = @"C:\new\x.ifc", Link = "20" }]);
        Assert.Equal(LinkReloadPair.Matched, pairs[0].Status);
        Assert.Equal(20, pairs[0].Link!.TypeId);
    }

    [Fact]
    public void NamingAnUnknownLink_IsUnmatched_AndListsTheLinks()
    {
        var pairs = LinkFileMatcher.Match(Links, [new LinkReloadRequest { Path = @"C:\new\x.ifc", Link = "VK" }]);
        Assert.Equal(LinkReloadPair.Unmatched, pairs[0].Status);
        Assert.Contains("ARH_2026-08-01.ifc", pairs[0].Reason);
    }

    [Fact]
    public void FileOfATypeWithoutLinks_IsUnmatched()
    {
        var pairs = LinkFileMatcher.Match(Links, [new LinkReloadRequest { Path = @"C:\new\KV.rvt" }]);
        Assert.Equal(LinkReloadPair.Unmatched, pairs[0].Status);
    }

    [Fact]
    public void MoreFilesThanLinks_LeavesTheExtraUnmatched()
    {
        var pairs = LinkFileMatcher.Match(Links,
        [
            new LinkReloadRequest { Path = @"C:\new\a.dwg" },
            new LinkReloadRequest { Path = @"C:\new\b.dwg" }
        ]);
        Assert.Contains(pairs, p => p.Status == LinkReloadPair.Unmatched);
        Assert.DoesNotContain(pairs, p => p.Status == LinkReloadPair.Matched);
    }

    [Fact]
    public void IfcCachePath_IsRejected()
    {
        var pairs = LinkFileMatcher.Match(Links, [new LinkReloadRequest { Path = @"C:\new\ARH.ifc.RVT" }]);
        Assert.Equal(LinkReloadPair.Invalid, pairs[0].Status);
    }

    [Fact]
    public void SamePathAsTheLink_ScoresHighest()
    {
        Assert.True(LinkFileMatcher.Similarity(@"c:/old/ARH_2026-08-01.ifc", Links[1]) > 1.0);
        Assert.Equal("ARH_2026-08-01", LinkFileMatcher.Stem("ARH_2026-08-01.ifc.RVT"));
    }

    // ── Long-running tools ─────────────────────────────────────────────────────

    [Fact]
    public void LongRunningTools_GetALongerTimeout_OthersKeepTheDefault()
    {
        Assert.Equal(LongRunningTools.TimeoutSeconds * 1000, LongRunningTools.TimeoutMsFor("revit_reload_links_from", 30_000));
        Assert.Equal(LongRunningTools.TimeoutSeconds * 1000, LongRunningTools.TimeoutMsFor("revit_sync_with_central", 30_000));
        Assert.Equal(30_000, LongRunningTools.TimeoutMsFor("revit_get_elements_info", 30_000));
        Assert.Equal(30_000, LongRunningTools.TimeoutMsFor(null, 30_000));
    }

    // ── Direct Edit by default (unattended runs) ───────────────────────────────

    [Theory]
    [InlineData("{\"approval\":{\"directEditByDefault\":true}}", true)]
    [InlineData("{\"approval\":{\"directEditByDefault\":false}}", false)]
    [InlineData("{\"approval\":{\"directEditByDefault\":\"true\"}}", false)]
    [InlineData("{\"approval\":{}}", false)]
    [InlineData("{\"village\":{\"enabled\":true}}", false)]
    public void DirectEditByDefault_NeedsAnExplicitBooleanTrue(string json, bool expected) =>
        Assert.Equal(expected, DirectEditDefault.IsEnabled(JsonNode.Parse(json) as JsonObject));

    [Fact]
    public void DirectEditByDefault_IsOffWithoutConfig() =>
        Assert.False(DirectEditDefault.IsEnabled(null));
}
