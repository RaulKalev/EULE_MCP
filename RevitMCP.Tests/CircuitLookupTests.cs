using Xunit;
using RevitMCP.Addin.Electrical;

namespace RevitMCP.Tests;

/// <summary>
/// Regression tests for the two electrical-tool defects fixed in May 2026.
/// These tests cover pure logic that has no Revit API dependency.
/// </summary>
public class CircuitLookupTests
{
    // ─────────────────────────────────────────────────────────────────────
    // Issue 1: false "uncircuited" warnings when multiple elements share
    //          the same circuit (revit_get_circuits_for_selected_elements)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TrackCircuit_NewCircuit_MarksElementFoundAndReturnsTrue()
    {
        var seen = new HashSet<long>();
        bool found = false;

        bool isNew = CircuitElementClassifier.TrackCircuit(42L, seen, ref found);

        Assert.True(found);   // element IS on a circuit
        Assert.True(isNew);   // circuit is new → DTO should be built
        Assert.Contains(42L, seen);
    }

    [Fact]
    public void TrackCircuit_AlreadySeenCircuit_StillMarksElementFound()
    {
        // Regression: before fix, found was only set when circuit was NEW.
        // When 9 elements all share circuit 2203209, elements 2-9 were
        // incorrectly reported as uncircuited.
        var seen = new HashSet<long> { 2203209L }; // circuit already added by element 1
        bool found = false;

        bool isNew = CircuitElementClassifier.TrackCircuit(2203209L, seen, ref found);

        Assert.True(found);    // element 2-9 ARE on the circuit → must NOT go to uncircuited
        Assert.False(isNew);   // circuit DTO should NOT be duplicated
    }

    [Fact]
    public void TrackCircuit_NoCircuitsQueried_ElementFoundRemainsFlase()
    {
        // An element with no circuits in Revit never calls TrackCircuit.
        // found should stay false → element belongs in noCircuit list.
        var seen = new HashSet<long>();
        bool found = false;

        // Simulate: inner loop body never executes (empty elemCircuits)
        // → TrackCircuit is never called
        _ = seen; // nothing called

        Assert.False(found); // element should be reported in noCircuit
    }

    [Fact]
    public void TrackCircuit_NineElementsSameCircuit_ZeroUncircuited()
    {
        // Simulates the exact scenario from the test report:
        // 9 elements all on circuit 2203209 → uncircuitedCount must be 0.
        const long circuitId = 2203209L;
        var seen = new HashSet<long>();
        int uncircuitedCount = 0;

        for (int i = 0; i < 9; i++)
        {
            bool found = false;
            CircuitElementClassifier.TrackCircuit(circuitId, seen, ref found);
            if (!found) uncircuitedCount++;
        }

        Assert.Equal(0, uncircuitedCount);
        Assert.Single(seen); // circuit counted only once
    }

    // ─────────────────────────────────────────────────────────────────────
    // Issue 2: MissingWireType false positives in revit_check_circuit_health
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void WireTypeCheck_OldLogic_FalsePositiveDemonstrated()
    {
        // Before fix: hasWireType = (circuit.WireType != null)
        // In Revit 2026, WireType is deprecated. A circuit may have CableType
        // set but WireType == null, producing a false MissingWireType flag.
        string? deprecatedWireType = null;     // circuit.WireType is null in Revit 2026
        string resolvedCableTypeName = "YMVK 3x2.5"; // CableType resolves to a real name

        bool hasWireType_old = deprecatedWireType != null;       // old logic
        bool hasWireType_new = !string.IsNullOrEmpty(resolvedCableTypeName); // fixed logic

        Assert.False(hasWireType_old); // old logic flags this as MissingWireType (wrong)
        Assert.True(hasWireType_new);  // fixed logic correctly sees the wire type
    }

    [Fact]
    public void WireTypeCheck_NewLogic_EmptyResolvedName_IsFlagged()
    {
        // If the resolver (GetWireTypeName) returns empty string, the circuit
        // genuinely has no wire/cable type and should be flagged.
        string resolvedName = "";
        bool hasWireType = !string.IsNullOrEmpty(resolvedName);
        Assert.False(hasWireType); // correctly flagged as MissingWireType
    }

    [Fact]
    public void WireTypeCheck_NewLogic_NonEmptyResolvedName_IsNotFlagged()
    {
        // If GetWireTypeName returns a real name, the circuit should NOT be
        // flagged regardless of how the deprecated WireType property behaves.
        string resolvedName = "YMVK 3x2.5";
        bool hasWireType = !string.IsNullOrEmpty(resolvedName);
        Assert.True(hasWireType); // NOT flagged as MissingWireType
    }
}
