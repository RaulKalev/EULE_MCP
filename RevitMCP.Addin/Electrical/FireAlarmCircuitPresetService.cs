using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using RevitMCP.Addin.Query;

namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Collects and organises Fire Alarm Device data grouped by loop parameter
/// (default: "Ahela nr.") for the fire alarm circuit preset tools.
/// Must be called on the Revit API thread inside an ExternalEvent.
/// </summary>
public static class FireAlarmCircuitPresetService
{
    // ── Public result types ───────────────────────────────────────────────────

    public sealed class PresetResult
    {
        public string PanelName { get; set; } = "";
        public int DeviceCount { get; set; }
        public int LoopCount { get; set; }
        public List<LoopData> Loops { get; set; } = [];
        public List<string> Warnings { get; set; } = [];
    }

    public sealed class LoopData
    {
        public string LoopName { get; set; } = "";
        public string LoopKind { get; set; } = "Unknown";
        public int DeviceCount { get; set; }
        public List<LevelSummary> Levels { get; set; } = [];
        public List<TypeSummary> DeviceTypeSummary { get; set; } = [];
        public CircuitInfo? CircuitInfo { get; set; }
        public List<DeviceEntry> Devices { get; set; } = [];
    }

    public sealed class LevelSummary { public string LevelName { get; set; } = ""; public int DeviceCount { get; set; } }
    public sealed class TypeSummary  { public string DeviceType { get; set; } = ""; public int Count { get; set; } }

    public sealed class CircuitInfo
    {
        public long RevitCircuitId { get; set; }
        public string RevitCircuitNumber { get; set; } = "";
        public string PanelName { get; set; } = "";
        public string CableType { get; set; } = "";
        public string WireType { get; set; } = "";
        public double? CircuitLengthMeters { get; set; }
    }

    public sealed class DeviceEntry
    {
        public long ElementId { get; set; }
        public string Level { get; set; } = "";
        public string DeviceNumber { get; set; } = "";
        public string LoopNumber { get; set; } = "";
        public string DeviceType { get; set; } = "";
        public string Description { get; set; } = "";
        public long? RevitCircuitId { get; set; }
    }

    // ── Main entry point ──────────────────────────────────────────────────────

    public static PresetResult Run(
        Document doc,
        string panelNameFilter,
        string loopParameterName,
        string deviceNumberParameterName,
        string deviceTypeParameterName,
        string descriptionParameterName,
        bool includeDeviceList,
        bool includeCircuitInfo,
        bool includeVoltageDrop,
        bool allowDeviceNumberXxx,
        string[] sortBy,
        int limit,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        // ── Step 1: collect fire alarm devices ────────────────────────────────
        var devices = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_FireAlarmDevices)
            .WhereElementIsNotElementType()
            .Cast<Element>()
            .Take(limit)
            .ToList();

        if (devices.Count == 0)
            warnings.Add("No Fire Alarm Devices found in the active document.");

        // ── Step 2: build element → circuit map ───────────────────────────────
        var elementToCircuit = new Dictionary<long, ElectricalSystem>();
        if (includeCircuitInfo)
        {
            var allCircuits = CircuitQueryService.GetAll(doc);
            // Filter to panel name if provided
            if (!string.IsNullOrWhiteSpace(panelNameFilter))
                allCircuits = CircuitQueryService.Filter(allCircuits, panelNameFilter, null, null);

            foreach (var circuit in allCircuits)
            {
                try
                {
                    if (circuit.Elements == null) continue;
                    foreach (Element e in circuit.Elements)
                        elementToCircuit.TryAdd(e.Id.Value, circuit);
                }
                catch { }
            }
        }

        // ── Step 3: read device data ──────────────────────────────────────────
        bool xxxWarningAdded = false;
        var deviceRows = new List<(string Loop, string DeviceNum, string Level, string DeviceType, string Desc, long ElemId, long? CircuitId)>();

        foreach (var e in devices)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var typeElem = GetTypeElement(doc, e);
            string loopNum   = ReadParam(e, typeElem, loopParameterName);
            string devNum    = ReadParam(e, typeElem, deviceNumberParameterName);
            string devType   = ReadParam(e, typeElem, deviceTypeParameterName);
            string desc      = ReadParam(e, typeElem, descriptionParameterName);
            string levelName = GetLevelName(doc, e);

            if (string.IsNullOrWhiteSpace(loopNum)) loopNum = "(No Loop)";

            if (!allowDeviceNumberXxx && devNum.Equals("XXX", StringComparison.OrdinalIgnoreCase))
                continue; // skip unassigned if not allowed

            if (allowDeviceNumberXxx && devNum.Equals("XXX", StringComparison.OrdinalIgnoreCase) && !xxxWarningAdded)
            {
                warnings.Add("Some devices have Seadme Nr. = XXX. This is accepted for the current project phase.");
                xxxWarningAdded = true;
            }

            long? circuitId = null;
            if (includeCircuitInfo && elementToCircuit.TryGetValue(e.Id.Value, out var cir))
                circuitId = cir.Id.Value;

