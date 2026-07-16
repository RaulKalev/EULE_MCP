using System.Globalization;
using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Documentation.Placement;

/// <summary>
/// Pure name-scoring logic ported from the PlaceViews plugin. Numbers are the
/// strongest signal and word-prefix matching accommodates inflected names.
/// </summary>
public static class PlaceViewsNameMatcher
{
    private static readonly Dictionary<string, long> TeenOrdinalRoots =
        new(StringComparer.Ordinal)
        {
            ["üheteistkümn"] = 11,
            ["kaheteistkümn"] = 12,
            ["kolmeteistkümn"] = 13,
            ["neljateistkümn"] = 14,
            ["viieteistkümn"] = 15,
            ["kuueteistkümn"] = 16,
            ["seitsmeteistkümn"] = 17,
            ["kaheksateistkümn"] = 18,
            ["üheksateistkümn"] = 19
        };

    private static readonly Dictionary<string, long> TensOrdinalRoots =
        new(StringComparer.Ordinal)
        {
            ["kahekümn"] = 20,
            ["kolmekümn"] = 30,
            ["neljakümn"] = 40,
            ["viiekümn"] = 50,
            ["kuuekümn"] = 60,
            ["seitsmekümn"] = 70,
            ["kaheksakümn"] = 80,
            ["üheksakümn"] = 90
        };

    private static readonly Dictionary<string, long> CompoundTensConnectors =
        new(StringComparer.Ordinal)
        {
            ["kahekümne"] = 20,
            ["kolmekümne"] = 30,
            ["neljakümne"] = 40,
            ["viiekümne"] = 50,
            ["kuuekümne"] = 60,
            ["seitsmekümne"] = 70,
            ["kaheksakümne"] = 80,
            ["üheksakümne"] = 90
        };

    public static int CalculateMatchScore(string sheetName, string viewName)
    {
        if (string.IsNullOrWhiteSpace(sheetName) || string.IsNullOrWhiteSpace(viewName))
            return 0;

        var sheetNumbers = ExtractNumbers(sheetName);
        var sheetWords = ExtractWords(sheetName);
        var viewNumbers = ExtractNumbers(viewName);
        var viewWords = ExtractWords(viewName);

        var score = 0;
        var numbersFullyMatched = false;
        if (sheetNumbers.Count > 0 && viewNumbers.Count > 0)
        {
            if (sheetNumbers.All(number => viewNumbers.Contains(number)))
            {
                numbersFullyMatched = true;
                score += 60;
            }
            else if (!sheetNumbers.Overlaps(viewNumbers))
            {
                return 0;
            }
            else
            {
                score += 20;
            }
        }

        var matchedWords = sheetWords.Count(sheetWord =>
            viewWords.Any(viewWord => WordsMatch(sheetWord, viewWord)));
        if (sheetWords.Count > 0 && viewWords.Count > 0 && matchedWords == 0)
            return 0;

        score += matchedWords * 15;
        if (sheetWords.Count > 0 && matchedWords == sheetWords.Count)
            score += 10;

        var sheetNameLower = sheetName.ToLowerInvariant();
        var viewNameLower = viewName.ToLowerInvariant();
        if (sheetNameLower.Length >= 3 && viewNameLower.Contains(sheetNameLower))
            score += 40;
        else if (viewNameLower.Length >= 3 && sheetNameLower.Contains(viewNameLower))
            score += 40;

        return numbersFullyMatched || matchedWords > 0 ? score : 0;
    }

    private static bool WordsMatch(string first, string second)
    {
        if (first == second)
            return true;

        if (first.Length >= 4 && second.Length >= 4)
        {
            return first.StartsWith(second, StringComparison.Ordinal) ||
                   second.StartsWith(first, StringComparison.Ordinal);
        }

        return false;
    }

    private static HashSet<long> ExtractNumbers(string name)
    {
        var numbers = new HashSet<long>();
        foreach (Match match in Regex.Matches(name, @"-?\d+"))
        {
            if (long.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                numbers.Add(value);
        }

        numbers.UnionWith(ExtractEstonianOrdinals(ExtractWords(name)));

        return numbers;
    }

    private static IEnumerable<long> ExtractEstonianOrdinals(IReadOnlyList<string> words)
    {
        var ordinalNumbers = new HashSet<long>();
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            if (TryParseUnitOrdinal(word, out var unitOrdinal))
                ordinalNumbers.Add(unitOrdinal);

            if (TryParseRootOrdinal(word, TeenOrdinalRoots, out var teenOrdinal))
                ordinalNumbers.Add(teenOrdinal);

            if (TryParseRootOrdinal(word, TensOrdinalRoots, out var tensOrdinal))
                ordinalNumbers.Add(tensOrdinal);

            if (CompoundTensConnectors.TryGetValue(word, out var tens) &&
                index + 1 < words.Count &&
                TryParseUnitOrdinal(words[index + 1], out var compoundUnit) &&
                compoundUnit < 10)
            {
                ordinalNumbers.Add(tens + compoundUnit);
            }
        }

        return ordinalNumbers;
    }

    private static bool TryParseUnitOrdinal(string word, out long value)
    {
        if (word == "esimene" || word.StartsWith("esimes", StringComparison.Ordinal))
            return SetValue(1, out value);
        if (word == "teine" || word.StartsWith("teis", StringComparison.Ordinal))
            return SetValue(2, out value);
        if (word == "kolmas" || word.StartsWith("kolmand", StringComparison.Ordinal))
            return SetValue(3, out value);
        if (word == "neljas" || word.StartsWith("neljand", StringComparison.Ordinal))
            return SetValue(4, out value);
        if (word == "viies" || word.StartsWith("viiend", StringComparison.Ordinal))
            return SetValue(5, out value);
        if (word == "kuues" || word.StartsWith("kuuend", StringComparison.Ordinal))
            return SetValue(6, out value);
        if (word == "seitsmes" || word.StartsWith("seitsmend", StringComparison.Ordinal))
            return SetValue(7, out value);
        if (word == "kaheksas" || word.StartsWith("kaheksand", StringComparison.Ordinal))
            return SetValue(8, out value);
        if (word == "üheksas" || word.StartsWith("üheksand", StringComparison.Ordinal))
            return SetValue(9, out value);
        if (word == "kümnes" || word.StartsWith("kümnend", StringComparison.Ordinal))
            return SetValue(10, out value);

        value = 0;
        return false;
    }

    private static bool TryParseRootOrdinal(
        string word,
        IReadOnlyDictionary<string, long> roots,
        out long value)
    {
        foreach (var pair in roots)
        {
            if (!word.StartsWith(pair.Key, StringComparison.Ordinal))
                continue;

            var suffix = word.Substring(pair.Key.Length);
            if (suffix == "es" || suffix.StartsWith("end", StringComparison.Ordinal))
                return SetValue(pair.Value, out value);
        }

        value = 0;
        return false;
    }

    private static bool SetValue(long parsedValue, out long value)
    {
        value = parsedValue;
        return true;
    }

    private static List<string> ExtractWords(string name)
    {
        return Regex.Matches(name, @"\p{L}+")
            .Cast<Match>()
            .Select(match => match.Value.ToLowerInvariant())
            .Distinct()
            .ToList();
    }
}
