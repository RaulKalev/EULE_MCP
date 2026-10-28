using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using RevitMCP.Addin.Mep;

namespace RevitMCP.Addin.Coordination.ClashFix;

/// <summary>Settings shared by the proposal and the apply step.</summary>
internal sealed class ClashFixOptions
{
    /// <summary>Gap left between the moved element and the obstacle, mm.</summary>
    public double ClearanceMm { get; set; } = 25;

    /// <summary>Travel is rounded up to a multiple of this, mm (0 = 0.1 mm).</summary>
    public double StepMm { get; set; } = 10;

    /// <summary>No proposal moves an element further than this, mm.</summary>
    public double MaxShiftMm { get; set; } = 1000;

    /// <summary>Intersection below a cube of this edge does not count (the detector's tolerance), mm.</summary>
    public double ToleranceMm { get; set; } = 5;

    /// <summary>Allowed directions (names or vertical/horizontal); empty = all.</summary>
    public List<string> Directions { get; set; } = new();

    public int MaxOptions { get; set; } = 3;

    public double ToleranceMm3 => ClashGeometry.ToleranceVolumeMm3(ToleranceMm);
}

/// <summary>What the mover is and what it is joined to.</summary>
internal sealed class MoverInfo
{
    public MoverInfo(Element element)
    {
        Element = element;
    }

    public Element Element { get; }
    public MoverShape Shape { get; set; }

    /// <summary>A run segment's direction.</summary>
    public FixVector? Axis { get; set; }

    /// <summary>A hosted instance's host normal: it only moves within that plane.</summary>
    public FixVector? HostNormal { get; set; }

    /// <summary>Null when it may move; else why not.</summary>
    public string? Blocked { get; set; }

    public int ConnectedEnds { get; set; }

    /// <summary>Directions of the runs it is joined to (through fittings).</summary>
    public List<FixVector> NeighbourAxes { get; } = new();

    /// <summary>Joined runs by id with their direction (to check after the move).</summary>
    public Dictionary<long, FixVector> Neighbours { get; } = new();

    public int ConnectedToEquipment { get; set; }

    /// <summary>Keys (ClashGeometry.KeyOf) of the elements joined to or hosting it: never new clashes of the move.</summary>
    public HashSet<string> Attached { get; } = new(StringComparer.Ordinal);

    /// <summary>Ids of the host-model elements that change with the move: the fittings that follow and the joined runs.</summary>
    public List<long> Followers { get; } = new();
}

/// <summary>One way to clear a clash.</summary>
internal sealed class FixOption
{
    public string Direction { get; set; } = string.Empty;
    public FixVector Vector { get; set; }
    public double ClearingTravelMm { get; set; }
    public double TravelMm { get; set; }
    public List<IntersectingElement> NewClashes { get; set; } = new();
    public List<IntersectingElement> AlsoClears { get; set; } = new();
    public FixVector DeltaMm => Vector.Scale(TravelMm);
}

/// <summary>
/// Proposes and verifies clash fixes by moving one element of the pair: a straight run segment (cable tray, conduit,
/// duct, pipe) or a family instance at a point. It never moves building elements, linked elements, pinned elements or
/// group members, and a run segment joined at its ends only moves along the runs it is joined to (which then stretch);
/// anything else needs a reroute by hand. Distances are found with the real solids, not boxes. API thread.
/// </summary>
internal static class ClashFixService
{
    private static readonly HashSet<BuiltInCategory> BuildingCategories = new()
    {
        BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Ceilings,
        BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Columns,
        BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFoundation,
        BuiltInCategory.OST_Stairs, BuiltInCategory.OST_StairsRailing, BuiltInCategory.OST_Ramps,
        BuiltInCategory.OST_CurtainWallPanels, BuiltInCategory.OST_CurtainWallMullions
    };

