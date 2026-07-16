using System.Globalization;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Skills.Tasks.DrawingNaming;
using RevitMCP.Addin.Skills.Tasks.DrawingNaming.Models;

namespace RevitMCP.Addin.Documentation.Sheets;

internal static class SheetNamingService
{
    public static string BuildValue(
        Document doc,
        ViewSheet sheet,
        IReadOnlyList<NamingMappingToken> tokens)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in tokens
                     .Where(t => string.Equals(t.Type, "Parameter", StringComparison.OrdinalIgnoreCase))
                     .Select(t => t.Value)
                     .Where(v => !string.IsNullOrWhiteSpace(v))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            values[name] = ReadSourceValue(doc, sheet, name);
        }

        return NamingMappingEngine.BuildName(tokens, values);
    }

    public static string GetTargetValue(ViewSheet sheet, string target)
    {
        if (string.Equals(target, "Sheet Number", StringComparison.OrdinalIgnoreCase))
            return sheet.SheetNumber ?? string.Empty;
        if (string.Equals(target, "Sheet Name", StringComparison.OrdinalIgnoreCase))
            return sheet.Name ?? string.Empty;

        var parameter = sheet.LookupParameter(target);
        return parameter == null ? string.Empty : ReadParameterValue(parameter, null);
    }

    public static string? ValidateTarget(ViewSheet sheet, string target)
    {
        if (string.Equals(target, "Sheet Number", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, "Sheet Name", StringComparison.OrdinalIgnoreCase))
            return null;

        var parameter = sheet.LookupParameter(target);
        if (parameter == null) return $"Parameter '{target}' was not found.";
        if (parameter.IsReadOnly) return $"Parameter '{target}' is read-only.";
        if (parameter.StorageType == StorageType.None)
            return $"Parameter '{target}' has unsupported storage type None.";
        return null;
    }

    public static void SetTargetValue(ViewSheet sheet, string target, string value)
    {
        if (string.Equals(target, "Sheet Number", StringComparison.OrdinalIgnoreCase))
        {
            sheet.SheetNumber = value;
            return;
        }
        if (string.Equals(target, "Sheet Name", StringComparison.OrdinalIgnoreCase))
        {
            sheet.Name = value;
            return;
        }

        var parameter = sheet.LookupParameter(target);
        if (parameter == null || parameter.IsReadOnly)
            throw new InvalidOperationException($"Parameter '{target}' was not found or is read-only.");

        switch (parameter.StorageType)
        {
            case StorageType.String:
                parameter.Set(value);
                return;
            case StorageType.Integer:
                if (parameter.SetValueString(value)) return;
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                {
                    parameter.Set(intValue);
                    return;
                }
                break;
            case StorageType.Double:
                if (parameter.SetValueString(value)) return;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
                {
                    parameter.Set(doubleValue);
                    return;
                }
                break;
            case StorageType.ElementId:
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idValue))
                {
                    parameter.Set(new ElementId(idValue));
                    return;
                }
                break;
        }

        throw new InvalidOperationException(
            $"Value '{value}' is not valid for parameter '{target}' ({parameter.StorageType}).");
    }

    private static string ReadSourceValue(Document doc, ViewSheet sheet, string name)
    {
        if (string.Equals(name, "Sheet Number", StringComparison.OrdinalIgnoreCase))
            return sheet.SheetNumber?.Trim() ?? string.Empty;
        if (string.Equals(name, "Sheet Name", StringComparison.OrdinalIgnoreCase))
            return sheet.Name?.Trim() ?? string.Empty;

        var parameter = sheet.LookupParameter(name);
        if (parameter != null) return ReadParameterValue(parameter, doc);

        parameter = doc.ProjectInformation?.LookupParameter(name);
        return parameter == null ? string.Empty : ReadParameterValue(parameter, doc);
    }

    private static string ReadParameterValue(Parameter parameter, Document? doc)
    {
        var formatted = parameter.AsValueString();
        if (!string.IsNullOrWhiteSpace(formatted)) return formatted.Trim();

        switch (parameter.StorageType)
        {
            case StorageType.String:
                return parameter.AsString()?.Trim() ?? string.Empty;
            case StorageType.Integer:
                return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
            case StorageType.Double:
                return parameter.AsDouble().ToString(CultureInfo.InvariantCulture);
            case StorageType.ElementId:
                var id = parameter.AsElementId();
                if (id == ElementId.InvalidElementId) return string.Empty;
                return doc?.GetElement(id)?.Name ?? id.Value.ToString(CultureInfo.InvariantCulture);
            default:
                return string.Empty;
        }
    }
}
