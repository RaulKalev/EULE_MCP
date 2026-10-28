namespace RevitMCP.Addin.Coordination.Bcf;

/// <summary>A BCF file (2.0, 2.1 or 3.0) as read from its zip: its topics and what could not be read. Pure.</summary>
public sealed class BcfFile
{
    public string Path { get; set; } = string.Empty;

    /// <summary>The VersionId of bcf.version ("2.1", "3.0"); null when the file has none.</summary>
    public string? Version { get; set; }

    /// <summary>The project name of project.bcfp, when present.</summary>
    public string? ProjectName { get; set; }

    public List<BcfTopic> Topics { get; } = new();
    public List<string> Warnings { get; } = new();
}

public sealed class BcfTopic
{
    public string Guid { get; set; } = string.Empty;

    /// <summary>The folder of the topic inside the zip (normally the topic guid).</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>The topic's own Index (BCF 2.x Topic/Index, 3.0 ServerAssignedId when numeric); null when absent.</summary>
    public int? Index { get; set; }

    /// <summary>1-based position in the file, sorted by Index then creation date: a stable number when Index is missing.</summary>
    public int Number { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? TopicType { get; set; }
    public string? TopicStatus { get; set; }
    public string? Priority { get; set; }
    public string? Stage { get; set; }
    public string? AssignedTo { get; set; }
    public string? Description { get; set; }
    public string? CreationDate { get; set; }
    public string? CreationAuthor { get; set; }
    public string? ModifiedDate { get; set; }
    public string? DueDate { get; set; }
    public List<string> Labels { get; } = new();

    /// <summary>IFC file names the topic's header refers to.</summary>
    public List<string> Files { get; } = new();

    public List<BcfComment> Comments { get; } = new();
    public List<BcfViewpoint> Viewpoints { get; } = new();
}

public sealed class BcfComment
{
    public string Guid { get; set; } = string.Empty;
    public string? Date { get; set; }
    public string? Author { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? ViewpointGuid { get; set; }
}

public sealed class BcfViewpoint
{
    public string Guid { get; set; } = string.Empty;

    /// <summary>The .bcfv entry name inside the topic folder.</summary>
    public string? FileName { get; set; }

    /// <summary>The snapshot entry name inside the topic folder; null when there is none.</summary>
    public string? SnapshotFileName { get; set; }

    /// <summary>The snapshot's size in bytes; 0 when it is missing.</summary>
    public long SnapshotBytes { get; set; }

    public BcfCamera? Camera { get; set; }
    public List<BcfClippingPlane> ClippingPlanes { get; } = new();

    /// <summary>Selected components: the clashing elements in a clash export.</summary>
    public List<BcfComponent> Selection { get; } = new();

    /// <summary>The default visibility of the viewpoint's Visibility element; null when the viewpoint has none.</summary>
    public bool? DefaultVisibility { get; set; }

    /// <summary>Components whose visibility differs from <see cref="DefaultVisibility"/>.</summary>
    public List<BcfComponent> VisibilityExceptions { get; } = new();

    /// <summary>Components given a colour, with the colour (ARGB or RGB hex).</summary>
    public List<BcfComponent> Colored { get; } = new();
}

public sealed class BcfComponent
{
    public string IfcGuid { get; set; } = string.Empty;
    public string? OriginatingSystem { get; set; }

    /// <summary>The authoring tool's own id (the Revit ElementId when the IFC came from Revit and the tool kept it).</summary>
    public string? AuthoringToolId { get; set; }

    /// <summary>Only for coloured components: the colour.</summary>
    public string? Color { get; set; }
}

public sealed class BcfCamera
{
    /// <summary>perspective or orthogonal.</summary>
    public string Kind { get; set; } = "perspective";

    public BcfVector ViewPoint { get; set; } = new();
    public BcfVector Direction { get; set; } = new();
    public BcfVector UpVector { get; set; } = new();

    /// <summary>Perspective cameras: the field of view in degrees.</summary>
    public double? FieldOfView { get; set; }

