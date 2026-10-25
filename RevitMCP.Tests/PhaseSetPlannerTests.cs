using RevitMCP.Addin.ModelState;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#109: validation and before/after rows of revit_set_phase.</summary>
public class PhaseSetPlannerTests
{
    private static readonly PhaseInfo Existing = new() { Id = 10, Name = "Olemasolev", Sequence = 1 };
    private static readonly PhaseInfo New = new() { Id = 11, Name = "Uus", Sequence = 2 };
    private static readonly PhaseInfo Later = new() { Id = 12, Name = "Etapp 2", Sequence = 3 };
    private static readonly IReadOnlyList<PhaseInfo> Phases = new[] { Existing, New, Later };

    private static PhaseElementState Element(long id, PhaseInfo? created, PhaseInfo? demolished = null, bool readOnly = false) => new()
    {
        Id = id,
        Category = "Electrical Fixtures",
        Name = "Socket",
        HasPhaseParameters = true,
        Created = created,
        Demolished = demolished,
        CreatedReadOnly = readOnly,
        DemolishedReadOnly = readOnly
    };

    private static (PhaseTarget Created, PhaseTarget Demolished) Targets(string? created, string? demolished)
    {
        Assert.True(PhaseSetPlanner.TryParseTargets(created, demolished, Phases, out var c, out var d, out var status, out var error),
            $"{status}: {error}");
        return (c, d);
    }

    [Theory]
    [InlineData("Uus", 11)]
    [InlineData("uus", 11)]
    [InlineData(" Etapp 2 ", 12)]
    [InlineData("10", 10)]
    public void ResolvePhase_ByNameOrId(string text, long expected) =>
        Assert.Equal(expected, PhaseSetPlanner.ResolvePhase(text, Phases)!.Id);

    [Fact]
    public void UnknownPhase_IsPhaseNotFound_WithTheAvailablePhases()
    {
        Assert.False(PhaseSetPlanner.TryParseTargets("Lammutus", null, Phases, out _, out _, out var status, out var error));
        Assert.Equal(PhaseSetPlanner.PhaseNotFound, status);
        Assert.Contains("Olemasolev (1), Uus (2), Etapp 2 (3)", error);

        Assert.False(PhaseSetPlanner.TryParseTargets(null, "99", Phases, out _, out _, out status, out _));
        Assert.Equal(PhaseSetPlanner.PhaseNotFound, status);
    }

    [Fact]
    public void Request_NeedsSomethingToSet_AndCreatedCannotBeNone()
    {
        Assert.False(PhaseSetPlanner.TryParseTargets("", " ", Phases, out _, out _, out var status, out _));
        Assert.Equal("validation_failed", status);
        Assert.False(PhaseSetPlanner.TryParseTargets("none", null, Phases, out _, out _, out status, out _));
        Assert.Equal("validation_failed", status);
    }

    [Fact]
    public void Request_DemolishedBeforeCreated_IsPhaseOrderInvalid()
    {
        Assert.False(PhaseSetPlanner.TryParseTargets("Etapp 2", "Uus", Phases, out _, out _, out var status, out _));
        Assert.Equal(PhaseSetPlanner.StatusOrderInvalid, status);
        // The same phase is Revit's Temporary status: allowed.
        Assert.True(PhaseSetPlanner.TryParseTargets("Uus", "Uus", Phases, out _, out _, out _, out _));
    }

    [Fact]
    public void Demolish_ShowsBeforeAndAfter()
    {
        var (c, d) = Targets(null, "Uus");
        var row = PhaseSetPlanner.PlanOne(Element(1, Existing), c, d);
        Assert.Equal(PhaseSetPlanner.StatusReady, row.Status);
        Assert.Equal("Olemasolev", row.CreatedBefore);
        Assert.Equal("Olemasolev", row.CreatedAfter);
        Assert.Null(row.DemolishedBefore);
        Assert.Equal("Uus", row.DemolishedAfter);
        Assert.False(row.SetCreated);
        Assert.True(row.SetDemolished);
        Assert.Equal(11, row.NewDemolishedId);
    }

