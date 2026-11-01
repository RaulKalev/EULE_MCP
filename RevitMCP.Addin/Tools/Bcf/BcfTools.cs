using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Coordination.Bcf;
using RevitMCP.Addin.Coordination.Clash.DTOs;
using RevitMCP.Addin.Coordination.Clash.Geometry;
using RevitMCP.Addin.Coordination.Clash.Services;
using RevitMCP.Addin.Coordination.ClashFix;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Bcf;

/// <summary>Shared argument handling of the BCF tools.</summary>
internal static class BcfToolSupport
{
    public static BcfFile? Open(McpToolRequest request, out McpToolResult? failure)
    {
        failure = null;
        var path = ToolArguments.GetString(request.Arguments, "path").Trim().Trim('"');
        if (path.Length == 0)
        {
            failure = Fail(request, "path is required: the full path of a .bcf or .bcfzip file.");
            return null;
        }
        if (!File.Exists(path))
        {
            failure = Fail(request, $"BCF file not found: {path}");
            return null;
        }

        try
        {
            return BcfReader.Read(path);
        }
        catch (InvalidDataException ex)
        {
            failure = Fail(request, $"'{path}' is not a zip file, so not a BCF file: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            failure = Fail(request, $"The BCF file could not be read: {ex.Message}");
            return null;
        }
    }

    public static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Status = "validation_failed", Message = message };

