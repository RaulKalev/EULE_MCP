using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Datums;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#164: revit_list_datums / create / edit — parsing, safety rules and the tool surface.</summary>
public class DatumTests
{
    private static readonly DatumOptions Default = new();

    private static List<ExistingDatum> Model() => new()
    {
        new ExistingDatum { Id = 10, Kind = DatumKinds.Level, Name = "Level 1", ElevationMm = 0 },
        new ExistingDatum { Id = 11, Kind = DatumKinds.Level, Name = "Level 2", ElevationMm = 3000 },
        new ExistingDatum { Id = 12, Kind = DatumKinds.Level, Name = "Level 4", ElevationMm = 12000 },
        new ExistingDatum { Id = 20, Kind = DatumKinds.Grid, Name = "A", Start = new MmPoint(0, 0, 0), End = new MmPoint(0, 10000, 0) },
        new ExistingDatum { Id = 30, Kind = DatumKinds.ReferencePlane, Name = "Axis", Start = new MmPoint(0, 0, 0), End = new MmPoint(5000, 0, 0) }
    };

    private static DatumCreateSpec Create(string levels = "", string grids = "", string planes = "")
    {
        var problems = new List<DatumProblem>();
        var spec = DatumSpecParser.ParseCreate(levels.Length == 0 ? null : JArray.Parse(levels), grids.Length == 0 ? null : JArray.Parse(grids),
            planes.Length == 0 ? null : JArray.Parse(planes), problems);
        Assert.Empty(problems);
        return spec;
    }

    private static readonly IReadOnlyDictionary<long, DatumViewInfo> PlanView = new Dictionary<long, DatumViewInfo>
    {
        [100] = new() { Id = 100, DirZ = -1 },
        [101] = new() { Id = 101, Problem = "ThreeD views cannot hold reference planes" }
    };

    // ── Parsing ──

    [Fact]
    public void ParsesLevelsGridsAndPlanes()
    {
        var spec = Create("[{\"name\":\"Level 3\",\"elevationMm\":6000}]",
            "[{\"name\":\"B\",\"startMm\":[0,0],\"endMm\":[0,5000]}]",
            "[{\"viewId\":100,\"bubbleEndMm\":[0,0,0],\"freeEndMm\":{\"x\":1000,\"y\":0,\"z\":0}}]");
        Assert.Equal(6000, spec.Levels[0].ElevationMm);
        Assert.Equal(new MmPoint(0, 5000, 0), spec.Grids[0].End);
        Assert.Equal(100, spec.ReferencePlanes[0].ViewId);
        Assert.Equal(1000, spec.ReferencePlanes[0].FreeEnd.X);
    }

    [Fact]
    public void ReportsMissingFieldsAndEmptyInput()
    {
        var problems = new List<DatumProblem>();
        DatumSpecParser.ParseCreate(JArray.Parse("[{\"name\":\"L\"}]"), null, null, problems);
        Assert.Contains(problems, p => p.Code == "missingElevation");

        problems.Clear();
        DatumSpecParser.ParseCreate(null, null, null, problems);
        Assert.Contains(problems, p => p.Code == "empty");

        problems.Clear();
        Assert.Empty(DatumSpecParser.ParseEdits(JArray.Parse("[{\"id\":5}]"), problems));
        Assert.Contains(problems, p => p.Code == "nothingToChange");
    }

    [Fact]
    public void ParsesEditsAndOptions()
    {
        var problems = new List<DatumProblem>();
        var edits = DatumSpecParser.ParseEdits("[{\"id\":10,\"newName\":\"EG\",\"elevationMm\":\"150,5\"},{\"id\":20,\"moveByMm\":[100,0]}]", problems);
        Assert.Empty(problems);
        Assert.Equal(150.5, edits[0].ElevationMm);
        Assert.Equal(new MmPoint(100, 0, 0), edits[1].MoveByMm);

        var o = DatumSpecParser.ParseOptions(null, false, false, true, "perItem");
        Assert.False(o.AllOrNothing);
        Assert.True(o.ConfirmHighImpact);
        Assert.Equal(DatumOptions.DefaultElevationToleranceMm, o.ElevationToleranceMm);
    }

    // ── Names ──

    [Theory]
    [InlineData("Level 3", null)]
    [InlineData("", "empty")]
    [InlineData("A:B", "not allow")]
    [InlineData(" X", "space")]
    public void NameValidation(string name, string? expected)
    {
        var problem = DatumPlanner.ValidateName(name);
        if (expected == null) Assert.Null(problem);
        else Assert.Contains(expected, problem);
    }

