using Newtonsoft.Json;
using RevitMCP.Addin.Geometry;

namespace RevitMCP.Addin.ViewElements;

/// <summary>
/// Where an element is, in mm (#136): a point (<c>Kind</c> point, text, tagHead, viewport) or a curve (<c>Kind</c> curve,
/// with start and end). Host model coordinates for elements of a view; sheet coordinates for elements on a sheet.
/// </summary>
public sealed class ViewElementLocationDto
{
    public string Kind { get; set; } = string.Empty;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public PointMmDto? Point { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? RotationDeg { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public PointMmDto? Start { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public PointMmDto? End { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? LengthMm { get; set; }

    /// <summary>Line, Arc, ... for a curve.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? CurveType { get; set; }
}

/// <summary>A short reference to a related element: the host, the owner view.</summary>
public sealed class ViewElementRefDto
{
    public long Id { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Category { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class ViewElementRoomDto
{
    public long Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
