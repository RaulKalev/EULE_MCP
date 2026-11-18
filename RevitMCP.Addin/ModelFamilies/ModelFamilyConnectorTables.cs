namespace RevitMCP.Addin.ModelFamilies;

/// <summary>The MEP domains a family connector can belong to (#174), as the wire names them.</summary>
public enum ConnectorDomain
{
    Electrical,
    Duct,
    Pipe,
    Conduit,
    CableTray
}

/// <summary>A system type a connector domain accepts: the wire name, the creation enum member and the classification.</summary>
public sealed class ConnectorSystemType
{
    public ConnectorSystemType(string wireName, string createMember, string classification)
    {
        WireName = wireName;
        CreateMember = createMember;
        Classification = classification;
    }

    /// <summary>The name callers pass, for example <c>powerBalanced</c>.</summary>
    public string WireName { get; }
    /// <summary>The member of ElectricalSystemType / DuctSystemType / PipeSystemType that creates the connector.</summary>
    public string CreateMember { get; }
    /// <summary>The member of MEPSystemClassification the connector reports (and that an edit sets).</summary>
    public string Classification { get; }
}

/// <summary>
/// The fixed tables of the connector feature (#174). Pure: the Revit side turns the member names here into its enums,
/// so the tables can be tested without Revit. Member names were checked against the Revit 2024 and 2026 API (both
/// have the same members).
/// </summary>
public static class ModelFamilyConnectorTables
{
    public static readonly IReadOnlyDictionary<string, ConnectorDomain> Domains =
        new Dictionary<string, ConnectorDomain>(StringComparer.OrdinalIgnoreCase)
        {
            ["electrical"] = ConnectorDomain.Electrical,
            ["duct"] = ConnectorDomain.Duct,
            ["pipe"] = ConnectorDomain.Pipe,
            ["conduit"] = ConnectorDomain.Conduit,
            ["cableTray"] = ConnectorDomain.CableTray
        };

    /// <summary>"an electrical connector", "a duct connector": for messages.</summary>
    public static string AConnector(ConnectorDomain domain) => $"{(domain == ConnectorDomain.Electrical ? "an" : "a")} {WireName(domain)} connector";

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

    public static string WireName(ConnectorDomain domain) => domain switch
    {
        ConnectorDomain.Electrical => "electrical",
        ConnectorDomain.Duct => "duct",
        ConnectorDomain.Pipe => "pipe",
        ConnectorDomain.Conduit => "conduit",
        _ => "cableTray"
    };

    private static readonly ConnectorSystemType[] Electrical =
    {
        new("powerBalanced", "PowerBalanced", "PowerBalanced"),
        new("powerUnBalanced", "PowerUnBalanced", "PowerUnBalanced"),
        new("data", "Data", "DataCircuit"),
        new("telephone", "Telephone", "Telephone"),
        new("security", "Security", "Security"),
        new("fireAlarm", "FireAlarm", "FireAlarm"),
        new("nurseCall", "NurseCall", "NurseCall"),
        new("controls", "Controls", "Controls"),
        new("communication", "Communication", "Communication")
    };

    private static readonly ConnectorSystemType[] Duct =
    {
        new("supplyAir", "SupplyAir", "SupplyAir"),
        new("returnAir", "ReturnAir", "ReturnAir"),
        new("exhaustAir", "ExhaustAir", "ExhaustAir"),
        new("otherAir", "OtherAir", "OtherAir"),
        new("fitting", "Fitting", "Fitting"),
        new("global", "Global", "Global")
    };

    private static readonly ConnectorSystemType[] Pipe =
    {
        new("supplyHydronic", "SupplyHydronic", "SupplyHydronic"),
        new("returnHydronic", "ReturnHydronic", "ReturnHydronic"),
        new("sanitary", "Sanitary", "Sanitary"),
        new("domesticHotWater", "DomesticHotWater", "DomesticHotWater"),
        new("domesticColdWater", "DomesticColdWater", "DomesticColdWater"),
        new("fireProtectWet", "FireProtectWet", "FireProtectWet"),
        new("fireProtectDry", "FireProtectDry", "FireProtectDry"),
        new("fireProtectPreaction", "FireProtectPreaction", "FireProtectPreaction"),
        new("fireProtectOther", "FireProtectOther", "FireProtectOther"),
        new("vent", "Vent", "Vent"),
        new("otherPipe", "OtherPipe", "OtherPipe"),
        new("fitting", "Fitting", "Fitting"),
        new("global", "Global", "Global")
    };

