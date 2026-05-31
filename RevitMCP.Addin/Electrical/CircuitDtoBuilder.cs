using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Builds serializable DTOs from ElectricalSystem elements for pipe responses.
/// </summary>
public static class CircuitDtoBuilder
{
    public static object Build(Document doc, ElectricalSystem circuit, bool includeElements, bool includeParameters)
    {
        var panel = TryGetPanel(circuit);
        string panelName = panel?.Name ?? "";
        long panelElementId = panel?.Id?.Value ?? 0;

        string wireTypeName = GetWireTypeName(doc, circuit);

        int elementCount = 0;
        List<object>? elements = null;
        // Only access circuit.Elements when needed — this property can hang the Revit UI thread
        // for circuits with large or complex element sets, causing all subsequent ExternalEvent
        // Raise() calls to return TimedOut. Skip it entirely when includeElements is false.
        if (includeElements)
        {
            try
            {
                var elemSet = circuit.Elements;
                elementCount = elemSet?.Size ?? 0;
                if (elemSet != null)
                {
                    elements = new List<object>();
                    foreach (Element e in elemSet)
                    {
                        var typeId = e.GetTypeId();
                        var typeElem = typeId != null && typeId != ElementId.InvalidElementId
                            ? doc.GetElement(typeId) : null;
                        elements.Add(new
                        {
                            elementId = e.Id.Value,
                            category = e.Category?.Name ?? "",
                            family = e is FamilyInstance fi2 ? fi2.Symbol?.Family?.Name ?? "" : "",
                            type = typeElem?.Name ?? e.Name
                        });
                    }
                }
            }
            catch { /* Elements may throw on certain system types */ }
        }

        List<object>? parameters = null;
        if (includeParameters)
            parameters = BuildParameters(circuit);

        double apparentLoad = RevitProp.TryRead(() => circuit.ApparentLoad);
        double voltage = RevitProp.TryRead(() => circuit.Voltage);
        int poles = RevitProp.TryRead(() => circuit.PolesNumber);

        return new
        {
            circuitId = circuit.Id.Value,
            uniqueId = circuit.UniqueId,
            circuitNumber = circuit.CircuitNumber ?? "",
            circuitName = circuit.Name ?? "",
            panelName,
            panelElementId,
            systemType = circuit.SystemType.ToString(),
            apparentLoad = $"{apparentLoad:F0} VA",
            voltage = $"{voltage:F0} V",
            poles,
            wireType = wireTypeName,
            cableType = wireTypeName,
            elementCount,
            elements,
            parameters
        };
    }

    public static FamilyInstance? TryGetPanel(ElectricalSystem circuit)
    {
        try { return circuit.BaseEquipment; }
        catch { return null; }
    }

    public static string GetWireTypeName(Document doc, ElectricalSystem circuit)
    {
        try
        {
#if !REVIT2024
            // CableType is the Revit 2025+ API; WireType is deprecated but kept as fallback
            var cableTypeId = circuit.CableType;
            if (cableTypeId != null && cableTypeId != ElementId.InvalidElementId)
                return doc.GetElement(cableTypeId)?.Name ?? "";
#endif
        }
        catch { }
#pragma warning disable CS0618
        try { return circuit.WireType?.Name ?? ""; } catch { }
#pragma warning restore CS0618
        return "";
    }

    private static List<object> BuildParameters(ElectricalSystem circuit)
    {
        var list = new List<object>();
        foreach (Parameter p in circuit.Parameters)
        {
            if (!p.HasValue) continue;
            list.Add(new
            {
                name = p.Definition?.Name ?? "",
                value = GetParamString(p)
            });
        }
        return list;
    }

    public static string GetParamString(Parameter p) =>
        p.StorageType switch
        {
            StorageType.String => p.AsString() ?? "",
            StorageType.Integer => p.AsInteger().ToString(),
            StorageType.Double => p.AsValueString() ?? p.AsDouble().ToString("F4"),
            StorageType.ElementId => p.AsElementId()?.Value.ToString() ?? "",
            _ => ""
        };
}
