using System.IO;
using RevitMCP.Addin.Checkpoints;
using Xunit;

namespace RevitMCP.Tests;

public class ChangeNettingTests
{
    private static ChangeBatch Batch(string by, long[]? added = null, long[]? modified = null, long[]? deleted = null, string op = "TransactionCommitted")
        => new()
        {
            Attribution = by,
            Operation = op,
            Added = added ?? Array.Empty<long>(),
            Modified = modified ?? Array.Empty<long>(),
            Deleted = deleted ?? Array.Empty<long>()
        };

    [Fact]
    public void AddThenDeleteNetsOut()
    {
        var net = ChangeNetting.Net(new[]
        {
            Batch("agent:revit_place_tags", added: new[] { 1L, 2L }),
            Batch("user", deleted: new[] { 1L })
        });

        Assert.False(net.ContainsKey(1));
        Assert.Equal(NetChangeKind.Added, net[2].Kind);
    }

    [Fact]
    public void AddThenModifyStaysAdded()
    {
        var net = ChangeNetting.Net(new[]
        {
            Batch("agent:revit_place_tags", added: new[] { 5L }),
            Batch("agent:revit_move_elements", modified: new[] { 5L })
        });

        Assert.Equal(NetChangeKind.Added, net[5].Kind);
        Assert.Equal("agent:revit_move_elements", net[5].By);
    }

    [Fact]
    public void LatestAttributionWinsAndAgentTouchIsRemembered()
    {
        var net = ChangeNetting.Net(new[]
        {
            Batch("agent:revit_move_elements", modified: new[] { 7L }),
            Batch("user", modified: new[] { 7L })
        });

        Assert.Equal(NetChangeKind.Modified, net[7].Kind);
        Assert.Equal("user", net[7].By);
        Assert.True(net[7].TouchedByAgent);
    }

    [Fact]
    public void UndoOfAnAdditionReversesIt()
    {
        var net = ChangeNetting.Net(new[]
        {
            Batch("agent:revit_create_text_notes", added: new[] { 10L }),
            Batch("user", deleted: new[] { 10L }, op: "TransactionUndone")
        });

        Assert.Empty(net);
    }

    [Fact]
    public void UndoOfADeletionLeavesTheElementModified()
    {
        var net = ChangeNetting.Net(new[]
        {
            Batch("user", deleted: new[] { 3L }),
            Batch("user", added: new[] { 3L }, op: "TransactionUndone")
        });

        Assert.Equal(NetChangeKind.Modified, net[3].Kind);
    }

    [Fact]
    public void DeletionOfPreExistingElementIsDeleted()
    {
        var net = ChangeNetting.Net(new[]
        {
            Batch("agent:revit_set_parameter", modified: new[] { 4L }),
            Batch("user", deleted: new[] { 4L })
        });

        Assert.Equal(NetChangeKind.Deleted, net[4].Kind);
        Assert.Equal("user", net[4].By);
    }

    [Fact]
    public void CountsByAttributionAndCategory()
    {
        var net = ChangeNetting.Net(new[]
        {
            Batch("agent:revit_move_elements", modified: new[] { 1L, 2L, 3L }),
            Batch("user", added: new[] { 9L })
        });
        var byAttribution = ChangeNetting.CountByAttribution(net.Values);
        var byCategory = ChangeNetting.CountByCategory(net.Values, id => id == 9 ? "Text Notes" : id == 3 ? null : "Electrical Fixtures");

        Assert.Equal(3, byAttribution["agent:revit_move_elements"]);
        Assert.Equal(1, byAttribution["user"]);
        Assert.Equal(2, byCategory["Electrical Fixtures"]);
        Assert.Equal(1, byCategory["Text Notes"]);
        Assert.Equal(1, byCategory["(unknown)"]);
    }

    [Theory]
    [InlineData("agent:revit_move_elements (Claude)", "agent", true)]
    [InlineData("agent:revit_move_elements (Claude)", "revit_move_elements", true)]
    [InlineData("agent:revit_move_elements", "user", false)]
    [InlineData("user", "user", true)]
    [InlineData("page", "all", true)]
    [InlineData("page", null, true)]
    public void AttributionFilter(string by, string? filter, bool expected)
        => Assert.Equal(expected, ChangeNetting.MatchesAttribution(by, filter));

