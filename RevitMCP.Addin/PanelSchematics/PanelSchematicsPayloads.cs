using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RevitMCP.Addin.PanelSchematics;

/// <summary>
/// Row groups payload of the PanelSchematics plugin (its RowGroupsModel): a parent row with dependent rows.
/// {"v":1,"groups":[{"id","parent","deps":[...],"links":[...]}]}, "links" omitted when empty. Rows are identity
/// strings: "C:&lt;circuit UniqueId&gt;" or "E:&lt;empty row UniqueId&gt;". Parsing is as strict as the plugin's, so
/// nothing is written that the plugin would refuse to read. No Revit types: unit tested.
/// </summary>
public static class RowGroupsPayload
{
    public const int Version = 1;

    public sealed class Group
    {
        public string Id { get; set; } = string.Empty;
        public string Parent { get; set; } = string.Empty;
        public List<string> Dependents { get; set; } = new();
        /// <summary>UniqueIds of 2D elements that move with the parent's symbol.</summary>
        public List<string> Links { get; set; } = new();
    }

    public enum ParseStatus { Ok, Unreadable, UnsupportedVersion }

    internal static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static bool IsIdentity(string? value)
        => !string.IsNullOrWhiteSpace(value) && value!.Length > 2
           && (value.StartsWith("C:", StringComparison.Ordinal) || value.StartsWith("E:", StringComparison.Ordinal));

    public static string CircuitIdentity(string circuitUniqueId) => "C:" + circuitUniqueId;
    public static string EmptyRowIdentity(string rowUniqueId) => "E:" + rowUniqueId;

    public static ParseStatus TryParse(string? json, out List<Group> groups)
    {
        groups = new List<Group>();
        if (string.IsNullOrWhiteSpace(json))
            return ParseStatus.Unreadable;

        try
        {
            if (JsonNode.Parse(json!) is not JsonObject root)
                return ParseStatus.Unreadable;
            if (!TryWhole(root["v"], out var version))
                return ParseStatus.Unreadable;
            if (version != Version)
                return ParseStatus.UnsupportedVersion;
            if (root["groups"] is not JsonArray array)
                return ParseStatus.Unreadable;

            var parsed = new List<Group>();
            foreach (var token in array)
            {
                if (token is not JsonObject obj || !TryText(obj["id"], out var id) || !TryText(obj["parent"], out var parent)
                    || obj["deps"] is not JsonArray depsArray)
                    return ParseStatus.Unreadable;

                var group = new Group { Id = id, Parent = parent };
                foreach (var dep in depsArray)
                {
                    if (!TryText(dep, out var depId)) return ParseStatus.Unreadable;
                    group.Dependents.Add(depId);
                }

                var linksToken = obj["links"];
                if (linksToken != null)
                {
                    if (linksToken is not JsonArray linksArray) return ParseStatus.Unreadable;
                    foreach (var link in linksArray)
                    {
                        if (!TryText(link, out var linkId) || string.IsNullOrWhiteSpace(linkId)) return ParseStatus.Unreadable;
                        group.Links.Add(linkId);
                    }
                }
                parsed.Add(group);
            }

            if (Validate(parsed).Count > 0)
                return ParseStatus.Unreadable;

            groups = parsed;
            return ParseStatus.Ok;
        }
        catch (JsonException)
        {
            return ParseStatus.Unreadable;
        }
    }

    /// <summary>What the plugin would reject: duplicate group ids, a row in two groups, a row depending on itself, an empty group.</summary>
    public static List<string> Validate(IReadOnlyList<Group> groups)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rows = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var label = $"Group {i + 1}";
            if (string.IsNullOrWhiteSpace(group.Id) || !ids.Add(group.Id))
                errors.Add($"{label}: the group id is empty or used twice.");
            if (!IsIdentity(group.Parent))
                errors.Add($"{label}: parent '{group.Parent}' is not a row identity (C:<circuit UniqueId> or E:<empty row UniqueId>).");
            else if (!rows.Add(group.Parent))
                errors.Add($"{label}: row {group.Parent} is already in a group.");

            foreach (var dep in group.Dependents)
            {
                if (!IsIdentity(dep))
                    errors.Add($"{label}: dependent '{dep}' is not a row identity.");
                else if (!rows.Add(dep))
                    errors.Add($"{label}: row {dep} is already in a group (a row belongs to one group, and never depends on itself).");
            }

