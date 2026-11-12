using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Families;

/// <summary>What remove names for one kind of element: everything of that kind (true), or these element ids.</summary>
public sealed class ElementRemoval
{
    public bool All { get; set; }
    public List<long> Ids { get; } = new();
    public bool Any => All || Ids.Count > 0;
}

/// <summary>The remove argument of revit_edit_detail_family.</summary>
public sealed class DetailFamilyRemoveSpec
{
    public ElementRemoval Curves { get; } = new();
    public ElementRemoval FilledRegions { get; } = new();
    /// <summary>Nested annotation instances (text and parameter labels).</summary>
    public ElementRemoval Labels { get; } = new();
    public ElementRemoval TextNotes { get; } = new();
    /// <summary>Family parameters to remove, by name.</summary>
    public List<string> Parameters { get; } = new();

    public bool IsEmpty => !Curves.Any && !FilledRegions.Any && !Labels.Any && !TextNotes.Any && Parameters.Count == 0;

    /// <summary>(argument key, selection) for each element kind, in the order they are removed.</summary>
    public IEnumerable<(string Key, ElementRemoval Removal)> ElementKinds()
    {
        yield return ("curves", Curves);
        yield return ("filledRegions", FilledRegions);
        yield return ("labels", Labels);
        yield return ("textNotes", TextNotes);
    }
}

/// <summary>Everything revit_edit_detail_family is asked to do.</summary>
public sealed class DetailFamilyEditSpec
{
    /// <summary>A family loaded in the project: its name or element id. Empty when <see cref="Path"/> is given.</summary>
    public string Family { get; set; } = string.Empty;
    /// <summary>An .rfa file. Empty when <see cref="Family"/> is given.</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>savePath as given: a folder or an .rfa path; empty = none.</summary>
    public string SavePath { get; set; } = string.Empty;
    public bool Overwrite { get; set; }
    /// <summary>Family target: load with the family's parameter values instead of keeping the project's.</summary>
    public bool OverwriteParameterValues { get; set; }
    /// <summary>What to add (and Purge, default false for edits). Its FamilyName is set once the family is known.</summary>
    public DetailFamilySpec Content { get; set; } = new() { Purge = false };
    public DetailFamilyRemoveSpec Remove { get; } = new();

    public bool IsPathTarget => Path.Length > 0;

    public bool AddsAnything =>
        Content.Curves.Count > 0 || Content.FilledRegions.Count > 0 || Content.FilledRegionTypes.Count > 0 || Content.Labels.Count > 0 ||
        Content.Parameters.Count > 0 || Content.LineStyles.Count > 0 || Content.Types.Count > 0;
}

/// <summary>A parameter removal that passed the checks, in the order it runs.</summary>
public sealed class ParameterRemovalPlan
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Also removed by this edit: the elements whose parameters were linked to it.</summary>
    public List<long> UnlinkedElementIds { get; } = new();
}

/// <summary>revit_edit_detail_family's arguments and checks (removals, parameter references). Pure: no Revit types, unit tested.</summary>
public static partial class DetailFamilySpecParser
{
    private static readonly string[] RemoveKeys = { "curves", "filledRegions", "labels", "textNotes", "parameters" };

    public static DetailFamilyEditSpec ParseEdit(IReadOnlyDictionary<string, object?> args, List<string> errors)
    {
        var edit = new DetailFamilyEditSpec
        {
            Family = Str(args, "family"),
            Path = Str(args, "path"),
            SavePath = Str(args, "savePath"),
            Overwrite = Bool(args, "overwrite", false),
            OverwriteParameterValues = Bool(args, "overwriteParameterValues", false)
        };
        edit.Content.Purge = Bool(args, "purge", false);
        edit.Content.SharedParameterFile = Str(args, "sharedParameterFile");
        edit.Content.AnnotationTemplatePath = Str(args, "annotationTemplatePath");

        if (edit.Family.Length > 0 && edit.Path.Length > 0)
            errors.Add("Give family (a family loaded in the project) or path (an .rfa file), not both.");
        else if (edit.Family.Length == 0 && edit.Path.Length == 0)
            errors.Add("Nothing to edit: give family (the name or element id of a family loaded in the project) or path (an .rfa file).");
        else if (edit.Path.Length > 0)
        {
            if (!edit.Path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                errors.Add($"path '{edit.Path}' is not a family file (.rfa).");
            else if (!System.IO.Path.IsPathRooted(edit.Path))
                errors.Add($"path '{edit.Path}' must be an absolute path.");
            else if (edit.SavePath.Length == 0 && !edit.Overwrite)
                errors.Add($"Saving back to '{edit.Path}' replaces it: pass overwrite=true, or savePath to save the edited family elsewhere.");
        }
        if (edit.OverwriteParameterValues && edit.Path.Length > 0)
            errors.Add("overwriteParameterValues applies to a family loaded in the project (family), not to a file (path).");

        ReadContent(args, edit.Content, errors);
        ReadRemove(args, "remove", edit.Remove, errors);

        if (!edit.AddsAnything && edit.Remove.IsEmpty)
            errors.Add("Nothing to change: give something to add (lines, rectangles, arcs, circles, filledRegions, labels, parameters, " +
                       "types, lineStyles, filledRegionTypes) or to remove (remove).");
        else if (edit.Content.Curves.Count > MaxCurves)
            errors.Add($"{edit.Content.Curves.Count} curves were requested; at most {MaxCurves} per edit.");
        return edit;
    }

    /// <summary>
    /// Where the edited family is saved: <paramref name="savePath"/> (a folder or an .rfa path) for the family
    /// <paramref name="familyName"/>. A family loaded in the project keeps its name (the file name must match it); a
    /// file target may be saved under another name, which becomes the family's name.
    /// </summary>
    public static (string? Path, string? FamilyName, string? Error) ResolveEditSavePath(string savePath, string familyName, bool keepName)
    {
        if (savePath.Trim().EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) && !keepName)
            return ResolveSavePath(savePath, string.Empty);
        var (path, name, error) = ResolveSavePath(savePath, familyName);
        if (error != null && keepName && error.Contains("differs from the file name"))
            error = $"savePath's file name must be the family's name '{familyName}': the edited family is loaded back into the project under that name.";
        return (path, name, error);
    }