    [Fact]
    public void AgentAttributionCarriesClientAndTool()
    {
        var by = ChangeAttribution.Agent("revit_move_elements", "Claude Code");
        Assert.Equal("agent:revit_move_elements (Claude Code)", by);
        Assert.Equal("revit_move_elements", ChangeAttribution.AgentTool(by));
        Assert.Null(ChangeAttribution.AgentTool("user"));
    }
}

public class SnapshotDiffTests
{
    private static ElementSnapshot Snap(double[]? point = null, double[]? line = null, double? rotation = null, params (string, string?)[] parameters)
    {
        var s = new ElementSnapshot { Category = "Electrical Fixtures", Name = "Socket", LocationMm = point, LocationLineMm = line, RotationRad = rotation };
        foreach (var (name, value) in parameters) s.Parameters[name] = value;
        return s;
    }

    [Fact]
    public void ParameterDeltasListOnlyWhatDiffers()
    {
        var before = Snap(parameters: new[] { ("Mark", (string?)"A1"), ("Comments", "x"), ("Height", "1200") });
        var now = Snap(parameters: new[] { ("Mark", (string?)"A2"), ("Comments", "x"), ("Height", "1200"), ("Circuit", "1") });

        var delta = SnapshotDiff.Compare(1, before, now);

        Assert.Equal(2, delta.Parameters.Count);
        var mark = delta.Parameters.Single(p => p.Name == "Mark");
        Assert.Equal("A1", mark.Before);
        Assert.Equal("A2", mark.Now);
        var circuit = delta.Parameters.Single(p => p.Name == "Circuit");
        Assert.Null(circuit.Before);
        Assert.Equal("1", circuit.Now);
    }

    [Fact]
    public void MovedRespectsTolerance()
    {
        var before = Snap(point: new[] { 0.0, 0.0, 0.0 });

        Assert.Null(SnapshotDiff.Compare(1, before, Snap(point: new[] { 0.5, 0.5, 0.0 })).Moved);
        var moved = SnapshotDiff.Compare(1, before, Snap(point: new[] { 1250.0, 0.0, 0.0 })).Moved;
        Assert.NotNull(moved);
        Assert.Equal(1250.0, moved!.DistanceMm);
    }

    [Fact]
    public void LineMoveUsesTheLargestEndpointDistance()
    {
        var before = Snap(line: new[] { 0.0, 0, 0, 1000, 0, 0 });
        var now = Snap(line: new[] { 0.0, 0, 0, 1000, 300, 0 });

        var delta = SnapshotDiff.Compare(1, before, now);

        Assert.Equal(300.0, delta.Moved!.DistanceMm);
    }

    [Fact]
    public void RotationIsDetectedAcrossTheWrap()
    {
        var delta = SnapshotDiff.Compare(1, Snap(point: new[] { 0.0, 0, 0 }, rotation: 0.0), Snap(point: new[] { 0.0, 0, 0 }, rotation: 2 * Math.PI));
        Assert.False(delta.Rotated);

        delta = SnapshotDiff.Compare(1, Snap(point: new[] { 0.0, 0, 0 }, rotation: 0.0), Snap(point: new[] { 0.0, 0, 0 }, rotation: Math.PI / 2));
        Assert.True(delta.Rotated);
    }

    [Fact]
    public void MissingElementIsDeleted()
    {
        var delta = SnapshotDiff.Compare(1, Snap(), null);
        Assert.True(delta.Missing);
        Assert.True(delta.HasChanges);
    }

    [Fact]
    public void CompareAllKeepsOnlyChangedElements()
    {
        var snapshot = new Dictionary<long, ElementSnapshot>
        {
            [1] = Snap(parameters: ("Mark", "A")),
            [2] = Snap(parameters: ("Mark", "B")),
            [3] = Snap(parameters: ("Mark", "C"))
        };
        var now = new Dictionary<long, ElementSnapshot>
        {
            [1] = Snap(parameters: ("Mark", "A")),
            [2] = Snap(parameters: ("Mark", "B2"))
        };

        var deltas = SnapshotDiff.CompareAll(snapshot, id => now.TryGetValue(id, out var s) ? s : null);

        Assert.Equal(new[] { 2L, 3L }, deltas.Keys.OrderBy(k => k).ToArray());
        Assert.True(deltas[3].Missing);
    }
}

