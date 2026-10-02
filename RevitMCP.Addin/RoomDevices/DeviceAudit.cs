using Autodesk.Revit.DB;
using RevitMCP.Addin.Tools;

namespace RevitMCP.Addin.RoomDevices;

/// <summary>A placed device (host model family instance), with its code when it has one.</summary>
internal sealed class DeviceRecord
{
    public FamilyInstance Element { get; set; } = null!;
    public long Id => Element.Id.Value;
    public DeviceCode? Code { get; set; }
    public P2 Point { get; set; }
    public double ZMm { get; set; }
    public P2? Facing { get; set; }
    public double? FacingDeg => Facing is { } f ? RoomGeometryMath.AngleDeg(f) : null;
    public Level? Level { get; set; }
    public double LevelElevationMm => Level != null ? RoomUnits.FtToMm(Level.ProjectElevation) : 0;
    public int RoomIndex { get; set; } = -1;

    public object PointPayload() => new { x = Math.Round(Point.X, 1), y = Math.Round(Point.Y, 1), z = Math.Round(ZMm, 1) };
}

/// <summary>All rooms of a source, indexed for point lookup.</summary>
internal sealed class RoomIndex
{
    public RoomSourceService Service { get; set; } = null!;
    public List<RoomRecord> Rooms { get; set; } = [];
    public List<RoomCandidate> Candidates { get; set; } = [];
    private readonly Dictionary<int, double?> _ceilings = [];

    public RoomRecord? Locate(DeviceRecord device)
    {
        device.RoomIndex = RoomAuditMath.LocateRoom(Candidates, device.Point, device.ZMm);
        return device.RoomIndex >= 0 ? Rooms[device.RoomIndex] : null;
    }

    public double? CeilingHeightMm(int roomIndex) =>
        _ceilings.TryGetValue(roomIndex, out var c) ? c : _ceilings[roomIndex] = Service.CeilingHeightMm(Rooms[roomIndex]);

    public static RoomIndex Build(RoomSourceService service, RoomFilter filter, List<string> warnings)
    {
        var rooms = service.LoadRooms(filter, warnings, limit: 5000);
        return new RoomIndex
        {
            Service = service,
            Rooms = rooms,
            Candidates = rooms.Select(r => new RoomCandidate
            {
                Number = r.Number,
                Name = r.Name,
                AreaM2 = r.AreaM2,
                Polygon = r.Polygon,
                FloorZMm = r.FloorZMm,
                HeightMm = r.HeightMm
            }).ToList()
        };
    }
}

/// <summary>Collects placed devices for the audit and adjust tools.</summary>
internal static class DeviceAudit
{
    /// <summary>
    /// Devices of the device code map (optionally limited to <paramref name="codes"/>), or — when
    /// <paramref name="elementIds"/> is given — exactly those elements, matched to a code when their
    /// type is one.
    /// </summary>
    public static List<DeviceRecord> Inventory(
        Document doc,
        DeviceCodeContext? codes,
        IReadOnlyCollection<string> onlyCodes,
        IReadOnlyCollection<long> elementIds,
        List<string> warnings)
    {
        var bySymbol = new Dictionary<long, DeviceCode>();
        if (codes != null)
        {
            foreach (var code in codes.Codes.Values)
            {
                if (onlyCodes.Count > 0 && !onlyCodes.Contains(code.Code, StringComparer.OrdinalIgnoreCase)) continue;
                var symbol = DeviceCodeContext.FindSymbol(doc, code.Family, code.Type);
                if (symbol == null)
                    warnings.Add($"Code '{code.Code}': type '{code.Family} : {code.Type}' is not loaded — no devices to check.");
                else
                    bySymbol[symbol.Id.Value] = code;
            }
            foreach (var missing in onlyCodes.Where(c => !codes.Codes.ContainsKey(c)))
                warnings.Add($"Unknown device code '{missing}'.");
        }

        IEnumerable<FamilyInstance> instances;
        if (elementIds.Count > 0)
        {
            var list = new List<FamilyInstance>();
            foreach (var id in elementIds.Distinct())
            {
                if (doc.GetElement(new ElementId(id)) is FamilyInstance fi) list.Add(fi);
                else warnings.Add($"Element {id} is not a family instance — skipped.");
            }
            instances = list;
        }
        else
        {
            instances = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(i => i.Symbol != null && bySymbol.ContainsKey(i.Symbol.Id.Value));
        }

        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => l.ProjectElevation).ToList();

        var result = new List<DeviceRecord>();
        foreach (var instance in instances)
        {
            if (instance.Location is not LocationPoint lp)
            {
                if (elementIds.Count > 0) warnings.Add($"Element {instance.Id.Value} has no insertion point — skipped.");
                continue;
            }

            var p = lp.Point;
            var record = new DeviceRecord
            {
                Element = instance,
                Code = instance.Symbol != null && bySymbol.TryGetValue(instance.Symbol.Id.Value, out var c) ? c : null,
                Point = new P2(RoomUnits.FtToMm(p.X), RoomUnits.FtToMm(p.Y)),
                ZMm = RoomUnits.FtToMm(p.Z),
                Facing = FacingOf(instance, lp),
                Level = doc.GetElement(instance.LevelId) as Level
                        ?? levels.LastOrDefault(l => l.ProjectElevation <= p.Z + 0.5)
                        ?? levels.FirstOrDefault()
            };
            result.Add(record);
        }
        return result;
    }

    /// <summary>The plan facing direction: FacingOrientation, or the location rotation when that is vertical/zero.</summary>
    public static P2? FacingOf(FamilyInstance instance, LocationPoint lp)
    {
        try
        {
            var f = instance.FacingOrientation;
            if (f != null && Math.Abs(f.X) + Math.Abs(f.Y) > 1e-6) return new P2(f.X, f.Y).Normalized();
        }
        catch { }
        try { return new P2(Math.Cos(lp.Rotation), Math.Sin(lp.Rotation)); }
        catch { return null; }
    }

    public static string[] Codes(Dictionary<string, object?> args) =>
        ToolArguments.GetStringArray(args, "codes")
            .Concat(new[] { ToolArguments.GetString(args, "deviceCode") })
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToArray();
}