    public static MoverInfo Classify(PlacedElement placed)
    {
        var element = placed.Element;
        var info = new MoverInfo(element);

        if (!placed.IsHost)
        {
            info.Blocked = $"in linked model '{placed.Link!.Name}': fix it in that model";
            return info;
        }
        if (element.Pinned)
        {
            info.Blocked = "pinned";
            return info;
        }
        if (element.GroupId != ElementId.InvalidElementId)
        {
            info.Blocked = $"member of group {element.GroupId.Value}";
            return info;
        }

        var category = element.Category == null ? (BuiltInCategory?)null : (BuiltInCategory)element.Category.Id.Value;

        if (element is MEPCurve curve)
        {
            if (element is FlexDuct || element is FlexPipe)
            {
                info.Blocked = "flexible duct or pipe: reshape it by hand";
                return info;
            }
            if (curve.Location is not LocationCurve { Curve: Line line })
            {
                info.Blocked = "not a straight segment";
                return info;
            }
            info.Shape = MoverShape.LinearRun;
            info.Axis = ClashGeometry.ToVector(line.Direction);
            ReadConnections(info);
            return info;
        }

        if (element is FamilyInstance instance)
        {
            if (category is { } c && BuildingCategories.Contains(c))
            {
                info.Blocked = $"{element.Category!.Name}: building and structural elements are not moved automatically";
                return info;
            }
            if (instance.MEPModel is MechanicalFitting)
            {
                info.Blocked = "a fitting: it moves with the run it belongs to";
                return info;
            }
            if (instance.Location is not LocationPoint)
            {
                info.Blocked = "no insertion point";
                return info;
            }

            info.Shape = MoverShape.PointInstance;
            info.HostNormal = HostNormalOf(instance);
            if (instance.Host != null) info.Attached.Add(ClashGeometry.KeyOf(0, instance.Host.Id.Value));
            foreach (var connector in MepConnectorFinder.EndConnectors(instance))
            {
                if (!IsPhysical(connector) || !MepConnectorFinder.IsConnected(connector)) continue;
                info.ConnectedEnds++;
                info.ConnectedToEquipment++;
            }
            if (info.ConnectedEnds > 0)
                info.Blocked = $"joined to {info.ConnectedEnds} run(s): move it together with its runs by hand";
            return info;
        }

        info.Blocked = $"{element.Category?.Name ?? element.GetType().Name}: only run segments and placed family instances are moved automatically";
        return info;
    }

    private static FixVector? HostNormalOf(FamilyInstance instance)
    {
        try
        {
            if (instance.HostFace != null) return ClashGeometry.ToVector(instance.GetTransform().BasisZ);
            if (instance.Host is Wall) return ClashGeometry.ToVector(instance.FacingOrientation);
            if (instance.Host is Ceiling || instance.Host is Floor || instance.Host is RoofBase) return FixVector.Up;
        }
        catch
        {
            // No usable host plane: the instance moves freely.
        }
        return null;
    }

