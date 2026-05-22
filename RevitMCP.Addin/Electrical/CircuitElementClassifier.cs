namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Pure-logic helper for per-element circuit deduplication.
/// No Revit API dependency — directly unit-testable.
/// </summary>
internal static class CircuitElementClassifier
{
    /// <summary>
    /// Registers a circuit observation for the current element.
    /// Always marks <paramref name="elementFound"/> = true (the element IS on this circuit,
    /// even if the circuit was already counted from another element).
    /// </summary>
    /// <param name="circuitId">The ElementId.Value of the ElectricalSystem.</param>
    /// <param name="seenCircuits">Set of circuit IDs already added to the response.</param>
    /// <param name="elementFound">Set to true; caller initialises to false before the element loop.</param>
    /// <returns>True if the circuit is new and its DTO should be appended to the response.</returns>
    public static bool TrackCircuit(long circuitId, HashSet<long> seenCircuits, ref bool elementFound)
    {
        elementFound = true;                  // element belongs to at least one circuit
        return seenCircuits.Add(circuitId);   // true  → new circuit, build DTO
                                              // false → already seen, skip DTO
    }
}