public class WarningDiffTests
{
    private static WarningKey W(string def, string text, string severity = "Warning", params long[] ids)
        => new() { DefinitionId = def, Description = text, Severity = severity, ElementIds = ids.OrderBy(i => i).ToArray() };

    [Fact]
    public void NewResolvedAndUnchanged()
    {
        var before = new[] { W("a", "Overlap", ids: new[] { 1L, 2L }), W("b", "Duplicate mark", ids: 5) };
        var now = new[] { W("a", "Overlap", ids: new[] { 1L, 2L }), W("c", "Room not enclosed", "Error", 9) };

        var diff = WarningDiff.Compare(before, now);

        Assert.Equal(1, diff.Unchanged);
        Assert.Equal("c", Assert.Single(diff.New).DefinitionId);
        Assert.Equal("b", Assert.Single(diff.Resolved).DefinitionId);
    }

    [Fact]
    public void SameDefinitionOnDifferentElementsIsADifferentWarning()
    {
        var before = new[] { W("a", "Overlap", ids: new[] { 1L, 2L }) };
        var now = new[] { W("a", "Overlap", ids: new[] { 1L, 2L }), W("a", "Overlap", ids: new[] { 3L, 4L }) };

        var since = WarningDiff.Since(before, now);

        Assert.Equal(new[] { 3L, 4L }, Assert.Single(since).ElementIds);
    }

    [Fact]
    public void GroupsByDescriptionErrorsFirst()
    {
        var groups = WarningDiff.Group(new[]
        {
            W("a", "Overlap", ids: new[] { 1L, 2L }),
            W("a", "Overlap", ids: new[] { 2L, 3L }),
            W("c", "Room not enclosed", "Error", 9)
        });

        Assert.Equal("Room not enclosed", groups[0].Description);
        Assert.Equal(2, groups[1].Count);
        Assert.Equal(new[] { 1L, 2L, 3L }, groups[1].ElementIds);
    }

    [Theory]
    [InlineData("Warning", "warning", true)]
    [InlineData("Warning", "error", false)]
    [InlineData("Error", "error", true)]
    [InlineData("DocumentCorruption", "error", true)]
    [InlineData("Error", "all", true)]
    public void SeverityFilter(string severity, string filter, bool expected)
        => Assert.Equal(expected, WarningDiff.MatchesSeverity(W("x", "t", severity), filter));
}

public class RollbackPlannerTests
{
    private static Checkpoint CheckpointWith(params (long Id, ElementSnapshot Snap)[] snapshot)
    {
        var cp = new Checkpoint { Id = "cp-000001", ModelId = "abc", Title = "t" };
        foreach (var (id, snap) in snapshot) cp.Snapshot[id] = snap;
        return cp;
    }

    private static ElementSnapshot Snap(string mark, double x = 0)
        => new()
        {
            Category = "Electrical Fixtures",
            LocationMm = new[] { x, 0.0, 0.0 },
            Parameters = { ["Mark"] = mark },
            Raw = { ["Mark"] = RawParameterValue.String(mark) }
        };

    [Fact]
    public void ParametersWithoutAStoredValueOrWithElementIdsAreNotRestored()
    {
        var before = Snap("A");
        before.Parameters["Area"] = "12 m²";                       // read-only: no raw value stored
        before.Parameters["Wire Type"] = "Cu 3x1.5";
        before.Raw["Wire Type"] = RawParameterValue.ElementId(77);
        var now = Snap("A");
        now.Parameters["Area"] = "14 m²";
        now.Parameters["Wire Type"] = "Cu 3x2.5";
        var cp = CheckpointWith((1, before));
        cp.Batches.Add(new ChangeBatch { Attribution = "agent:revit_set_parameter", Modified = new[] { 1L } });
        var diff = DiffFor(cp, new() { [1] = now });

        var plan = RollbackPlanner.Plan(diff, cp, new RollbackOptions());

        Assert.Empty(plan.Values);
        Assert.Contains(plan.Skipped, s => s.Reason.Contains("'Area'"));
        Assert.Contains(plan.Skipped, s => s.Reason.Contains("element reference"));
    }

