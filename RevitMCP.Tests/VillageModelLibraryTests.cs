using RevitMCP.Village;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// The models folder is served read-only over the loopback listener, so path handling is the
/// security-relevant part: no request may escape the folder, however it is spelled.
/// </summary>
public class VillageModelLibraryTests : IDisposable
{
    private readonly string _root;

    public VillageModelLibraryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "village-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "landmarks"));
        Directory.CreateDirectory(Path.Combine(_root, "warehouses"));
        File.WriteAllBytes(Path.Combine(_root, "landmarks", "town_hall.glb"), new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(Path.Combine(_root, "warehouses", "fire_alarm_devices.glb"), new byte[] { 5, 6 });
        // A file that must never be served: right next to the folder, wrong extension.
        File.WriteAllText(Path.Combine(_root, "secrets.txt"), "nope");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private VillageModelLibrary Library() => new(_root);

    [Fact]
    public void IndexListsOnlyGlbFilesInKnownFolders()
    {
        var index = Library().Index(folderConfigured: true);

        Assert.True(index.Enabled);
        Assert.Equal(new[] { "landmarks/town_hall.glb", "warehouses/fire_alarm_devices.glb" },
            index.Models.Select(m => m.Path));
        Assert.Equal("town_hall", index.Models[0].Name);
        Assert.Equal("landmarks", index.Models[0].Kind);
        Assert.Equal(4, index.Models[0].Bytes);
    }

    [Fact]
    public void AMissingFolderIsNotAnError_ItJustHasNoModels()
    {
        var library = new VillageModelLibrary(Path.Combine(_root, "does-not-exist"));

        Assert.Null(library.Root);
        var index = library.Index(folderConfigured: false);
        Assert.False(index.Enabled);
        Assert.Empty(index.Models);
        Assert.Null(index.Error);
    }

    [Fact]
    public void AConfiguredFolderThatIsMissingSaysSo()
    {
        var index = new VillageModelLibrary(Path.Combine(_root, "nope")).Index(folderConfigured: true);
        Assert.NotNull(index.Error);
    }

    [Fact]
    public void AFlatFolderWorksToo_BecauseAssetPacksShipThatWay()
    {
        // The exported pack is one folder of .glb files; making someone sort 33 of them into
        // sub-folders before seeing anything would be a poor first five minutes.
        File.WriteAllBytes(Path.Combine(_root, "town-hall.glb"), new byte[] { 9 });
        var library = Library();

        var index = library.Index(folderConfigured: true);
        Assert.Contains(index.Models, m => m.Path == "town-hall.glb" && m.Kind == VillageModelLibrary.RootKind);
        Assert.NotNull(library.Resolve("town-hall.glb"));

        // A sub-folder path still works, and the traversal rules are unchanged.
        Assert.NotNull(library.Resolve("landmarks/town_hall.glb"));
        Assert.Null(library.Resolve("../town-hall.glb"));
        Assert.Null(library.Resolve("secrets.txt"));
    }

    [Fact]
    public void HyphensAreAcceptedInNames()
    {
        // The pack names files fire-alarm-devices.glb; the viewer normalises when matching, so the
        // library only has to agree that a hyphen is a safe character.
        Assert.True(VillageModelLibrary.IsSafeName("fire-alarm-devices"));
        File.WriteAllBytes(Path.Combine(_root, "warehouses", "fire-alarm-devices.glb"), new byte[] { 1 });
        Assert.NotNull(Library().Resolve("warehouses/fire-alarm-devices.glb"));
    }

    [Fact]
    public void ValidPathsResolve()
    {
        var library = Library();
        Assert.NotNull(library.Resolve("landmarks/town_hall.glb"));
        Assert.NotNull(library.Resolve("warehouses/fire_alarm_devices.glb"));
        // Backslashes are normalised, since a viewer or a proxy may send either.
        Assert.NotNull(library.Resolve(@"landmarks\town_hall.glb"));
    }

    [Theory]
    // Traversal, in every spelling worth worrying about.
    [InlineData("../secrets.txt")]
    [InlineData("landmarks/../../secrets.txt")]
    [InlineData("landmarks/../secrets.txt")]
    [InlineData(@"..\secrets.txt")]
    [InlineData("landmarks/..%2fsecrets.txt")]
    [InlineData("%2e%2e/secrets.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("//server/share/x.glb")]
    // Wrong shape: not exactly <known kind>/<name>.glb
    [InlineData("secrets.txt")]
    [InlineData("landmarks/town_hall.txt")]
    [InlineData("unknown_kind/town_hall.glb")]
    [InlineData("landmarks/sub/town_hall.glb")]
    [InlineData("landmarks/")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("landmarks/town hall.glb")]
    [InlineData("landmarks/town_hall.glb.exe")]
    public void NothingEscapesTheFolder(string? path)
    {
        Assert.Null(Library().Resolve(path));
    }

    [Fact]
    public void AVeryLongPathIsRejected()
    {
        Assert.Null(Library().Resolve("landmarks/" + new string('a', 300) + ".glb"));
    }

    [Fact]
    public void ResolveReturnsNullWhenNoFolderIsConfigured()
    {
        Assert.Null(new VillageModelLibrary(null).Resolve("landmarks/town_hall.glb"));
    }

    [Fact]
    public void SafeNamesAreSlugShaped()
    {
        Assert.True(VillageModelLibrary.IsSafeName("fire_alarm_devices"));
        Assert.True(VillageModelLibrary.IsSafeName("tree-2"));
        Assert.False(VillageModelLibrary.IsSafeName("fire alarm"));
        Assert.False(VillageModelLibrary.IsSafeName("../x"));
        Assert.False(VillageModelLibrary.IsSafeName(""));
    }

    [Fact]
    public void TheWarehouseFileNameIsTheCategorySlug_WithoutTheIdPrefix()
    {
        // The id the snapshot publishes is wh_fire_alarm_devices; the file is named after the
        // category, so what you export matches what Revit calls it.
        var yard = VillageWarehouseYard.Plan(new List<VillageThemeEvidence>
        {
            new() { Name = "Fire Alarm Devices", Count = 120 }
        });

        Assert.Equal("wh_fire_alarm_devices", yard[0].Id);
        Assert.Equal("warehouses/fire_alarm_devices.glb", VillageModelLibrary.FileNameFor("warehouses", yard[0].Id));
        Assert.Equal("landmarks/town_hall.glb", VillageModelLibrary.FileNameFor("landmarks", VillageLayout.TownHall));
    }

    [Fact]
    public void EveryLandmarkHasAReachableFileName()
    {
        // A landmark whose id the library would refuse to serve could never be given a model, so
        // this actually creates the file and asks the library to resolve it back.
        var library = Library();
        foreach (var b in VillageLayout.Default)
        {
            var name = VillageModelLibrary.FileNameFor("landmarks", b.Id);
            Assert.StartsWith("landmarks/", name);
            Assert.EndsWith(".glb", name);
            Assert.True(VillageModelLibrary.IsSafeName(b.Id), b.Id + " is not a safe file name");

            File.WriteAllBytes(Path.Combine(_root, "landmarks", b.Id + ".glb"), new byte[] { 1 });
            Assert.True(library.Resolve(name) != null, name + " could not be resolved back");
        }
    }

    [Fact]
    public void OversizedFilesAreSkipped()
    {
        // Guard rather than allocate 32 MB: the cap is what the index and Resolve both check.
        Assert.Equal(32L * 1024 * 1024, VillageModelLibrary.MaxBytes);
        Assert.Equal(".glb", VillageModelLibrary.Extension);
    }

    [Fact]
    public void TheModelsFolderComesFromConfig()
    {
        var user = VillageOptions.ParseConfig("{\"village\":{\"modelsFolder\":\"D:\\\\art\\\\village\"}}");
        Assert.Equal(@"D:\art\village", VillageOptions.FromConfig(user, null).ModelsFolder);
        Assert.Null(VillageOptions.FromConfig(null, null).ModelsFolder);
    }
}
