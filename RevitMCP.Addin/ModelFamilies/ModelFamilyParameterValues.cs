using System.Globalization;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Families;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>A value of a family type, converted from the wire form (mm, degrees, names) to what Revit stores.</summary>
public sealed class ModelFamilyConvertedValue
{
    /// <summary>A string, int or double in Revit's internal units; null for a material, which is looked up by name.</summary>
    public object? Internal { get; set; }

    /// <summary>The number is in the document's display unit for this data type (electrical and slope specs); the Revit side converts.</summary>
    public bool NeedsDocumentUnit { get; set; }

    public bool IsMaterial { get; set; }

    /// <summary>The material's name, or null to clear it.</summary>
    public string? MaterialName { get; set; }

    /// <summary>A material given by element id.</summary>
    public long? MaterialId { get; set; }
}

/// <summary>
/// Converts the values of family parameters between the wire form and Revit's internal units (#172). Pure. The
/// friendly data type names are those of <see cref="Parameters.ParameterSpecMap"/>, so these tools and the Detail Item
/// and project parameter tools read a value the same way.
/// </summary>
public static class ModelFamilyParameterValues
{
    private const double SquareFeetPerSquareMetre = 10.763910416709722;
    private const double CubicFeetPerCubicMetre = 35.31466672148859;

    /// <summary>Data types whose numbers are given in the document's display unit (volts, amperes, watts, slope).</summary>
    public static readonly IReadOnlyList<string> DocumentUnitTypes = new[]
    {
        "electricalCurrent", "electricalPotential", "electricalPower", "apparentPower", "wattage", "slope"
    };

    /// <summary>The wire form's unit for a data type, for messages: "mm", "degrees", "m²", …</summary>
    public static string UnitOf(string? friendly) => friendly switch
    {
        "length" => "mm",
        "angle" => "degrees",
        "area" => "m²",
        "volume" => "m³",
        _ => string.Empty
    };

    /// <summary>What a value of this data type looks like in a request, for an error message.</summary>
    public static string ExpectedForm(string? friendly) => friendly switch
    {
        "text" or "multilineText" or "url" => "a string",
        "yesNo" => "true or false",
        "integer" or "numberOfPoles" => "a whole number",
        "length" => "a number in millimetres",
        "angle" => "a number in degrees",
        "area" => "a number in square metres",
        "volume" => "a number in cubic metres",
        "number" or "currency" => "a number",
        "material" => "the name of a material of the family (or null for none)",
        "slope" or "electricalCurrent" or "electricalPotential" or "electricalPower" or "apparentPower" or "wattage" => "a number in the document's display unit",
        _ => "a value of that data type"
    };

    /// <summary>Converts <paramref name="value"/> for a parameter of data type <paramref name="friendly"/>; an error names the expected form.</summary>
    public static (ModelFamilyConvertedValue? Value, string? Error) ToInternal(string? friendly, JToken value)
    {
        if (friendly == null)
            return (null, "values of this data type cannot be set here; leave the value out, or use a formula.");

        if (friendly == "material")
        {
            if (value.Type == JTokenType.Null || (value.Type == JTokenType.String && string.IsNullOrWhiteSpace((string?)value)))
                return (new ModelFamilyConvertedValue { IsMaterial = true }, null);
            if (value.Type == JTokenType.String)
                return (new ModelFamilyConvertedValue { IsMaterial = true, MaterialName = ((string?)value)!.Trim() }, null);
            if (value.Type == JTokenType.Integer)
                return (new ModelFamilyConvertedValue { IsMaterial = true, MaterialId = value.Value<long>() }, null);
            return (null, $"a material value must be {ExpectedForm(friendly)}.");
        }

        if (DocumentUnitTypes.Contains(friendly))
        {
            var number = ModelFamilyJson.AsNumber(value);
            return number == null
                ? (null, $"a {friendly} value must be {ExpectedForm(friendly)}.")
                : (new ModelFamilyConvertedValue { Internal = number.Value, NeedsDocumentUnit = true }, null);
        }

        if (friendly == "image")
            return (null, "an image value cannot be set here.");

        var (converted, error) = DetailFamilySpecParser.ConvertValue(friendly, value);
        if (error != null) return (null, error.Replace("leave the value out, or give a formula", "leave the value out, or use a formula"));
        return (new ModelFamilyConvertedValue { Internal = converted }, null);
    }

    /// <summary>Revit's internal number as the wire form: millimetres, degrees, square or cubic metres, else as it is.</summary>
    public static double ToWireNumber(string? friendly, double internalValue) => friendly switch
    {
        "length" => ModelFamilyJson.ToMm(internalValue),
        "angle" => Math.Round(internalValue * 180 / Math.PI, 6),
        "area" => Math.Round(internalValue / SquareFeetPerSquareMetre, 6),
        "volume" => Math.Round(internalValue / CubicFeetPerCubicMetre, 6),
        _ => Math.Round(internalValue, 9)
    };

    /// <summary>A value as the plain text a message shows.</summary>
    public static string Describe(JToken? token) =>
        token == null || token.Type == JTokenType.Null ? "(none)"
        : token.Type == JTokenType.Float ? token.Value<double>().ToString("0.######", CultureInfo.InvariantCulture)
        : token.ToString(Newtonsoft.Json.Formatting.None);
}