    [Fact]
    public void RawValuesRoundTrip()
    {
        Assert.True(RawParameterValue.TryParse(RawParameterValue.Double(0.1 + 0.2), out var kind, out var text));
        Assert.Equal('D', kind);
        Assert.Equal(0.1 + 0.2, double.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(RawParameterValue.TryParse(RawParameterValue.String(null), out kind, out text));
        Assert.Equal(('S', ""), (kind, text));
        Assert.False(RawParameterValue.TryParse("X:1", out _, out _));
    }

    private static CheckpointDiff DiffFor(Checkpoint cp, Dictionary<long, ElementSnapshot?> now)
        => CheckpointDiffBuilder.Build(cp, SnapshotDiff.CompareAll(cp.Snapshot, id => now.TryGetValue(id, out var s) ? s : null));

    [Fact]
    public void RestoresAgentChangesAndSkipsTheUsersWithoutForce()
    {
        var cp = CheckpointWith((1, Snap("A", 0)), (2, Snap("B", 0)));
        cp.Batches.Add(new ChangeBatch { Attribution = "agent:revit_move_elements", Modified = new[] { 1L, 2L } });
        cp.Batches.Add(new ChangeBatch { Attribution = "user", Modified = new[] { 2L } });
        cp.Batches.Add(new ChangeBatch { Attribution = "agent:revit_place_tags", Added = new[] { 50L } });
        var diff = DiffFor(cp, new() { [1] = Snap("A", 1250), [2] = Snap("B", 800) });

        var plan = RollbackPlanner.Plan(diff, cp, new RollbackOptions());

        Assert.Equal(new[] { 50L }, plan.Delete);
        var location = Assert.Single(plan.Locations);
        Assert.Equal(1, location.Id);
        Assert.Equal(-1250.0, location.TranslateMm![0]);
        Assert.Contains(plan.Skipped, s => s.Id == 2 && s.Reason.Contains("user"));

        var forced = RollbackPlanner.Plan(diff, cp, new RollbackOptions { Force = true });
        Assert.Equal(2, forced.Locations.Count);
    }

    [Fact]
    public void WhatSelectsActions()
    {
        var cp = CheckpointWith((1, Snap("A")));
        cp.Batches.Add(new ChangeBatch { Attribution = "agent:revit_set_parameter", Modified = new[] { 1L }, Added = new[] { 9L } });
        var diff = DiffFor(cp, new() { [1] = Snap("A2", 10) });
        var options = new RollbackOptions();
        Assert.True(RollbackOptions.TryParseWhat(new[] { "restoreValues" }, options, out _));

        var plan = RollbackPlanner.Plan(diff, cp, options);

        Assert.Empty(plan.Delete);
        Assert.Empty(plan.Locations);
        var value = Assert.Single(plan.Values);
        Assert.Equal("Mark", value.Parameter);
        Assert.Equal("A", value.Value);
        Assert.Equal("S:A", value.Raw);
        Assert.False(RollbackOptions.TryParseWhat(new[] { "recreate" }, new RollbackOptions(), out var unknown));
        Assert.Equal("recreate", unknown);
    }

    [Fact]
    public void DeletedElementsAreNeverRestored()
    {
        var cp = CheckpointWith((1, Snap("A")));
        cp.Batches.Add(new ChangeBatch { Attribution = "agent:revit_delete_elements", Deleted = new[] { 1L, 7L } });
        var diff = DiffFor(cp, new());

        var plan = RollbackPlanner.Plan(diff, cp, new RollbackOptions { Force = true });

        Assert.True(plan.IsEmpty);
        Assert.Equal(2, plan.CannotRestoreDeleted);
        Assert.All(plan.Skipped, s => Assert.Contains("not recreated", s.Reason));
    }

    [Fact]
    public void ModifiedWithoutSnapshotCannotBeRestored()
    {
        var cp = CheckpointWith();
        cp.Batches.Add(new ChangeBatch { Attribution = "agent:revit_set_parameter", Modified = new[] { 3L } });
        var diff = DiffFor(cp, new());

        var plan = RollbackPlanner.Plan(diff, cp, new RollbackOptions());

        Assert.True(plan.IsEmpty);
        Assert.Equal(1, plan.CannotRestoreNotSnapshotted);
    }

    [Fact]
    public void ChangeSeenOnlyBySnapshotNeedsForce()
    {
        var cp = CheckpointWith((1, Snap("A")));
        var diff = DiffFor(cp, new() { [1] = Snap("Z") });

        Assert.Equal(ChangeAttribution.Unknown, diff.Entries[1].By);
        Assert.Empty(RollbackPlanner.Plan(diff, cp, new RollbackOptions()).Values);
        Assert.Single(RollbackPlanner.Plan(diff, cp, new RollbackOptions { Force = true }).Values);
    }

    [Fact]
    public void ElementIdFilterLimitsThePlan()
    {
        var cp = CheckpointWith();
        cp.Batches.Add(new ChangeBatch { Attribution = "agent:revit_place_tags", Added = new[] { 1L, 2L, 3L } });
        var diff = DiffFor(cp, new());

        var plan = RollbackPlanner.Plan(diff, cp, new RollbackOptions { ElementIds = new HashSet<long> { 2 } });

        Assert.Equal(new[] { 2L }, plan.Delete);
    }

    [Fact]
    public void StretchedLineIsNotRestoredByAMove()
    {
        Assert.NotNull(RollbackPlanner.Translation(new[] { 0.0, 0, 0, 1000, 0, 0 }, new[] { 100.0, 0, 0, 1100, 0, 0 }, out _));
        Assert.Null(RollbackPlanner.Translation(new[] { 0.0, 0, 0, 1000, 0, 0 }, new[] { 0.0, 0, 0, 1500, 0, 0 }, out var reason));
        Assert.Contains("length", reason);
    }

    [Fact]
    public void RotationIsRestoredAboutTheCheckpointPoint()
    {
        var before = Snap("A", 0);
        before.RotationRad = 0;
        var now = Snap("A", 0);
        now.RotationRad = Math.PI / 2;
        var cp = CheckpointWith((1, before));
        cp.Batches.Add(new ChangeBatch { Attribution = "agent:revit_rotate_elements", Modified = new[] { 1L } });
        var diff = DiffFor(cp, new() { [1] = now });

        var location = Assert.Single(RollbackPlanner.Plan(diff, cp, new RollbackOptions()).Locations);

        Assert.Null(location.TranslateMm);
        Assert.Equal(-Math.PI / 2, location.RotateRad!.Value, 6);
    }
}

public class CheckpointStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rmcp-cp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch { }
    }

    private static Checkpoint New(string model = "a1b2c3d4", DateTimeOffset? at = null)
        => new()
        {
            Id = CheckpointIds.NewId(),
            ModelId = model,
            Title = "Before tagging",
            DocumentTitle = "P125",
            CreatedAt = at ?? DateTimeOffset.Now
        };

    [Fact]
    public void RoundTripsAndMarksTrackingIncompleteAfterReload()
    {
        var cp = New();
        cp.Snapshot[1] = new ElementSnapshot { Category = "Doors", LocationMm = new[] { 1.0, 2.0, 3.0 }, Parameters = { ["Mark"] = "D1" } };
        cp.Warnings.Add(new WarningKey { DefinitionId = "g", Description = "Overlap", ElementIds = new[] { 1L } });
        using (var store = new CheckpointStore(_folder, TimeSpan.FromMilliseconds(10)))
        {
            store.Add(cp);
            store.Record(cp.ModelId, new ChangeBatch { Attribution = "user", Modified = new[] { 1L }, Added = new[] { 2L } },
                new Dictionary<long, ElementIdentity> { [2] = new ElementIdentity { Category = "Text Notes" } });
            Assert.True(store.Find(cp.ModelId, cp.Id)!.TrackingComplete);
            store.Flush();
        }

        using var reopened = new CheckpointStore(_folder);
        var loaded = reopened.Find(cp.ModelId, cp.Id);

        Assert.NotNull(loaded);
        Assert.False(loaded!.TrackingComplete);
        Assert.Equal(CheckpointStore.RestartNote, loaded.TrackingNote);
        Assert.Equal("D1", loaded.Snapshot[1].Parameters["Mark"]);
        Assert.Equal(1.0, loaded.Snapshot[1].LocationMm![0]);
        Assert.Single(loaded.Batches);
        Assert.Equal("Text Notes", loaded.Identities[2].Category);
        Assert.Equal("Overlap", Assert.Single(loaded.Warnings).Description);
    }

    [Fact]
    public void EvictsTheOldestAtTheCap()
    {
        using var store = new CheckpointStore(_folder, TimeSpan.FromMilliseconds(10));
        var start = DateTimeOffset.Now.AddHours(-1);
        var first = New(at: start);
        store.Add(first);
        for (var i = 1; i < CheckpointStore.MaxPerModel; i++) Assert.Empty(store.Add(New(at: start.AddMinutes(i))));

        var evicted = store.Add(New(at: start.AddMinutes(30)));

        Assert.Equal(first.Id, Assert.Single(evicted).Id);
        Assert.Equal(CheckpointStore.MaxPerModel, store.ForModel(first.ModelId).Count);
        store.Flush();
        Assert.False(File.Exists(Path.Combine(_folder, first.ModelId, first.Id + ".json")));
    }

    [Fact]
    public void RecordOnlyReachesTheModelsCheckpoints()
    {
        using var store = new CheckpointStore(_folder, TimeSpan.FromMilliseconds(10));
        var a = New("aaaa");
        var b = New("bbbb");
        store.Add(a);
        store.Add(b);

        store.Record("aaaa", new ChangeBatch { Modified = new[] { 1L } }, null);

        Assert.Single(store.Find("aaaa", a.Id)!.Batches);
        Assert.Empty(store.Find("bbbb", b.Id)!.Batches);
        Assert.Equal("bbbb", store.FindModelOf(b.Id));
    }

    [Fact]
    public void MissingIdentitiesSkipsKnownIds()
    {
        using var store = new CheckpointStore(_folder, TimeSpan.FromMilliseconds(10));
        var cp = New();
        cp.Snapshot[1] = new ElementSnapshot();
        store.Add(cp);
        store.Record(cp.ModelId, new ChangeBatch { Added = new[] { 2L } }, new Dictionary<long, ElementIdentity> { [2] = new() });

        Assert.Equal(new[] { 3L }, store.MissingIdentities(cp.ModelId, new[] { 1L, 2L, 3L }));
        Assert.Empty(store.MissingIdentities("other", new[] { 1L }));
    }

    [Fact]
    public void RemoveDeletesTheFiles()
    {
        using var store = new CheckpointStore(_folder, TimeSpan.FromMilliseconds(10));
        var cp = New();
        store.Add(cp);
        store.Flush();

        Assert.True(store.Remove(cp.ModelId, cp.Id));
        store.Flush();

        Assert.False(File.Exists(Path.Combine(_folder, cp.ModelId, cp.Id + ".json")));
        Assert.False(store.Remove(cp.ModelId, cp.Id));
    }

    [Theory]
    [InlineData("cp-3fa9c1", true)]
    [InlineData("cp-3FA9C1", false)]
    [InlineData("cp-3fa9c", false)]
    [InlineData("cp-3fa9cg", false)]
    [InlineData("../cp-3f", false)]
    [InlineData(null, false)]
    public void IdValidation(string? id, bool valid)
        => Assert.Equal(valid, CheckpointIds.IsValid(id));

    [Fact]
    public void NewIdsAreValid()
    {
        for (var i = 0; i < 20; i++) Assert.True(CheckpointIds.IsValid(CheckpointIds.NewId()));
        Assert.False(CheckpointIds.IsValidModelId("..\\x"));
        Assert.True(CheckpointIds.IsValidModelId("no-document"));
    }

    [Fact]
    public void AddRefusesInvalidModelId()
    {
        using var store = new CheckpointStore(_folder);
        var cp = New("../evil");
        Assert.Throws<ArgumentException>(() => store.Add(cp));
    }
}

