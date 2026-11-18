using System.Text;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>The parameters a family formula uses, and the identifiers in it that are none of them.</summary>
public sealed class ModelFamilyFormulaReferences
{
    /// <summary>Known parameter names the formula uses, once each, in order of first use.</summary>
    public List<string> Parameters { get; } = new();

    /// <summary>Identifiers that are neither a known parameter, a function nor a unit. Revit is the judge of those.</summary>
    public List<string> Unknown { get; } = new();
}

/// <summary>
/// Reads which parameters a family formula uses (#172). Pure. Revit formulas name parameters as written, spaces
/// included, so the known names are matched longest-first against the text; what is left over is split into
/// identifiers. Function names, numbers with their unit suffix and quoted text are skipped.
/// </summary>
public static class ModelFamilyFormula
{
    /// <summary>Function and keyword names a formula may use; never reported as unknown parameters.</summary>
    public static readonly IReadOnlyList<string> Functions = new[]
    {
        "if", "and", "or", "not", "sin", "cos", "tan", "asin", "acos", "atan", "sqrt", "abs", "exp", "log", "ln", "pi",
        "round", "roundup", "rounddown", "true", "false", "min", "max", "sign", "pow"
    };

    private static readonly HashSet<string> Reserved = new(Functions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        "mm", "cm", "m", "km", "in", "ft", "inch", "inches", "deg", "degree", "degrees", "rad", "radian", "radians",
        "sf", "sqft", "sqm", "m2", "m3", "ft2", "ft3", "cf", "cm2", "mm2", "w", "kw", "v", "a", "va", "kva"
    };

    public static ModelFamilyFormulaReferences References(string? formula, IEnumerable<string> knownNames)
    {
        var result = new ModelFamilyFormulaReferences();
        if (string.IsNullOrWhiteSpace(formula)) return result;
        var text = formula!;
        var names = knownNames.Where(n => n.Length > 0).Distinct(StringComparer.Ordinal)
            .OrderByDescending(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).ToList();

        var run = new StringBuilder();

        void Flush()
        {
            if (run.Length > 0 && !result.Unknown.Contains(run.ToString(), StringComparer.Ordinal)) result.Unknown.Add(run.ToString());
            run.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                Flush();
                var close = text.IndexOf('"', i + 1);
                i = close < 0 ? text.Length : close + 1;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var atBoundary = i == 0 || !IsWordChar(text[i - 1]);
            if (atBoundary)
            {
                var matched = false;
                foreach (var name in names)
                {
                    if (i + name.Length > text.Length || string.CompareOrdinal(text, i, name, 0, name.Length) != 0) continue;
                    var end = i + name.Length;
                    if (end < text.Length && IsWordChar(text[end]) && IsWordChar(name[name.Length - 1])) continue;
                    Flush();
                    if (!result.Parameters.Contains(name, StringComparer.Ordinal)) result.Parameters.Add(name);
                    i = end;
                    matched = true;
                    break;
                }
                if (matched) continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                Flush();
                i = SkipNumber(text, i);
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && IsWordChar(text[i])) i++;
                var word = text.Substring(start, i - start);
                var next = i;
                while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                if ((next < text.Length && text[next] == '(') || Reserved.Contains(word))
                {
                    Flush();
                    continue;
                }
                // Words separated by spaces are one name (parameter names may contain spaces).
                if (run.Length > 0) run.Append(' ');
                run.Append(word);
                continue;
            }

            // An operator, bracket, comma or other symbol ends an identifier run.
            Flush();
            i++;
        }
        Flush();
        return result;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The index after a number and its unit: "10", "2.5m", "10 mm", "45°", "3 m2".</summary>
    private static int SkipNumber(string text, int start)
    {
        var i = start;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E') && i + 1 < text.Length &&
            (char.IsDigit(text[i + 1]) || ((text[i + 1] == '-' || text[i + 1] == '+') && i + 2 < text.Length && char.IsDigit(text[i + 2]))))
        {
            i += 2;
            while (i < text.Length && char.IsDigit(text[i])) i++;
        }
        // A unit directly after the number.
        if (i < text.Length && text[i] == '°') return i + 1;
        if (i < text.Length && char.IsLetter(text[i]))
        {
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '²' || text[i] == '³')) i++;
            return i;
        }
        // A unit after spaces: only a known unit word.
        var j = i;
        while (j < text.Length && text[j] == ' ') j++;
        if (j > i && j < text.Length && char.IsLetter(text[j]))
        {
            var k = j;
            while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] == '²' || text[k] == '³')) k++;
            if (Units.Contains(text.Substring(j, k - j)) && !(k < text.Length && text[k] == '(')) return k;
        }
        return i;
    }
}

/// <summary>The formula dependency graph of a family's parameters: cycles and what depends on what (#172). Pure.</summary>
public static class ModelFamilyFormulaGraph
{
    /// <summary>parameter → the parameters its formula uses. Parameters without a formula have an empty list.</summary>
    public static Dictionary<string, List<string>> Build(IReadOnlyDictionary<string, string?> formulaByParameter)
    {
        var names = formulaByParameter.Keys.ToList();
        var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, formula) in formulaByParameter)
            graph[name] = ModelFamilyFormula.References(formula, names).Parameters;
        return graph;
    }

    /// <summary>
    /// The first dependency cycle as a path that ends where it starts (<c>A, B, A</c>), or null. Parameters are
    /// visited in name order so the same family always reports the same cycle.
    /// </summary>
    public static List<string>? FindCycle(IReadOnlyDictionary<string, List<string>> dependsOn)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);   // 1 = on the path, 2 = done
        var path = new List<string>();

        List<string>? Visit(string node)
        {
            state[node] = 1;
            path.Add(node);
            if (dependsOn.TryGetValue(node, out var next))
            {
                foreach (var target in next.OrderBy(n => n, StringComparer.Ordinal))
                {
                    if (!dependsOn.ContainsKey(target)) continue;
                    state.TryGetValue(target, out var seen);
                    if (seen == 1)
                    {
                        var cycle = path.Skip(path.IndexOf(target)).ToList();
                        cycle.Add(target);
                        return cycle;
                    }
                    if (seen == 0 && Visit(target) is { } found) return found;
                }
            }
            path.RemoveAt(path.Count - 1);
            state[node] = 2;
            return null;
        }

        foreach (var node in dependsOn.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (state.ContainsKey(node)) continue;
            if (Visit(node) is { } cycle) return cycle;
        }
        return null;
    }

    public static string DescribeCycle(IReadOnlyList<string> cycle) => string.Join(" → ", cycle);

    /// <summary>parameter → the parameters whose formulas use it directly.</summary>
    public static Dictionary<string, List<string>> UsedBy(IReadOnlyDictionary<string, List<string>> dependsOn)
    {
        var used = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, targets) in dependsOn)
            foreach (var target in targets)
            {
                if (!used.TryGetValue(target, out var list)) used[target] = list = new List<string>();
                if (!list.Contains(name, StringComparer.Ordinal)) list.Add(name);
            }
        return used;
    }

    /// <summary>Every parameter that depends on <paramref name="parameter"/>, directly or through other formulas.</summary>
    public static List<string> Dependents(IReadOnlyDictionary<string, List<string>> usedBy, string parameter)
    {
        var result = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(parameter);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!usedBy.TryGetValue(current, out var users)) continue;
            foreach (var user in users.OrderBy(n => n, StringComparer.Ordinal))
            {
                if (user == parameter || result.Contains(user, StringComparer.Ordinal)) continue;
                result.Add(user);
                queue.Enqueue(user);
            }
        }
        return result;
    }
}
