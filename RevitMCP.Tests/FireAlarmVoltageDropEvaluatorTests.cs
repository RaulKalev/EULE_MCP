using RevitMCP.Addin.Electrical;
using Xunit;

namespace RevitMCP.Tests;

public class FireAlarmVoltageDropEvaluatorTests
{
    // ── Missing data never yields Ok ──────────────────────────────────────────

    [Fact]
    public void SounderLine_NoCircuit_IsInsufficientData()
    {
        var e = FireAlarmVoltageDropEvaluator.EvaluateSounderLine(
            hasCircuit: false, lengthMeters: null, cableType: "", resistanceOhmPerMeter: 0.035,
            sounderCount: 200, sounderCurrentMilliAmps: 50, supplyVoltage: 24, minimumVoltage: 18);

        Assert.Equal(FireAlarmVoltageDropEvaluator.InsufficientData, e.Status);
        Assert.Contains("not on an electrical circuit", e.Reason);
        Assert.Null(e.EstimatedVoltageDrop);
        Assert.Null(e.EstimatedEndVoltage);
        Assert.Null(e.TotalCurrentMilliAmps);
    }

    [Theory]
    [InlineData(null, "1x2x1.0 FE180", "no length")]
    [InlineData(0.0, "1x2x1.0 FE180", "no length")]
    [InlineData(50.0, "", "no cable/wire type")]
    [InlineData(50.0, "   ", "no cable/wire type")]
    public void SounderLine_MissingLengthOrCable_IsInsufficientData(double? length, string cable, string expectedReason)
    {
        var e = FireAlarmVoltageDropEvaluator.EvaluateSounderLine(
            true, length, cable, 0.035, sounderCount: 5, sounderCurrentMilliAmps: 50, supplyVoltage: 24, minimumVoltage: 18);

        Assert.Equal(FireAlarmVoltageDropEvaluator.InsufficientData, e.Status);
        Assert.Contains(expectedReason, e.Reason);
    }

    [Fact]
    public void SounderLine_ZeroResistance_IsInsufficientData()
    {
        var e = FireAlarmVoltageDropEvaluator.EvaluateSounderLine(
            true, 50, "Unknown cable", 0, 5, 50, 24, 18);

        Assert.Equal(FireAlarmVoltageDropEvaluator.InsufficientData, e.Status);
        Assert.Contains("resistance", e.Reason);
    }

    [Fact]
    public void SounderLine_NoSounderOnLine_IsInsufficientData()
    {
        var e = FireAlarmVoltageDropEvaluator.EvaluateSounderLine(
            true, 50, "1x2x1.0", 0.035, sounderCount: 0, sounderCurrentMilliAmps: 50, supplyVoltage: 24, minimumVoltage: 18);

        Assert.Equal(FireAlarmVoltageDropEvaluator.InsufficientData, e.Status);
        Assert.Contains("sounder", e.Reason);
        Assert.Null(e.EstimatedVoltageDrop);
    }

    [Fact]
    public void LoopResistance_NoCircuit_IsInsufficientData()
    {
        var e = FireAlarmVoltageDropEvaluator.EvaluateLoopResistance(false, null, "", 0.035, 120);

        Assert.Equal(FireAlarmVoltageDropEvaluator.InsufficientData, e.Status);
        Assert.Null(e.EstimatedLoopResistanceOhm);
        Assert.Null(e.PercentOfLimit);
    }

    [Fact]
    public void LoopResistance_ZeroLength_IsInsufficientDataNotZeroOhmOk()
    {
        var e = FireAlarmVoltageDropEvaluator.EvaluateLoopResistance(true, 0, "2x0.8 CCA", 0.068, 120);

        Assert.Equal(FireAlarmVoltageDropEvaluator.InsufficientData, e.Status);
        Assert.Contains("no length", e.Reason);
        Assert.Null(e.EstimatedLoopResistanceOhm);
    }

    [Fact]
    public void LoopResistance_NoLimit_IsInsufficientData()
    {
        var e = FireAlarmVoltageDropEvaluator.EvaluateLoopResistance(true, 100, "2x0.8 CCA", 0.068, 0);

        Assert.Equal(FireAlarmVoltageDropEvaluator.InsufficientData, e.Status);
        Assert.Contains("limit", e.Reason);
    }

