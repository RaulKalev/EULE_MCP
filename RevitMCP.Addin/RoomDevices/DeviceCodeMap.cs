using System.Text.Json.Nodes;

namespace RevitMCP.Addin.RoomDevices;

/// <summary>Mount kinds a device code can declare.</summary>
public static class DeviceMounts
{
    public const string Wall = "wall";
    public const string Ceiling = "ceiling";
    public const string Floor = "floor";

    public static readonly string[] All = [Wall, Ceiling, Floor];

    public static string? Normalize(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return All.Contains(v) ? v : null;
    }
}

/// <summary>
/// One entry of the project's device code map: which family type a code means and how it is mounted.
/// All lengths in mm, angles in degrees.
/// </summary>
public sealed class DeviceCode
{
    public string Code { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Mount { get; set; } = DeviceMounts.Wall;

    /// <summary>Wall/floor devices: height of the insertion point above the level.</summary>
    public double? HeightMm { get; set; }
    /// <summary>Wall devices: distance from the finished wall face into the room.</summary>
    public double OffsetFromWallMm { get; set; }
    /// <summary>Ceiling devices: distance below the ceiling.</summary>
    public double OffsetFromCeilingMm { get; set; }
    /// <summary>Default door side for nearDoor placement: lock | hinge.</summary>
    public string DoorSide { get; set; } = "lock";
    /// <summary>Clearance between the door edge and the device for nearDoor placement.</summary>
    public double DoorOffsetMm { get; set; } = 150;
    /// <summary>Added to the computed rotation when the family's front is not its facing direction.</summary>
    public double RotationOffsetDeg { get; set; }

    /// <summary>Linked-model categories the device must not sit over (ceiling devices).</summary>
    public List<string> AvoidCategories { get; set; } = [];
    public double ClearanceMm { get; set; } = 300;

    /// <summary>Grid defaults for place_in_room strategy=grid.</summary>
    public double? MaxSpacingMm { get; set; }
    public double? MaxDistFromWallMm { get; set; }

    /// <summary>Type to duplicate when <see cref="Type"/> is missing: "Type" (same family) or "Family : Type".</summary>
    public string? SourceType { get; set; }
    /// <summary>Type parameter values written by revit_ensure_device_types on a created type.</summary>
    public Dictionary<string, string> TypeParameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsWall => Mount == DeviceMounts.Wall;
    public bool IsCeiling => Mount == DeviceMounts.Ceiling;

    /// <summary>Parses "Family : Type" or a bare type name into (family, type).</summary>
    public (string Family, string Type) SourceFamilyAndType()
    {
        var source = (SourceType ?? string.Empty).Trim();
        var sep = source.IndexOf(" : ", StringComparison.Ordinal);
        if (sep < 0) sep = source.IndexOf(':');
        if (sep < 0) return (Family, source);
        var family = source.Substring(0, sep).Trim();
        var type = source.Substring(sep + (source[sep] == ' ' ? 3 : 1)).Trim();
        return (family.Length == 0 ? Family : family, type);
    }
}

/// <summary>
/// The "deviceCodes" section of the project config, keyed by code (case-insensitive).
/// No Revit API dependency — unit tested in RevitMCP.Tests.
/// </summary>
public static class DeviceCodeMap
{
    public const string SectionName = "deviceCodes";
    public const string SettingsSectionName = "devicePlacement";

    /// <summary>
    /// Parses a deviceCodes object. Invalid entries are reported in <paramref name="errors"/> and left
    /// out of the result; unknown properties are ignored.
    /// </summary>
    public static Dictionary<string, DeviceCode> Parse(JsonObject? section, List<string> errors)
    {
        var map = new Dictionary<string, DeviceCode>(StringComparer.OrdinalIgnoreCase);
        if (section == null) return map;

        foreach (var pair in section)
        {
            var code = pair.Key.Trim();
            if (pair.Value is not JsonObject obj)
            {
                errors.Add($"{code}: entry must be an object.");
                continue;
            }

            var entry = ParseEntry(code, obj, errors);
            if (entry != null) map[code] = entry;
        }

        return map;
    }

