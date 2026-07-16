using System.Globalization;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Documentation.Views;

internal static class ViewParameterService
{
    public static string GetValue(View view, string parameterName)
    {
        var parameter = view.LookupParameter(parameterName);
        if (parameter == null) return string.Empty;

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
                return view.Document.GetElement(id)?.Name ?? id.Value.ToString(CultureInfo.InvariantCulture);
            default:
                return string.Empty;
        }
    }

    public static string? ValidateWritable(View view, string parameterName)
    {
        var parameter = view.LookupParameter(parameterName);
        if (parameter == null) return $"Parameter '{parameterName}' was not found.";
        if (parameter.IsReadOnly) return $"Parameter '{parameterName}' is read-only.";
        if (parameter.StorageType == StorageType.None)
            return $"Parameter '{parameterName}' has unsupported storage type None.";
        return null;
    }

    public static void SetValue(View view, string parameterName, string value)
    {
        var parameter = view.LookupParameter(parameterName);
        if (parameter == null || parameter.IsReadOnly)
            throw new InvalidOperationException(
                $"Parameter '{parameterName}' was not found or is read-only on view '{view.Name}'.");

        SetValue(parameter, value);
    }

    public static void SetValue(Parameter parameter, string value)
    {
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
            $"Value '{value}' is not valid for parameter '{parameter.Definition.Name}' ({parameter.StorageType}).");
    }

    public static int ApplyOverrides(
        View view,
        IReadOnlyDictionary<string, string> overrides,
        List<string> warnings)
    {
        var applied = 0;
        foreach (var pair in overrides)
        {
            try
            {
                SetValue(view, pair.Key, pair.Value);
                applied++;
            }
            catch (Exception ex)
            {
                warnings.Add($"View '{view.Name}', parameter '{pair.Key}': {ex.Message}");
            }
        }
        return applied;
    }
}