            if (group.Links.Any(string.IsNullOrWhiteSpace))
                errors.Add($"{label}: a linked element id is empty.");
            if (group.Dependents.Count == 0 && group.Links.Count == 0)
                errors.Add($"{label}: a group needs at least one dependent row or linked element.");
        }
        return errors;
    }

    public static string Serialize(IEnumerable<Group> groups)
    {
        var array = new JsonArray();
        foreach (var group in groups)
        {
            var obj = new JsonObject
            {
                ["id"] = group.Id,
                ["parent"] = group.Parent,
                ["deps"] = new JsonArray(group.Dependents.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray())
            };
            var links = group.Links.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.Ordinal).ToList();
            if (links.Count > 0)
                obj["links"] = new JsonArray(links.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
            array.Add(obj);
        }
        return new JsonObject { ["v"] = Version, ["groups"] = array }.ToJsonString(WriteOptions);
    }

    internal static bool TryText(JsonNode? node, out string text)
    {
        text = string.Empty;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String) return false;
        text = value.GetValue<string>();
        return true;
    }

    internal static bool TryWhole(JsonNode? node, out int number)
    {
        number = 0;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return false;
        var raw = value.ToJsonString();
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number);
    }
}

/// <summary>
/// The plugin's panel record (its PanelRecordModel): which view holds a panel's schematic, by UniqueId.
/// {"v":1,"panel":"…","view":"…","panelName":"…","viewName":"…"}; the names are diagnostics only.
/// </summary>
public static class PanelRecordPayload
{
    public const int Version = 1;

    public sealed class Record
    {
        public string PanelUniqueId { get; set; } = string.Empty;
        public string ViewUniqueId { get; set; } = string.Empty;
        public string PanelName { get; set; } = string.Empty;
        public string ViewName { get; set; } = string.Empty;
    }

    public static RowGroupsPayload.ParseStatus TryParse(string? json, out Record? record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(json))
            return RowGroupsPayload.ParseStatus.Unreadable;
        try
        {
            if (JsonNode.Parse(json!) is not JsonObject root || !RowGroupsPayload.TryWhole(root["v"], out var version))
                return RowGroupsPayload.ParseStatus.Unreadable;
            if (version != Version)
                return RowGroupsPayload.ParseStatus.UnsupportedVersion;
            if (!RowGroupsPayload.TryText(root["panel"], out var panel) || !RowGroupsPayload.TryText(root["view"], out var view)
                || string.IsNullOrWhiteSpace(panel) || string.IsNullOrWhiteSpace(view))
                return RowGroupsPayload.ParseStatus.Unreadable;

            RowGroupsPayload.TryText(root["panelName"], out var panelName);
            RowGroupsPayload.TryText(root["viewName"], out var viewName);
            record = new Record { PanelUniqueId = panel, ViewUniqueId = view, PanelName = panelName, ViewName = viewName };
            return RowGroupsPayload.ParseStatus.Ok;
        }
        catch (JsonException)
        {
            return RowGroupsPayload.ParseStatus.Unreadable;
        }
    }

    public static string Serialize(Record record) => new JsonObject
    {
        ["v"] = Version,
        ["panel"] = record.PanelUniqueId,
        ["view"] = record.ViewUniqueId,
        ["panelName"] = record.PanelName,
        ["viewName"] = record.ViewName,
    }.ToJsonString(RowGroupsPayload.WriteOptions);
}

/// <summary>
/// The plugin's Kilpide ülevaade colour configuration (its PanelColorConfig), edited in the stored JSON:
/// {"ColorLabels":[{"Id","Hex","Label"}],"PanelColors":{"&lt;panelId&gt;":"&lt;label Id&gt;"}}.
/// </summary>
public static class PanelColorsPayload
{
    public sealed class LabelEdit
    {
        /// <summary>Existing label to change; empty for a new label.</summary>
        public string? Id { get; set; }
        public string? Hex { get; set; }
        public string? Label { get; set; }
    }

    public sealed class Result
    {
        public string? Json { get; set; }
        public bool Changed { get; set; }
        public List<string> Changes { get; } = new();
        public List<string> Errors { get; } = new();
    }

