using System.IO;
using RevitMCP.Addin.ParameterQA;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// Tests for ParameterQaRuleSetService — Fix 3 validation.
/// Uses a temp directory to avoid touching the real AppData file.
/// </summary>
public class ParameterQaRuleSetServiceTests
{
    // ── Default rule sets ─────────────────────────────────────────────────────

    [Fact]
    public void GetDefaultRuleSets_ReturnsAtLeastOneRuleSet()
    {
        var defaults = ParameterQaRuleSetService.GetDefaultRuleSets();

        Assert.NotEmpty(defaults);
    }

    [Fact]
    public void GetDefaultRuleSets_EuleneaBasicQa_IsPresent()
    {
        var defaults = ParameterQaRuleSetService.GetDefaultRuleSets();

        var euleNea = defaults.FirstOrDefault(rs =>
            rs.Name.Equals("ELENEA Basic QA", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(euleNea);
        Assert.NotEmpty(euleNea.Rules);
    }

    [Fact]
    public void GetDefaultRuleSets_EuleneaBasicQa_HasFireAlarmDevicesRule()
    {
        var defaults = ParameterQaRuleSetService.GetDefaultRuleSets();
        var ruleSet = defaults.First(rs => rs.Name.Equals("ELENEA Basic QA", StringComparison.OrdinalIgnoreCase));

        var rule = ruleSet.Rules.FirstOrDefault(r =>
            r.Category.Equals("Fire Alarm Devices", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(rule);
        Assert.NotEmpty(rule.RequiredParameters);
    }

    [Fact]
    public void GetDefaultRuleSets_EuleneaBasicQa_HasLightingFixturesRule()
    {
        var defaults = ParameterQaRuleSetService.GetDefaultRuleSets();
        var ruleSet = defaults.First(rs => rs.Name.Equals("ELENEA Basic QA", StringComparison.OrdinalIgnoreCase));

        var rule = ruleSet.Rules.FirstOrDefault(r =>
            r.Category.Equals("Lighting Fixtures", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(rule);
        Assert.NotEmpty(rule.RequiredParameters);
    }

    // ── Serialization round-trip ──────────────────────────────────────────────

    [Fact]
    public void ParameterQaFile_Serializes_AndDeserializesCorrectly()
    {
        var file = new ParameterQaFile
        {
            Version = 1,
            RuleSets = ParameterQaRuleSetService.GetDefaultRuleSets()
        };

        var json = System.Text.Json.JsonSerializer.Serialize(file);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<ParameterQaFile>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(file.RuleSets.Count, deserialized.RuleSets.Count);
        Assert.Equal(file.RuleSets[0].Name, deserialized.RuleSets[0].Name);
    }

    // ── Rule model structure ──────────────────────────────────────────────────

    [Fact]
    public void ParameterQaRule_DefaultsToIncludeInstanceParameters()
    {
        var rule = new ParameterQaRule();
        Assert.True(rule.IncludeInstanceParameters);
    }

    [Fact]
    public void ParameterQaRule_DefaultsToIncludeTypeParameters()
    {
        var rule = new ParameterQaRule();
        Assert.True(rule.IncludeTypeParameters);
    }
}
