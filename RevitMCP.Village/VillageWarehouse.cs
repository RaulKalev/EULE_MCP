using System.Globalization;
using System.Text;
using Newtonsoft.Json;

namespace RevitMCP.Village;

/// <summary>
/// One storage building in the warehouse yard, standing for a single Revit category that has
/// elements in the model graph. Sizes are derived from the element count; nothing here is a live
/// Revit value (the counts come from the graph snapshot, captured at build time).
/// </summary>
public sealed class VillageWarehouse
{
    /// <summary>Stable, sanitized id (<c>wh_fire_alarm_devices</c>). Unique inside one yard.</summary>
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;

    /// <summary>Revit category name exactly as the graph reported it.</summary>
    [JsonProperty("category")] public string Category { get; set; } = string.Empty;

    /// <summary>Display name; the category name today, a localised label later.</summary>
    [JsonProperty("label")] public string Label { get; set; } = string.Empty;

    /// <summary>Elements of this category in the graph. Always greater than zero.</summary>
    [JsonProperty("count")] public long Count { get; set; }

    /// <summary>Share of all stored elements (0-1), for the inspector.</summary>
    [JsonProperty("share")] public double Share { get; set; }

    /// <summary>Theme system this category belongs to, or <c>other</c>.</summary>
    [JsonProperty("system")] public string System { get; set; } = VillageWarehouseYard.OtherSystem;

    /// <summary>Isometric tile coordinates of the warehouse centre; the viewer scales them.</summary>
    [JsonProperty("tile_x")] public double TileX { get; set; }

    [JsonProperty("tile_y")] public double TileY { get; set; }

    /// <summary>Footprint in tiles, scaled from <see cref="Count"/> (see <see cref="VillageWarehouseYard.FootprintFor"/>).</summary>
    [JsonProperty("footprint")] public double Footprint { get; set; }

    /// <summary>Coarse size bucket 1-6, used for sprite detail.</summary>
    [JsonProperty("size")] public int Size { get; set; } = 1;

    /// <summary>Loading bays drawn on the front wall (2-6).</summary>
    [JsonProperty("bays")] public int Bays { get; set; } = 2;

    [JsonProperty("primary")] public string Primary { get; set; } = "#7a8b99";

    [JsonProperty("accent")] public string Accent { get; set; } = "#c9d3dc";

    /// <summary>Roof hue in degrees; the viewer uses it for shading.</summary>
    [JsonProperty("hue")] public int Hue { get; set; }

    /// <summary>Position in the count-descending order, starting at zero.</summary>
    [JsonProperty("rank")] public int Rank { get; set; }

    public VillageWarehouse Clone() => new()
    {
        Id = Id, Category = Category, Label = Label, Count = Count, Share = Share, System = System,
        TileX = TileX, TileY = TileY, Footprint = Footprint, Size = Size, Bays = Bays,
        Primary = Primary, Accent = Accent, Hue = Hue, Rank = Rank
    };
}

/// <summary>
/// Plans the warehouse yard: one warehouse per Revit category that actually has elements, sized
/// from the element count and laid out on a fixed grid below the village. Pure and deterministic —
/// the same graph categories always produce the same yard. A category with no elements gets no
/// warehouse.
/// </summary>
public static class VillageWarehouseYard
{
    /// <summary>Category that matches none of the configured theme systems.</summary>
    public const string OtherSystem = "other";

    /// <summary>Default number of warehouses; the rest of the categories are not drawn.</summary>
    public const int DefaultMax = 10;

    /// <summary>Warehouses per yard row.</summary>
    public const int Columns = 5;

    public const double ColumnPitch = 2.6;
    public const double RowPitch = 2.8;

    /// <summary>
    /// Yard origin. X keeps the widest warehouse clear of the ground grid's left edge (the map is
    /// an isometric diamond, so tile x below zero has no ground under it); Y clears the lowest
    /// landmark, the utility district at tile y 11.
    /// </summary>
    public const double OriginX = 1.0;
    public const double OriginY = 13.4;

    /// <summary>Element count at which a warehouse reaches its maximum footprint.</summary>
    public const long FullSizeCount = 5000;

    /// <summary>
    /// Categories that never get a warehouse. They do have graph elements, but none of them is
    /// stock a yard would hold: materials and material assets are definitions, legend components
    /// are drawing symbols, RVT links are other models, and cameras are views. Replace the list
    /// through <c>village.warehouseExcludeCategories</c>.
    /// </summary>
    public static readonly string[] DefaultExcludedCategories =
    {
        "Materials", "Legend Components", "Material Assets", "RVT Links", "Cameras"
    };