    // ── Create ──

    [Fact]
    public void ValidItemsPass()
    {
        var results = DatumPlanner.CheckCreate(Create("[{\"name\":\"Level 3\",\"elevationMm\":6000}]",
            "[{\"name\":\"B\",\"startMm\":[1000,0],\"endMm\":[1000,5000]}]",
            "[{\"viewId\":100,\"bubbleEndMm\":[0,1000],\"freeEndMm\":[4000,1000]}]"), Model(), PlanView, Default);
        Assert.All(results, r => Assert.Equal("ok", r.Status));
        Assert.True(DatumPlanner.CanApply(results, Default, out _));
    }

    [Fact]
    public void LevelNameAndElevationClashesAreRefusedUnlessAllowed()
    {
        var spec = Create("[{\"name\":\"level 1\",\"elevationMm\":6000},{\"name\":\"Roof\",\"elevationMm\":3000.5}]");
        var results = DatumPlanner.CheckCreate(spec, Model(), PlanView, Default);
        Assert.Contains(results[0].Problems, p => p.Code == "nameConflict");
        Assert.Contains(results[1].Problems, p => p.Code == "elevationClash");

        var allowed = DatumPlanner.CheckCreate(spec, Model(), PlanView, new DatumOptions { AllowEquivalentLevels = true });
        Assert.Equal("refused", allowed[0].Status); // the name still conflicts
        Assert.Equal("ok", allowed[1].Status);
    }

    [Fact]
    public void RepeatsInsideTheCallAreCaught()
    {
        var results = DatumPlanner.CheckCreate(Create("[{\"name\":\"N\",\"elevationMm\":9000},{\"name\":\"n\",\"elevationMm\":9000}]"), Model(), PlanView, Default);
        Assert.Equal("ok", results[0].Status);
        Assert.Equal("refused", results[1].Status);
        Assert.Contains(results[1].Problems, p => p.Code == "nameConflict");
        Assert.Contains(results[1].Problems, p => p.Code == "elevationClash");
    }

    [Fact]
    public void UnitMistakesAndBadGeometryAreRefused()
    {
        var results = DatumPlanner.CheckCreate(Create("[{\"name\":\"Far\",\"elevationMm\":5000000000}]",
            "[{\"startMm\":[0,0],\"endMm\":[0,1]},{\"startMm\":[0,0,0],\"endMm\":[100,0,500]},{\"startMm\":[0,0],\"endMm\":[0,10000]}]"), Model(), PlanView, Default);
        Assert.Contains(results[0].Problems, p => p.Code == "elevationOutOfRange");
        Assert.Contains(results[1].Problems, p => p.Code == "badGeometry");
        Assert.Contains(results[2].Problems, p => p.Code == "badGeometry");
        Assert.Contains(results[3].Problems, p => p.Code == "duplicateGeometry");
    }

    [Fact]
    public void ReferencePlaneNeedsAViewThatCanHoldIt()
    {
        var results = DatumPlanner.CheckCreate(Create(planes:
            "[{\"viewId\":999,\"bubbleEndMm\":[0,0],\"freeEndMm\":[100,0]}," +
            " {\"viewId\":101,\"bubbleEndMm\":[0,0],\"freeEndMm\":[100,0]}," +
            " {\"viewId\":100,\"bubbleEndMm\":[0,0,0],\"freeEndMm\":[0,0,1000]}," +
            " {\"viewId\":100,\"name\":\"Axis\",\"bubbleEndMm\":[0,0,0],\"freeEndMm\":[5000,0,0]}]"), Model(), PlanView, Default);
        Assert.Contains(results[0].Problems, p => p.Code == "viewNotFound");
        Assert.Contains(results[1].Problems, p => p.Code == "unsupportedView");
        Assert.Contains(results[2].Problems, p => p.Code == "badGeometry"); // along the view direction
        Assert.Contains(results[3].Problems, p => p.Code == "nameConflict");
    }