    public static Dictionary<string, int> CountBy(IEnumerable<string?> values) =>
        values
            .GroupBy(v => string.IsNullOrWhiteSpace(v) ? "(none)" : v!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>Lists the topics of a BCF file, or one topic in full. Needs no model.</summary>
public class BcfReadTool : IRevitMcpTool
{
    public string Name => "revit_bcf_read";

    public string Description =>
        "Reads a BCF file (2.0, 2.1 or 3.0 .bcf/.bcfzip) from a clash or issue tool (Navisworks, Solibri, BIMcollab, " +
        "Revizto, ACC). Without topic: every topic with number, guid, title, status, type, priority, assignee, labels, " +
        "comment and viewpoint counts and how many elements its viewpoint selects, plus counts by status, type and " +
        "priority. With topic (number, BCF-0003, Index or guid): the topic in full — description, comments, viewpoints " +
        "with camera, clipping planes and the selected components (IfcGuid, AuthoringToolId). extractSnapshots=true " +
        "writes the snapshots to a temp folder and returns their paths. Does not touch the model; revit_bcf_check " +
        "matches the topics to Revit elements.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Coordination;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var file = BcfToolSupport.Open(request, out var failure);
        if (file == null) return Task.FromResult(failure!);

        var args = request.Arguments;
        var topicKey = ToolArguments.GetString(args, "topic").Trim();
        var limit = Math.Max(1, ToolArguments.GetInt(args, "limit", 200));
        var extractSnapshots = ToolArguments.GetBool(args, "extractSnapshots");
        var warnings = new List<string>(file.Warnings);

        var topics = BcfTopicSelector.Select(file.Topics, topicKey.Length > 0 ? new[] { topicKey } : Array.Empty<string>(), out var unknown);
        if (unknown.Count > 0)
            return Task.FromResult(BcfToolSupport.Fail(request, $"Topic '{topicKey}' is not in the file (it has {file.Topics.Count} topics, numbered 1-{file.Topics.Count})."));

        var snapshotFolder = extractSnapshots ? SnapshotFolder(file.Path) : null;
        var snapshots = snapshotFolder == null
            ? new Dictionary<string, string>()
            : ExtractSnapshots(file, topics.Take(limit).ToList(), snapshotFolder, warnings);

        object data;
        if (topicKey.Length > 0)
        {
            var topic = topics[0];
            data = new
            {
                file = file.Path,
                version = file.Version,
                topic = Detail(topic, snapshots)
            };
        }
        else
        {
            if (file.Topics.Count > limit) warnings.Add($"Listed the first {limit} of {file.Topics.Count} topics; raise limit for more.");
            data = new
            {
                file = file.Path,
                version = file.Version,
                project = file.ProjectName,
                topicCount = file.Topics.Count,
                byStatus = BcfToolSupport.CountBy(file.Topics.Select(t => t.TopicStatus)),
                byType = BcfToolSupport.CountBy(file.Topics.Select(t => t.TopicType)),
                byPriority = BcfToolSupport.CountBy(file.Topics.Select(t => t.Priority)),
                snapshotFolder,
                topics = topics.Take(limit).Select(t => Summary(t, snapshots)).ToList()
            };
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = topicKey.Length > 0
                ? $"Topic {topics[0].Number} of {file.Topics.Count}: {topics[0].Title}"
                : $"{file.Topics.Count} topic(s) in {Path.GetFileName(file.Path)} (BCF {file.Version ?? "?"}).",
            Data = data,
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static object Summary(BcfTopic topic, Dictionary<string, string> snapshots)
    {
        var (components, source) = BcfClashComponents.Of(topic);
        return new
        {
            number = topic.Number,
            clashId = BcfTopicSelector.ClashIdOf(topic),
            guid = topic.Guid,
            index = topic.Index,
            title = topic.Title,
            status = topic.TopicStatus,
            type = topic.TopicType,
            priority = topic.Priority,
            assignedTo = topic.AssignedTo,
            labels = topic.Labels.Count > 0 ? topic.Labels : null,
            created = topic.CreationDate,
            author = topic.CreationAuthor,
            comments = topic.Comments.Count,
            viewpoints = topic.Viewpoints.Count,
            elements = components.Count,
            elementsFrom = source,
            snapshot = snapshots.TryGetValue(topic.Guid, out var path) ? path : null
        };
    }

    private static object Detail(BcfTopic topic, Dictionary<string, string> snapshots)
    {
        var (components, source) = BcfClashComponents.Of(topic);
        return new
        {
            number = topic.Number,
            clashId = BcfTopicSelector.ClashIdOf(topic),
            guid = topic.Guid,
            index = topic.Index,
            title = topic.Title,
            status = topic.TopicStatus,
            type = topic.TopicType,
            priority = topic.Priority,
            stage = topic.Stage,
            assignedTo = topic.AssignedTo,
            labels = topic.Labels,
            description = topic.Description,
            created = topic.CreationDate,
            author = topic.CreationAuthor,
            modified = topic.ModifiedDate,
            due = topic.DueDate,
            files = topic.Files,
            elementsFrom = source,
            elements = components.Select(c => new { ifcGuid = c.IfcGuid, authoringToolId = c.AuthoringToolId, originatingSystem = c.OriginatingSystem }),
            snapshot = snapshots.TryGetValue(topic.Guid, out var path) ? path : null,
            comments = topic.Comments.Select(c => new { date = c.Date, author = c.Author, text = c.Text, viewpoint = c.ViewpointGuid }),
            viewpoints = topic.Viewpoints.Select(v => new
            {
                guid = v.Guid,
                snapshot = v.SnapshotFileName,
                camera = v.Camera == null ? null : new
                {
                    kind = v.Camera.Kind,
                    position = v.Camera.ViewPoint,
                    direction = v.Camera.Direction,
                    up = v.Camera.UpVector,
                    fieldOfView = v.Camera.FieldOfView,
                    viewToWorldScale = v.Camera.ViewToWorldScale,
                    unit = "m (IFC world coordinates)"
                },
                clippingPlanes = v.ClippingPlanes,
                selected = v.Selection.Count,
                defaultVisibility = v.DefaultVisibility,
                visibilityExceptions = v.VisibilityExceptions.Count,
                colored = v.Colored.Count
            })
        };
    }

    private static string SnapshotFolder(string bcfPath)
    {
        var name = string.Concat(Path.GetFileNameWithoutExtension(bcfPath).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(Path.GetTempPath(), "RevitMCP", "bcf", name);
    }

    /// <summary>Topic guid → extracted snapshot path, for the first viewpoint with a snapshot.</summary>
    private static Dictionary<string, string> ExtractSnapshots(BcfFile file, List<BcfTopic> topics, string folder, List<string> warnings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Directory.CreateDirectory(folder);
            using var zip = new ZipArchive(File.OpenRead(file.Path), ZipArchiveMode.Read);
            var entries = zip.Entries
                .GroupBy(e => e.FullName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var topic in topics)
            {
                var viewpoint = topic.Viewpoints.FirstOrDefault(v => v.SnapshotFileName != null);
                if (viewpoint == null || !entries.TryGetValue(topic.Folder + "/" + viewpoint.SnapshotFileName, out var entry)) continue;
                var extension = Path.GetExtension(viewpoint.SnapshotFileName);
                var target = Path.Combine(folder, $"{topic.Number:D4}{(string.IsNullOrEmpty(extension) ? ".png" : extension)}");
                using (var input = entry.Open())
                using (var output = File.Create(target))
                    input.CopyTo(output);
                result[topic.Guid] = target;
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Snapshots could not be extracted: {ex.Message}");
        }
        return result;
    }
}

/// <summary>
/// Matches BCF topics to elements of the host and its loaded links, re-tests each clash with the elements' solids and
/// stores the matched topics as the document's clash run (clash ids BCF-0001 …), so the clash review tools work on them.
/// </summary>
public class BcfCheckTool : IRevitMcpTool
{
    public string Name => "revit_bcf_check";

    public string Description =>
        "Matches the topics of a BCF file to Revit elements and re-tests each clash against the live model. Elements are " +
        "matched in the host and its loaded links by IFC GlobalId (the stored IfcGUID parameter or Revit's export id) and " +
        "the component's AuthoringToolId (Low confidence when only the id matched). Per topic: check = still_clashing " +
        "(solids still intersect, with the volume), cleared (both elements found, no longer intersecting), partial (only " +
        "some elements found), not_found, single_element or no_components, with the matched elements. Topics with two " +
        "matched elements are saved as the document's clash run (replacing the last run) under clash ids BCF-0001 …, so " +
        "revit_focus_clash, revit_select_clash_elements, revit_get_adjacent_clash, revit_export_clash_report_to_excel " +
        "and revit_propose_clash_fixes work on them. Optional: topics (numbers, BCF ids or guids; default all), " +
        "toleranceMm (default 5), saveAsLastRun (default true), limit (default 500). Does not change the model.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Coordination;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;

        var file = BcfToolSupport.Open(request, out var failure);
        if (file == null) return Task.FromResult(failure!);

        var args = request.Arguments;
        var toleranceMm = ToolArguments.GetDouble(args, "toleranceMm", 5.0);
        var saveAsLastRun = ToolArguments.GetBool(args, "saveAsLastRun", true);
        var limit = Math.Max(1, ToolArguments.GetInt(args, "limit", 500));
        var warnings = new List<string>(file.Warnings);

        var topics = BcfTopicSelector.Select(file.Topics, ToolArguments.GetStringArray(args, "topics"), out var unknown);
        if (unknown.Count > 0) warnings.Add($"Not in the file: {string.Join(", ", unknown)}.");
        if (topics.Count > limit)
        {
            warnings.Add($"Checked the first {limit} of {topics.Count} topics; raise limit or pass topics for the rest.");
            topics = topics.Take(limit).ToList();
        }

        var resolver = new BcfElementResolver(doc);
        if (resolver.UnloadedLinks.Count > 0)
            warnings.Add($"Links not loaded, so their elements cannot be matched: {string.Join(", ", resolver.UnloadedLinks)}.");

        var toleranceMm3 = ClashGeometry.ToleranceVolumeMm3(toleranceMm);
        var rows = new List<object>();
        var clashes = new List<ClashResultDto>();
        var checks = new List<string>();

        foreach (var topic in topics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (components, source) = BcfClashComponents.Of(topic);
            var resolved = components.Select(resolver.Resolve).ToList();
            var placed = resolved.Where(r => r.Placed != null)
                .GroupBy(r => r.Placed!.Key)
                .Select(g => g.First())
                .ToList();

            string check;
            double? volume = null;
            ResolvedComponent? first = null, second = null;
            if (components.Count == 0) check = "no_components";
            else if (placed.Count == 0) check = "not_found";
            else if (components.Count == 1) check = "single_element";
            else if (placed.Count == 1) check = "partial";
            else
            {
                // The pair that intersects most (a clash has two elements; a group topic may list more).
                var solids = placed.ToDictionary(p => p.Placed!.Key, p => ClashGeometry.Solids(p.Placed!));
                var failures = 0;
                for (var i = 0; i < placed.Count; i++)
                {
                    for (var j = i + 1; j < placed.Count; j++)
                    {
                        var v = ClashGeometry.IntersectionVolumeMm3(solids[placed[i].Placed!.Key], solids[placed[j].Placed!.Key], out var f);
                        failures += f;
                        if (first == null || v > volume)
                        {
                            volume = v;
                            first = placed[i];
                            second = placed[j];
                        }
                    }
                }
                check = volume > toleranceMm3 ? "still_clashing"
                    : failures > 0 ? "unknown"
                    : "cleared";
                if (placed.Count < components.Count) check += "_partial";
            }
            checks.Add(check);

            var clashId = BcfTopicSelector.ClashIdOf(topic);
            if (first != null && second != null)
            {
                // The host element first: the review tools select a linked element through its link instance.
                if (!first.Placed!.IsHost && second.Placed!.IsHost) (first, second) = (second, first);
                clashes.Add(ToClash(clashId, file, topic, first.Placed!, second.Placed!, volume, check,
                    first.Confidence == "Low" || second.Confidence == "Low"));
            }

            rows.Add(new
            {
                clashId,
                number = topic.Number,
                guid = topic.Guid,
                title = topic.Title,
                topicStatus = topic.TopicStatus,
                priority = topic.Priority,
                check,
                intersectionVolumeMm3 = volume == null ? (double?)null : Math.Round(volume.Value),
                lowConfidence = resolved.Any(r => r.Confidence == "Low") ? true : (bool?)null,
                elementsFrom = source,
                elements = resolved.Select(r => r.Describe()).ToList()
            });
        }

        string? runId = null;
        if (saveAsLastRun && clashes.Count > 0)
        {
            var cache = ClashRunScope.Cache(uiapp, request);
            if (cache.Load() is { } previous && !previous.Clashes.Any(c => c.BcfFile != null))
                warnings.Add($"Replaced the document's last clash run ({previous.TotalClashes} detected clashes, run {previous.RunId}).");
            runId = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
            cache.Save(new ClashRunResultDto
            {
                RunId = runId,
                RunAt = DateTime.Now,
                PresetName = "BCF: " + Path.GetFileName(file.Path),
                TotalClashes = clashes.Count,
                Clashes = clashes,
                Warnings = warnings.ToList(),
                CountByRule = new Dictionary<string, int> { [Path.GetFileName(file.Path)] = clashes.Count }
            });
        }

        if (resolver.IndexedModels > 0)
            warnings.Add($"{resolver.IndexedModels} model(s) were indexed by IFC GlobalId (the AuthoringToolId did not confirm every element).");

        var summary = BcfToolSupport.CountBy(checks);
        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Checked {topics.Count} topic(s): " + string.Join(", ", summary.Select(p => $"{p.Value} {p.Key}")) +
                      (runId != null ? $". Saved {clashes.Count} as clash run {runId} (clash ids BCF-xxxx)." : "."),
            Data = new
            {
                file = file.Path,
                document = doc.Title,
                summary,
                runId,
                topics = rows,
                next = runId == null ? null :
                    "revit_focus_clash clashId=BCF-xxxx to look at one; revit_propose_clash_fixes for the still_clashing ones; " +
                    "publish the list with revit_publish_page (rows with data-revit-id) for the user to go through."
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static ClashResultDto ToClash(
        string clashId, BcfFile file, BcfTopic topic, PlacedElement source, PlacedElement target, double? volume, string check, bool lowConfidence)
    {
        var sourceBox = HostBox(source);
        var targetBox = HostBox(target);
        ClashLocationDto? location = null;
        if (sourceBox != null && targetBox != null)
        {
            var (x, y, z) = ClashLocationResolver.ResolveMeters(sourceBox, targetBox);
            location = new ClashLocationDto { X = x, Y = y, Z = z };
        }

        return new ClashResultDto
        {
            ClashId = clashId,
            RuleName = Path.GetFileName(file.Path),
            ClashType = "HardClash",
            Severity = BcfTopicSelector.SeverityOf(topic.Priority),
            Source = source.ToRef(),
            Target = target.ToRef(),
            Location = location,
            Level = LevelName(source) ?? LevelName(target),
            IntersectionVolume = volume,
            DetectionMethod = "BcfSolidCheck",
            Confidence = lowConfidence ? "Low" : "High",
            Status = check.StartsWith("still_clashing", StringComparison.Ordinal) ? "Active"
                : check.StartsWith("cleared", StringComparison.Ordinal) ? "Resolved"
                : "Unknown",
            Message = topic.Title,
            BcfTopicGuid = topic.Guid,
            BcfFile = file.Path
        };
    }

    private static BoundingBoxXYZ? HostBox(PlacedElement placed)
    {
        var box = placed.Element.get_BoundingBox(null);
        return box == null ? null : ClashTransformHelper.TransformBoundingBox(box, placed.ToHost);
    }

    private static string? LevelName(PlacedElement placed) =>
        placed.IsHost && placed.Element.LevelId != ElementId.InvalidElementId
            ? placed.Document.GetElement(placed.Element.LevelId)?.Name
            : null;
}
