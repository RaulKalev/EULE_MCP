using RevitMCP.Village;
using Xunit;

namespace RevitMCP.Tests;

public class VillageWarehouseTests
{
    private static List<VillageThemeEvidence> Categories(params (string Name, long Count)[] rows) =>
        rows.Select(r => new VillageThemeEvidence { Name = r.Name, Count = r.Count }).ToList();

    [Fact]
    public void CategoriesWithoutElements_GetNoWarehouse()
    {
        var yard = VillageWarehouseYard.Plan(Categories(
            ("Fire Alarm Devices", 120), ("Security Devices", 0), ("Data Devices", -3), ("   ", 50)));

        Assert.Single(yard);
        Assert.Equal("Fire Alarm Devices", yard[0].Category);
    }

    [Fact]
    public void EmptyOrMissingCategoryList_ProducesNoYard()
    {
        Assert.Empty(VillageWarehouseYard.Plan(null));
        Assert.Empty(VillageWarehouseYard.Plan(new List<VillageThemeEvidence>()));
        Assert.Empty(VillageWarehouseYard.Plan(Categories(("Walls", 900)), max: 0));
    }

    [Fact]
    public void WarehousesAreOrderedByCountAndRanked()
    {
        var yard = VillageWarehouseYard.Plan(Categories(
            ("Data Devices", 150), ("Fire Alarm Devices", 400), ("Security Devices", 40)));

        Assert.Equal(new[] { "Fire Alarm Devices", "Data Devices", "Security Devices" }, yard.Select(w => w.Category));
        Assert.Equal(new[] { 0, 1, 2 }, yard.Select(w => w.Rank));
    }

    [Fact]
    public void EqualCountsBreakTiesByName_SoTheYardIsStable()
    {
        var a = VillageWarehouseYard.Plan(Categories(("Zones", 10), ("Air Terminals", 10)));
        var b = VillageWarehouseYard.Plan(Categories(("Air Terminals", 10), ("Zones", 10)));

        Assert.Equal(new[] { "Air Terminals", "Zones" }, a.Select(w => w.Category));
        Assert.Equal(a.Select(w => w.Id), b.Select(w => w.Id));
        Assert.Equal(a.Select(w => w.TileX), b.Select(w => w.TileX));
    }

    [Fact]
    public void MaxCountIsHonouredAndKeepsTheLargestCategories()
    {
        var rows = Enumerable.Range(1, 30).Select(i => ("Category " + i, (long)i * 10)).ToArray();
        var yard = VillageWarehouseYard.Plan(Categories(rows), max: 6);

        Assert.Equal(6, yard.Count);
        Assert.Equal(300, yard[0].Count);
        Assert.All(yard, w => Assert.True(w.Count >= 250));
    }

    [Fact]
    public void SharesUseEveryCountedCategory_NotOnlyTheOnesThatFit()
    {
        // 600 elements in total; the yard shows only the first two but the share stays honest.
        var yard = VillageWarehouseYard.Plan(Categories(("A", 300), ("B", 200), ("C", 100)), max: 2);

        Assert.Equal(2, yard.Count);
        Assert.Equal(0.5, yard[0].Share);
        Assert.Equal(0.3333, yard[1].Share, 4);
    }

    [Fact]
    public void FootprintGrowsWithTheCount_AndStaysBounded()
    {
        Assert.Equal(0, VillageWarehouseYard.FootprintFor(0));
        var sizes = new[] { 1L, 10, 100, 1000, 5000, 500_000 }.Select(VillageWarehouseYard.FootprintFor).ToArray();

        for (var i = 1; i < sizes.Length; i++) Assert.True(sizes[i] >= sizes[i - 1], "footprints must not shrink as counts grow");
        Assert.True(sizes[0] > VillageWarehouseYard.MinFootprint);
        Assert.True(sizes[3] > sizes[0], "a 1000-element category must be visibly bigger than a single-element one");
        Assert.Equal(VillageWarehouseYard.MaxFootprint, sizes[4], 3);
        Assert.Equal(VillageWarehouseYard.MaxFootprint, sizes[5], 3);
    }

