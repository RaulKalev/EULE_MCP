using Autodesk.Revit.DB;
using RevitMCP.Addin.Query;

namespace RevitMCP.Addin.Families;

/// <summary>
/// Shared Revit-side helpers for the family type tools: resolving which types a request targets,
/// describing them, collecting the names already used inside a family, and writing parameter
/// values in a unit-aware way.
/// </summary>
internal static class FamilyTypeSupport
{
    /// <summary>
    /// Resolves the target types from explicit ids, falling back to a familyName/typeName match.
    /// Returns null and sets <paramref name="error"/> when the request cannot be resolved.
    /// </summary>
    public static List<ElementType>? ResolveTypes(
        Document doc,
        long[] typeIds,
        string familyName,
        string typeName,
        List<string> warnings,
        out string? error)
    {
        error = null;
        var resolved = new List<ElementType>();

        if (typeIds.Length > 0)
        {
            foreach (var id in typeIds.Distinct())
            {
                var element = doc.GetElement(new ElementId(id));
                if (element == null)
                    warnings.Add($"Type {id} was not found.");
                else if (element is not ElementType type)
                    warnings.Add($"Element {id} is a {element.GetType().Name}, not a family type — skipped.");
                else
                    resolved.Add(type);
            }

            if (resolved.Count == 0)
            {
                error = "None of the supplied typeIds resolved to a family type. " +
                        "Use revit_list_family_types to find valid type ids.";
                return null;
            }

            return resolved;
        }

        if (familyName.Length == 0 && typeName.Length == 0)
        {
            error = "Provide typeIds, or familyName and/or typeName. " +
                    "Use revit_list_family_types to find the types you want.";
            return null;
        }

        var candidates = CollectTypes(doc)
            .Where(t => Matches(SafeFamilyName(t), familyName) && Matches(SafeName(t), typeName))
            .ToList();

        // Prefer exact (case-insensitive) matches so "Standard" does not also pull in "Standard 2".
        var exact = candidates
            .Where(t => MatchesExactly(SafeFamilyName(t), familyName) && MatchesExactly(SafeName(t), typeName))
            .ToList();
        if (exact.Count > 0)
            candidates = exact;

        if (candidates.Count == 0)
        {
            error = $"No family type matches familyName='{familyName}', typeName='{typeName}'. " +
                    "Use revit_list_family_types to browse the available types.";
            return null;
        }

        if (candidates.Count > 1)
        {
            var sample = string.Join("; ", candidates.Take(10)
                .Select(t => $"{SafeFamilyName(t)} : {SafeName(t)} (typeId {t.Id.Value})"));
            error = $"{candidates.Count} family types match — narrow the name or pass typeIds. Candidates: {sample}";
            return null;
        }

        resolved.Add(candidates[0]);
        return resolved;
    }

    /// <summary>All element types in the document that carry a category, i.e. real family types.</summary>
    public static IEnumerable<ElementType> CollectTypes(Document doc) =>
        new FilteredElementCollector(doc)
            .WhereElementIsElementType()
            .OfType<ElementType>()
            .Where(t => t.Category != null);

    /// <summary>
    /// The type names already used by siblings of <paramref name="type"/>. Revit requires type names
    /// to be unique inside a loadable family, and inside a system type class for system families.
    /// </summary>
    public static HashSet<string> CollectSiblingNames(Document doc, ElementType type)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (type is FamilySymbol symbol)
        {
            try
            {
                var family = symbol.Family;
                if (family != null)
                {
                    foreach (var id in family.GetFamilySymbolIds())
                    {
                        if (doc.GetElement(id) is ElementType sibling)
                            names.Add(SafeName(sibling));
                    }
                    return names;
                }
            }
            catch { /* fall through to the category scan below */ }
        }

        try
        {
            foreach (var element in new FilteredElementCollector(doc).OfClass(type.GetType()))
                names.Add(SafeName(element));
            if (names.Count > 0)
                return names;
        }
        catch { /* OfClass rejects abstract API classes — fall back to a category scan */ }