public class CheckpointDiffReportTests
{
    [Fact]
    public void SummaryCountsAndRestoredElementsAreNotModified()
    {
        var cp = new Checkpoint { Id = "cp-0000aa", ModelId = "m", Title = "Before", CreatedAt = DateTimeOffset.Now.AddMinutes(-5) };
        cp.Snapshot[1] = new ElementSnapshot { Category = "Electrical Fixtures", Parameters = { ["Mark"] = "A" }, Raw = { ["Mark"] = "S:A" } };
        cp.Snapshot[2] = new ElementSnapshot { Category = "Electrical Fixtures", Parameters = { ["Mark"] = "B" }, Raw = { ["Mark"] = "S:B" } };
        cp.Batches.Add(new ChangeBatch { Attribution = "agent:revit_set_parameter", Operation = "TransactionCommitted", TransactionNames = { "Revit MCP - Set Parameter" }, Modified = new[] { 1L, 2L } });
        cp.Batches.Add(new ChangeBatch { Attribution = "user", Operation = "TransactionCommitted", TransactionNames = { "Text" }, Added = new[] { 9L } });
        cp.Identities[9] = new ElementIdentity { Category = "Text Notes" };
        var now = new Dictionary<long, ElementSnapshot>
        {
            [1] = new() { Parameters = { ["Mark"] = "A2" } },
            [2] = new() { Parameters = { ["Mark"] = "B" } }   // set back by hand: touched but unchanged
        };
        var diff = CheckpointDiffBuilder.Build(cp, SnapshotDiff.CompareAll(cp.Snapshot, id => now[id]));
        var warnings = WarningDiff.Compare(Array.Empty<WarningKey>(), new[] { new WarningKey { DefinitionId = "d", Description = "Overlap", Severity = "Error", ElementIds = new[] { 1L } } });

        var report = CheckpointDiffReport.Build(cp, diff, warnings, new DiffReportOptions { Include = "elements" }, DateTimeOffset.Now);

        var counts = (Dictionary<string, object?>)report["counts"]!;
        Assert.Equal(1, counts["added"]);
        Assert.Equal(1, counts["modified"]);
        Assert.Equal(1, counts["touchedButUnchanged"]);
        var byCategory = (Dictionary<string, int>)counts["byCategory"]!;
        Assert.Equal(1, byCategory["Text Notes"]);
        var modified = (List<Dictionary<string, object?>>)report["modified"]!;
        Assert.Equal(1L, Assert.Single(modified)["id"]);
        Assert.Equal(1, ((Dictionary<string, object?>)report["warnings"]!)["newErrors"]);
        var undo = (Dictionary<string, object?>)report["undo"]!;
        Assert.Equal(new[] { "Revit MCP - Set Parameter", "Text" }, (List<string>)undo["transactionNames"]!);
        var can = (Dictionary<string, object?>)undo["rollbackCanRestore"]!;
        Assert.Equal(1, can["restoreValues"]);
        Assert.Equal(0, can["deleteAdded"]);   // the text note was the user's
    }

