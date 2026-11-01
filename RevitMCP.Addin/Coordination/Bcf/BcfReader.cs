using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

namespace RevitMCP.Addin.Coordination.Bcf;

/// <summary>
/// Reads a BCF zip (2.0, 2.1 and 3.0): every topic folder's markup.bcf and its viewpoints. Elements are matched by
/// local name, so the namespaces and the small layout differences between the versions (2.x keeps comments and
/// viewpoints next to the topic, 3.0 inside it; 2.0 lists components without a Selection element) do not matter.
/// A topic that cannot be read is skipped with a warning; it never fails the file. Pure.
/// </summary>
public static class BcfReader
{
    public static BcfFile Read(string path)
    {
        using var stream = File.OpenRead(path);
        var file = Read(stream);
        file.Path = path;
        return file;
    }

    public static BcfFile Read(Stream stream)
    {
        var file = new BcfFile();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        // Some writers use backslashes; folder names are compared case-insensitively.
        var entries = zip.Entries
            .Where(e => e.Length > 0 || !e.FullName.EndsWith("/", StringComparison.Ordinal))
            .GroupBy(e => Normalize(e.FullName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        if (entries.TryGetValue("bcf.version", out var versionEntry))
        {
            var version = TryLoad(versionEntry, file.Warnings);
            file.Version = version?.Root?.Attribute("VersionId")?.Value
                           ?? Child(version?.Root, "DetailedVersion")?.Value;
        }

        var projectEntry = entries.Keys.FirstOrDefault(k => !k.Contains('/') && k.EndsWith(".bcfp", StringComparison.OrdinalIgnoreCase));
        if (projectEntry != null)
        {
            var project = TryLoad(entries[projectEntry], file.Warnings);
            file.ProjectName = project?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
        }

        foreach (var markupKey in entries.Keys.Where(k => k.EndsWith("/markup.bcf", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.Ordinal))
        {
            var folder = markupKey.Substring(0, markupKey.Length - "/markup.bcf".Length);
            try
            {
                var topic = ReadTopic(folder, entries, file.Warnings);
                if (topic != null) file.Topics.Add(topic);
            }
            catch (Exception ex)
            {
                file.Warnings.Add($"Topic folder '{folder}' could not be read: {ex.Message}");
            }
        }

        if (file.Topics.Count == 0 && !entries.Keys.Any(k => k.EndsWith("/markup.bcf", StringComparison.OrdinalIgnoreCase)))
            file.Warnings.Add("The file has no topic folders with a markup.bcf: it is not a BCF zip, or it is empty.");

        Number(file.Topics);
        return file;
    }

    /// <summary>Numbers topics 1..n by their Index (topics without one last), then creation date, then title.</summary>
    internal static void Number(List<BcfTopic> topics)
    {
        var ordered = topics
            .OrderBy(t => t.Index ?? int.MaxValue)
            .ThenBy(t => t.CreationDate ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(t => t.Title, StringComparer.Ordinal)
            .ToList();
        topics.Clear();
        topics.AddRange(ordered);
        for (var i = 0; i < topics.Count; i++) topics[i].Number = i + 1;
    }

    private static BcfTopic? ReadTopic(string folder, Dictionary<string, ZipArchiveEntry> entries, List<string> warnings)
    {
        var markup = TryLoad(entries[folder + "/markup.bcf"], warnings);
        var root = markup?.Root;
        if (root == null)
        {
            warnings.Add($"Topic folder '{folder}': markup.bcf is not valid XML.");
            return null;
        }

        var topicElement = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Topic");
        if (topicElement == null)
        {
            warnings.Add($"Topic folder '{folder}': markup.bcf has no Topic.");
            return null;
        }

        var topic = new BcfTopic
        {
            Folder = folder,
            Guid = topicElement.Attribute("Guid")?.Value ?? folder,
            TopicType = Attr(topicElement, "TopicType"),
            TopicStatus = Attr(topicElement, "TopicStatus"),
            Title = Text(topicElement, "Title") ?? string.Empty,
            Priority = Text(topicElement, "Priority"),
            Stage = Text(topicElement, "Stage"),
            AssignedTo = Text(topicElement, "AssignedTo"),
            Description = Text(topicElement, "Description"),
            CreationDate = Text(topicElement, "CreationDate"),
            CreationAuthor = Text(topicElement, "CreationAuthor"),
            ModifiedDate = Text(topicElement, "ModifiedDate"),
            DueDate = Text(topicElement, "DueDate"),
            Index = ParseInt(Text(topicElement, "Index")) ?? ParseInt(Attr(topicElement, "ServerAssignedId"))
        };

        // 2.x: one Labels element per label; 3.0: Labels > Label.
        foreach (var labels in topicElement.Elements().Where(e => e.Name.LocalName == "Labels"))
        {
            var inner = labels.Elements().Where(e => e.Name.LocalName == "Label").Select(e => e.Value.Trim()).ToList();
            if (inner.Count > 0) topic.Labels.AddRange(inner.Where(l => l.Length > 0));
            else if (labels.Value.Trim().Length > 0) topic.Labels.Add(labels.Value.Trim());
        }

        foreach (var fileElement in root.Descendants().Where(e => e.Name.LocalName == "File"))
        {
            var name = Text(fileElement, "Filename") ?? Attr(fileElement, "Filename");
            if (!string.IsNullOrWhiteSpace(name)) topic.Files.Add(name!);
        }

        // A comment is the Comment element with a Guid; its text is its inner Comment element.
        foreach (var commentElement in root.Descendants().Where(e => e.Name.LocalName == "Comment" && e.Attribute("Guid") != null))
        {
            topic.Comments.Add(new BcfComment
            {
                Guid = commentElement.Attribute("Guid")!.Value,
                Date = Text(commentElement, "Date"),
                Author = Text(commentElement, "Author"),
                Text = Text(commentElement, "Comment") ?? string.Empty,
                ViewpointGuid = Child(commentElement, "Viewpoint")?.Attribute("Guid")?.Value
            });
        }

        // A viewpoint reference is an element with a Guid and a Viewpoint child holding the .bcfv file name
        // (2.x: Viewpoints, 3.0: ViewPoint). A comment's Viewpoint child is empty, so comments never match.
        var refs = root.Descendants()
            .Where(e => e.Attribute("Guid") != null && Child(e, "Viewpoint") is { HasElements: false } v && v.Value.Trim().Length > 0)
            .Select(e => new
            {
                Guid = e.Attribute("Guid")!.Value,
                File = Child(e, "Viewpoint")!.Value.Trim(),
                Snapshot = Text(e, "Snapshot"),
                Index = ParseInt(Text(e, "Index"))
            })
            .OrderBy(r => r.Index ?? int.MaxValue)
            .ToList();

        // Old writers sometimes leave the markup without references: fall back to the folder's .bcfv files.
        if (refs.Count == 0)
        {
            refs = entries.Keys
                .Where(k => k.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase) &&
                            k.EndsWith(".bcfv", StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => new { Guid = string.Empty, File = k.Substring(folder.Length + 1), Snapshot = (string?)null, Index = (int?)null })
                .ToList();
        }

        foreach (var reference in refs)
        {
            var viewpoint = new BcfViewpoint { Guid = reference.Guid, FileName = reference.File };
            var snapshot = reference.Snapshot;
            if (snapshot == null && string.Equals(reference.File, "viewpoint.bcfv", StringComparison.OrdinalIgnoreCase) &&
                entries.ContainsKey(folder + "/snapshot.png"))
                snapshot = "snapshot.png";
            if (snapshot != null && entries.TryGetValue(folder + "/" + Normalize(snapshot), out var snapshotEntry))
            {
                viewpoint.SnapshotFileName = Normalize(snapshot);
                viewpoint.SnapshotBytes = snapshotEntry.Length;
            }

            if (entries.TryGetValue(folder + "/" + Normalize(reference.File), out var viewpointEntry))
            {
                var visualization = TryLoad(viewpointEntry, warnings);
                if (visualization?.Root != null) ReadVisualization(visualization.Root, viewpoint);
                if (viewpoint.Guid.Length == 0) viewpoint.Guid = visualization?.Root?.Attribute("Guid")?.Value ?? string.Empty;
            }
            else
            {
                warnings.Add($"Topic '{topic.Title}': viewpoint file '{reference.File}' is missing from the zip.");
            }

            topic.Viewpoints.Add(viewpoint);
        }

        return topic;
    }

    internal static void ReadVisualization(XElement root, BcfViewpoint viewpoint)
    {
        var components = Child(root, "Components");
        if (components != null)
        {
            var selection = Child(components, "Selection");
            if (selection != null)
            {
                viewpoint.Selection.AddRange(ComponentsOf(selection));
            }
            else
            {
                // BCF 2.0: Components > Component with Selected/Visible attributes.
                foreach (var c in components.Elements().Where(e => e.Name.LocalName == "Component"))
                {
                    var component = ComponentOf(c);
                    if (component == null) continue;
                    if (string.Equals(Attr(c, "Selected"), "true", StringComparison.OrdinalIgnoreCase))
                        viewpoint.Selection.Add(component);
                    if (string.Equals(Attr(c, "Visible"), "false", StringComparison.OrdinalIgnoreCase))
                        viewpoint.VisibilityExceptions.Add(component);
                }
            }

            var visibility = Child(components, "Visibility");
            if (visibility != null)
            {
                viewpoint.DefaultVisibility = !string.Equals(Attr(visibility, "DefaultVisibility"), "false", StringComparison.OrdinalIgnoreCase);
                var exceptions = Child(visibility, "Exceptions");
                if (exceptions != null) viewpoint.VisibilityExceptions.AddRange(ComponentsOf(exceptions));
            }

            var coloring = Child(components, "Coloring");
            if (coloring != null)
            {
                foreach (var color in coloring.Elements().Where(e => e.Name.LocalName == "Color"))
                {
                    foreach (var component in ComponentsOf(color))
                    {
                        component.Color = Attr(color, "Color");
                        viewpoint.Colored.Add(component);
                    }
                }
            }
        }

        var perspective = Child(root, "PerspectiveCamera");
        var orthogonal = Child(root, "OrthogonalCamera");
        var camera = perspective ?? orthogonal;
        if (camera != null)
        {
            viewpoint.Camera = new BcfCamera
            {
                Kind = perspective != null ? "perspective" : "orthogonal",
                ViewPoint = Vector(Child(camera, "CameraViewPoint")),
                Direction = Vector(Child(camera, "CameraDirection")),
                UpVector = Vector(Child(camera, "CameraUpVector")),
                FieldOfView = ParseDouble(Text(camera, "FieldOfView")),
                ViewToWorldScale = ParseDouble(Text(camera, "ViewToWorldScale"))
            };
        }

        var planes = Child(root, "ClippingPlanes");
        if (planes != null)
        {
            foreach (var plane in planes.Elements().Where(e => e.Name.LocalName == "ClippingPlane"))
            {
                viewpoint.ClippingPlanes.Add(new BcfClippingPlane
                {
                    Location = Vector(Child(plane, "Location")),
                    Direction = Vector(Child(plane, "Direction"))
                });
            }
        }
    }

    private static IEnumerable<BcfComponent> ComponentsOf(XElement parent) =>
        parent.Elements().Where(e => e.Name.LocalName == "Component").Select(ComponentOf).Where(c => c != null)!;

    private static BcfComponent? ComponentOf(XElement element)
    {
        var guid = Attr(element, "IfcGuid");
        var authoringToolId = Text(element, "AuthoringToolId") ?? Attr(element, "AuthoringToolId");
        if (string.IsNullOrWhiteSpace(guid) && string.IsNullOrWhiteSpace(authoringToolId)) return null;
        return new BcfComponent
        {
            IfcGuid = guid?.Trim() ?? string.Empty,
            OriginatingSystem = Text(element, "OriginatingSystem") ?? Attr(element, "OriginatingSystem"),
            AuthoringToolId = authoringToolId?.Trim()
        };
    }

    private static BcfVector Vector(XElement? element) => element == null
        ? new BcfVector()
        : new BcfVector
        {
            X = ParseDouble(Text(element, "X")) ?? 0,
            Y = ParseDouble(Text(element, "Y")) ?? 0,
            Z = ParseDouble(Text(element, "Z")) ?? 0
        };

    private static XDocument? TryLoad(ZipArchiveEntry entry, List<string> warnings)
    {
        try
        {
            using var s = entry.Open();
            return XDocument.Load(s);
        }
        catch (Exception ex)
        {
            warnings.Add($"'{entry.FullName}' is not valid XML: {ex.Message}");
            return null;
        }
    }

    private static string Normalize(string name) => name.Replace('\\', '/').TrimStart('/');

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? Text(XElement parent, string localName)
    {
        var value = Child(parent, localName)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? Attr(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