    [Fact]
    public void SizeBucketsAndBaysFollowTheCount()
    {
        Assert.Equal(0, VillageWarehouseYard.SizeFor(0));
        Assert.Equal(1, VillageWarehouseYard.SizeFor(9));
        Assert.Equal(2, VillageWarehouseYard.SizeFor(10));
        Assert.Equal(6, VillageWarehouseYard.SizeFor(5000));

        Assert.Equal(0, VillageWarehouseYard.BaysFor(0));
        Assert.Equal(2, VillageWarehouseYard.BaysFor(1));
        Assert.Equal(2, VillageWarehouseYard.BaysFor(20));
        Assert.Equal(6, VillageWarehouseYard.BaysFor(20_000));
    }

    [Fact]
    public void KnownCategoriesTakeTheirSystemColours()
    {
        var yard = VillageWarehouseYard.Plan(Categories(
            ("Fire Alarm Devices", 400), ("Data Devices", 300), ("Electrical Equipment", 200),
            ("Security Devices", 100), ("Lighting Fixtures", 50)));

        Assert.Equal("fire_alarm", yard[0].System);
        Assert.Equal("#b8432f", yard[0].Primary);
        Assert.Equal("it_av", yard[1].System);
        Assert.Equal("electrical", yard[2].System);
        Assert.Equal("security", yard[3].System);
        Assert.Equal("lighting", yard[4].System);
    }

    [Fact]
    public void UnmappedCategoryGetsAStableGeneratedColour()
    {
        var a = VillageWarehouseYard.Plan(Categories(("Structural Framing", 800)))[0];
        var b = VillageWarehouseYard.Plan(Categories(("Structural Framing", 800)))[0];

        Assert.Equal(VillageWarehouseYard.OtherSystem, a.System);
        Assert.Equal(a.Hue, b.Hue);
        Assert.Equal(a.Primary, b.Primary);
        Assert.Matches("^#[0-9a-f]{6}$", a.Primary);
        Assert.Matches("^#[0-9a-f]{6}$", a.Accent);
    }

    [Fact]
    public void CustomThemeCategoryListsAreHonoured()
    {
        var themes = VillageThemeConfig.FromJson("{\"systems\":{\"fire_alarm\":{\"categories\":[\"Tulekahjuandurid\"],\"primary\":\"#aa0000\"}}}");
        var yard = VillageWarehouseYard.Plan(Categories(("Tulekahjuandurid", 90), ("Fire Alarm Devices", 80)), themes);

        Assert.Equal("fire_alarm", yard[0].System);
        Assert.Equal("#aa0000", yard[0].Primary);
        // The default category no longer belongs to the system once the config replaces the list.
        Assert.Equal(VillageWarehouseYard.OtherSystem, yard[1].System);
    }

    [Fact]
    public void IdsAreSluggedAndUnique()
    {
        var yard = VillageWarehouseYard.Plan(Categories(("Fire Alarm Devices", 30), ("Fire-Alarm  Devices", 20)));

        Assert.Equal("wh_fire_alarm_devices", yard[0].Id);
        Assert.Equal("wh_fire_alarm_devices_2", yard[1].Id);
    }

    [Fact]
    public void YardFillsRowsOfFiveWithoutOverlapping()
    {
        var rows = Enumerable.Range(1, 12).Select(i => ("Category " + i, (long)(1000 - i))).ToArray();
        var yard = VillageWarehouseYard.Plan(Categories(rows), max: 12);

        Assert.Equal(12, yard.Count);
        Assert.Equal(VillageWarehouseYard.OriginX, yard[0].TileX, 3);
        Assert.Equal(VillageWarehouseYard.OriginY, yard[0].TileY, 3);
        Assert.Equal(yard[0].TileY, yard[4].TileY, 3);
        Assert.True(yard[5].TileY > yard[0].TileY, "the sixth warehouse starts a new row");
        Assert.Equal(3, yard.Select(w => w.TileY).Distinct().Count());

        // Every warehouse fits inside its pitch, so no two buildings can touch.
        Assert.All(yard, w => Assert.True(w.Footprint < VillageWarehouseYard.ColumnPitch && w.Footprint < VillageWarehouseYard.RowPitch));
        Assert.Equal(12, yard.Select(w => (w.TileX, w.TileY)).Distinct().Count());
    }