    private static readonly Regex HexColor = new("^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.CultureInvariant);

    public static bool TryRead(string? storedJson, out JsonObject config, out string? error)
    {
        config = new JsonObject { ["ColorLabels"] = new JsonArray(), ["PanelColors"] = new JsonObject() };
        error = null;
        if (string.IsNullOrWhiteSpace(storedJson))
            return true;
        try
        {
            if (JsonNode.Parse(storedJson!) is not JsonObject root)
            {
                error = "The stored colour configuration is not a JSON object.";
                return false;
            }
            if (root["ColorLabels"] == null) root["ColorLabels"] = new JsonArray();
            if (root["PanelColors"] == null) root["PanelColors"] = new JsonObject();
            if (root["ColorLabels"] is not JsonArray labels || root["PanelColors"] is not JsonObject
                || labels.Any(l => l is not JsonObject o || !RowGroupsPayload.TryText(o["Id"], out _)))
            {
                error = "The stored colour configuration does not have the expected shape.";
                return false;
            }
            config = root;
            return true;
        }
        catch (JsonException ex)
        {
            error = "The stored colour configuration cannot be read: " + ex.Message;
            return false;
        }
    }

    /// <param name="assignments">Panel id (text) to label id; null or empty clears the panel's colour.</param>
    public static Result Apply(string? storedJson, IReadOnlyList<LabelEdit> labelEdits, IReadOnlyList<string> removeLabelIds,
        IReadOnlyDictionary<string, string?> assignments, Func<string>? newId = null)
    {
        var result = new Result();
        newId ??= () => Guid.NewGuid().ToString("N").Substring(0, 8);

        if (!TryRead(storedJson, out var config, out var error))
        {
            result.Errors.Add(error!);
            return result;
        }
        var labels = (JsonArray)config["ColorLabels"]!;
        var panelColors = (JsonObject)config["PanelColors"]!;
        var before = config.ToJsonString(RowGroupsPayload.WriteOptions);

        JsonObject? FindLabel(string id) => labels.OfType<JsonObject>()
            .FirstOrDefault(l => RowGroupsPayload.TryText(l["Id"], out var existing) && string.Equals(existing, id, StringComparison.Ordinal));

        foreach (var edit in labelEdits)
        {
            if (edit.Hex != null && !HexColor.IsMatch(edit.Hex.Trim()))
            {
                result.Errors.Add($"Colour '{edit.Hex}' is not #RRGGBB.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(edit.Id))
            {
                if (string.IsNullOrWhiteSpace(edit.Hex))
                {
                    result.Errors.Add("A new colour label needs hex (#RRGGBB).");
                    continue;
                }
                var id = newId();
                if (string.IsNullOrWhiteSpace(id) || FindLabel(id) != null)
                {
                    result.Errors.Add("Could not make a new colour label id.");
                    continue;
                }
                labels.Add(new JsonObject { ["Id"] = id, ["Hex"] = edit.Hex!.Trim(), ["Label"] = edit.Label ?? string.Empty });
                result.Changes.Add($"New colour label {id}: {edit.Hex!.Trim()} '{edit.Label}'.");
                continue;
            }

            var label = FindLabel(edit.Id!.Trim());
            if (label == null)
            {
                result.Errors.Add($"Colour label '{edit.Id}' does not exist. Leave id empty to add a label.");
                continue;
            }
            if (edit.Hex != null) label["Hex"] = edit.Hex.Trim();
            if (edit.Label != null) label["Label"] = edit.Label;
            result.Changes.Add($"Colour label {edit.Id}: {EmptyRowsEditor.Shown(label["Hex"])} '{EmptyRowsEditor.Shown(label["Label"])}'.");
        }

        foreach (var id in removeLabelIds.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.Ordinal))
        {
            var label = FindLabel(id);
            if (label == null)
            {
                result.Errors.Add($"Colour label '{id}' does not exist.");
                continue;
            }
            labels.Remove(label);
            var users = panelColors.Where(p => RowGroupsPayload.TryText(p.Value, out var used) && used == id).Select(p => p.Key).ToList();
            foreach (var panel in users) panelColors.Remove(panel);
            result.Changes.Add($"Removed colour label {id}" + (users.Count > 0 ? $" and cleared it from {users.Count} panel(s)." : "."));
        }

        foreach (var assignment in assignments)
        {
            if (string.IsNullOrWhiteSpace(assignment.Value))
            {
                if (panelColors.Remove(assignment.Key))
                    result.Changes.Add($"Panel {assignment.Key}: colour cleared.");
                continue;
            }
            var labelId = assignment.Value!.Trim();
            if (FindLabel(labelId) == null)
            {
                result.Errors.Add($"Panel {assignment.Key}: colour label '{labelId}' does not exist.");
                continue;
            }
            panelColors[assignment.Key] = labelId;
            result.Changes.Add($"Panel {assignment.Key}: colour label {labelId}.");
        }

        if (result.Errors.Count > 0)
            return result;

        result.Json = config.ToJsonString(RowGroupsPayload.WriteOptions);
        result.Changed = !string.Equals(before, result.Json, StringComparison.Ordinal);
        return result;
    }
}
