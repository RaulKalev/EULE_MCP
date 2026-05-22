using RevitMCP.Addin.Electrical;
using Xunit;

namespace RevitMCP.Tests;

public class FireAlarmLoopClassifierTests
{
    // ── ConventionalSounderLine — name-based ──────────────────────────────────

    [Fact]
    public void HashInLoopName_IsConventionalSounderLine()
    {
        Assert.Equal("ConventionalSounderLine",
            FireAlarmLoopClassifier.Classify("Loop#1", []));
    }

    [Fact]
    public void HashInLoopName_RegardlessOfDeviceTypes_IsConventionalSounderLine()
    {
        Assert.Equal("ConventionalSounderLine",
            FireAlarmLoopClassifier.Classify("AHJ#3", ["Detektor", "Sireen"]));
    }

    // ── ConventionalSounderLine — device-type-based ───────────────────────────

    [Theory]
    [InlineData("Sireen 100")]
    [InlineData("SIREEN")]
    [InlineData("sireen")]
    public void SireenDeviceType_IsConventionalSounderLine(string deviceType)
    {
        Assert.Equal("ConventionalSounderLine",
            FireAlarmLoopClassifier.Classify("Ahel-A1", [deviceType]));
    }

    [Fact]
    public void VilkurDeviceType_IsConventionalSounderLine()
    {
        Assert.Equal("ConventionalSounderLine",
            FireAlarmLoopClassifier.Classify("Ahel-B2", ["Vilkur XL"]));
    }

    [Fact]
    public void AlarmseadeDeviceType_IsConventionalSounderLine()
    {
        Assert.Equal("ConventionalSounderLine",
            FireAlarmLoopClassifier.Classify("Loop-1", ["Kombineeritud Alarmseade"]));
    }

    // ── ModuleLoop ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Moodul SIM")]
    [InlineData("IO-Moodul")]
    [InlineData("MOODUL")]
    public void MoodulDeviceType_IsModuleLoop(string deviceType)
    {
        Assert.Equal("ModuleLoop",
            FireAlarmLoopClassifier.Classify("Ahel-C1", [deviceType]));
    }

    [Fact]
    public void SisendDeviceType_IsModuleLoop()
    {
        Assert.Equal("ModuleLoop",
            FireAlarmLoopClassifier.Classify("Zone-1", ["Sisend 24V"]));
    }

    [Fact]
    public void VäljundDeviceType_IsModuleLoop()
    {
        Assert.Equal("ModuleLoop",
            FireAlarmLoopClassifier.Classify("Zone-1", ["Väljund releed"]));
    }

    [Fact]
    public void SimKeyword_IsModuleLoop()
    {
        Assert.Equal("ModuleLoop",
            FireAlarmLoopClassifier.Classify("Zone-1", ["SIM-kaart moodul"]));
    }

    [Fact]
    public void SomKeyword_IsModuleLoop()
    {
        Assert.Equal("ModuleLoop",
            FireAlarmLoopClassifier.Classify("Zone-1", ["SOM-väljund"]));
    }

    // ── ModuleLoop has lower priority than ConventionalSounderLine ────────────

    [Fact]
    public void HashName_WithModuleDevices_StillConventionalSounderLine()
    {
        // '#' in loop name takes priority — check order of conditions
        Assert.Equal("ConventionalSounderLine",
            FireAlarmLoopClassifier.Classify("Bus#2", ["Moodul", "Sisend"]));
    }

    // ── AddressableLoop ───────────────────────────────────────────────────────

    [Fact]
    public void LoopNameContainsAhel_IsAddressableLoop()
    {
        Assert.Equal("AddressableLoop",
            FireAlarmLoopClassifier.Classify("Ahel-1", ["Detektor"]));
    }

    [Fact]
    public void LoopNameAhel_CaseInsensitive_IsAddressableLoop()
    {
        Assert.Equal("AddressableLoop",
            FireAlarmLoopClassifier.Classify("AHEL-01", ["Detektor"]));
    }

    [Fact]
    public void LoopNameAhel_LowerCase_IsAddressableLoop()
    {
        Assert.Equal("AddressableLoop",
            FireAlarmLoopClassifier.Classify("ahel-3a", []));
    }

    // ── Unknown ───────────────────────────────────────────────────────────────

    [Fact]
    public void NoMatchingPatterns_IsUnknown()
    {
        Assert.Equal("Unknown",
            FireAlarmLoopClassifier.Classify("Zone-4", ["Detektor", "Käsikäiviti"]));
    }

    [Fact]
    public void EmptyLoopName_NoDevices_IsUnknown()
    {
        Assert.Equal("Unknown",
            FireAlarmLoopClassifier.Classify("", []));
    }

    [Fact]
    public void EmptyLoopName_WithDetektorOnly_IsUnknown()
    {
        Assert.Equal("Unknown",
            FireAlarmLoopClassifier.Classify("", ["Detektor"]));
    }

    // ── Priority ordering ─────────────────────────────────────────────────────

    [Fact]
    public void AhelNameWithSireenDevice_SireenTakesPriorityOverAddressable()
    {
        // Sireen device check comes before Ahel name check
        Assert.Equal("ConventionalSounderLine",
            FireAlarmLoopClassifier.Classify("Ahel-2", ["Sireen standard"]));
    }

    [Fact]
    public void AhelNameWithModuleDevice_ModuleTakesPriorityOverAddressable()
    {
        // Module check comes before Ahel name check
        Assert.Equal("ModuleLoop",
            FireAlarmLoopClassifier.Classify("Ahel-3", ["IO-moodul"]));
    }
}