    public static DeviceCode? ParseEntry(string code, JsonObject obj, List<string> errors)
    {
        var before = errors.Count;
        var entry = new DeviceCode
        {
            Code = code,
            Family = Str(obj, "family"),
            Type = Str(obj, "type"),
            HeightMm = Num(obj, "heightMm"),
            OffsetFromWallMm = Num(obj, "offsetFromWallMm") ?? 0,
            OffsetFromCeilingMm = Num(obj, "offsetFromCeilingMm") ?? 0,
            DoorOffsetMm = Num(obj, "doorOffsetMm") ?? 150,
            RotationOffsetDeg = Num(obj, "rotationOffsetDeg") ?? 0,
            ClearanceMm = Num(obj, "clearanceMm") ?? 300,
            MaxSpacingMm = Num(obj, "maxSpacingMm"),
            MaxDistFromWallMm = Num(obj, "maxDistFromWallMm"),
            SourceType = Str(obj, "sourceType") is { Length: > 0 } s ? s : null
        };

        if (code.Length == 0) errors.Add("A device code is empty.");
        if (entry.Family.Length == 0) errors.Add($"{code}: 'family' is required.");
        if (entry.Type.Length == 0) errors.Add($"{code}: 'type' is required.");

        var mount = DeviceMounts.Normalize(Str(obj, "mount", DeviceMounts.Wall));
        if (mount == null)
            errors.Add($"{code}: 'mount' must be one of {string.Join(", ", DeviceMounts.All)}.");
        else
            entry.Mount = mount;

        if (entry.IsWall && entry.HeightMm == null)
            errors.Add($"{code}: wall-mounted codes need 'heightMm' (height above the level).");

        var doorSide = Str(obj, "doorSide", "lock").ToLowerInvariant();
        if (doorSide is not ("lock" or "hinge"))
            errors.Add($"{code}: 'doorSide' must be 'lock' or 'hinge'.");
        else
            entry.DoorSide = doorSide;

        foreach (var (name, value) in new[]
                 {
                     ("heightMm", entry.HeightMm), ("offsetFromWallMm", (double?)entry.OffsetFromWallMm),
                     ("offsetFromCeilingMm", entry.OffsetFromCeilingMm), ("doorOffsetMm", entry.DoorOffsetMm),
                     ("clearanceMm", entry.ClearanceMm), ("maxDistFromWallMm", entry.MaxDistFromWallMm)
                 })
        {
            if (value is < 0) errors.Add($"{code}: '{name}' cannot be negative.");
        }
        if (entry.MaxSpacingMm is <= 0) errors.Add($"{code}: 'maxSpacingMm' must be positive.");

        if (obj["avoidCategories"] is JsonArray avoid)
        {
            entry.AvoidCategories = avoid
                .Select(n => n?.ToString().Trim() ?? string.Empty)
                .Where(s => s.Length > 0)
                .ToList();
        }

        if (obj["typeParameters"] is JsonObject parameters)
        {
            foreach (var p in parameters)
                entry.TypeParameters[p.Key] = p.Value?.ToString() ?? string.Empty;
        }

        return errors.Count == before ? entry : null;
    }

    /// <summary>
    /// Merges <paramref name="updates"/> into <paramref name="existing"/> (a code in updates replaces
    /// the whole entry), removes <paramref name="remove"/>, and returns the new section.
    /// With <paramref name="replace"/> the existing section is discarded first.
    /// </summary>
    public static JsonObject Merge(JsonObject? existing, JsonObject updates, IEnumerable<string> remove, bool replace)
    {
        var result = new JsonObject();
        if (!replace && existing != null)
        {
            foreach (var pair in existing)
                result[pair.Key] = pair.Value?.DeepClone();
        }

        foreach (var pair in updates)
        {
            var key = FindKey(result, pair.Key) ?? pair.Key;
            result.Remove(key);
            result[pair.Key] = pair.Value?.DeepClone();
        }

        foreach (var code in remove)
        {
            var key = FindKey(result, code);
            if (key != null) result.Remove(key);
        }

        return result;
    }

    private static string? FindKey(JsonObject obj, string code) =>
        obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, code, StringComparison.OrdinalIgnoreCase));

    private static string Str(JsonObject obj, string name, string fallback = "")
    {
        var node = obj[name];
        if (node == null) return fallback;
        try { return node.GetValue<string>().Trim(); }
        catch { return node.ToString().Trim(); }
    }

    private static double? Num(JsonObject obj, string name)
    {
        var node = obj[name];
        if (node == null) return null;
        try { return node.GetValue<double>(); }
        catch
        {
            return double.TryParse(node.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        }
    }
}