    /// <summary>The system types a domain accepts; empty for conduit and cable tray, which take none.</summary>
    public static IReadOnlyList<ConnectorSystemType> SystemTypes(ConnectorDomain domain) => domain switch
    {
        ConnectorDomain.Electrical => Electrical,
        ConnectorDomain.Duct => Duct,
        ConnectorDomain.Pipe => Pipe,
        _ => Array.Empty<ConnectorSystemType>()
    };

    public static ConnectorSystemType? SystemType(ConnectorDomain domain, string wireName) =>
        SystemTypes(domain).FirstOrDefault(t => string.Equals(t.WireName, wireName.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The wire name of a connector's MEPSystemClassification member (<c>DataCircuit</c> â†’ <c>data</c>); other
    /// classifications come back camel-cased (<c>CableTrayConduit</c> â†’ <c>cableTrayConduit</c>).
    /// </summary>
    public static string ClassificationWireName(string classification)
    {
        foreach (var table in new[] { Electrical, Duct, Pipe })
            if (table.FirstOrDefault(t => string.Equals(t.Classification, classification, StringComparison.Ordinal)) is { } found)
                return found.WireName;
        return classification.Length == 0 ? classification : char.ToLowerInvariant(classification[0]) + classification.Substring(1);
    }

    /// <summary>
    /// The wire domain of a connector read back from Revit: its Domain member (<c>DomainHvac</c>â€¦) and, for the shared
    /// cable tray/conduit domain, its shape (conduit is round, cable tray rectangular).
    /// </summary>
    public static string DomainWireName(string revitDomain, string shape) => revitDomain switch
    {
        "DomainElectrical" => "electrical",
        "DomainHvac" => "duct",
        "DomainPiping" => "pipe",
        "DomainCableTrayConduit" => string.Equals(shape, "Round", StringComparison.OrdinalIgnoreCase) ? "conduit" : "cableTray",
        _ => revitDomain
    };

    // â”€â”€ Shapes and sizes â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>The shapes a domain allows (ConnectorProfileType member names); electrical connectors have none.</summary>
    public static IReadOnlyList<string> Shapes(ConnectorDomain domain) => domain switch
    {
        ConnectorDomain.Duct => new[] { "Round", "Rectangular", "Oval" },
        ConnectorDomain.Pipe or ConnectorDomain.Conduit => new[] { "Round" },
        ConnectorDomain.CableTray => new[] { "Rectangular" },
        _ => Array.Empty<string>()
    };

    /// <summary>A wire shape name (<c>round</c>, <c>rectangular</c>, <c>oval</c>) as its ConnectorProfileType member, or null.</summary>
    public static string? ParseShape(string text) => text.Trim().ToLowerInvariant() switch
    {
        "round" => "Round",
        "rectangular" => "Rectangular",
        "oval" => "Oval",
        _ => null
    };

    /// <summary>
    /// Checks a connector's shape and size arguments for its domain and returns the shape to use (null for
    /// electrical). Duct takes <c>shape</c>, or infers round from <c>diameter</c> and rectangular from
    /// <c>width</c>+<c>height</c>; pipe and conduit are round (<c>diameter</c>); cable tray is rectangular
    /// (<c>width</c>, <c>height</c>). <paramref name="currentShape"/> is the shape of an existing connector (edit).
    /// </summary>
    public static string? CheckShapeAndSize(ConnectorDomain domain, string? shape, double? diameter, double? width, double? height,
        string source, List<string> problems, string? currentShape = null)
    {
        var allowed = Shapes(domain);
        var hasSize = diameter != null || width != null || height != null;
        if (allowed.Count == 0)
        {
            if (shape != null || hasSize)
                problems.Add($"{source}: an electrical connector has no shape or size; remove shape, diameter, width and height.");
            return null;
        }
        foreach (var (name, value) in new[] { ("diameter", diameter), ("width", width), ("height", height) })
            if (value != null && value <= 0) problems.Add($"{source}.{name}: must be greater than 0 mm.");

        var resolved = shape;
        if (resolved == null && domain == ConnectorDomain.Duct && currentShape == null)
            resolved = diameter != null ? "Round" : width != null || height != null ? "Rectangular" : null;
        resolved ??= currentShape ?? (allowed.Count == 1 ? allowed[0] : null);
        if (resolved == null)
        {
            problems.Add($"{source}.shape: a duct connector needs shape round, rectangular or oval (or diameter, or width and height).");
            return null;
        }
        if (!allowed.Contains(resolved, StringComparer.Ordinal))
        {
            problems.Add($"{source}.shape: {AConnector(domain)} is {string.Join(" or ", allowed.Select(s => s.ToLowerInvariant()))}, not {resolved.ToLowerInvariant()}.");
            return null;
        }
        if (resolved == "Round" && (width != null || height != null))
            problems.Add($"{source}: a round connector takes diameter, not width or height.");
        if (resolved != "Round" && diameter != null)
            problems.Add($"{source}: a {resolved.ToLowerInvariant()} connector takes width and height, not diameter.");
        return resolved;
    }

    // â”€â”€ Host faces â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>The face labels of a host selector and the outward normal each stands for (family coordinates: front is âˆ’Y).</summary>
    public static readonly IReadOnlyDictionary<string, double[]> FaceLabels =
        new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["left"] = new[] { -1.0, 0, 0 },
            ["right"] = new[] { 1.0, 0, 0 },
            ["front"] = new[] { 0, -1.0, 0 },
            ["back"] = new[] { 0, 1.0, 0 },
            ["top"] = new[] { 0, 0, 1.0 },
            ["bottom"] = new[] { 0, 0, -1.0 }
        };

    /// <summary>The largest angle, in degrees, between a face's normal and the requested one.</summary>
    public const double FaceToleranceDegrees = 1.0;

    public static double AngleDegrees(double[] a, double[] b)
    {
        var la = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
        var lb = Math.Sqrt(b[0] * b[0] + b[1] * b[1] + b[2] * b[2]);
        if (la < 1e-12 || lb < 1e-12) return 180;
        var cos = (a[0] * b[0] + a[1] * b[1] + a[2] * b[2]) / (la * lb);
        return Math.Acos(Math.Max(-1, Math.Min(1, cos))) * 180 / Math.PI;
    }

    /// <summary>The label of a normal within <see cref="FaceToleranceDegrees"/>, or null for any other direction.</summary>
    public static string? LabelOf(double[] normal) =>
        FaceLabels.Where(l => AngleDegrees(l.Value, normal) <= FaceToleranceDegrees).Select(l => l.Key).FirstOrDefault();

    /// <summary>
    /// Picks the face for a host selector among an element's planar faces: those whose outward normal is within
    /// <see cref="FaceToleranceDegrees"/> of <paramref name="wanted"/>, and of those the outermost one (largest
    /// offset of its origin along the normal, for example the rear face of a box rather than a recess). Returns the
    /// index into <paramref name="faces"/> and how many faces matched; -1 when none did.
    /// </summary>
    public static (int Index, int Matches) PickFace(IReadOnlyList<(double[] Normal, double[] Origin)> faces, double[] wanted)
    {
        var best = -1;
        var bestOffset = double.NegativeInfinity;
        var matches = 0;
        var length = Math.Sqrt(wanted[0] * wanted[0] + wanted[1] * wanted[1] + wanted[2] * wanted[2]);
        for (var i = 0; i < faces.Count; i++)
        {
            if (AngleDegrees(faces[i].Normal, wanted) > FaceToleranceDegrees) continue;
            matches++;
            var offset = (faces[i].Origin[0] * wanted[0] + faces[i].Origin[1] * wanted[1] + faces[i].Origin[2] * wanted[2]) / length;
            if (offset > bestOffset + 1e-9)
            {
                best = i;
                bestOffset = offset;
            }
        }
        return (best, matches);
    }

    /// <summary>
    /// Projects a requested position onto the plane of a connector (through <paramref name="origin"/>, normal
    /// <paramref name="normal"/>); returns the projected point and how far off the plane the request was. Any units.
    /// </summary>
    public static (double[] Projected, double Distance) ProjectOntoPlane(double[] point, double[] origin, double[] normal)
    {
        var length = Math.Sqrt(normal[0] * normal[0] + normal[1] * normal[1] + normal[2] * normal[2]);
        var n = new[] { normal[0] / length, normal[1] / length, normal[2] / length };
        var d = (point[0] - origin[0]) * n[0] + (point[1] - origin[1]) * n[1] + (point[2] - origin[2]) * n[2];
        return (new[] { point[0] - d * n[0], point[1] - d * n[1], point[2] - d * n[2] }, Math.Abs(d));
    }

    // â”€â”€ Category and domain â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>The domains each MEP family category normally carries (BuiltInCategory names).</summary>
    public static readonly IReadOnlyDictionary<string, ConnectorDomain[]> CategoryDomains =
        new Dictionary<string, ConnectorDomain[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["OST_ElectricalFixtures"] = new[] { ConnectorDomain.Electrical, ConnectorDomain.Conduit },
            ["OST_ElectricalEquipment"] = new[] { ConnectorDomain.Electrical, ConnectorDomain.Conduit, ConnectorDomain.CableTray },
            ["OST_LightingFixtures"] = new[] { ConnectorDomain.Electrical, ConnectorDomain.Conduit },
            ["OST_LightingDevices"] = new[] { ConnectorDomain.Electrical, ConnectorDomain.Conduit },
            ["OST_CommunicationDevices"] = new[] { ConnectorDomain.Electrical, ConnectorDomain.Conduit },
            ["OST_DataDevices"] = new[] { ConnectorDomain.Electrical, ConnectorDomain.Conduit },
            ["OST_FireAlarmDevices"] = new[] { ConnectorDomain.Electrical, ConnectorDomain.Conduit },
            ["OST_NurseCallDevices"] = new[] { ConnectorDomain.Electrical, ConnectorDomain.Conduit },
            ["OST_SecurityDevices"] = new[] { ConnectorDomain.Electrical, ConnectorDomain.Conduit },
            ["OST_TelephoneDevices"] = new[] { ConnectorDomain.Electrical, ConnectorDomain.Conduit },
            ["OST_AudioVisualDevices"] = new[] { ConnectorDomain.Electrical, ConnectorDomain.Conduit },
            ["OST_MechanicalControlDevices"] = new[] { ConnectorDomain.Electrical, ConnectorDomain.Conduit },
            ["OST_MechanicalEquipment"] = new[] { ConnectorDomain.Duct, ConnectorDomain.Pipe, ConnectorDomain.Electrical, ConnectorDomain.Conduit },
            ["OST_PlumbingEquipment"] = new[] { ConnectorDomain.Pipe, ConnectorDomain.Electrical },
            ["OST_DuctTerminal"] = new[] { ConnectorDomain.Duct },
            ["OST_DuctFitting"] = new[] { ConnectorDomain.Duct },
            ["OST_DuctAccessory"] = new[] { ConnectorDomain.Duct, ConnectorDomain.Electrical },
            ["OST_PipeFitting"] = new[] { ConnectorDomain.Pipe },
            ["OST_PipeAccessory"] = new[] { ConnectorDomain.Pipe, ConnectorDomain.Electrical },
            ["OST_PlumbingFixtures"] = new[] { ConnectorDomain.Pipe, ConnectorDomain.Electrical },
            ["OST_Sprinklers"] = new[] { ConnectorDomain.Pipe },
            ["OST_ConduitFitting"] = new[] { ConnectorDomain.Conduit },
            ["OST_CableTrayFitting"] = new[] { ConnectorDomain.CableTray },
            ["OST_SpecialityEquipment"] = new[] { ConnectorDomain.Electrical, ConnectorDomain.Duct, ConnectorDomain.Pipe, ConnectorDomain.Conduit, ConnectorDomain.CableTray }
        };

    /// <summary>
    /// A warning when the family's category does not normally carry <paramref name="domain"/> (a duct connector on an
    /// Electrical Fixture), or is not an MEP category at all (Generic Model); null when it fits.
    /// </summary>
    public static string? CategoryWarning(string builtInCategory, string categoryName, ConnectorDomain domain)
    {
        const string partType = " For MEP categories the family's Part Type (settings.partType, model-family-documents.md) also decides how it behaves in systems.";
        if (!CategoryDomains.TryGetValue(builtInCategory, out var domains))
            return $"The family category '{categoryName}' is not an MEP category: {AConnector(domain)} is created, but placed instances may not join {WireName(domain)} systems or circuits. Set an MEP category first (settings.category, model-family-documents.md).{partType}";
        if (!domains.Contains(domain))
            return $"{Capital(AConnector(domain))} is unusual in a '{categoryName}' family (it normally carries {string.Join(", ", domains.Select(WireName))}); check the category.{partType}";
        return null;
    }

    // â”€â”€ Enumerated connector parameters â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Integer connector parameters whose values are an enum, with the values a caller may name: BuiltInParameter â†’
    /// (Revit enum, wire value â†’ member). The Revit side parses the member into that enum.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Enum, IReadOnlyDictionary<string, string> Values)> EnumParameters =
        new Dictionary<string, (string, IReadOnlyDictionary<string, string>)>(StringComparer.OrdinalIgnoreCase)
        {
            ["RBS_DUCT_FLOW_DIRECTION_PARAM"] = ("FlowDirectionType", FlowDirection),
            ["RBS_PIPE_FLOW_DIRECTION_PARAM"] = ("FlowDirectionType", FlowDirection),
            ["RBS_DUCT_FLOW_CONFIGURATION_PARAM"] = ("DuctFlowConfigurationType", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["calculated"] = "Calculated", ["preset"] = "Preset", ["system"] = "System"
            }),
            ["RBS_PIPE_FLOW_CONFIGURATION_PARAM"] = ("PipeFlowConfigurationType", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["calculated"] = "Calculated", ["preset"] = "Preset", ["system"] = "System", ["demand"] = "Demand"
            })
        };

    private static IReadOnlyDictionary<string, string> FlowDirection => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["in"] = "In", ["out"] = "Out", ["bidirectional"] = "Bidirectional"
    };
}
