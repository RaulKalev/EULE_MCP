using RevitMCP.Addin.Skills.Tasks.Security;
using RevitMCP.Core.Models.Issues;
using Xunit;

namespace RevitMCP.Tests;

public class ValveLabipaasAuditTests
{
    // ── ApplyMarkupAndRound ───────────────────────────────────────────────────

    [Theory]
    [InlineData(1000.0, 30.0, 100, 1300)]   // 1000 * 1.30 = 1300 → exactly 1300
    [InlineData(1001.0, 30.0, 100, 1400)]   // 1001 * 1.30 = 1301.3 → ceil to 1400
    [InlineData(769.0,  30.0, 100,  800)]   // 769  * 1.30 = 999.7  → ceil to 1000? actually ceil(999.7/100)*100 = 1000
    [InlineData(500.0,  30.0, 100,  700)]   // 500  * 1.30 = 650    → ceil(650/100)*100 = 700
    [InlineData(600.0,  30.0, 100,  800)]   // 600  * 1.30 = 780    → ceil(780/100)*100 = 800
    [InlineData(0.0,    30.0, 100,    0)]   // zero input → 0
    [InlineData(100.0,   0.0, 100,  100)]   // no markup → 100
    [InlineData(150.0,  30.0, 100,  200)]   // 150 * 1.30 = 195 → ceil to 200
    public void ApplyMarkupAndRound_FollowsBusinessRule(
        double rawMeters, double markupPct, int roundTo, int expected)
    {
        var result = ValveLabipaasRules.ApplyMarkupAndRound(rawMeters, markupPct, roundTo);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ApplyMarkupAndRound_ZeroRoundUp_ReturnsZero()
    {
        // Guard against division-by-zero
        var result = ValveLabipaasRules.ApplyMarkupAndRound(500.0, 30.0, roundUpToMeters: 0);
        Assert.Equal(0, result);
    }

    [Fact]
    public void ApplyMarkupAndRound_NegativeRaw_ReturnsZero()
    {
        var result = ValveLabipaasRules.ApplyMarkupAndRound(-100.0, 30.0, 100);
        Assert.Equal(0, result);
    }

    // ── Multi-source mapping: row 4.2 (Kaardilugeja laiendusmoodul) ──────────

    [Fact]
    public void Row42_SumsTwoFamilyTypePairs()
    {
        // Row 4.2 maps: EN_Keskseade_LPS_1:Reader expander + EN_Keskseade_LPS_1:ISC
        var row = ValveLabipaasRowDefinitions.AllRows.Single(r => r.Position == "4.2");

        var counts = new Dictionary<(string, string), int>
        {
            [("EN_Keskseade_LPS_1", "Reader expander")] = 5,
            [("EN_Keskseade_LPS_1", "ISC")]             = 3,
        };

        var total = ValveLabipaasAuditHelpers.SumMappings(row, counts);
        Assert.Equal(8, total);
    }

    [Fact]
    public void Row42_MissingOnePair_StillSumsPresent()
    {
        var row = ValveLabipaasRowDefinitions.AllRows.Single(r => r.Position == "4.2");

        var counts = new Dictionary<(string, string), int>
        {
            [("EN_Keskseade_LPS_1", "Reader expander")] = 7,
            // ISC not in model
        };

        var total = ValveLabipaasAuditHelpers.SumMappings(row, counts);
        Assert.Equal(7, total);
    }

    // ── Multi-source mapping: row 4.3 (Valve laiendusmoodul) ─────────────────

    [Fact]
    public void Row43_SumsTwoDifferentFamilies()
    {
        // Row 4.3 maps: EN_Keskseade_LPS_11:Zone expander + EN_Keskseade_LPS_1:ILAM
        var row = ValveLabipaasRowDefinitions.AllRows.Single(r => r.Position == "4.3");

        var counts = new Dictionary<(string, string), int>
        {
            [("EN_Keskseade_LPS_11", "Zone expander")] = 14,
            [("EN_Keskseade_LPS_1",  "ILAM")]          = 3,
        };

        var total = ValveLabipaasAuditHelpers.SumMappings(row, counts);
        Assert.Equal(17, total);  // matches the user's example "14+3=17"
    }

    // ── Procurement-only rows (4.14, 4.15) ───────────────────────────────────

    [Fact]
    public void ProcurementOnlyRows_ProduceInfoIssues()
    {
        int seq = 0;
        var rows = new List<ValveLabipaasAuditRow>
        {
            new() { Position = "4.14", ItemName = "Raadiopult",      RowType = "procurement", BeforeValue = 5, AfterValue = 5 },
            new() { Position = "4.15", ItemName = "Läbipääsu kaardid", RowType = "procurement", BeforeValue = 100, AfterValue = 100 },
        };

        var issues = ValveLabipaasAuditHelpers.BuildIssues(rows, "company.security.valve-labipaas-spec-check", "TESTRUN", ref seq);

        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(IssueSeverity.Info, i.Severity));
        Assert.All(issues, i => Assert.Equal("ValveLabipaas.ProcurementOnly", i.Category));
    }