    [Fact]
    public void AllOrNothingRefusesTheWholeCallPerItemWritesTheRest()
    {
        var results = DatumPlanner.CheckCreate(Create("[{\"name\":\"Level 1\",\"elevationMm\":6000},{\"name\":\"Level 9\",\"elevationMm\":9000}]"), Model(), PlanView, Default);
        Assert.False(DatumPlanner.CanApply(results, Default, out var reason));
        Assert.Contains("allOrNothing", reason);
        Assert.True(DatumPlanner.CanApply(results, new DatumOptions { AllOrNothing = false }, out _));
    }

    // ── Edit ──

    private static DatumTarget Level(long id, string name, double mm, int hosted = 0, int views = 0, bool pinned = false) => new()
    {
        Id = id, Kind = DatumKinds.Level, Name = name, ElevationMm = mm, Pinned = pinned,
        Dependents = new DatumDependents { Hosted = hosted, Views = views, Examples = hosted > 0 ? new List<string> { $"{hosted} Walls" } : new() }
    };

    private static Dictionary<long, DatumTarget?> Targets(params DatumTarget?[] t) =>
        t.Where(x => x != null).ToDictionary(x => x!.Id, x => x)!;

    private static List<DatumEdit> Edits(string json)
    {
        var problems = new List<DatumProblem>();
        var edits = DatumSpecParser.ParseEdits(JArray.Parse(json), problems);
        Assert.Empty(problems);
        return edits;
    }

    [Fact]
    public void RenameIsLowImpactAndMovingAHostedLevelIsHigh()
    {
        var targets = Targets(Level(10, "Level 1", 0), Level(11, "Level 2", 3000, hosted: 120, views: 3));
        var rename = DatumPlanner.CheckEdits(Edits("[{\"id\":10,\"newName\":\"Ground\"}]"), targets, Model(), Default);
        Assert.Equal("ok", rename[0].Status);
        Assert.Equal("low", rename[0].Impact);

        var move = DatumPlanner.CheckEdits(Edits("[{\"id\":11,\"elevationMm\":3200}]"), targets, Model(), Default);
        Assert.Equal("refused", move[0].Status);
        Assert.Equal("high", move[0].Impact);
        Assert.True(move[0].NeedsConfirmation);
        Assert.Contains(move[0].Problems, p => p.Code == "confirmationRequired");
        Assert.Contains(move[0].Warnings, w => w.Contains("120 hosted") && w.Contains("3 view"));

        var confirmed = DatumPlanner.CheckEdits(Edits("[{\"id\":11,\"elevationMm\":3200}]"), targets, Model(), new DatumOptions { ConfirmHighImpact = true });
        Assert.Equal("ok", confirmed[0].Status);
        Assert.Equal("high", confirmed[0].Impact);
    }

    [Fact]
    public void AnEmptyLevelMovesFreelyButALargeMoveIsStillHigh()
    {
        var targets = Targets(Level(10, "Level 1", 0), Level(11, "Level 2", 3000));
        Assert.Equal("low", DatumPlanner.CheckEdits(Edits("[{\"id\":11,\"elevationMm\":3100}]"), targets, Model(), Default)[0].Impact);
        Assert.Equal("high", DatumPlanner.CheckEdits(Edits("[{\"id\":11,\"elevationMm\":4500}]"), targets, Model(), Default)[0].Impact);
    }

    [Fact]
    public void EditRefusals()
    {
        var targets = Targets(
            Level(10, "Level 1", 0), Level(11, "Level 2", 3000), Level(12, "Level 4", 12000, pinned: true),
            new DatumTarget { Id = 20, Kind = DatumKinds.Grid, Name = "A", OwnedByOther = true, Owner = "kim" },
            new DatumTarget { Id = 21, Kind = DatumKinds.Grid, Name = "B" },
            new DatumTarget { Id = 30, Kind = DatumKinds.ReferencePlane, Name = "Axis" });
        var results = DatumPlanner.CheckEdits(Edits(
            "[{\"id\":99,\"newName\":\"X\"}," +                   // not a datum
            " {\"id\":12,\"elevationMm\":12300}," +                 // pinned
            " {\"id\":20,\"newName\":\"Z\"}," +                    // checked out
            " {\"id\":10,\"elevationMm\":3000.2}," +               // lands on Level 2
            " {\"id\":21,\"elevationMm\":100}," +                  // wrong field
            " {\"id\":10,\"moveByMm\":[1,1]}," +                   // wrong field, and a second edit of id 10
            " {\"id\":30,\"newName\":\"axis a\"}]"), targets, Model(), new DatumOptions { ConfirmHighImpact = true });
        Assert.Contains(results[0].Problems, p => p.Code == "notFound");
        Assert.Contains(results[1].Problems, p => p.Code == "pinned");
        Assert.Contains(results[2].Problems, p => p.Code == "ownedByOther");
        Assert.Contains(results[3].Problems, p => p.Code == "elevationClash");
        Assert.Contains(results[4].Problems, p => p.Code == "wrongField");
        Assert.Contains(results[5].Problems, p => p.Code == "duplicateEdit");
        Assert.Equal("ok", results[6].Status);
    }