    private static void ReadRemove(IReadOnlyDictionary<string, object?> args, string key, DetailFamilyRemoveSpec remove, List<string> errors)
    {
        if (!args.TryGetValue(key, out var raw) || raw == null) return;
        JToken? token = raw switch
        {
            JToken t => t,
            string s when s.Trim().Length == 0 => null,
            string s => TryParse(s),
            _ => TryFrom(raw)
        };
        if (token == null || token.Type == JTokenType.Null) return;
        if (token is not JObject obj)
        {
            errors.Add($"{key} must be an object: {{curves?, filledRegions?, labels?, textNotes?: true | [ids], parameters?: [names]}}.");
            return;
        }

        foreach (var property in obj.Properties())
        {
            if (!RemoveKeys.Contains(property.Name, StringComparer.Ordinal))
            {
                var close = SuggestNames(property.Name, RemoveKeys);
                errors.Add($"{key}.{property.Name} is not known: {string.Join(" | ", RemoveKeys)}" +
                           $"{(close.Count > 0 ? $" (did you mean {close[0]}?)" : string.Empty)}.");
            }
        }

        foreach (var (kind, removal) in remove.ElementKinds())
            ReadElementRemoval(obj[kind], $"{key}.{kind}", removal, errors);

        var parameters = obj["parameters"];
        if (parameters == null || parameters.Type == JTokenType.Null) return;
        if (parameters is not JArray names)
        {
            errors.Add($"{key}.parameters must be an array of parameter names (there is no 'all': name each one).");
            return;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in names)
        {
            var source = $"{key}.parameters[{index++}]";
            var name = item.Type == JTokenType.String ? ((string?)item ?? string.Empty).Trim() : string.Empty;
            if (name.Length == 0) { errors.Add($"{source} must be a parameter name."); continue; }
            if (!seen.Add(name)) { errors.Add($"{source}: '{name}' is listed twice."); continue; }
            remove.Parameters.Add(name);
        }
    }

    private static void ReadElementRemoval(JToken? token, string source, ElementRemoval removal, List<string> errors)
    {
        if (token == null || token.Type == JTokenType.Null) return;
        if (token.Type == JTokenType.Boolean)
        {
            removal.All = (bool)token;
            return;
        }
        if (token is not JArray array)
        {
            errors.Add($"{source} must be true (all of them) or an array of element ids.");
            return;
        }
        var index = 0;
        foreach (var item in array)
        {
            var number = Num(item);
            if (number == null || number <= 0 || Math.Abs(number.Value - Math.Round(number.Value)) > 1e-9)
            {
                errors.Add($"{source}[{index}] must be an element id (a positive whole number).");
                index++;
                continue;
            }
            var id = (long)Math.Round(number.Value);
            if (!removal.Ids.Contains(id)) removal.Ids.Add(id);
            index++;
        }
    }

    /// <summary>
    /// The element ids one remove entry stands for: every element of the kind (true), or the given ids, each of which
    /// must be such an element. <paramref name="describe"/> says what another id is (null = no such element).
    /// </summary>
    public static List<long> ResolveElementRemoval(ElementRemoval removal, string source, string kindLabel,
        IReadOnlyCollection<long> available, Func<long, string?> describe, List<string> errors)
    {
        if (removal.All) return available.OrderBy(id => id).ToList();
        var set = new HashSet<long>(available);
        var result = new List<long>();
        foreach (var id in removal.Ids)
        {
            if (set.Contains(id)) { result.Add(id); continue; }
            var other = describe(id);
            errors.Add(other == null
                ? $"{source}: the family has no element {id}."
                : $"{source}: element {id} is {other}, not {kindLabel}.");
        }
        return result;
    }