        var categoryId = type.Category?.Id.Value;
        foreach (var sibling in CollectTypes(doc))
        {
            if (categoryId != null && sibling.Category?.Id.Value == categoryId)
                names.Add(SafeName(sibling));
        }
        return names;
    }

    /// <summary>Counts placed instances per type id, in a single pass over the model.</summary>
    public static Dictionary<long, int> CountInstancesByType(Document doc)
    {
        var counts = new Dictionary<long, int>();
        foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            ElementId typeId;
            try { typeId = element.GetTypeId(); }
            catch { continue; }

            if (typeId == null || typeId == ElementId.InvalidElementId)
                continue;

            counts.TryGetValue(typeId.Value, out var current);
            counts[typeId.Value] = current + 1;
        }
        return counts;
    }

    /// <summary>
    /// Finds the parameter a caller means by name: exact match first, then a single normalized
    /// partial match. Ambiguous partial matches are reported instead of guessed.
    /// </summary>
    public static Parameter? FindParameter(Element element, string parameterName, out string? problem)
    {
        problem = null;

        Parameter? exact = null;
        try { exact = element.LookupParameter(parameterName); }
        catch { }
        if (exact != null)
            return exact;

        var matches = new List<Parameter>();
        try
        {
            foreach (Parameter p in element.Parameters)
            {
                var name = p.Definition?.Name ?? string.Empty;
                if (ParameterMatcher.Matches(name, parameterName, "ContainsNormalized"))
                    matches.Add(p);
            }
        }
        catch { }

        if (matches.Count == 1)
            return matches[0];

        if (matches.Count > 1)
        {
            problem = $"'{parameterName}' matches {matches.Count} parameters " +
                      $"({string.Join(", ", matches.Take(5).Select(p => p.Definition?.Name ?? "?"))}). " +
                      "Use the exact parameter name.";
            return null;
        }

        problem = $"Parameter '{parameterName}' was not found on this type.";
        return null;
    }

    /// <summary>
    /// Checks — without writing — whether a parameter can take a value. Used by the preview tools.
    /// </summary>
    public static FamilyTypeParameterResult CheckParameter(Element element, string parameterName, string value)
    {
        var result = new FamilyTypeParameterResult { Name = parameterName, RequestedValue = value };

        var parameter = FindParameter(element, parameterName, out var problem);
        if (parameter == null)
        {
            result.Status = "notFound";
            result.Message = problem ?? $"Parameter '{parameterName}' was not found on this type.";
            return result;
        }

        result.Name = parameter.Definition?.Name ?? parameterName;
        result.StorageType = parameter.StorageType.ToString();
        result.CurrentValue = ReadDisplayValue(parameter);

        if (parameter.IsReadOnly)
        {
            result.Status = "readOnly";
            result.Message = "Parameter is read-only.";
            return result;
        }

        switch (parameter.StorageType)
        {
            case StorageType.Integer when !CanParseInteger(value):
                result.Status = "invalidValue";
                result.Message = "Value is not an integer or a yes/no value.";
                return result;
            case StorageType.Double when !LooksNumeric(value):
                result.Status = "invalidValue";
                result.Message = "Value does not contain a number. Pass it in project units, e.g. '200' or '200 mm'.";
                return result;
        }

        result.Status = "willSet";
        result.Message = parameter.StorageType == StorageType.Double
            ? "Will be written in project display units."
            : "Will be written.";
        return result;
    }

    /// <summary>
    /// Writes a parameter value. Doubles go through SetValueString first so the caller can pass
    /// project display units ("200", "200 mm"); the raw internal-unit path is the fallback.
    /// Must run inside an open transaction.
    /// </summary>
    public static FamilyTypeParameterResult SetParameter(
        Document doc,
        Element element,
        string parameterName,
        string value)
    {
        var result = new FamilyTypeParameterResult { Name = parameterName, RequestedValue = value };

        var parameter = FindParameter(element, parameterName, out var problem);
        if (parameter == null)
        {
            result.Status = "notFound";
            result.Message = problem ?? $"Parameter '{parameterName}' was not found on this type.";
            return result;
        }

        result.Name = parameter.Definition?.Name ?? parameterName;
        result.StorageType = parameter.StorageType.ToString();
        result.CurrentValue = ReadDisplayValue(parameter);

        if (parameter.IsReadOnly)
        {
            result.Status = "readOnly";
            result.Message = "Parameter is read-only.";
            return result;
        }

        try
        {
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    parameter.Set(value ?? string.Empty);
                    break;

                case StorageType.Integer:
                    if (!TryParseInteger(value, out var intValue))
                    {
                        result.Status = "invalidValue";
                        result.Message = "Value is not an integer or a yes/no value.";
                        return result;
                    }
                    parameter.Set(intValue);
                    break;

                case StorageType.Double:
                    if (!TrySetDouble(parameter, value, out var doubleMessage))
                    {
                        result.Status = "invalidValue";
                        result.Message = doubleMessage;
                        return result;
                    }
                    result.Message = doubleMessage;
                    break;

                case StorageType.ElementId:
                    if (!TrySetElementId(doc, parameter, value))
                    {
                        result.Status = "invalidValue";
                        result.Message = "Value is not an element id or the name of an existing element or type.";
                        return result;
                    }
                    break;

                default:
                    result.Status = "unsupported";
                    result.Message = $"Unsupported storage type: {parameter.StorageType}.";
                    return result;
            }
        }
        catch (Exception ex)
        {
            result.Status = "failed";
            result.Message = ex.Message;
            return result;
        }

        result.Status = "set";
        result.NewValue = ReadDisplayValue(parameter);
        return result;
    }

    public static string ReadDisplayValue(Parameter parameter)
    {
        try
        {
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return parameter.AsString() ?? string.Empty;
                case StorageType.ElementId:
                    return parameter.AsElementId()?.Value.ToString() ?? string.Empty;
                default:
                    var asString = parameter.AsValueString();
                    if (!string.IsNullOrEmpty(asString))
                        return asString!;
                    return parameter.StorageType == StorageType.Integer
                        ? parameter.AsInteger().ToString()
                        : parameter.AsDouble().ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch { return string.Empty; }
    }

    public static string SafeName(Element element)
    {
        try { return element.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    public static string SafeFamilyName(ElementType type)
    {
        try
        {
            if (type is FamilySymbol symbol && symbol.Family != null)
                return symbol.Family.Name ?? string.Empty;
            return type.FamilyName ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    public static string KindOf(ElementType type) => type is FamilySymbol ? "Loadable" : "System";

    private static bool TrySetDouble(Parameter parameter, string value, out string message)
    {
        var text = (value ?? string.Empty).Trim();

        // Project display units first: "200", "200 mm", "1/2\"" all round-trip through Revit's own parser.
        try
        {
            if (parameter.SetValueString(text))
            {
                message = "Written in project display units.";
                return true;
            }
        }
        catch { /* fall through to the raw internal-unit path */ }

        if (double.TryParse(text, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var raw))
        {
            parameter.Set(raw);
            message = "Revit could not parse the value as a display-unit string; " +
                      "it was written as a raw internal-unit value (feet-based).";
            return true;
        }

        message = "Value could not be parsed as a number in project units.";
        return false;
    }

    private static bool TrySetElementId(Document doc, Parameter parameter, string value)
    {
        var text = (value ?? string.Empty).Trim();

        if (long.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var numeric))
        {
            if (numeric <= 0)
            {
                parameter.Set(ElementId.InvalidElementId);
                return true;
            }
            if (doc.GetElement(new ElementId(numeric)) == null)
                return false;
            parameter.Set(new ElementId(numeric));
            return true;
        }

        if (text.Length == 0)
        {
            parameter.Set(ElementId.InvalidElementId);
            return true;
        }

        var typeMatch = new FilteredElementCollector(doc)
            .WhereElementIsElementType()
            .FirstOrDefault(e => string.Equals(SafeName(e), text, StringComparison.OrdinalIgnoreCase));
        if (typeMatch != null)
        {
            parameter.Set(typeMatch.Id);
            return true;
        }

        var instanceMatch = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .FirstOrDefault(e => string.Equals(SafeName(e), text, StringComparison.OrdinalIgnoreCase));
        if (instanceMatch != null)
        {
            parameter.Set(instanceMatch.Id);
            return true;
        }

        return false;
    }

    private static bool CanParseInteger(string value) => TryParseInteger(value, out _);

    private static bool TryParseInteger(string value, out int parsed)
    {
        var text = (value ?? string.Empty).Trim();
        if (int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out parsed))
            return true;

        // Yes/No parameters are stored as integers.
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase))
        {
            parsed = 1;
            return true;
        }
        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "no", StringComparison.OrdinalIgnoreCase))
        {
            parsed = 0;
            return true;
        }

        parsed = 0;
        return false;
    }

    private static bool LooksNumeric(string value)
    {
        foreach (var c in value ?? string.Empty)
        {
            if (char.IsDigit(c))
                return true;
        }
        return false;
    }

    private static bool Matches(string value, string filter) =>
        filter.Length == 0 || value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool MatchesExactly(string value, string filter) =>
        filter.Length == 0 || string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Outcome of a single parameter write (or planned write, in the preview tools).</summary>
internal sealed class FamilyTypeParameterResult
{
    public string Name { get; set; } = string.Empty;
    public string RequestedValue { get; set; } = string.Empty;
    public string StorageType { get; set; } = string.Empty;
    public string CurrentValue { get; set; } = string.Empty;
    public string? NewValue { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    public bool Succeeded => Status == "set";
    public bool WillSucceed => Status == "willSet";

    public object ToPayload() => new
    {
        name = Name,
        requestedValue = RequestedValue,
        storageType = StorageType,
        currentValue = CurrentValue,
        newValue = NewValue,
        status = Status,
        message = Message
    };
}