    [Fact]
    public void ProcurementOnlyRows_SpecQtyCarriedThrough_NoMismatchIssue()
    {
        // Procurement rows should NOT emit a Warning even when AfterValue == BeforeValue,
        // because the AfterValue is intentionally set to the spec quantity.
        int seq = 0;
        var rows = new List<ValveLabipaasAuditRow>
        {
            new() { Position = "4.14", ItemName = "Raadiopult", RowType = "procurement", BeforeValue = 10, AfterValue = 10 },
        };

        var issues = ValveLabipaasAuditHelpers.BuildIssues(rows, "skill", "run", ref seq);

        Assert.Single(issues);
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Warning);
    }

    // ── Issue severity rules ─────────────────────────────────────────────────

    [Fact]
    public void QuantityMismatch_PlacedElement_ProducesWarning()
    {
        int seq = 0;
        var rows = new List<ValveLabipaasAuditRow>
        {
            new() { Position = "4.6", ItemName = "Valve kilp", RowType = "count", BeforeValue = 10, AfterValue = 9 },
        };

        var issues = ValveLabipaasAuditHelpers.BuildIssues(rows, "skill", "run", ref seq);

        Assert.Single(issues);
        Assert.Equal(IssueSeverity.Warning, issues[0].Severity);
        Assert.Equal("ValveLabipaas.QuantityMismatch", issues[0].Category);
    }

    [Fact]
    public void MissingSource_ProducesError()
    {
        int seq = 0;
        var rows = new List<ValveLabipaasAuditRow>
        {
            new() { Position = "4.7", ItemName = "Kaardilugeja", RowType = "count", BeforeValue = 35, AfterValue = 0 },
        };

        var issues = ValveLabipaasAuditHelpers.BuildIssues(rows, "skill", "run", ref seq);

        Assert.Single(issues);
        Assert.Equal(IssueSeverity.Error, issues[0].Severity);
        Assert.Equal("ValveLabipaas.MissingSource", issues[0].Category);
    }

    [Fact]
    public void MatchingRow_NoIssueProduced()
    {
        int seq = 0;
        var rows = new List<ValveLabipaasAuditRow>
        {
            new() { Position = "4.8", ItemName = "IR", RowType = "count", BeforeValue = 44, AfterValue = 44 },
        };

        var issues = ValveLabipaasAuditHelpers.BuildIssues(rows, "skill", "run", ref seq);

        Assert.Empty(issues);
    }

    // ── Row definitions sanity ────────────────────────────────────────────────

    [Fact]
    public void AllRowPositions_AreUnique()
    {
        var positions = ValveLabipaasRowDefinitions.AllRows.Select(r => r.Position).ToList();
        Assert.Equal(positions.Distinct().Count(), positions.Count);
    }

    [Fact]
    public void AllRows_Have20Entries()
    {
        Assert.Equal(20, ValveLabipaasRowDefinitions.AllRows.Count);
    }

    [Fact]
    public void Row414_IsProcurementOnly()
    {
        var row = ValveLabipaasRowDefinitions.AllRows.Single(r => r.Position == "4.14");
        Assert.Equal(ValveLabipaasRowSource.ProcurementOnly, row.Source);
    }

    [Fact]
    public void Row415_IsProcurementOnly()
    {
        var row = ValveLabipaasRowDefinitions.AllRows.Single(r => r.Position == "4.15");
        Assert.Equal(ValveLabipaasRowSource.ProcurementOnly, row.Source);
    }

    // ── Cable type match functions ────────────────────────────────────────────

    [Fact]
    public void Row416_MatchesCqrCableButNotBundled()
    {
        var row = ValveLabipaasRowDefinitions.AllRows.Single(r => r.Position == "4.16");
        Assert.True(row.CableTypeMatch!("XX_EN_CQR 8 x 0,22 (CCA)"));
        Assert.False(row.CableTypeMatch("XX_EN_CQR 8 x 0,22 (CCA) + 3 x 0.75, HF, CCA"));
    }

    [Fact]
    public void Row416_BundledMatcherHitsLockCable()
    {
        var row = ValveLabipaasRowDefinitions.AllRows.Single(r => r.Position == "4.16");
        Assert.NotNull(row.BundledCableTypeMatch);
        Assert.True(row.BundledCableTypeMatch!("XX_EN_CQR 8 x 0,22 (CCA) + 3 x 0.75, HF, CCA"));
        Assert.False(row.BundledCableTypeMatch("XX_EN_CQR 8 x 0,22 (CCA)"));
    }

    [Fact]
    public void Row420_MatchesBundledCqrOnly()
    {
        var row = ValveLabipaasRowDefinitions.AllRows.Single(r => r.Position == "4.20");
        Assert.True (row.CableTypeMatch!("XX_EN_CQR 8 x 0,22 (CCA) + 3 x 0.75, HF, CCA"));
        Assert.False(row.CableTypeMatch("XX_EN_CQR 8 x 0,22 (CCA)"));
    }

    [Fact]
    public void Row418_MatchesCat6NotOutdoor()
    {
        var row = ValveLabipaasRowDefinitions.AllRows.Single(r => r.Position == "4.18");
        Assert.True (row.CableTypeMatch!("XX_EN_Cat 6 F/UTP (CCA)"));
        Assert.False(row.CableTypeMatch("XX_EN_Cat 6 F/UTP, ilmastikukindel"));
    }

    [Fact]
    public void Row419_MatchesOutdoorCat6Only()
    {
        var row = ValveLabipaasRowDefinitions.AllRows.Single(r => r.Position == "4.19");
        Assert.True (row.CableTypeMatch!("XX_EN_Cat 6 F/UTP, ilmastikukindel"));
        Assert.False(row.CableTypeMatch("XX_EN_Cat 6 F/UTP (CCA)"));
    }
}
