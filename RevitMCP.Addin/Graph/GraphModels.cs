namespace RevitMCP.Addin.Graph;

/// <summary>One row of the <c>nodes</c> table.</summary>
public sealed class GraphNode
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = GraphSchema.Kinds.Element;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public string Workset { get; set; } = string.Empty;

    /// <summary>Small JSON object with routing hints (family, type, sheet number ...). Never parameter values.</summary>
    public string? Extra { get; set; }
}

/// <summary>One row of the <c>edges</c> table. Direction is always src → dst.</summary>
public sealed class GraphEdge
{
    public GraphEdge() { }

    public GraphEdge(string src, string dst, string rel)
    {
        Src = src;
        Dst = dst;
        Rel = rel;
    }

    public string Src { get; set; } = string.Empty;
    public string Dst { get; set; } = string.Empty;
    public string Rel { get; set; } = string.Empty;
}

/// <summary>Typed view over the <c>meta</c> table.</summary>
public sealed class GraphMeta
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;

    public string? ModelPath      => Get(GraphSchema.MetaKeys.ModelPath);
    public string? ModelName      => Get(GraphSchema.MetaKeys.ModelName);
    public string? BuiltAt        => Get(GraphSchema.MetaKeys.BuiltAt);
    public string? CentralVersion => Get(GraphSchema.MetaKeys.CentralVersion);
    public string? BuiltBy        => Get(GraphSchema.MetaKeys.BuiltBy);
    public string? VersionSource  => Get(GraphSchema.MetaKeys.VersionSource);

    public long? ElementCount  => ParseLong(Get(GraphSchema.MetaKeys.ElementCount));
    public int?  SchemaVersion => (int?)ParseLong(Get(GraphSchema.MetaKeys.SchemaVersion));
    public long? NodeCount     => ParseLong(Get(GraphSchema.MetaKeys.NodeCount));
    public long? EdgeCount     => ParseLong(Get(GraphSchema.MetaKeys.EdgeCount));

    private static long? ParseLong(string? s) =>
        long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>
/// The "current model version" signal used for freshness checks. Built by the Revit-side
/// reader on the API thread; compared against the stored meta by <see cref="GraphFreshness"/>.
/// </summary>
public sealed class ModelVersionSignal
{
    /// <summary>Opaque version string, e.g. <c>central:42:guid</c> or <c>saves:17:guid</c>. Empty when unknown.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Human-readable description of which Revit API signal produced <see cref="Value"/>.</summary>
    public string Source { get; set; } = string.Empty;

    public long ElementCount { get; set; }
    public bool IsWorkshared { get; set; }
    public string ModelPath { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Project number (or name) used as the folder segment under the shared root.</summary>
    public string ProjectKey { get; set; } = string.Empty;
    public string RevitVersion { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
}

public sealed class GraphNeighbor
{
    public GraphNode Node { get; set; } = new();
    public string Rel { get; set; } = string.Empty;

    /// <summary>"out" when the queried node is the edge source, "in" when it is the destination.</summary>
    public string Direction { get; set; } = string.Empty;
}

public sealed class GraphPathStep
{
    public GraphNode Node { get; set; } = new();

    /// <summary>Relationship used to reach this node from the previous step; null for the start node.</summary>
    public string? Rel { get; set; }

    /// <summary>"out" if the edge points previous → this, "in" if it points this → previous.</summary>
    public string? Direction { get; set; }
}

public sealed class GraphPathResult
{
    public bool Found { get; set; }
    public int Hops { get; set; }
    public List<GraphPathStep> Steps { get; set; } = new();
    public int VisitedNodes { get; set; }
    public bool SearchTruncated { get; set; }
}

public sealed class GraphSubtreeNode
{
    public GraphNode Node { get; set; } = new();
    public int Depth { get; set; }
    public string? ParentId { get; set; }
}

public sealed class GraphSubtreeResult
{
    public GraphNode? Root { get; set; }
    public List<GraphSubtreeNode> Nodes { get; set; } = new();
    public bool Truncated { get; set; }
    public int MaxDepthReached { get; set; }
}

public sealed class GraphCount
{
    public string Key { get; set; } = string.Empty;
    public long Count { get; set; }
}

public sealed class GraphPanelSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public long CircuitCount { get; set; }
    public long FedElementCount { get; set; }
}

public sealed class GraphSummaryResult
{
    public List<GraphCount> NodesByKind { get; set; } = new();
    public List<GraphCount> EdgesByRel { get; set; } = new();
    public List<GraphCount> ElementsByCategory { get; set; } = new();
    public List<GraphCount> ElementsByLevel { get; set; } = new();
    public List<GraphCount> ElementsByWorkset { get; set; } = new();
    public List<GraphPanelSummary> Panels { get; set; } = new();

    public long CircuitsWithoutPanelCount { get; set; }
    public List<GraphNode> CircuitsWithoutPanel { get; set; } = new();
    public long CircuitsWithoutElementsCount { get; set; }
    public List<GraphNode> CircuitsWithoutElements { get; set; } = new();

    public long ElementsWithoutLocationCount { get; set; }
    public List<GraphNode> ElementsWithoutLocation { get; set; } = new();

    /// <summary>True when any count list was cut at the requested top-N.</summary>
    public int TopN { get; set; }
}
