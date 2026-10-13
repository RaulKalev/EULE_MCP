using System.Text.Json.Nodes;
using RevitMCP.Addin.PanelSchematics;
using Xunit;

namespace RevitMCP.Tests;

public class PanelSchematicsEmptyRowsEditorTests
{
    // Two rows as the plugin's Newtonsoft serializer stores them, with a field the connector does not know.
    private const string Stored =
        "[{\"PanelId\":555,\"CircuitId\":null,\"WireTypeName\":\"\",\"ReaNr\":3,\"CircuitLength\":0.0,\"GroupName\":\"Reserv\",\"GroupNr\":\"F3\"," +
        "\"ApparentLoad\":0.0,\"CircuitBreaker\":\"C16\",\"Samm\":0,\"UniqueId\":\"row-a\",\"Symbol\":null,\"Rikkevooluga\":\"Ei\",\"IsChecked\":true," +
        "\"RowBackground\":\"#00FFFFFF\",\"FutureField\":{\"x\":1.50},\"LisaKW\":0.0,\"TotalKW\":0.0}," +
        "{\"PanelId\":555,\"CircuitId\":null,\"WireTypeName\":\"\",\"ReaNr\":5,\"GroupName\":\"Küte\",\"GroupNr\":\"F5\",\"UniqueId\":\"row-b\",\"Rikkevooluga\":\"Jah\"}]";

    private static readonly EmptyRowsEditor.CircuitRow[] Circuits =
    [
        new("101", 1), new("102", 2), new("104", 4), new("106", 6)
    ];

    private static EmptyRowsEditor.Operation Op(string action, string? uniqueId = null, int? reaNr = null, bool insert = false,
        params (string Field, object? Value)[] values)
    {
        var operation = new EmptyRowsEditor.Operation { Action = action, UniqueId = uniqueId, ReaNr = reaNr, Insert = insert };
        foreach (var (field, value) in values)
            operation.Values[field] = value == null ? null : JsonValue.Create(value);
        return operation;
    }

    private static JsonArray Rows(EmptyRowsEditor.Result result) => (JsonArray)JsonNode.Parse(result.Json!)!;

    [Fact]
    public void Add_WithoutReaNr_GoesToTheEnd_AndLooksLikeThePluginsBlankRow()
    {
        var result = EmptyRowsEditor.Apply(Stored, [Op("add", values: [("GroupNr", "F7"), ("Group Name", "Uus"), ("kW", "1,5")])], Circuits, 555, () => "new-id");

        Assert.True(result.Success, string.Join("; ", result.Errors));
        var rows = Rows(result);
        Assert.Equal(3, rows.Count);
        var added = (JsonObject)rows[2]!;
        Assert.Equal("new-id", (string?)added["UniqueId"]);
        Assert.Equal(7, (int)added["ReaNr"]!);
        Assert.Equal("F7", (string?)added["GroupNr"]);
        Assert.Equal("Uus", (string?)added["GroupName"]);
        Assert.Equal(1.5, (double)added["ApparentLoad"]!);
        Assert.Equal("Ei", (string?)added["Rikkevooluga"]);
        Assert.True((bool)added["IsChecked"]!);
        Assert.Null(added["CircuitId"]);
        Assert.Empty(result.CircuitRenumbers);
    }

    [Fact]
    public void Edit_KeepsOtherRowsAndUnknownFieldsExactlyAsStored()
    {
        var result = EmptyRowsEditor.Apply(Stored, [Op("update", "row-b", values: [("CircuitBreaker", "B10")])], Circuits, 555);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        var original = (JsonArray)JsonNode.Parse(Stored)!;
        var rows = Rows(result);
        Assert.True(JsonNode.DeepEquals(original[0], rows[0]));
        Assert.Contains("\"FutureField\":{\"x\":1.50}", result.Json);       // number text untouched
        Assert.Contains("\"GroupName\":\"Küte\"", result.Json);              // no \u escapes
        Assert.Equal("B10", (string?)rows[1]!["CircuitBreaker"]);
        var change = Assert.Single(result.Changes);
        Assert.Equal("row-b", change.UniqueId);
        var field = Assert.Single(change.Fields);
        Assert.Equal(("CircuitBreaker", (string?)null, "B10"), (field.Field, field.Before, field.After));
    }

