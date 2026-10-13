namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Plans the arcs that show which devices are on one circuit, as a stand-in for Revit's wires: which device is
/// joined to which, and where each arc bulges. Works on 2D points in a view's plane, in any length unit.
/// No Revit types: unit tested.
/// </summary>
public static class CircuitArcPlanner
{
    public const string Chain = "chain";
    public const string Tree = "tree";

    public sealed class Node
    {
        public Node(long id, double x, double y)
        {
            Id = id;
            X = x;
            Y = y;
        }

        public long Id { get; }
        public double X { get; }
        public double Y { get; }
    }

    public sealed class ArcSegment
    {
        public long FromId { get; set; }
        public long ToId { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        /// <summary>The point of the arc furthest from the chord.</summary>
        public double MidX { get; set; }
        public double MidY { get; set; }
        public double Chord { get; set; }
    }

    public sealed class Plan
    {
        public List<ArcSegment> Arcs { get; } = new();
        /// <summary>Devices that sit on top of an already joined device (closer than the minimum distance): no arc is drawn to them.</summary>
        public List<long> Coincident { get; } = new();
    }

    /// <summary>
    /// Joins the nodes starting from <paramref name="startIndex"/>.
    /// chain: each device to the nearest one not joined yet, like a daisy-chained cable.
    /// tree: the shortest total length, with branches (a minimum spanning tree).
    /// </summary>
    /// <param name="bulge">Height of an arc as a fraction of its chord (0.2 = a fifth).</param>
    /// <param name="side">left, right or alternate: which side of its direction an arc bulges to.</param>
    public static Plan Build(IReadOnlyList<Node> nodes, int startIndex, string mode, double bulge, string side, double minDistance)
    {
        var plan = new Plan();
        if (nodes.Count < 2) return plan;
        if (startIndex < 0 || startIndex >= nodes.Count) startIndex = 0;

        var joined = new List<int> { startIndex };
        var open = Enumerable.Range(0, nodes.Count).Where(i => i != startIndex).ToList();
        bool tree = string.Equals(mode, Tree, StringComparison.OrdinalIgnoreCase);
        int tail = startIndex;
        int drawn = 0;

        while (open.Count > 0)
        {
            int from = tail, to = open[0];
            double best = double.MaxValue;
            foreach (var candidate in open)
            {
                // tree: from any joined device; chain: only from the end of the chain
                foreach (var source in tree ? joined : (IEnumerable<int>)new[] { tail })
                {
                    var d = Distance(nodes[source], nodes[candidate]);
                    if (d < best)
                    {
                        best = d;
                        from = source;
                        to = candidate;
                    }
                }
            }

            open.Remove(to);
            joined.Add(to);
            if (best < minDistance)
            {
                // On top of a joined device: part of the circuit, but nothing to draw. The chain does not move.
                plan.Coincident.Add(nodes[to].Id);
                continue;
            }
            tail = to;

            int direction = side?.Trim().ToLowerInvariant() switch
            {
                "right" => -1,
                "alternate" => drawn % 2 == 0 ? 1 : -1,
                _ => 1
            };
            drawn++;

            var a = nodes[from];
            var b = nodes[to];
            Midpoint(a.X, a.Y, b.X, b.Y, bulge, direction, out var midX, out var midY);
            plan.Arcs.Add(new ArcSegment
            {
                FromId = a.Id, ToId = b.Id, X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, MidX = midX, MidY = midY, Chord = best
            });
        }
        return plan;
    }

    /// <summary>
    /// A home run: a short arc from a device that points at the panel and ends in the open, the drafting sign for
    /// "this circuit goes to its panel". Null when the device sits on the panel. The arc is never longer than
    /// <paramref name="maxShare"/> of the way to the panel.
    /// </summary>
    public static ArcSegment? HomeRun(Node device, double panelX, double panelY, double length, double bulge, string side, double maxShare = 0.8)
    {
        double dx = panelX - device.X, dy = panelY - device.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        double chord = Math.Min(length, distance * maxShare);
        if (distance <= 0 || chord <= 0) return null;

        double endX = device.X + dx / distance * chord;
        double endY = device.Y + dy / distance * chord;
        int direction = string.Equals(side?.Trim(), "left", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
        Midpoint(device.X, device.Y, endX, endY, bulge, direction, out var midX, out var midY);
        return new ArcSegment
        {
            FromId = device.Id, ToId = 0, X1 = device.X, Y1 = device.Y, X2 = endX, Y2 = endY, MidX = midX, MidY = midY, Chord = chord
        };
    }

    /// <summary>The index of the node nearest to a point (the panel), to start the chain at.</summary>
    public static int Nearest(IReadOnlyList<Node> nodes, double x, double y)
    {
        int index = 0;
        double best = double.MaxValue;
        for (int i = 0; i < nodes.Count; i++)
        {
            var d = Math.Sqrt((nodes[i].X - x) * (nodes[i].X - x) + (nodes[i].Y - y) * (nodes[i].Y - y));
            if (d < best)
            {
                best = d;
                index = i;
            }
        }
        return index;
    }

    /// <summary>
    /// The arc's middle point: the chord's midpoint moved sideways by bulge × chord. Left is to the left when
    /// looking from the first point to the second.
    /// </summary>
    public static void Midpoint(double x1, double y1, double x2, double y2, double bulge, int direction, out double midX, out double midY)
    {
        double dx = x2 - x1, dy = y2 - y1;
        double chord = Math.Sqrt(dx * dx + dy * dy);
        double height = Math.Max(0, bulge) * chord * (direction < 0 ? -1 : 1);
        // Left normal of (dx, dy) is (-dy, dx)
        double nx = chord > 0 ? -dy / chord : 0;
        double ny = chord > 0 ? dx / chord : 0;
        midX = (x1 + x2) / 2 + nx * height;
        midY = (y1 + y2) / 2 + ny * height;
    }

    private static double Distance(Node a, Node b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
