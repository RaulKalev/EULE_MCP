using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>Which family document a model-family tool works on (#168).</summary>
public enum ModelFamilyTargetKind
{
    None,
    /// <summary>A family loaded in the project: edited as a copy and loaded back.</summary>
    LoadedFamily,
    /// <summary>An .rfa file that is not open in Revit: opened in the background, saved back or to savePath.</summary>
    File,
    /// <summary>A family document open in Revit: edited in place, saved only when asked.</summary>
    OpenDocument,
    /// <summary>A new family from a template (.rft), saved to savePath.</summary>
    Template
}

/// <summary>What a session may do with its target.</summary>
public enum ModelFamilySessionMode
{
    /// <summary>Reads only: nothing is saved or loaded, so no save arguments are needed.</summary>
    Read,
    /// <summary>Edits an existing family.</summary>
    Edit,
    /// <summary>Creates a family from a template; an existing family is not a valid target.</summary>
    Create
}

/// <summary>
/// The <c>target</c> argument every model-family tool takes:
/// <c>{family | path | familyDocument, savePath?, overwrite?, save?, load?, overwriteParameterValues?}</c>, and for a
/// new family <c>{templatePath?, familyName?, savePath}</c>. Pure: parsing and the rules that need no Revit.
/// </summary>
public sealed class ModelFamilyTargetSpec
{
    public const string DefaultTemplateFileName = "Metric Generic Model.rft";

    public ModelFamilyTargetKind Kind { get; set; }
    /// <summary>Name or element id of a family loaded in the project.</summary>
    public string Family { get; set; } = string.Empty;
    /// <summary>Full path of an .rfa file.</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>Title of an open family document, or "active".</summary>
    public string FamilyDocument { get; set; } = string.Empty;
    public string TemplatePath { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    /// <summary>A folder or an .rfa path; empty = none.</summary>
    public string SavePath { get; set; } = string.Empty;
    public bool Overwrite { get; set; }
    /// <summary>Open document only: save it to its own file after the edit.</summary>
    public bool Save { get; set; }
    /// <summary>File, open document and template: load the result into the project. A loaded family is always loaded back.</summary>
    public bool Load { get; set; }
    public bool OverwriteParameterValues { get; set; }

    public static readonly string[] KnownKeys =
    {
        "family", "path", "familyDocument", "templatePath", "familyName", "savePath", "overwrite", "save", "load",
        "overwriteParameterValues"
    };

    /// <summary>
    /// Reads <paramref name="target"/> (an object, or a JSON string of one). Problems start with "target" so a caller
    /// can tell them from problems of the edit itself.
    /// </summary>
    public static ModelFamilyTargetSpec Parse(object? target, ModelFamilySessionMode mode, List<string> problems)
    {
        var spec = new ModelFamilyTargetSpec();
        var obj = AsObject(target, out var parseError);
        if (parseError != null)
        {
            problems.Add($"target: {parseError}");
            return spec;
        }
        if (obj == null)
        {
            problems.Add(mode == ModelFamilySessionMode.Create
                ? "target: give {savePath, familyName?, templatePath?} for the new family."
                : "target: give {family} (a family loaded in the project), {path} (an .rfa file) or {familyDocument} (the title of an open family document, or \"active\").");
            return spec;
        }

        foreach (var property in obj.Properties())
            if (!KnownKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                problems.Add($"target.{property.Name}: unknown key. Known: {string.Join(", ", KnownKeys)}.");

        spec.Family = Str(obj, "family");
        spec.Path = Str(obj, "path");
        spec.FamilyDocument = Str(obj, "familyDocument");
        spec.TemplatePath = Str(obj, "templatePath");
        spec.FamilyName = Str(obj, "familyName");
        spec.SavePath = Str(obj, "savePath");
        spec.Overwrite = Bool(obj, "overwrite", problems);
        spec.Save = Bool(obj, "save", problems);
        spec.Load = Bool(obj, "load", problems);
        spec.OverwriteParameterValues = Bool(obj, "overwriteParameterValues", problems);

        var given = new List<string>();
        if (spec.Family.Length > 0) given.Add("family");
        if (spec.Path.Length > 0) given.Add("path");
        if (spec.FamilyDocument.Length > 0) given.Add("familyDocument");

        if (mode == ModelFamilySessionMode.Create)
        {
            if (given.Count > 0)
                problems.Add($"target.{given[0]}: a new family takes no existing target; give savePath (and templatePath, familyName).");
            if (spec.SavePath.Length == 0)
                problems.Add("target.savePath is required: ask the user where to save the family (a folder with familyName, or the full .rfa path). There is no default location.");
            if (spec.TemplatePath.Length > 0 && !spec.TemplatePath.EndsWith(".rft", StringComparison.OrdinalIgnoreCase))
                problems.Add($"target.templatePath '{spec.TemplatePath}' is not a family template (.rft).");
            if (spec.Save) problems.Add("target.save applies to an open family document only; a new family is saved to savePath.");
            spec.Kind = ModelFamilyTargetKind.Template;
            return spec;
        }

        if (spec.TemplatePath.Length > 0)
            problems.Add("target.templatePath: only the create tool starts from a template.");
        if (given.Count == 0)
        {
            problems.Add("target: give family (a family loaded in the project), path (an .rfa file) or familyDocument (the title of an open family document, or \"active\").");
            return spec;
        }
        if (given.Count > 1)
        {
            problems.Add($"target: give one of family, path and familyDocument, not {string.Join(" and ", given)}.");
            return spec;
        }

        switch (given[0])
        {
            case "family":
                spec.Kind = ModelFamilyTargetKind.LoadedFamily;
                if (mode == ModelFamilySessionMode.Edit)
                {
                    if (spec.Save) problems.Add("target.save applies to an open family document only; a loaded family is loaded back into the project.");
                    if (spec.FamilyName.Length > 0) problems.Add("target.familyName: a loaded family keeps its name.");
                }
                break;
            case "path":
                spec.Kind = ModelFamilyTargetKind.File;
                if (!spec.Path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"target.path '{spec.Path}' is not a family file (.rfa).");
                else if (!System.IO.Path.IsPathRooted(spec.Path))
                    problems.Add($"target.path '{spec.Path}' must be an absolute path.");
                if (mode == ModelFamilySessionMode.Edit)
                {
                    if (spec.Save) problems.Add("target.save applies to an open family document only; pass overwrite=true to save the file back, or savePath.");
                    if (spec.SavePath.Length == 0 && !spec.Overwrite)
                        problems.Add("target: pass overwrite=true to save the edit back to path, or savePath to save it elsewhere.");
                }
                break;
            default:
                spec.Kind = ModelFamilyTargetKind.OpenDocument;
                break;
        }
        return spec;
    }

    /// <summary>True when the problem is about the target itself, so the family must not be opened.</summary>
    public static bool IsTargetProblem(string problem) => problem.StartsWith("target", StringComparison.Ordinal);

    public string Describe() => Kind switch
    {
        ModelFamilyTargetKind.LoadedFamily => $"the loaded family '{Family}'",
        ModelFamilyTargetKind.File => $"the family file '{Path}'",
        ModelFamilyTargetKind.OpenDocument => string.Equals(FamilyDocument, "active", StringComparison.OrdinalIgnoreCase)
            ? "the active family document"
            : $"the open family document '{FamilyDocument}'",
        ModelFamilyTargetKind.Template => $"a new family at '{SavePath}'",
        _ => "the family"
    };

    /// <summary>What happens to the result after a successful edit, for approval summaries and previews.</summary>
    public string DescribeOutcome()
    {
        var load = Load ? $" and load it into the project ({(OverwriteParameterValues ? "with the family's parameter values" : "keeping the project's parameter values")})" : string.Empty;
        switch (Kind)
        {
            case ModelFamilyTargetKind.LoadedFamily:
                return $"load it back into the project as one undo step ({(OverwriteParameterValues ? "with the family's parameter values" : "keeping the project's parameter values")})" +
                       (SavePath.Length > 0 ? $" and save a copy to '{SavePath}'" : string.Empty);
            case ModelFamilyTargetKind.File:
                return (SavePath.Length > 0 ? $"save it to '{SavePath}'{(Overwrite ? " (replacing an existing file)" : string.Empty)}" : $"save it back to '{Path}', replacing it") + load;
            case ModelFamilyTargetKind.OpenDocument:
                return (SavePath.Length > 0 ? $"save it as '{SavePath}'" : Save ? "save it" : "leave it open and unsaved") + load;
            case ModelFamilyTargetKind.Template:
                return $"save it to '{SavePath}'{(Overwrite ? " (replacing an existing file)" : string.Empty)}" + load;
            default:
                return "nothing";
        }
    }

    public static JObject? AsObject(object? value, out string? error)
    {
        error = null;
        switch (value)
        {
            case null:
                return null;
            case JObject o:
                return o;
            case JValue { Type: JTokenType.Null }:
                return null;
            case JValue { Type: JTokenType.String } v:
                return FromString((string?)v ?? string.Empty, out error);
            case string s:
                return FromString(s, out error);
            case JToken:
                error = "must be an object.";
                return null;
            default:
                try { return JObject.FromObject(value); }
                catch { error = "must be an object."; return null; }
        }
    }

    private static JObject? FromString(string text, out string? error)
    {
        error = null;
        if (text.Trim().Length == 0) return null;
        try { return JObject.Parse(text); }
        catch { error = "must be an object, e.g. {\"path\":\"C:\\\\families\\\\Device.rfa\",\"overwrite\":true}."; return null; }
    }

    private static string Str(JObject obj, string key)
    {
        var token = obj.GetValue(key, StringComparison.OrdinalIgnoreCase);
        if (token == null || token.Type == JTokenType.Null) return string.Empty;
        return (token.Type == JTokenType.String ? (string?)token : token.ToString())?.Trim() ?? string.Empty;
    }

    private static bool Bool(JObject obj, string key, List<string> problems)
    {
        var token = obj.GetValue(key, StringComparison.OrdinalIgnoreCase);
        if (token == null || token.Type == JTokenType.Null) return false;
        if (token.Type == JTokenType.Boolean) return (bool)token;
        if (token.Type == JTokenType.String && bool.TryParse(((string?)token)?.Trim(), out var parsed)) return parsed;
        // A value that is not a boolean must not be read as false: "overwrite": 1 would silently not overwrite.
        problems.Add($"target.{key}: must be true or false.");
        return false;
    }
}
