using Newtonsoft.Json;
using RevitMCP.Addin.Query;
using Xunit;

namespace RevitMCP.Tests;

public class QueryOptimizationTests
{
    [Fact]
    public void FullResponse_ReturnsOriginalElementList()
    {
        var elements = new List<ElementInfoDto>
        {
            new() { ElementId = 42, Category = "Walls" }
        };

        var formatted = ElementQueryResponseFormatter.FormatElements(elements, compact: false);

        Assert.Same(elements, formatted);
    }

    [Fact]
    public void CompactResponse_KeepsValuesAndOmitsVerboseParameterMetadata()
    {
        var elements = new List<ElementInfoDto>
        {
            new()
            {
                ElementId = 42,
                UniqueId = "long-stable-unique-id",
                Category = "Walls",
                Type = "Basic Wall",
                Parameters = new Dictionary<string, ParameterValueDto>
                {
                    ["Mark"] = new()
                    {
                        Name = "Mark",
                        NormalizedName = "mark",
                        Value = "W-101",
                        RawValue = "W-101",
                        StorageType = "String",
                        Scope = "Instance",
                        IsReadOnly = false,
                        ParameterId = -1001203,
                        BuiltInParameterName = "ALL_MODEL_MARK"
                    }
                }
            }
        };

        var fullJson = JsonConvert.SerializeObject(elements);
        var compact = ElementQueryResponseFormatter.FormatElements(elements, compact: true);
        var compactJson = JsonConvert.SerializeObject(compact);

        Assert.Contains("\"ElementId\":42", compactJson);
        Assert.Contains("\"Mark\":\"W-101\"", compactJson);
        Assert.DoesNotContain("NormalizedName", compactJson);
        Assert.DoesNotContain("StorageType", compactJson);
        Assert.DoesNotContain("UniqueId", compactJson);
        Assert.True(compactJson.Length < fullJson.Length / 2);
    }

    [Theory]
    [InlineData("Fire Rating", "Instance", "FireRating", "ExactNormalized", "Instance", true)]
    [InlineData("Fire Rating", "Type", "Fire", "Contains", "InstanceAndType", true)]
    [InlineData("Fire Rating", "Type", "Fire", "Contains", "Instance", false)]
    [InlineData("Comments", "Instance", "Mark", "Exact", "InstanceAndType", false)]
    public void ParameterSelector_PreservesNameMatchAndScope(
        string parameterName,
        string parameterScope,
        string requestedName,
        string matchMode,
        string requestedScope,
        bool expected)
    {
        var selector = new ParameterSelector
        {
            Name = requestedName,
            MatchMode = matchMode,
            Scope = requestedScope
        };

        Assert.Equal(expected, selector.Matches(parameterName, parameterScope));
    }
}