    private static bool IsPhysical(Connector connector)
    {
        try
        {
            return connector.ConnectorType != ConnectorType.Logical &&
                   connector.Domain is Domain.DomainHvac or Domain.DomainPiping or Domain.DomainCableTrayConduit;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Follows each joined end through fittings (up to three deep) to the runs or equipment beyond.</summary>
    private static void ReadConnections(MoverInfo info)
    {
        var self = info.Element.Id.Value;
        var visited = new HashSet<long> { self };
        foreach (var end in MepConnectorFinder.EndConnectors(info.Element))
        {
            if (!MepConnectorFinder.IsConnected(end)) continue;
            info.ConnectedEnds++;
            var frontier = OwnersJoinedTo(end, visited);
            for (var depth = 0; depth < 3 && frontier.Count > 0; depth++)
            {
                var next = new List<Element>();
                foreach (var owner in frontier)
                {
                    info.Attached.Add(ClashGeometry.KeyOf(0, owner.Id.Value));
                    if (owner is MEPCurve run && run.Location is LocationCurve { Curve: Line line })
                    {
                        var axis = ClashGeometry.ToVector(line.Direction);
                        info.NeighbourAxes.Add(axis);
                        info.Neighbours[owner.Id.Value] = axis;
                        info.Followers.Add(owner.Id.Value);
                    }
                    else if (owner is FamilyInstance { MEPModel: MechanicalFitting })
                    {
                        info.Followers.Add(owner.Id.Value);
                        foreach (var fittingEnd in MepConnectorFinder.EndConnectors(owner))
                            if (MepConnectorFinder.IsConnected(fittingEnd)) next.AddRange(OwnersJoinedTo(fittingEnd, visited));
                    }
                    else
                    {
                        info.ConnectedToEquipment++;
                    }
                }
                frontier = next;
            }
            // A fitting chain longer than three is treated like equipment: it cannot be checked.
            if (frontier.Count > 0) info.ConnectedToEquipment += frontier.Count;
        }
    }

    private static List<Element> OwnersJoinedTo(Connector connector, HashSet<long> visited)
    {
        var owners = new List<Element>();
        try
        {
            foreach (Connector other in connector.AllRefs)
            {
                if (other.ConnectorType == ConnectorType.Logical) continue;
                var owner = other.Owner;
                if (owner == null || !visited.Add(owner.Id.Value)) continue;
                owners.Add(owner);
            }
        }
        catch
        {
            // Unreadable connections: nothing to follow.
        }
        return owners;
    }

    /// <summary>
    /// The options for moving <paramref name="mover"/> out of <paramref name="obstacle"/>, best first (fewest new clashes,
    /// then the shortest travel), and the directions that were ruled out with the reason.
    /// </summary>
    public static (List<FixOption> Options, List<(string Direction, string Reason)> Blocked) Propose(
        Document host, PlacedElement mover, MoverInfo info, PlacedElement obstacle, ClashFixOptions options)
    {
        var result = new List<FixOption>();
        var blocked = new List<(string, string)>();

        var moverSolids = ClashGeometry.Solids(mover);
        var obstacleSolids = ClashGeometry.Solids(obstacle);
        if (moverSolids.Count == 0 || obstacleSolids.Count == 0)
        {
            blocked.Add(("all", "no solid geometry to measure"));
            return (result, blocked);
        }

        var moverPoints = ClashGeometry.PointsMm(moverSolids);
        var obstaclePoints = ClashGeometry.PointsMm(obstacleSolids);
        var exclude = Exclusions(mover, info, obstacle);
        var before = ClashGeometry.FindIntersecting(host, moverSolids, exclude, options.ToleranceMm3);
        var tolerance = options.ToleranceMm3;

        foreach (var direction in ClashFixMath.Directions(info.Shape, info.Axis, info.HostNormal, options.Directions))
        {
            if (info.ConnectedEnds > 0)
            {
                var blocker = ClashFixMath.ConnectedMoveBlocker(direction.Vector, info.NeighbourAxes, info.ConnectedToEquipment);
                if (blocker != null)
                {
                    blocked.Add((direction.Name, blocker));
                    continue;
                }
            }

            var bound = ClashFixMath.TravelToPass(moverPoints, obstaclePoints, direction.Vector) ?? 0;
            var upper = Math.Min(bound, options.MaxShiftMm);

            bool Clears(double travelMm) =>
                ClashGeometry.IntersectionVolumeMm3(
                    ClashGeometry.Translate(moverSolids, ClashGeometry.ToFeet(direction.Vector.Scale(travelMm))),
                    obstacleSolids, out _) <= tolerance;

            var clearing = ClashFixMath.MinimalClearingTravel(upper, Clears, precision: 2);
            if (clearing == null)
            {
                blocked.Add((direction.Name, bound > options.MaxShiftMm
                    ? $"needs more than maxShiftMm ({options.MaxShiftMm} mm)"
                    : "does not clear the obstacle in this direction"));
                continue;
            }

            var travel = ClashFixMath.ProposedTravel(clearing.Value, options.ClearanceMm, options.StepMm);
            if (travel > options.MaxShiftMm)
            {
                blocked.Add((direction.Name, $"needs {travel} mm with the clearance, more than maxShiftMm ({options.MaxShiftMm} mm)"));
                continue;
            }
            if (!Clears(travel))
            {
                blocked.Add((direction.Name, $"clears at {Math.Round(clearing.Value)} mm but runs into the obstacle again at {travel} mm"));
                continue;
            }

            var moved = ClashGeometry.Translate(moverSolids, ClashGeometry.ToFeet(direction.Vector.Scale(travel)));
            var after = ClashGeometry.FindIntersecting(host, moved, exclude, tolerance);
            var beforeKeys = new HashSet<string>(before.Select(b => b.Placed.Key));
            var afterKeys = new HashSet<string>(after.Select(a => a.Placed.Key));

            result.Add(new FixOption
            {
                Direction = direction.Name,
                Vector = direction.Vector,
                ClearingTravelMm = Math.Round(clearing.Value, 1),
                TravelMm = travel,
                NewClashes = after.Where(a => !beforeKeys.Contains(a.Placed.Key)).ToList(),
                AlsoClears = before.Where(b => !afterKeys.Contains(b.Placed.Key)).ToList()
            });
        }

        var ordered = result
            .OrderBy(o => o.NewClashes.Count)
            .ThenBy(o => o.TravelMm)
            .ThenBy(o => o.Direction == "up" || o.Direction == "down" ? 0 : 1)
            .Take(Math.Max(1, options.MaxOptions))
            .ToList();
        return (ordered, blocked);
    }

    /// <summary>The mover, what it is joined to or hosted by, and the obstacle: none of them is a new clash.</summary>
    public static HashSet<string> Exclusions(PlacedElement mover, MoverInfo info, PlacedElement obstacle) =>
        new(info.Attached, StringComparer.Ordinal) { mover.Key, obstacle.Key };
}
