using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;

namespace RevitMCP.Addin;

/// <summary>
/// Records exceptions that best-effort code deliberately ignores, so a real failure hidden in a
/// <c>catch</c> can still be found. Behaviour of the calling code is unchanged: this never throws.
///
/// Each call site is counted in memory. When <see cref="LogDirectory"/> is set (the add-in sets it at
/// startup; tests and the bridge leave it null), the 1st, 10th, 100th, … occurrence per call site is
/// appended to <c>swallowed-yyyy-MM-dd.log</c> there, which keeps loops over thousands of elements cheap.
/// No Revit API, so files that are linked into the bridge and the test project can use it.
/// </summary>
public static class Swallowed
{
    private static readonly ConcurrentDictionary<(string File, int Line), int> Counts = new();
    private static readonly object FileLock = new();

    /// <summary>Folder for the log file; null (the default) keeps counting but writes nothing.</summary>
    public static string? LogDirectory { get; set; }

    /// <summary>Notes an ignored exception at the calling line.</summary>
    public static void Note(Exception exception, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        try
        {
            var count = Counts.AddOrUpdate((file, line), 1, (_, n) => n == int.MaxValue ? n : n + 1);
            var directory = LogDirectory;
            if (directory == null || !IsPowerOfTen(count)) return;

            var text = $"{DateTime.Now:HH:mm:ss.fff} {Path.GetFileName(file)}:{line} x{count} " +
                       $"{exception.GetType().Name}: {FirstLine(exception.Message)}{Environment.NewLine}";
            lock (FileLock)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"swallowed-{DateTime.Now:yyyy-MM-dd}.log"), text);
            }
        }
        catch
        {
            // Diagnostics must never change what the caller does.
        }
    }

    /// <summary>Call sites and how often each ignored an exception since the process (or last reload) started.</summary>
    public static IReadOnlyList<(string File, int Line, int Count)> Snapshot()
        => Counts.Select(pair => (Path.GetFileName(pair.Key.File), pair.Key.Line, pair.Value))
            .OrderByDescending(entry => entry.Value)
            .ToList();

    internal static bool IsPowerOfTen(int count)
    {
        if (count < 1) return false;
        while (count % 10 == 0) count /= 10;
        return count == 1;
    }

    private static string FirstLine(string? message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        var end = message!.IndexOfAny(new[] { '\r', '\n' });
        return end < 0 ? message : message.Substring(0, end);
    }
}
