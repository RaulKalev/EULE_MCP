using Newtonsoft.Json;

namespace RevitMCP.Addin.Village;

/// <summary>One persistent landmark of the village and the areas it represents.</summary>
public sealed class VillageBuilding
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>Areas whose activity happens at this building. Order matters only for display.</summary>
    [JsonProperty("areas")]
    public string[] Areas { get; set; } = Array.Empty<string>();

    /// <summary>Isometric tile coordinates on a fixed grid; the viewer scales them.</summary>
    [JsonProperty("tile_x")]
    public int TileX { get; set; }

    [JsonProperty("tile_y")]
    public int TileY { get; set; }

    /// <summary>Base footprint (1 = small house, 3 = town hall). Graph counts adjust <see cref="Size"/>.</summary>
    [JsonProperty("base_size")]
    public int BaseSize { get; set; } = 1;

    /// <summary>Effective size after graph-derived scaling (1-4). Defaults to <see cref="BaseSize"/>.</summary>
    [JsonProperty("size")]
    public int Size { get; set; } = 1;

    /// <summary>Hint for the viewer's procedural sprite: town_hall, archive, tower, market, workshop, sign, houses, substation, survey, office, warning.</summary>
    [JsonProperty("sprite")]
    public string Sprite { get; set; } = "house";

    /// <summary>Always shown, even when the graph reports nothing for it.</summary>
    [JsonProperty("always_visible")]
    public bool AlwaysVisible { get; set; } = true;

    /// <summary>Short deterministic description shown when the building is selected.</summary>
    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// The default village: eleven persistent landmarks. Districts for the ELV systems (fire alarm,
/// security, lighting, IT/AV) live around the utility district; the theme decides how prominent
/// they look. Configurable through <c>village.buildings</c> in a later version; the mapping is
/// documented in docs/project-village.md.
/// </summary>
public static class VillageLayout
{
    public const string TownHall   = "town_hall";
    public const string Archive    = "archive";
    public const string Lookout    = "lookout";
    public const string Market     = "market";
    public const string Workshop   = "workshop";
    public const string SignShop   = "sign_workshop";
    public const string Houses     = "houses";
    public const string Utility    = "utility_district";
    public const string Survey     = "survey_post";
    public const string Office     = "records_office";
    public const string Warning    = "warning_area";
    /// <summary>Where the agent waits when it is idle or the area is unknown. Not a building.</summary>
    public const string Square     = "square";