    [Fact]
    public void Update_ToTheSameValue_ChangesNothing()
    {
        var result = EmptyRowsEditor.Apply(Stored, [Op("update", "row-a", values: [("ApparentLoad", 0), ("GroupNr", "F3")])], Circuits, 555);

        Assert.True(result.Success);
        Assert.False(result.Changed);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Add_OnATakenReaNr_IsRefused_UnlessInsert()
    {
        var refused = EmptyRowsEditor.Apply(Stored, [Op("add", reaNr: 4)], Circuits, 555);
        Assert.False(refused.Success);
        Assert.Contains("already used", refused.Errors[0]);

        var inserted = EmptyRowsEditor.Apply(Stored, [Op("add", reaNr: 4, insert: true)], Circuits, 555, () => "new-id");
        Assert.True(inserted.Success, string.Join("; ", inserted.Errors));

        // 4, 5, 6 were one unbroken run: circuit 104 -> 5, empty row-b -> 6, circuit 106 -> 7. Rows 1-3 keep their numbers.
        Assert.Equal(new[] { ("104", 4, 5), ("106", 6, 7) }, inserted.CircuitRenumbers.Select(r => (r.Key, r.From, r.To)).ToArray());
        var rows = Rows(inserted);
        Assert.Equal(3, (int)rows[0]!["ReaNr"]!);
        Assert.Equal(6, (int)rows[1]!["ReaNr"]!);
        Assert.Equal(4, (int)rows[2]!["ReaNr"]!);
    }

    [Fact]
    public void Insert_StopsAtTheFirstFreeNumber()
    {
        // 1 and 2 are circuits, 3 is row-a, 4 is a circuit; a gap follows at... none until 7, so add a gap by using other circuits
        var circuits = new[] { new EmptyRowsEditor.CircuitRow("101", 1), new EmptyRowsEditor.CircuitRow("102", 2), new EmptyRowsEditor.CircuitRow("108", 8) };
        var result = EmptyRowsEditor.Apply(Stored, [Op("add", reaNr: 2, insert: true)], circuits, 555);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(new[] { ("102", 2, 3) }, result.CircuitRenumbers.Select(r => (r.Key, r.From, r.To)).ToArray());
        var rows = Rows(result);
        Assert.Equal(4, (int)rows[0]!["ReaNr"]!);   // row-a pushed from 3
        Assert.Equal(5, (int)rows[1]!["ReaNr"]!);   // row-b stays: 4 was free
    }

    [Fact]
    public void Update_ReaNrOntoACircuit_IsRefused()
    {
        var result = EmptyRowsEditor.Apply(Stored, [Op("update", "row-a", values: [("ReaNr", 2)])], Circuits, 555);
        Assert.False(result.Success);
    }

    [Fact]
    public void Remove_LastRow_AsksForTheEntityToBeDeleted()
    {
        var result = EmptyRowsEditor.Apply(Stored, [Op("remove", "row-a"), Op("remove", reaNr: 5)], Circuits, 555);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.True(result.Changed);
        Assert.Null(result.Json);
        Assert.Equal(0, result.RowCount);
        Assert.Equal(2, result.Changes.Count(c => c.Action == "remove"));
    }

    [Theory]
    [InlineData("UniqueId", "x")]
    [InlineData("CircuitId", 5)]
    [InlineData("Nonsense", "x")]
    public void ProtectedAndUnknownFields_AreRefused(string field, object value)
    {
        var result = EmptyRowsEditor.Apply(Stored, [Op("update", "row-a", values: [(field, value)])], Circuits, 555);
        Assert.False(result.Success);
    }

    [Theory]
    [InlineData("Samm", "abc")]
    [InlineData("ReaNr", 1.5)]
    [InlineData("Rikkevooluga", "maybe")]
    [InlineData("LisaKW", "x")]
    public void WrongTypedValues_AreRefused(string field, object value)
    {
        var result = EmptyRowsEditor.Apply(Stored, [Op("update", "row-a", values: [(field, value)])], Circuits, 555);
        Assert.False(result.Success);
    }

    [Fact]
    public void Rikkevooluga_IsNormalised()
    {
        var result = EmptyRowsEditor.Apply(Stored, [Op("update", "row-a", values: [("Rikkevooluga", true)])], Circuits, 555);
        Assert.True(result.Success);
        Assert.Equal("Jah", (string?)Rows(result)[0]!["Rikkevooluga"]);
    }

    [Theory]
    [InlineData("{\"not\":\"a list\"}")]
    [InlineData("[1,2]")]
    [InlineData("[{\"ReaNr\":1}")]
    public void UnreadableStoredText_BlocksEveryWrite(string stored)
    {
        var result = EmptyRowsEditor.Apply(stored, [Op("add")], Circuits, 555);
        Assert.False(result.Success);
        Assert.Null(result.Json);
    }

    [Fact]
    public void NothingStored_FirstRowIsWritten()
    {
        var result = EmptyRowsEditor.Apply(null, [Op("add", values: [("GroupNr", "F1")])], [], 555, () => "first");
        Assert.True(result.Success);
        Assert.Equal(1, (int)Rows(result)[0]!["ReaNr"]!);
    }

    [Fact]
    public void StateToken_ChangesWithContent_AndTellsNullFromEmpty()
    {
        Assert.Equal(EmptyRowsEditor.StateToken("a", "b"), EmptyRowsEditor.StateToken("a", "b"));
        Assert.NotEqual(EmptyRowsEditor.StateToken("a", "b"), EmptyRowsEditor.StateToken("ab", ""));
        Assert.NotEqual(EmptyRowsEditor.StateToken((string?)null), EmptyRowsEditor.StateToken(""));
    }
}

public class PanelSchematicsContractTests
{
    [Theory]
    [InlineData("ReaNr", "ReaNr")]
    [InlineData("Rea Nr", "ReaNr")]
    [InlineData("group name", "GroupName")]
    [InlineData("Sümbol", "Symbol")]
    [InlineData("kW", "ApparentLoad")]
    [InlineData("Lisa kW", "LisaKW")]
    [InlineData("Wire Type", "WireTypeName")]
    [InlineData("Phase", "CircuitPhase")]
    public void RowFields_AreFoundByNameParameterOrLabel(string given, string expected)
        => Assert.Equal(expected, PanelSchematicsContract.FindRowField(given)?.Name);