    [Fact]
    public void AttributionAndCategoryFilters()
    {
        var cp = new Checkpoint { Id = "cp-0000ab", ModelId = "m", Title = "t" };
        cp.Batches.Add(new ChangeBatch { Attribution = "agent:revit_place_tags", Added = new[] { 1L, 2L } });
        cp.Batches.Add(new ChangeBatch { Attribution = "user", Added = new[] { 3L } });
        cp.Identities[1] = new ElementIdentity { Category = "Electrical Fixture Tags" };
        cp.Identities[2] = new ElementIdentity { Category = "Text Notes" };
        var diff = CheckpointDiffBuilder.Build(cp, new Dictionary<long, ElementDelta>());

        var agentOnly = CheckpointDiffReport.Build(cp, diff, null, new DiffReportOptions { Attribution = "agent" }, DateTimeOffset.Now);
        var tagsOnly = CheckpointDiffReport.Build(cp, diff, null,
            new DiffReportOptions { Categories = new HashSet<string> { "Electrical Fixture Tags" } }, DateTimeOffset.Now);

        Assert.Equal(2, ((Dictionary<string, object?>)agentOnly["counts"]!)["added"]);
        Assert.Single((List<object>)agentOnly["transactions"]!);
        Assert.Equal(1, ((Dictionary<string, object?>)tagsOnly["counts"]!)["added"]);
        Assert.False(agentOnly.ContainsKey("added"));
    }
}