    public static IReadOnlyList<VillageBuilding> Default { get; } = new[]
    {
        new VillageBuilding
        {
            Id = TownHall, Label = "Town hall", Sprite = "town_hall", TileX = 5, TileY = 2, BaseSize = 3,
            Areas = new[] { VillageAreas.Project, VillageAreas.Graph },
            Description = "Project overview, connection status and the model graph."
        },
        new VillageBuilding
        {
            Id = Archive, Label = "Archive", Sprite = "archive", TileX = 2, TileY = 3, BaseSize = 2,
            Areas = new[] { VillageAreas.Sheets },
            Description = "Sheets, title blocks and revisions."
        },
        new VillageBuilding
        {
            Id = Lookout, Label = "Lookout tower", Sprite = "tower", TileX = 8, TileY = 3, BaseSize = 2,
            Areas = new[] { VillageAreas.Views },
            Description = "Views, view templates and CAD graphics."
        },
        new VillageBuilding
        {
            Id = Market, Label = "Market hall", Sprite = "market", TileX = 3, TileY = 6, BaseSize = 2,
            Areas = new[] { VillageAreas.Schedules },
            Description = "Schedules."
        },
        new VillageBuilding
        {
            Id = Workshop, Label = "Workshop", Sprite = "workshop", TileX = 7, TileY = 6, BaseSize = 2,
            Areas = new[] { VillageAreas.FamiliesTypes },
            Description = "Families and types."
        },
        new VillageBuilding
        {
            Id = SignShop, Label = "Sign workshop", Sprite = "sign", TileX = 9, TileY = 8, BaseSize = 2,
            Areas = new[] { VillageAreas.TagsAnnotations },
            Description = "Tags, dimensions, text and detail lines."
        },
        new VillageBuilding
        {
            Id = Houses, Label = "Houses", Sprite = "houses", TileX = 1, TileY = 9, BaseSize = 2,
            Areas = new[] { VillageAreas.Elements },
            Description = "Model elements, parameters, rooms and spaces."
        },
        new VillageBuilding
        {
            Id = Utility, Label = "Utility district", Sprite = "substation", TileX = 5, TileY = 11, BaseSize = 2,
            Areas = new[] { VillageAreas.Electrical, VillageAreas.FireAlarm, VillageAreas.Security, VillageAreas.Lighting, VillageAreas.ItAv },
            Description = "Electrical circuits and panels plus the ELV districts: fire alarm, security, lighting, IT/AV."
        },
        new VillageBuilding
        {
            Id = Survey, Label = "Survey post", Sprite = "survey", TileX = 10, TileY = 5, BaseSize = 1,
            Areas = new[] { VillageAreas.Coordination },
            Description = "Clash detection and coordination reviews."
        },
        new VillageBuilding
        {
            Id = Office, Label = "Records office", Sprite = "office", TileX = 1, TileY = 5, BaseSize = 1,
            Areas = new[] { VillageAreas.Office },
            Description = "Files, Excel, reports, delivery checks, configuration, standards and skills."
        },
        new VillageBuilding
        {
            Id = Warning, Label = "Warning area", Sprite = "warning", TileX = 10, TileY = 11, BaseSize = 1,
            Areas = Array.Empty<string>(),
            Description = "Recent failures, stale graph data and reported model-health issues."
        }
    };

    /// <summary>Human names for areas, used in deterministic step labels.</summary>
    public static readonly Dictionary<string, string> AreaLabels = new(StringComparer.Ordinal)
    {
        [VillageAreas.Project]         = "the project",
        [VillageAreas.Graph]           = "the model graph",
        [VillageAreas.Sheets]          = "sheets",
        [VillageAreas.Schedules]       = "schedules",
        [VillageAreas.Views]           = "views",
        [VillageAreas.FamiliesTypes]   = "families and types",
        [VillageAreas.TagsAnnotations] = "tags and annotations",
        [VillageAreas.Elements]        = "model elements",
        [VillageAreas.FireAlarm]       = "the fire alarm system",
        [VillageAreas.Security]        = "the security system",
        [VillageAreas.Lighting]        = "lighting",
        [VillageAreas.ItAv]            = "IT and AV",
        [VillageAreas.Electrical]      = "electrical systems",
        [VillageAreas.Coordination]    = "coordination",
        [VillageAreas.Office]          = "the records office",
        [VillageAreas.Unknown]         = "the village square"
    };

    public static string AreaLabel(string? area) =>
        area != null && AreaLabels.TryGetValue(area, out var label) ? label : AreaLabels[VillageAreas.Unknown];

    /// <summary>Building id for an area; <see cref="Square"/> for unknown areas.</summary>
    public static string BuildingFor(string? area, IReadOnlyList<VillageBuilding>? buildings = null)
    {
        if (string.IsNullOrEmpty(area)) return Square;
        foreach (var b in buildings ?? Default)
            if (Array.IndexOf(b.Areas, area) >= 0) return b.Id;
        return Square;
    }

    /// <summary>Deep copy so per-project sizing never mutates the shared default.</summary>
    public static List<VillageBuilding> Clone(IReadOnlyList<VillageBuilding>? source = null)
    {
        var list = new List<VillageBuilding>();
        foreach (var b in source ?? Default)
        {
            list.Add(new VillageBuilding
            {
                Id = b.Id, Label = b.Label, Areas = (string[])b.Areas.Clone(), TileX = b.TileX, TileY = b.TileY,
                BaseSize = b.BaseSize, Size = b.Size < 1 ? b.BaseSize : b.Size, Sprite = b.Sprite,
                AlwaysVisible = b.AlwaysVisible, Description = b.Description
            });
        }
        return list;
    }
}
