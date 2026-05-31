#if REVIT2024
// Compatibility shims for .NET Framework 4.8 (Revit 2024 build).
// These extension methods backfill BCL APIs that exist on .NET 8 (Revit 2026 build)
// but are missing or have different signatures on net48.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace RevitMCP.Addin.Compat;

internal static class KeyValuePairPolyfill
{
    public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> kvp, out TKey key, out TValue value)
    {
        key = kvp.Key;
        value = kvp.Value;
    }
}

internal static class DictionaryPolyfill
{
    public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, TValue value)
    {
        if (dict.ContainsKey(key)) return false;
        dict.Add(key, value);
        return true;
    }

    public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key)
        => dict.TryGetValue(key, out var v) ? v : default!;

    public static TValue GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, TValue defaultValue)
        => dict.TryGetValue(key, out var v) ? v : defaultValue;
}

internal static class StringPolyfill
{
    // NOTE: Contains(char) is provided on net48 by System.Memory's StringCompatibilityExtensions
    // (transitive dep of System.Text.Json). Do NOT redefine — would cause CS0121 ambiguity.
    // StartsWith/EndsWith(char) and Split(char, StringSplitOptions) are NOT provided there.

    public static bool StartsWith(this string s, char c) => s.Length > 0 && s[0] == c;
    public static bool EndsWith(this string s, char c) => s.Length > 0 && s[s.Length - 1] == c;

    public static string[] Split(this string s, char separator, StringSplitOptions options)
        => s.Split(new[] { separator }, options);

    public static string Replace(this string s, string oldValue, string newValue, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(oldValue)) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            int idx = s.IndexOf(oldValue, i, comparison);
            if (idx < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, idx - i);
            sb.Append(newValue);
            i = idx + oldValue.Length;
        }
        return sb.ToString();
    }
}

internal static class FilePolyfill
{
    /// <summary>
    /// Async wrapper used as drop-in replacement for File.AppendAllTextAsync on net48.
    /// Routes to Task.Run for the synchronous BCL method.
    /// </summary>
    public static Task AppendAllTextAsync(string path, string contents)
        => Task.Run(() => File.AppendAllText(path, contents));
}

internal static class MathPolyfill
{
    public static int Clamp(int value, int min, int max)
        => value < min ? min : value > max ? max : value;
    public static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;
}

/// <summary>
/// StringSplitOptions.TrimEntries equivalent (added in .NET 5). Not a real enum flag here;
/// callers should use <see cref="SplitAndTrim"/> instead.
/// </summary>
internal static class SplitOptionsPolyfill
{
    public static string[] SplitAndTrim(this string s, char separator, bool removeEmpty)
    {
        var raw = s.Split(separator);
        var result = new List<string>(raw.Length);
        foreach (var part in raw)
        {
            var t = part.Trim();
            if (removeEmpty && t.Length == 0) continue;
            result.Add(t);
        }
        return result.ToArray();
    }
}
#endif
