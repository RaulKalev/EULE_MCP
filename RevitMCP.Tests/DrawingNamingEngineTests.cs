using RevitMCP.Addin.Skills.Tasks.DrawingNaming;
using RevitMCP.Addin.Skills.Tasks.DrawingNaming.Models;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// Unit tests for NamingMappingEngine.BuildName().
/// </summary>
public class DrawingNamingEngineTests
{
    // ── Helper to build a standard EULE mapping (matches the built-in skill) ──

    private static List<NamingMappingToken> EuleTokens() =>
    [
        new() { Type = "Parameter", Value = "Project Number"   },
        new() { Type = "Separator",  Value = "_"               },
        new() { Type = "Parameter", Value = "Project Status"   },
        new() { Type = "Separator",  Value = "_"               },
        new() { Type = "Parameter", Value = "Projekti osa"     },
        new() { Type = "Separator",  Value = "-"               },
        new() { Type = "Parameter", Value = "Grupi tähis"      },
        new() { Type = "Separator",  Value = "-"               },
        new() { Type = "Parameter", Value = "Järjekorra tähis" },
        new() { Type = "Separator",  Value = "_"               },
        new() { Type = "Parameter", Value = "Märkus"           },
    ];

    // ── All parameters present ─────────────────────────────────────────────────

    [Fact]
    public void AllParamsPresent_ReturnsConcatenated()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Project Number"]   = "1626",
            ["Project Status"]   = "TP",
            ["Projekti osa"]     = "EL",
            ["Grupi tähis"]      = "A",
            ["Järjekorra tähis"] = "001",
            ["Märkus"]           = "REV1",
        };

        var result = NamingMappingEngine.BuildName(EuleTokens(), values);

        Assert.Equal("1626_TP_EL-A-001_REV1", result);
    }

    // ── Last parameter empty → trailing separator dropped ─────────────────────

    [Fact]
    public void LastParamEmpty_TrailingSeparatorDropped()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Project Number"]   = "1626",
            ["Project Status"]   = "TP",
            ["Projekti osa"]     = "EL",
            ["Grupi tähis"]      = "A",
            ["Järjekorra tähis"] = "001",
            ["Märkus"]           = "",       // empty
        };

        var result = NamingMappingEngine.BuildName(EuleTokens(), values);

        Assert.Equal("1626_TP_EL-A-001", result);
    }

    // ── Middle parameter empty → both surrounding separators dropped ───────────

    [Fact]
    public void MiddleParamEmpty_SurroundingSeparatorsDropped()
    {
        // Projekti osa is empty — drops "_" before it and "-" after it
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Project Number"]   = "1626",
            ["Project Status"]   = "TP",
            ["Projekti osa"]     = "",       // empty
            ["Grupi tähis"]      = "A",
            ["Järjekorra tähis"] = "001",
            ["Märkus"]           = "",
        };

        var result = NamingMappingEngine.BuildName(EuleTokens(), values);

        // "_TP" → next token empty, pending "_" discarded → "-A-001"
        Assert.Equal("1626_TP-A-001", result);
    }

    // ── First parameter empty → no leading separator ───────────────────────────

    [Fact]
    public void FirstParamEmpty_NoLeadingSeparator()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Project Number"]   = "",       // empty
            ["Project Status"]   = "TP",
            ["Projekti osa"]     = "EL",
            ["Grupi tähis"]      = "A",
            ["Järjekorra tähis"] = "001",
            ["Märkus"]           = "",
        };

        var result = NamingMappingEngine.BuildName(EuleTokens(), values);

        Assert.Equal("TP_EL-A-001", result);
    }

    // ── All parameters empty → empty string ───────────────────────────────────

    [Fact]
    public void AllParamsEmpty_ReturnsEmpty()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Project Number"]   = "",
            ["Project Status"]   = "",
            ["Projekti osa"]     = "",
            ["Grupi tähis"]      = "",
            ["Järjekorra tähis"] = "",
            ["Märkus"]           = "",
        };

        var result = NamingMappingEngine.BuildName(EuleTokens(), values);

        Assert.Equal("", result);
    }

    // ── Single parameter token ─────────────────────────────────────────────────

    [Fact]
    public void SingleParameterToken_ReturnsJustValue()
    {
        var tokens = new List<NamingMappingToken>
        {
            new() { Type = "Parameter", Value = "Project Number" },
        };
        var values = new Dictionary<string, string> { ["Project Number"] = "1234" };

        var result = NamingMappingEngine.BuildName(tokens, values);

        Assert.Equal("1234", result);
    }

    // ── Empty token list → empty string ───────────────────────────────────────

    [Fact]
    public void EmptyTokenList_ReturnsEmpty()
    {
        var result = NamingMappingEngine.BuildName([], new Dictionary<string, string>());

        Assert.Equal("", result);
    }

    // ── Parameter not in dictionary is treated as empty ───────────────────────

    [Fact]
    public void MissingDictionaryEntry_TreatedAsEmpty()
    {
        var tokens = new List<NamingMappingToken>
        {
            new() { Type = "Parameter", Value = "A" },
            new() { Type = "Separator",  Value = "_" },
            new() { Type = "Parameter", Value = "B" },  // not in dict
            new() { Type = "Separator",  Value = "_" },
            new() { Type = "Parameter", Value = "C" },
        };
        var values = new Dictionary<string, string> { ["A"] = "x", ["C"] = "z" };

        var result = NamingMappingEngine.BuildName(tokens, values);

        Assert.Equal("x_z", result);
    }

    // ── Only separators in token list → empty string ──────────────────────────

    [Fact]
    public void OnlySeparatorTokens_ReturnsEmpty()
    {
        var tokens = new List<NamingMappingToken>
        {
            new() { Type = "Separator", Value = "_" },
            new() { Type = "Separator", Value = "-" },
        };

        var result = NamingMappingEngine.BuildName(tokens, new Dictionary<string, string>());

        Assert.Equal("", result);
    }

    // ── Type comparison is case-insensitive ────────────────────────────────────

    [Fact]
    public void TokenTypeCaseInsensitive_ReturnsCorrectResult()
    {
        var tokens = new List<NamingMappingToken>
        {
            new() { Type = "PARAMETER", Value = "X" },
            new() { Type = "separator", Value = "-" },
            new() { Type = "Parameter", Value = "Y" },
        };
        var values = new Dictionary<string, string> { ["X"] = "foo", ["Y"] = "bar" };

        var result = NamingMappingEngine.BuildName(tokens, values);

        Assert.Equal("foo-bar", result);
    }
}