    /// <summary>
    /// Checks and orders the parameter removals: each must be a parameter of the family, not built in, not used in the
    /// formula of a parameter that stays, and linked only to elements this edit also removes. A parameter whose formula
    /// uses another removed one is removed first.
    /// </summary>
    public static List<ParameterRemovalPlan> PlanParameterRemovals(IReadOnlyList<string> names,
        IReadOnlyDictionary<string, KnownFamilyParameter> existing, ISet<long> removedElementIds, List<string> errors)
    {
        var accepted = new List<KnownFamilyParameter>();
        foreach (var name in names)
        {
            if (!existing.TryGetValue(name, out var parameter))
            {
                var close = SuggestNames(name, existing.Values.Where(p => !p.IsBuiltIn).Select(p => p.Name));
                errors.Add($"remove.parameters: the family has no parameter '{name}'" +
                           $"{(close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : ".")}");
                continue;
            }
            if (parameter.IsBuiltIn)
            {
                errors.Add($"remove.parameters: '{parameter.Name}' is a built-in parameter of the family; built-in parameters are never removed.");
                continue;
            }
            accepted.Add(parameter);
        }

        var removing = new HashSet<string>(accepted.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var ok = new List<KnownFamilyParameter>();
        foreach (var parameter in accepted)
        {
            var usedBy = existing.Values
                .Where(other => !removing.Contains(other.Name) && other.Formula.Length > 0 && FormulaUses(other.Formula, parameter.Name))
                .ToList();
            if (usedBy.Count > 0)
            {
                errors.Add($"remove.parameters: '{parameter.Name}' is used in the formula of " +
                           $"{string.Join(", ", usedBy.Select(o => $"'{o.Name}' ({o.Formula})"))}; change that formula, or remove those parameters too.");
                continue;
            }
            var staying = parameter.AssociatedElementIds.Where(id => !removedElementIds.Contains(id)).Distinct().ToList();
            if (staying.Count > 0)
            {
                errors.Add($"remove.parameters: '{parameter.Name}' is linked to {staying.Count} element(s) that stay " +
                           $"(ids {string.Join(", ", staying.Take(10))}{(staying.Count > 10 ? ", …" : string.Empty)}): a visibility, label or " +
                           "dimension uses it. Remove those elements in the same edit, or unlink them in the Family Editor first.");
                continue;
            }
            ok.Add(parameter);
        }

        // A parameter whose formula uses another one goes first; Revit has no formula cycles.
        var ordered = new List<ParameterRemovalPlan>();
        var left = ok.ToList();
        while (left.Count > 0)
        {
            var next = left.FirstOrDefault(p => !left.Any(other => other != p && other.Formula.Length > 0 && FormulaUses(other.Formula, p.Name)))
                       ?? left[0];
            left.Remove(next);
            var plan = new ParameterRemovalPlan { Name = next.Name };
            plan.UnlinkedElementIds.AddRange(next.AssociatedElementIds.Distinct());
            ordered.Add(plan);
        }
        return ordered;
    }

    /// <summary>
    /// True when <paramref name="formula"/> names <paramref name="parameter"/>: the name, not part of a longer name
    /// (letters, digits or underscore on either side), ignoring case.
    /// </summary>
    public static bool FormulaUses(string formula, string parameter)
    {
        if (formula.Length == 0 || parameter.Length == 0) return false;
        static bool Word(char c) => char.IsLetterOrDigit(c) || c == '_';
        var start = 0;
        while (true)
        {
            var at = formula.IndexOf(parameter, start, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            var end = at + parameter.Length;
            var before = at == 0 || !Word(formula[at - 1]) || !Word(parameter[0]);
            var after = end >= formula.Length || !Word(formula[end]) || !Word(parameter[parameter.Length - 1]);
            if (before && after) return true;
            start = at + 1;
        }
    }

    /// <summary>
    /// Names for the new label annotation families of an edit that are not taken by a family the edited family already
    /// nests: {familyName}_Text{n}, counting on from the highest number in use.
    /// </summary>
    public static void RenameLabelGroups(List<DetailLabelGroup> groups, string familyName, IEnumerable<string> takenNames)
    {
        var taken = new HashSet<string>(takenNames, StringComparer.OrdinalIgnoreCase);
        var next = 1;
        foreach (var group in groups)
        {
            while (taken.Contains($"{familyName}_Text{next}")) next++;
            group.FamilyName = $"{familyName}_Text{next}";
            taken.Add(group.FamilyName);
            next++;
        }
    }

    /// <summary>The .rfa's family name: its file name without extension.</summary>
    public static string FamilyNameOfPath(string path) => System.IO.Path.GetFileNameWithoutExtension(path);
}