    [Fact]
    public void Stores_HaveUniqueKeysAndGuids_AndLegacyStoresAreReadOnly()
    {
        Assert.Equal(PanelSchematicsContract.Stores.Count, PanelSchematicsContract.Stores.Select(s => s.Key).Distinct().Count());
        Assert.Equal(PanelSchematicsContract.Stores.Count, PanelSchematicsContract.Stores.Select(s => s.SchemaGuid).Distinct().Count());
        Assert.All(PanelSchematicsContract.Stores, s => Assert.True(Guid.TryParse(s.SchemaGuid, out _)));
        Assert.False(PanelSchematicsContract.GetStore(PanelSchematicsContract.OverviewValuesLegacyKey).Writable);
        Assert.False(PanelSchematicsContract.GetStore(PanelSchematicsContract.InfoLinksLegacyKey).Writable);
    }

    [Fact]
    public void MetadataFields_NameTheTitlePageParameterLikeThePlugin()
    {
        Assert.Equal("1.1 Nimipinge", PanelSchematicsContract.FindMetadataField("ET_1")!.DetailItemParameter);
        Assert.Equal("2.1 Keskuse tüüp", PanelSchematicsContract.FindMetadataField("ea_1")!.DetailItemParameter);
        Assert.Equal("3.2 Väljuvad kaablid", PanelSchematicsContract.FindMetadataField("KT_2")!.DetailItemParameter);
        Assert.Equal("1234567_EA_1", PanelSchematicsContract.MetadataMapKey(1234567, "EA_1"));
    }
}

public class PanelSchematicsSymbolsTests
{
    private const string File =
        "{\"SymbolMappings\":[" +
        "{\"Category\":\"Lülitid\",\"DisplayName\":\"Kaitselüliti\",\"FamilyName\":\"Kilp_1\",\"FamilyType\":\"1\",\"CustomName\":\"\"}," +
        "{\"Category\":\"Pealülitid\",\"DisplayName\":\"Pealüliti\",\"FamilyName\":\"Kilp_Pealüliti\",\"FamilyType\":\"1\"}," +
        "{\"Category\":\"Pealülitid\",\"DisplayName\":\"Pealüliti Cu/Cu\",\"FamilyName\":\"Kilp_Pealüliti\",\"FamilyType\":\"Cu/Cu\"}," +
        "{\"Category\":\"\",\"DisplayName\":\" Klemm \",\"FamilyName\":\"Kilp_Klemm\",\"FamilyType\":\"1\"}," +
        "{\"Category\":\"Muu\",\"DisplayName\":\"\",\"FamilyName\":\"x\",\"FamilyType\":\"1\"}]}";