    [Fact]
    public void RenameConflictsAgainstModelAndAgainstOtherEdits()
    {
        var targets = Targets(Level(10, "Level 1", 0), Level(11, "Level 2", 3000));
        var results = DatumPlanner.CheckEdits(Edits("[{\"id\":10,\"newName\":\"level 2\"},{\"id\":11,\"newName\":\"Top\"},{\"id\":10,\"newName\":\"Top\"}]"),
            targets, Model(), Default);
        Assert.Contains(results[0].Problems, p => p.Code == "nameConflict");
        Assert.Equal("ok", results[1].Status);
        Assert.Contains(results[2].Problems, p => p.Code == "duplicateEdit");
    }

    [Fact]
    public void SwappingElevationsOfTwoLevelsIsNotAClash()
    {
        var targets = Targets(Level(10, "Level 1", 0), Level(11, "Level 2", 3000));
        var results = DatumPlanner.CheckEdits(Edits("[{\"id\":10,\"elevationMm\":3000},{\"id\":11,\"elevationMm\":0}]"), targets, Model(), new DatumOptions { ConfirmHighImpact = true });
        Assert.All(results, r => Assert.Equal("ok", r.Status));
    }

    [Fact]
    public void UnchangedEditsAreReportedNotWritten()
    {
        var targets = Targets(Level(10, "Level 1", 0));
        var results = DatumPlanner.CheckEdits(Edits("[{\"id\":10,\"newName\":\"Level 1\",\"elevationMm\":0.01}]"), targets, Model(), Default);
        Assert.Equal("unchanged", results[0].Status);
        Assert.False(DatumPlanner.CanApply(results, Default, out var reason));
        Assert.Contains("nothing", reason);
    }

    [Fact]
    public void GridMovesWithDependentsNeedConfirmation()
    {
        var grid = new DatumTarget { Id = 20, Kind = DatumKinds.Grid, Name = "A", Dependents = new DatumDependents { Other = 4 } };
        var results = DatumPlanner.CheckEdits(Edits("[{\"id\":20,\"moveByMm\":[500,0]}]"), Targets(grid), Model(), Default);
        Assert.Equal("high", results[0].Impact);
        Assert.Equal("refused", results[0].Status);
    }

    [Fact]
    public void ApprovalTextIsConcise()
    {
        var targets = Targets(Level(11, "Level 2", 3000, hosted: 5));
        var results = DatumPlanner.CheckEdits(Edits("[{\"id\":11,\"elevationMm\":3200}]"), targets, Model(), new DatumOptions { ConfirmHighImpact = true });
        var text = DatumPlanner.ApprovalText("Edit", "Project", results, new DatumOptions { ConfirmHighImpact = true });
        Assert.Contains("Edit 1 datum(s) in 'Project'", text);
        Assert.Contains("HIGH IMPACT", text);
        Assert.True(text.Split('\n').Length <= 6);
    }

    // ── Surface ──

    [Theory]
    [InlineData("revit_list_datums", "ReadOnly")]
    [InlineData("revit_preview_create_datums", "ReadOnly")]
    [InlineData("revit_create_datums", "RequiresApproval")]
    [InlineData("revit_preview_edit_datums", "ReadOnly")]
    [InlineData("revit_edit_datums", "DestructiveRequiresManualApproval")]
    public void ToolsAreInTheEditGroupWithTheRightPermissions(string tool, string permission)
    {
        Assert.Equal("edit", BridgeToolGroups.GroupOf(tool));
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
    }

    [Fact]
    public void ListLevelsStaysWhereItWas()
    {
        Assert.Equal("devices", BridgeToolGroups.GroupOf("revit_list_levels"));
    }

    [Fact]
    public void CreatesAndModifiesForPlans()
    {
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_create_datums"));
        Assert.Equal(PlanToolKind.Modifies, PlanToolClassification.Classify("revit_edit_datums"));
    }
}