    /// <summary>Orthogonal cameras: the view-to-world scale.</summary>
    public double? ViewToWorldScale { get; set; }
}

public sealed class BcfClippingPlane
{
    public BcfVector Location { get; set; } = new();
    public BcfVector Direction { get; set; } = new();
}

/// <summary>
/// The elements a topic is about. Clash exports (Navisworks, Solibri, BIMcollab, Revizto) select the clashing
/// elements; when no viewpoint selects anything, the few elements shown against a hidden model (DefaultVisibility
/// false) or the coloured ones stand in. Pure.
/// </summary>
public static class BcfClashComponents
{
    /// <summary>More than this many shown or coloured elements is a context view, not a clash.</summary>
    public const int MaxFallbackComponents = 10;

    public static (List<BcfComponent> Components, string Source) Of(BcfTopic topic)
    {
        foreach (var viewpoint in topic.Viewpoints)
            if (viewpoint.Selection.Count > 0) return (Distinct(viewpoint.Selection), "selection");

        foreach (var viewpoint in topic.Viewpoints)
        {
            if (viewpoint.DefaultVisibility == false && viewpoint.VisibilityExceptions.Count is > 0 and <= MaxFallbackComponents)
                return (Distinct(viewpoint.VisibilityExceptions), "visible");
        }

        foreach (var viewpoint in topic.Viewpoints)
        {
            var colored = Distinct(viewpoint.Colored);
            if (colored.Count is > 0 and <= MaxFallbackComponents) return (colored, "colored");
        }

        return (new List<BcfComponent>(), "none");
    }

    private static List<BcfComponent> Distinct(IEnumerable<BcfComponent> components) =>
        components
            .GroupBy(c => c.IfcGuid.Length > 0 ? "g:" + c.IfcGuid : "a:" + c.AuthoringToolId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
}

/// <summary>Picks topics by number (as revit_bcf_read lists them), clash id (BCF-0003), Index or GUID. Pure.</summary>
public static class BcfTopicSelector
{
    public const string ClashIdPrefix = "BCF-";

    /// <summary>The clash id a checked topic gets in the clash run.</summary>
    public static string ClashIdOf(BcfTopic topic) => $"{ClashIdPrefix}{topic.Number:D4}";

    /// <summary>The topics the keys name, in file order; every topic when there are no keys.</summary>
    public static List<BcfTopic> Select(IReadOnlyList<BcfTopic> topics, IReadOnlyCollection<string> keys, out List<string> unknown)
    {
        unknown = new List<string>();
        if (keys.Count == 0) return topics.ToList();

        var picked = new HashSet<BcfTopic>();
        foreach (var raw in keys)
        {
            var key = raw.Trim();
            if (key.Length == 0) continue;
            var number = key.StartsWith(ClashIdPrefix, StringComparison.OrdinalIgnoreCase) ? key.Substring(ClashIdPrefix.Length) : key.TrimStart('#');
            var match = int.TryParse(number, out var n)
                ? topics.FirstOrDefault(t => t.Number == n) ?? topics.FirstOrDefault(t => t.Index == n)
                : topics.FirstOrDefault(t => string.Equals(t.Guid, key, StringComparison.OrdinalIgnoreCase));
            if (match != null) picked.Add(match);
            else unknown.Add(key);
        }

        return topics.Where(picked.Contains).ToList();
    }

    /// <summary>Clash severity from a BCF priority: Critical, High, Medium (the default) or Low.</summary>
    public static string SeverityOf(string? priority)
    {
        var p = (priority ?? string.Empty).Trim().ToLowerInvariant();
        if (p.Contains("critical") || p.Contains("blocker")) return "Critical";
        if (p.Contains("high") || p.Contains("major") || p.Contains("kõrge")) return "High";
        if (p.Contains("low") || p.Contains("minor") || p.Contains("madal")) return "Low";
        return "Medium";
    }
}

/// <summary>A BCF point or direction: metres in the IFC file's world coordinates.</summary>
public sealed class BcfVector
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}