    [Fact]
    public void Parse_ReadsDisplayNames_LikeThePlugin()
    {
        var library = PanelSchematicsSymbols.Parse(File);

        Assert.True(library.Loaded);
        Assert.Equal(["Kaitselüliti", "Pealüliti", "Pealüliti Cu/Cu", "Klemm"], library.Symbols.Select(s => s.Name));
        Assert.Equal("Muu", library.Symbols[3].Category);          // empty category
        Assert.True(library.Contains("Pealüliti"));
        Assert.False(library.Contains("pealüliti"));                // the plugin matches the exact name
    }

    [Fact]
    public void Suggest_OffersTheSameNameInAnotherCaseFirst_ThenNamesContainingIt()
    {
        var library = PanelSchematicsSymbols.Parse(File);
        Assert.Equal(["Pealüliti", "Pealüliti Cu/Cu"], library.Suggest("pealüliti"));
        Assert.Empty(library.Suggest("Trafo"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    public void Parse_OfSomethingElse_IsNotLoaded(string json)
    {
        var library = PanelSchematicsSymbols.Parse(json);
        Assert.False(library.Loaded);
        Assert.NotNull(library.Problem);
    }
}

public class PanelSchematicsPayloadTests
{
    [Fact]
    public void RowGroups_RoundTrip_InThePluginsFormat()
    {
        var groups = new List<RowGroupsPayload.Group>
        {
            new() { Id = "g1", Parent = "C:abc", Dependents = ["E:row-a", "C:def"] },
            new() { Id = "g2", Parent = "E:row-b", Links = ["link-1"] }
        };

        var json = RowGroupsPayload.Serialize(groups);
        Assert.Equal("{\"v\":1,\"groups\":[{\"id\":\"g1\",\"parent\":\"C:abc\",\"deps\":[\"E:row-a\",\"C:def\"]},{\"id\":\"g2\",\"parent\":\"E:row-b\",\"deps\":[],\"links\":[\"link-1\"]}]}", json);

        Assert.Equal(RowGroupsPayload.ParseStatus.Ok, RowGroupsPayload.TryParse(json, out var parsed));
        Assert.Equal(2, parsed.Count);
        Assert.Equal(["E:row-a", "C:def"], parsed[0].Dependents);
        Assert.Equal(["link-1"], parsed[1].Links);
    }

    [Theory]
    [InlineData("{\"v\":2,\"groups\":[]}", RowGroupsPayload.ParseStatus.UnsupportedVersion)]
    [InlineData("{\"groups\":[]}", RowGroupsPayload.ParseStatus.Unreadable)]
    [InlineData("{\"v\":1,\"groups\":[{\"id\":\"g\",\"parent\":\"C:a\",\"deps\":[]}]}", RowGroupsPayload.ParseStatus.Unreadable)]             // empty group
    [InlineData("{\"v\":1,\"groups\":[{\"id\":\"g\",\"parent\":\"C:a\",\"deps\":[\"C:a\"]}]}", RowGroupsPayload.ParseStatus.Unreadable)]      // depends on itself
    [InlineData("{\"v\":1,\"groups\":[{\"id\":\"g\",\"parent\":\"row 3\",\"deps\":[\"C:b\"]}]}", RowGroupsPayload.ParseStatus.Unreadable)]    // not an identity
    [InlineData("{\"v\":1,\"groups\":[]}", RowGroupsPayload.ParseStatus.Ok)]
    public void RowGroups_AreParsedAsStrictlyAsThePlugin(string json, RowGroupsPayload.ParseStatus expected)
        => Assert.Equal(expected, RowGroupsPayload.TryParse(json, out _));

    [Fact]
    public void RowGroups_ARowInTwoGroups_IsAnError()
    {
        var errors = RowGroupsPayload.Validate(
        [
            new() { Id = "g1", Parent = "C:a", Dependents = ["C:b"] },
            new() { Id = "g2", Parent = "C:c", Dependents = ["C:b"] }
        ]);
        Assert.Single(errors);
    }

    [Fact]
    public void PanelRecord_RoundTrip()
    {
        var json = PanelRecordPayload.Serialize(new PanelRecordPayload.Record { PanelUniqueId = "p", ViewUniqueId = "v", PanelName = "JK-1", ViewName = "JK-1" });
        Assert.Equal("{\"v\":1,\"panel\":\"p\",\"view\":\"v\",\"panelName\":\"JK-1\",\"viewName\":\"JK-1\"}", json);
        Assert.Equal(RowGroupsPayload.ParseStatus.Ok, PanelRecordPayload.TryParse(json, out var record));
        Assert.Equal("v", record!.ViewUniqueId);
        Assert.Equal(RowGroupsPayload.ParseStatus.UnsupportedVersion, PanelRecordPayload.TryParse("{\"v\":3,\"panel\":\"p\",\"view\":\"v\"}", out _));
    }

    [Fact]
    public void Colors_AddLabel_AssignAndRemove()
    {
        const string stored = "{\"ColorLabels\":[{\"Id\":\"aaaa1111\",\"Hex\":\"#4CAF50\",\"Label\":\"Korrus 1\"}],\"PanelColors\":{\"10\":\"aaaa1111\"},\"Extra\":true}";

        var added = PanelColorsPayload.Apply(stored, [new() { Hex = "#FF0000", Label = "Korrus 2" }], [],
            new Dictionary<string, string?> { ["20"] = "bbbb2222" }, () => "bbbb2222");
        Assert.Empty(added.Errors);
        Assert.True(added.Changed);
        Assert.Contains("\"20\":\"bbbb2222\"", added.Json);
        Assert.Contains("\"Extra\":true", added.Json);

        var removed = PanelColorsPayload.Apply(stored, [], ["aaaa1111"], new Dictionary<string, string?>());
        Assert.Empty(removed.Errors);
        Assert.DoesNotContain("aaaa1111", removed.Json);

        var bad = PanelColorsPayload.Apply(stored, [new() { Hex = "green" }], [], new Dictionary<string, string?> { ["30"] = "nope" });
        Assert.Equal(2, bad.Errors.Count);
        Assert.Null(bad.Json);
    }
}