    [Fact]
    public void YardStartsBelowTheLowestLandmark()
    {
        var lowest = VillageLayout.Default.Max(b => b.TileY);
        Assert.True(VillageWarehouseYard.OriginY > lowest + 1, "the yard must not collide with the village");
    }

    [Fact]
    public void EvenTheWidestWarehouseStaysInsideTheGround()
    {
        // The map is an isometric diamond over tiles 0..n: anything at a negative tile x has no
        // ground under it, so the first column has to clear half of the largest footprint.
        var rows = Enumerable.Range(1, 20).Select(i => ("Category " + i, 9_000L)).ToArray();
        var yard = VillageWarehouseYard.Plan(Categories(rows), max: 20);

        Assert.All(yard, w => Assert.True(w.TileX + 0.5 - w.Footprint / 2 > 0,
            "warehouse " + w.Id + " hangs off the left edge of the ground grid"));
    }

    [Fact]
    public void ExtentCoversTheWholeYard()
    {
        var yard = VillageWarehouseYard.Plan(Categories(("A", 5000), ("B", 4000), ("C", 3000)));
        var (width, height) = VillageWarehouseYard.Extent(yard);

        Assert.True(width >= yard.Max(w => w.TileX));
        Assert.True(height >= yard.Max(w => w.TileY));
        Assert.Equal((0d, 0d), VillageWarehouseYard.Extent(null));
    }

    [Fact]
    public void CloneIsDeep()
    {
        var yard = VillageWarehouseYard.Plan(Categories(("Fire Alarm Devices", 120)));
        var copy = VillageWarehouseYard.Clone(yard);
        copy[0].Count = 1;

        Assert.Equal(120, yard[0].Count);
        Assert.Empty(VillageWarehouseYard.Clone(null));
    }

    [Fact]
    public void HslToHexProducesCanonicalColours()
    {
        Assert.Equal("#ff0000", VillageWarehouseYard.HslToHex(0, 1, 0.5));
        Assert.Equal("#00ff00", VillageWarehouseYard.HslToHex(120, 1, 0.5));
        Assert.Equal("#0000ff", VillageWarehouseYard.HslToHex(240, 1, 0.5));
        // Out-of-range hues wrap: -240° and 480° are both 120°.
        Assert.Equal("#00ff00", VillageWarehouseYard.HslToHex(-240, 1, 0.5));
        Assert.Equal("#00ff00", VillageWarehouseYard.HslToHex(480, 1, 0.5));
        Assert.Equal("#808080", VillageWarehouseYard.HslToHex(37, 0, 0.5));
    }

    [Fact]
    public void SlugFallsBackForNamesWithoutAsciiLetters()
    {
        Assert.Equal("category", VillageWarehouseYard.Slug("—"));
        Assert.Equal("category", VillageWarehouseYard.Slug(null));
        Assert.Equal("data_devices", VillageWarehouseYard.Slug("  Data / Devices  "));
    }

    [Fact]
    public void SnapshotCarriesTheYardAndItsLimit()
    {
        var aggregator = new VillageAggregator(new VillageOptions { MaxWarehouses = 7 });
        aggregator.Warehouses = VillageWarehouseYard.Plan(Categories(("Fire Alarm Devices", 120)));

        var snapshot = aggregator.Snapshot();

        Assert.Single(snapshot.Warehouses);
        Assert.Equal("wh_fire_alarm_devices", snapshot.Warehouses[0].Id);
        Assert.Equal(7, snapshot.ViewerOptions.MaxWarehouses);

        // The snapshot must be a copy: mutating it cannot reach back into the aggregator.
        snapshot.Warehouses[0].Count = 0;
        Assert.Equal(120, aggregator.Warehouses[0].Count);
    }
}