    [Fact]
    public void FindMissingCircuitData_ListsEveryMissingInput()
    {
        var reason = FireAlarmVoltageDropEvaluator.FindMissingCircuitData(true, 0, "", 0);

        Assert.NotNull(reason);
        Assert.Contains("no length", reason);
        Assert.Contains("no cable/wire type", reason);
        Assert.Contains("resistance", reason);
        Assert.Null(FireAlarmVoltageDropEvaluator.FindMissingCircuitData(true, 10, "FE180", 0.035));
    }

    // ── Computed estimates ────────────────────────────────────────────────────

    [Fact]
    public void SounderLine_CurrentIsSounderCountTimesSounderCurrent()
    {
        // 4 sounders × 50 mA = 0.2 A; 0.2 A × 100 m × 0.035 Ω/m = 0.7 V.
        var e = FireAlarmVoltageDropEvaluator.EvaluateSounderLine(true, 100, "1x2x1.0", 0.035, 4, 50, 24, 18);

        Assert.Equal(FireAlarmVoltageDropEvaluator.Ok, e.Status);
        Assert.Null(e.Reason);
        Assert.Equal(4, e.SounderCount);
        Assert.Equal(200.0, e.TotalCurrentMilliAmps);
        Assert.Equal(0.7, e.EstimatedVoltageDrop);
        Assert.Equal(23.3, e.EstimatedEndVoltage);
    }

    [Theory]
    [InlineData(100, 4, "Ok")]        // 0.7 V drop → 23.3 V
    [InlineData(500, 20, "Critical")] // 1 A × 500 m × 0.035 = 17.5 V drop → 6.5 V
    [InlineData(500, 6, "Warning")]   // 0.3 A × 500 × 0.035 = 5.25 V drop → 18.75 V (< 20)
    public void SounderLine_StatusThresholds(double length, int sounders, string expected)
    {
        var e = FireAlarmVoltageDropEvaluator.EvaluateSounderLine(true, length, "FE180", 0.035, sounders, 50, 24, 18);
        Assert.Equal(expected, e.Status);
    }

    [Theory]
    [InlineData(1000, "Ok")]       // 35 Ω
    [InlineData(3000, "Warning")]  // 105 Ω > 96
    [InlineData(4000, "Critical")] // 140 Ω > 120
    public void LoopResistance_StatusThresholds(double length, string expected)
    {
        var e = FireAlarmVoltageDropEvaluator.EvaluateLoopResistance(true, length, "FE180", 0.035, 120);

        Assert.Equal(expected, e.Status);
        Assert.Equal(Math.Round(length * 0.035, 2), e.EstimatedLoopResistanceOhm);
        Assert.NotNull(e.PercentOfLimit);
    }

    // ── Classifier: sounders and the "(No Loop)" bucket ──────────────────────

    [Theory]
    [InlineData("Sireen 24V", true)]
    [InlineData("Vilkur", true)]
    [InlineData("ALARMSEADE sise", true)]
    [InlineData("Optiline suitsuandur", false)]
    [InlineData("Käsiteatenupp", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSounder_OnlySoundersAndFlashers(string? deviceType, bool expected)
    {
        Assert.Equal(expected, FireAlarmLoopClassifier.IsSounder(deviceType));
    }

    [Fact]
    public void CountSounders_IgnoresDetectorsAndCallPoints()
    {
        var types = new[] { "Sireen", "Optiline suitsuandur", "Käsiteatenupp", "Vilkur", "Optiline suitsuandur", "" };
        Assert.Equal(2, FireAlarmLoopClassifier.CountSounders(types));
    }

    [Fact]
    public void NoLoopBucket_WithSiren_IsNotASounderLine()
    {
        Assert.Equal("Unknown",
            FireAlarmLoopClassifier.Classify(FireAlarmLoopClassifier.NoLoopName, ["Sireen", "Optiline suitsuandur"]));
        Assert.True(FireAlarmLoopClassifier.IsNoLoop(FireAlarmLoopClassifier.NoLoopName));
        Assert.False(FireAlarmLoopClassifier.IsNoLoop("Ahel 1"));
    }
}
