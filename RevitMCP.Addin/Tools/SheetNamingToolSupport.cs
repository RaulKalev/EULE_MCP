using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Skills.Tasks.DrawingNaming.Models;

namespace RevitMCP.Addin.Tools;

internal static class SheetNamingToolSupport
{
    public static List<NamingMappingToken> GetTokens(Dictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("tokens", out var raw) || raw == null)
            return new List<NamingMappingToken>();

        try
        {
            var array = raw switch
            {
                JArray jArray => jArray,
                string text => JArray.Parse(text),
                _ => JArray.Parse(JsonConvert.SerializeObject(raw))
            };
            return array.ToObject<List<NamingMappingToken>>() ?? new List<NamingMappingToken>();
        }
        catch
        {
            return new List<NamingMappingToken>();
        }
    }

    public static List<ViewSheet> ResolveSheets(
        Document doc,
        Dictionary<string, object?> arguments,
        out string? error)
    {
        var sheetIds = ToolArguments.GetLongArray(arguments, "sheetIds");
        var sheetNumbers = ToolArguments.GetStringArray(arguments, "sheetNumbers");
        var nameFilter = ToolArguments.GetString(arguments, "nameFilter");
        var numberFilter = ToolArguments.GetString(arguments, "numberFilter");
        var allSheets = ToolArguments.GetBool(arguments, "allSheets", false);

        if (sheetIds.Length == 0 && sheetNumbers.Length == 0 &&
            string.IsNullOrWhiteSpace(nameFilter) && string.IsNullOrWhiteSpace(numberFilter) && !allSheets)
        {
            error = "Provide sheetIds, sheetNumbers, nameFilter, numberFilter, or allSheets=true.";
            return new List<ViewSheet>();
        }

        IEnumerable<ViewSheet> sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(s => !s.IsPlaceholder);

        if (sheetIds.Length > 0)
        {
            var ids = sheetIds.ToHashSet();
            sheets = sheets.Where(s => ids.Contains(s.Id.Value));
        }
        else if (sheetNumbers.Length > 0)
        {
            var numbers = sheetNumbers.Select(n => n.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            sheets = sheets.Where(s => numbers.Contains(s.SheetNumber));
        }

        if (!string.IsNullOrWhiteSpace(nameFilter))
            sheets = sheets.Where(s => s.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(numberFilter))
            sheets = sheets.Where(s => s.SheetNumber.Contains(numberFilter, StringComparison.OrdinalIgnoreCase));

        error = null;
        return sheets.ToList();
    }
}