            deviceRows.Add((loopNum, devNum, levelName, devType, desc, e.Id.Value, circuitId));
        }

        // ── Step 4: group by loop ─────────────────────────────────────────────
        var loops = new List<LoopData>();
        var byLoop = deviceRows.GroupBy(r => r.Loop).OrderBy(g => g.Key);

        foreach (var loopGroup in byLoop)
        {
            var loopName = loopGroup.Key;
            var loopDevices = loopGroup.ToList();
            var deviceTypes = loopDevices.Select(d => d.DeviceType).ToList();
            var loopKind = ClassifyLoop(loopName, deviceTypes);

            // Level summary
            var levelSummary = loopDevices
                .GroupBy(d => d.Level)
                .OrderBy(g => g.Key)
                .Select(g => new LevelSummary { LevelName = g.Key, DeviceCount = g.Count() })
                .ToList();

            // Type summary
            var typeSummary = loopDevices
                .Where(d => !string.IsNullOrWhiteSpace(d.DeviceType))
                .GroupBy(d => d.DeviceType)
                .OrderByDescending(g => g.Count())
                .Select(g => new TypeSummary { DeviceType = g.Key, Count = g.Count() })
                .ToList();

            // Circuit info (one circuit per loop — use first found)
            CircuitInfo? circInfo = null;
            if (includeCircuitInfo)
            {
                var circuitId = loopDevices.Select(d => d.CircuitId).FirstOrDefault(id => id.HasValue);
                if (circuitId.HasValue)
                {
                    var cir = elementToCircuit.Values.FirstOrDefault(c => c.Id.Value == circuitId.Value);
                    if (cir != null)
                    {
                        var cPanel = CircuitDtoBuilder.TryGetPanel(cir);
                        var wireType = CircuitDtoBuilder.GetWireTypeName(doc, cir);

                        double? lengthMeters = null;
                        try
                        {
                            var lenParam = cir.LookupParameter("Length");
                            if (lenParam?.HasValue == true)
                            {
                                double lenFt = lenParam.AsDouble();
                                if (lenFt > 0)
                                    lengthMeters = LengthUnitHelper.RoundMeters(LengthUnitHelper.ToMeters(lenFt));
                            }
                        }
                        catch { }

                        circInfo = new CircuitInfo
                        {
                            RevitCircuitId = cir.Id.Value,
                            RevitCircuitNumber = cir.CircuitNumber ?? "",
                            PanelName = cPanel?.Name ?? "",
                            CableType = wireType,
                            WireType = wireType,
                            CircuitLengthMeters = lengthMeters
                        };
                    }
                }
            }

            // Device list
            var deviceEntries = includeDeviceList
                ? loopDevices
                    .OrderBy(d => d.Level)
                    .ThenBy(d => d.DeviceNum)
                    .Select(d => new DeviceEntry
                    {
                        ElementId = d.ElemId,
                        Level = d.Level,
                        DeviceNumber = d.DeviceNum,
                        LoopNumber = d.Loop,
                        DeviceType = d.DeviceType,
                        Description = d.Desc,
                        RevitCircuitId = d.CircuitId
                    })
                    .ToList()
                : [];

            loops.Add(new LoopData
            {
                LoopName = loopName,
                LoopKind = loopKind,
                DeviceCount = loopDevices.Count,
                Levels = levelSummary,
                DeviceTypeSummary = typeSummary,
                CircuitInfo = circInfo,
                Devices = deviceEntries
            });
        }

        return new PresetResult
        {
            PanelName = panelNameFilter,
            DeviceCount = deviceRows.Count,
            LoopCount = loops.Count,
            Loops = loops,
            Warnings = warnings
        };
    }

    // ── Loop classification ───────────────────────────────────────────────────

    /// <summary>Delegates to <see cref="FireAlarmLoopClassifier.Classify"/>.</summary>
    public static string ClassifyLoop(string loopName, IEnumerable<string> deviceTypes)
        => FireAlarmLoopClassifier.Classify(loopName, deviceTypes);

    // ── Private helpers ───────────────────────────────────────────────────────

    private static Element? GetTypeElement(Document doc, Element element)
    {
        try
        {
            var typeId = element.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId)
                return doc.GetElement(typeId);
        }
        catch { }
        return null;
    }

    private static string GetLevelName(Document doc, Element element)
    {
        try
        {
            var levelId = element.LevelId;
            if (levelId != null && levelId != ElementId.InvalidElementId)
                return (doc.GetElement(levelId) as Level)?.Name ?? "";
        }
        catch { }
        return "";
    }

    /// <summary>
    /// Reads a parameter value from instance parameters first, then type parameters.
    /// Uses ContainsNormalized matching so "ELENEA_Nimetus" matches "ELENEA_ÜLD 001_Nimetus".
    /// </summary>
    private static string ReadParam(Element instance, Element? typeElem, string paramName)
    {
        if (string.IsNullOrWhiteSpace(paramName)) return "";

        // Instance parameters
        try
        {
            foreach (Parameter p in instance.Parameters)
            {
                try
                {
                    if (!ParameterMatcher.Matches(p.Definition.Name, paramName, "ContainsNormalized"))
                        continue;
                    if (!p.HasValue) continue;
                    return p.StorageType switch
                    {
                        Autodesk.Revit.DB.StorageType.String  => p.AsString() ?? "",
                        Autodesk.Revit.DB.StorageType.Integer => p.AsInteger().ToString(),
                        Autodesk.Revit.DB.StorageType.Double  => p.AsValueString() ?? p.AsDouble().ToString("F2"),
                        _ => p.AsValueString() ?? ""
                    };
                }
                catch { }
            }
        }
        catch { }

        // Type parameters
        if (typeElem != null)
        {
            try
            {
                foreach (Parameter p in typeElem.Parameters)
                {
                    try
                    {
                        if (!ParameterMatcher.Matches(p.Definition.Name, paramName, "ContainsNormalized"))
                            continue;
                        if (!p.HasValue) continue;
                        return p.StorageType switch
                        {
                            Autodesk.Revit.DB.StorageType.String  => p.AsString() ?? "",
                            Autodesk.Revit.DB.StorageType.Integer => p.AsInteger().ToString(),
                            Autodesk.Revit.DB.StorageType.Double  => p.AsValueString() ?? p.AsDouble().ToString("F2"),
                            _ => p.AsValueString() ?? ""
                        };
                    }
                    catch { }
                }
            }
            catch { }
        }

        return "";
    }
}