    [Fact]
    public void None_ClearsDemolished()
    {
        var (c, d) = Targets(null, "none");
        Assert.Equal(PhaseTargetKind.Clear, d.Kind);
        var row = PhaseSetPlanner.PlanOne(Element(1, Existing, New), c, d);
        Assert.Equal(PhaseSetPlanner.StatusReady, row.Status);
        Assert.True(row.SetDemolished);
        Assert.Null(row.NewDemolishedId);
        Assert.Equal("Uus", row.DemolishedBefore);
        Assert.Null(row.DemolishedAfter);

        var notDemolished = PhaseSetPlanner.PlanOne(Element(2, Existing), c, d);
        Assert.Equal(PhaseSetPlanner.StatusUnchanged, notDemolished.Status);
    }

    [Fact]
    public void PerElementOrder_IsChecked_AgainstTheCurrentOtherPhase()
    {
        // Created later than the element's current demolished phase.
        var (c, d) = Targets("Etapp 2", null);
        var row = PhaseSetPlanner.PlanOne(Element(1, Existing, New), c, d);
        Assert.Equal(PhaseSetPlanner.StatusOrderInvalid, row.Status);
        Assert.True(row.IsBlocking);

        // Demolished before the element's current created phase.
        var (c2, d2) = Targets(null, "Olemasolev");
        Assert.Equal(PhaseSetPlanner.StatusOrderInvalid, PhaseSetPlanner.PlanOne(Element(2, New), c2, d2).Status);
    }

    [Fact]
    public void MovingBothLater_WritesDemolishedFirst()
    {
        var (c, d) = Targets("Etapp 2", "Etapp 2");
        var row = PhaseSetPlanner.PlanOne(Element(1, Existing, New), c, d);
        Assert.Equal(PhaseSetPlanner.StatusReady, row.Status);
        Assert.True(row.DemolishedFirst);

        var (c2, d2) = Targets("Olemasolev", "Uus");
        Assert.False(PhaseSetPlanner.PlanOne(Element(2, New, Later), c2, d2).DemolishedFirst);
    }

    [Fact]
    public void ElementsWithoutPhaseParameters_AreSkipped_NotBlocking()
    {
        var (c, d) = Targets("Uus", null);
        var annotation = new PhaseElementState { Id = 5, Category = "Text Notes", HasPhaseParameters = false };
        var row = PhaseSetPlanner.PlanOne(annotation, c, d);
        Assert.Equal(PhaseSetPlanner.StatusSkipped, row.Status);
        Assert.False(row.IsBlocking);
        Assert.Contains("no phase parameters", row.Reason);
    }

    [Fact]
    public void ReadOnlyAndMissingElements_BlockAnAtomicRequest()
    {
        var (c, d) = Targets("Uus", null);
        var rows = PhaseSetPlanner.Plan(new[]
        {
            Element(1, Existing),
            Element(2, Existing, readOnly: true),
            new PhaseElementState { Id = 3, Exists = false },
            Element(4, New)
        }, c, d);

        Assert.Equal(new[] { PhaseSetPlanner.StatusReady, PhaseSetPlanner.StatusReadOnly, PhaseSetPlanner.StatusNotFound, PhaseSetPlanner.StatusUnchanged },
            rows.Select(r => r.Status));
        Assert.True(PhaseSetPlanner.ShouldReject(atomic: true, rows));
        Assert.False(PhaseSetPlanner.ShouldReject(atomic: false, rows));
    }

    [Fact]
    public void ReadOnly_OnlyMattersForAParameterThatChanges()
    {
        var (c, d) = Targets("Uus", null);
        var row = PhaseSetPlanner.PlanOne(Element(1, New, readOnly: true), c, d);
        Assert.Equal(PhaseSetPlanner.StatusUnchanged, row.Status);
    }

    [Fact]
    public void ReadOnlyReason_IsReported()
    {
        var (c, d) = Targets("Uus", null);
        var grouped = Element(1, Existing, readOnly: true);
        grouped.ReadOnlyReason = "The element is a member of group 77.";
        Assert.Equal("The element is a member of group 77.", PhaseSetPlanner.PlanOne(grouped, c, d).Reason);
    }
}