    public const double MinFootprint = 1.05;
    public const double MaxFootprint = 2.10;

    /// <summary>
    /// Builds the yard from the graph's category counts, largest first. Categories with no
    /// elements are skipped entirely; at most <paramref name="max"/> warehouses are returned.
    /// </summary>
    public static List<VillageWarehouse> Plan(
        IReadOnlyList<VillageThemeEvidence>? categories,
        VillageThemeConfig? themes = null,
        int max = DefaultMax,
        IReadOnlyCollection<string>? excludedCategories = null)
    {
        var yard = new List<VillageWarehouse>();
        if (categories == null || categories.Count == 0 || max <= 0) return yard;

        var excluded = new HashSet<string>(
            (excludedCategories ?? DefaultExcludedCategories).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var ordered = categories
            .Where(c => c != null && c.Count > 0 && !string.IsNullOrWhiteSpace(c.Name))
            .Where(c => !excluded.Contains(c.Name.Trim()))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        if (ordered.Count == 0) return yard;

        // The share is of everything the graph counted, not only of what fits in the yard, so a
        // truncated yard still reports honest percentages.
        double total = ordered.Sum(c => (double)c.Count);
        var systems = BuildSystemIndex(themes);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rank = 0;

        foreach (var category in ordered)
        {
            if (yard.Count >= max) break;
            var name = category.Name.Trim();
            var warehouse = new VillageWarehouse
            {
                Id = UniqueId(name, ids),
                Category = name,
                Label = name,
                Count = category.Count,
                Share = total > 0 ? Math.Round(category.Count / total, 4) : 0,
                Footprint = FootprintFor(category.Count),
                Size = SizeFor(category.Count),
                Bays = BaysFor(category.Count),
                Rank = rank
            };

            if (systems.TryGetValue(name, out var system))
            {
                warehouse.System = system.Id;
                warehouse.Primary = system.Primary;
                warehouse.Accent = system.Accent;
                warehouse.Hue = HueOf(system.Primary, name);
            }
            else
            {
                warehouse.System = OtherSystem;
                warehouse.Hue = HashHue(name);
                warehouse.Primary = HslToHex(warehouse.Hue, 0.30, 0.42);
                warehouse.Accent = HslToHex(warehouse.Hue, 0.42, 0.72);
            }

            Place(warehouse, rank);
            yard.Add(warehouse);
            rank++;
        }

        return yard;
    }

    /// <summary>Row-major placement, five per row, below the village.</summary>
    public static void Place(VillageWarehouse warehouse, int index)
    {
        var column = index % Columns;
        var row = index / Columns;
        warehouse.TileX = Math.Round(OriginX + column * ColumnPitch, 3);
        warehouse.TileY = Math.Round(OriginY + row * RowPitch, 3);
    }

    /// <summary>
    /// Footprint in tiles on an absolute logarithmic scale: a category with a handful of elements
    /// gets a shed, one with <see cref="FullSizeCount"/> or more gets the largest warehouse. The
    /// scale is absolute so a warehouse does not change size when another category grows.
    /// </summary>
    public static double FootprintFor(long count)
    {
        if (count <= 0) return 0;
        var ratio = Math.Log10(1 + count) / Math.Log10(1 + FullSizeCount);
        if (ratio > 1) ratio = 1;
        return Math.Round(MinFootprint + (MaxFootprint - MinFootprint) * ratio, 3);
    }

    /// <summary>Coarse bucket used for sprite detail: 0 for an empty category, otherwise 1-6.</summary>
    public static int SizeFor(long count) => count switch
    {
        <= 0 => 0,
        < 10 => 1,
        < 50 => 2,
        < 200 => 3,
        < 1000 => 4,
        < 5000 => 5,
        _ => 6
    };

    /// <summary>Loading bays on the front wall: two for the smallest shed, six for the largest.</summary>
    public static int BaysFor(long count)
    {
        var size = SizeFor(count);
        if (size <= 0) return 0;
        return Math.Max(2, Math.Min(6, size));
    }

    /// <summary>Yard extent in tiles (right and bottom edges), for the viewer's grid fit.</summary>
    public static (double Width, double Height) Extent(IReadOnlyList<VillageWarehouse>? yard)
    {
        double w = 0, h = 0;
        if (yard == null) return (w, h);
        foreach (var warehouse in yard)
        {
            w = Math.Max(w, warehouse.TileX + warehouse.Footprint);
            h = Math.Max(h, warehouse.TileY + warehouse.Footprint);
        }
        return (w, h);
    }

    public static List<VillageWarehouse> Clone(IReadOnlyList<VillageWarehouse>? yard)
    {
        var list = new List<VillageWarehouse>();
        if (yard == null) return list;
        foreach (var warehouse in yard) list.Add(warehouse.Clone());
        return list;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static Dictionary<string, VillageThemeSystem> BuildSystemIndex(VillageThemeConfig? themes)
    {
        var index = new Dictionary<string, VillageThemeSystem>(StringComparer.OrdinalIgnoreCase);
        foreach (var system in (themes ?? VillageThemeConfig.Default()).Systems)
            foreach (var category in system.Categories)
            {
                var key = (category ?? string.Empty).Trim();
                // First system wins so the yard colours stay stable when a category is listed twice.
                if (key.Length > 0 && !index.ContainsKey(key)) index[key] = system;
            }
        return index;
    }

    private static string UniqueId(string category, HashSet<string> taken)
    {
        var slug = Slug(category);
        var id = "wh_" + slug;
        if (taken.Add(id)) return id;
        for (var i = 2; i < 100; i++)
        {
            var candidate = id + "_" + i.ToString(CultureInfo.InvariantCulture);
            if (taken.Add(candidate)) return candidate;
        }
        return id;
    }

    /// <summary>Lower-case ASCII slug; runs of anything else collapse into one underscore.</summary>
    public static string Slug(string? value)
    {
        var sb = new StringBuilder();
        var pending = false;
        foreach (var c in value ?? string.Empty)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') { if (pending && sb.Length > 0) sb.Append('_'); pending = false; sb.Append(c); }
            else if (c is >= 'A' and <= 'Z') { if (pending && sb.Length > 0) sb.Append('_'); pending = false; sb.Append(char.ToLowerInvariant(c)); }
            else pending = true;
            if (sb.Length >= 48) break;
        }
        return sb.Length == 0 ? "category" : sb.ToString();
    }

    /// <summary>Hue of a configured colour, falling back to the name hash when it cannot be parsed.</summary>
    private static int HueOf(string? hex, string name)
    {
        var rgb = ParseHex(hex);
        if (rgb == null) return HashHue(name);
        var (r, g, b) = rgb.Value;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (Math.Abs(max - min) < 1e-9) return 0;
        double h;
        if (Math.Abs(max - r) < 1e-9) h = (g - b) / (max - min);
        else if (Math.Abs(max - g) < 1e-9) h = 2 + (b - r) / (max - min);
        else h = 4 + (r - g) / (max - min);
        h *= 60;
        if (h < 0) h += 360;
        return (int)Math.Round(h) % 360;
    }

    /// <summary>FNV-1a over the lower-cased name: stable across processes and frameworks.</summary>
    public static int HashHue(string? name)
    {
        var text = (name ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0) return 0;
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in text) { hash ^= c; hash *= 16777619; }
            return (int)(hash % 360);
        }
    }

    private static (double R, double G, double B)? ParseHex(string? hex)
    {
        var text = (hex ?? string.Empty).Trim();
        if (text.Length == 4 && text[0] == '#')
            text = "#" + text[1] + text[1] + text[2] + text[2] + text[3] + text[3];
        if (text.Length != 7 || text[0] != '#') return null;
        if (!int.TryParse(text.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !int.TryParse(text.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !int.TryParse(text.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return null;
        return (r / 255.0, g / 255.0, b / 255.0);
    }

    /// <summary>HSL to <c>#rrggbb</c>. Hue in degrees, saturation and lightness in 0-1.</summary>
    public static string HslToHex(double hue, double saturation, double lightness)
    {
        var h = ((hue % 360) + 360) % 360 / 360.0;
        var s = Math.Max(0, Math.Min(1, saturation));
        var l = Math.Max(0, Math.Min(1, lightness));
        double r, g, b;
        if (s <= 0) { r = g = b = l; }
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = Channel(p, q, h + 1.0 / 3); g = Channel(p, q, h); b = Channel(p, q, h - 1.0 / 3);
        }
        return "#" + Byte(r) + Byte(g) + Byte(b);
    }

    private static double Channel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static string Byte(double value) =>
        ((int)Math.Round(Math.Max(0, Math.Min(1, value)) * 255)).ToString("x2", CultureInfo.InvariantCulture);
}
