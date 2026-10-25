using Newtonsoft.Json;
using RevitMCP.Addin.Query;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#109: the model-state fields leave default element payloads byte-for-byte unchanged.</summary>
public class ElementInfoDtoJsonTests
{
    private static ElementInfoDto Sample() => new()
    {
        ElementId = 123,
        UniqueId = "u-1",
        Category = "Electrical Fixtures",
        Family = "EN_SIDE_Pistik",
        Type = "2 x RJ45",
        TypeElementId = 77,
        Name = "2 x RJ45",
        Level = "1. korrus",
        Parameters = new Dictionary<string, ParameterValueDto>
        {
            ["Comments"] = new() { Name = "Comments", Value = "x", Scope = "Instance", StorageType = "String" }
        }
    };

    // The serialized DTO before #109, as the connector sent it.
    private const string DefaultJson =
        "{\"ElementId\":123,\"UniqueId\":\"u-1\",\"Category\":\"Electrical Fixtures\",\"Family\":\"EN_SIDE_Pistik\"," +
        "\"Type\":\"2 x RJ45\",\"TypeElementId\":77,\"Name\":\"2 x RJ45\",\"Level\":\"1. korrus\",\"Parameters\":{\"Comments\":";

    [Fact]
    public void DefaultPayload_HasNoModelStateFields()
    {
        var json = JsonConvert.SerializeObject(Sample());
        Assert.StartsWith(DefaultJson, json);
        foreach (var field in new[] { "PhaseCreated", "PhaseDemolished", "DesignOption", "Workset", "OwnedBy" })
            Assert.DoesNotContain(field, json);
    }

    [Fact]
    public void ModelState_IsSerializedWhenRequested_EmptyMeansNone()
    {
        var dto = Sample();
        dto.PhaseCreated = "Uus";
        dto.PhaseDemolished = "";
        dto.DesignOption = "";
        dto.Workset = "EN_Nõrkvool";
        var json = JsonConvert.SerializeObject(dto);
        Assert.Contains("\"PhaseCreated\":\"Uus\",\"PhaseDemolished\":\"\",\"DesignOption\":\"\",\"Workset\":\"EN_Nõrkvool\"", json);
        Assert.DoesNotContain("OwnedBy", json);
    }

    [Fact]
    public void CompactProjection_CarriesTheModelStateOnlyWhenSet()
    {
        var plain = JsonConvert.SerializeObject(ElementQueryResponseFormatter.FormatElements(new[] { Sample() }, compact: true));
        Assert.DoesNotContain("Phase", plain);
        Assert.DoesNotContain("Workset", plain);

        var dto = Sample();
        dto.PhaseCreated = "Uus";
        dto.OwnedBy = "raul";
        var compact = JsonConvert.SerializeObject(ElementQueryResponseFormatter.FormatElements(new[] { dto }, compact: true));
        Assert.Contains("\"PhaseCreated\":\"Uus\"", compact);
        Assert.Contains("\"OwnedBy\":\"raul\"", compact);
        Assert.DoesNotContain("DesignOption", compact);
    }
}
