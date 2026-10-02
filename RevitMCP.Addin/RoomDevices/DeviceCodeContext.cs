using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Tools;

namespace RevitMCP.Addin.RoomDevices;

/// <summary>The device code map loaded from the project config, with each code's family type resolved.</summary>
internal sealed class DeviceCodeContext
{
    public Dictionary<string, DeviceCode> Codes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DevicePlacementSettings Settings { get; set; } = new();
    public JsonObject? Config { get; set; }
    public string? ConfigPath { get; set; }
    public List<string> ParseErrors { get; set; } = [];

    /// <summary>
    /// Loads deviceCodes + devicePlacement from the project config. An inline "deviceCodes" argument
    /// (object) overrides the file so codes can be tried before they are saved.
    /// </summary>
    public static DeviceCodeContext? Load(Document doc, Dictionary<string, object?> args, out string? error)
    {
        error = null;
        var context = new DeviceCodeContext();

        var (config, path, readError) = ProjectConfigLocator.Read(doc, ToolArguments.GetString(args, "projectRoot"));
        context.Config = config;
        context.ConfigPath = path;

        var inline = InlineCodes(args);
        var section = inline ?? config?[DeviceCodeMap.SectionName] as JsonObject;
        if (section == null)
        {
            error = inline == null && config == null
                ? readError + " Device codes can also be passed inline as deviceCodes."
                : "The project config has no 'deviceCodes' section — set it with revit_set_device_codes.";
            return null;
        }

        context.Codes = DeviceCodeMap.Parse(section, context.ParseErrors);
        context.Settings = DevicePlacementSettings.From(config);
        var roomParameter = ToolArguments.GetString(args, "roomParameter").Trim();
        if (roomParameter.Length > 0) context.Settings.RoomParameter = roomParameter;
        return context;
    }

    public static JsonObject? InlineCodes(Dictionary<string, object?> args)
    {
        if (!args.TryGetValue("deviceCodes", out var raw) || raw == null) return null;
        try
        {
            var text = raw is string s ? s : Newtonsoft.Json.JsonConvert.SerializeObject(raw);
            return JsonNode.Parse(text) as JsonObject;
        }
        catch { return null; }
    }

    public DeviceCode? Get(string code, out string? error)
    {
        error = null;
        if (Codes.TryGetValue(code.Trim(), out var entry)) return entry;
        error = ParseErrors.Any(e => e.StartsWith(code + ":", StringComparison.OrdinalIgnoreCase))
            ? $"Device code '{code}' is invalid: {string.Join("; ", ParseErrors.Where(e => e.StartsWith(code + ":", StringComparison.OrdinalIgnoreCase)))}"
            : $"Unknown device code '{code}'. Known: {string.Join(", ", Codes.Keys.OrderBy(k => k))}";
        return null;
    }

    /// <summary>Exact (case-insensitive) family + type lookup among loaded family symbols.</summary>
    public static FamilySymbol? FindSymbol(Document doc, string family, string type) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .FirstOrDefault(s =>
                string.Equals(SafeFamilyName(s), family, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Name, type, StringComparison.OrdinalIgnoreCase));

    public static bool FamilyExists(Document doc, string family) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Any(f => string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase));

    private static string SafeFamilyName(FamilySymbol s)
    {
        try { return s.Family?.Name ?? s.FamilyName ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>Per-code model status: ok | missingType (sourceType available) | missingFamily | missingSource.</summary>
    public static object Validate(Document doc, DeviceCode code)
    {
        var symbol = FindSymbol(doc, code.Family, code.Type);
        string status;
        string message;
        long? sourceTypeId = null;

        if (symbol != null)
        {
            status = "ok";
            message = "Family type is loaded.";
        }
        else if (!FamilyExists(doc, code.Family) && code.SourceType == null)
        {
            status = "missingFamily";
            message = $"Family '{code.Family}' is not loaded in the model.";
        }
        else if (code.SourceType != null)
        {
            var (sf, st) = code.SourceFamilyAndType();
            var source = FindSymbol(doc, sf, st);
            sourceTypeId = source?.Id.Value;
            status = source != null ? "missingType" : "missingSource";
            message = source != null
                ? $"Type '{code.Type}' is missing; revit_ensure_device_types can create it from '{sf} : {st}'."
                : $"Type '{code.Type}' is missing and its sourceType '{sf} : {st}' was not found either.";
        }
        else
        {
            status = "missingType";
            message = $"Type '{code.Type}' is missing in family '{code.Family}'. Add sourceType to create it with revit_ensure_device_types.";
        }

        return new
        {
            code = code.Code,
            family = code.Family,
            type = code.Type,
            mount = code.Mount,
            heightMm = code.HeightMm,
            status,
            message,
            typeId = symbol?.Id.Value,
            sourceTypeId
        };
    }
}
