using System.Reflection;
using System.Text.RegularExpressions;

namespace RevitMCP.Core.Configuration;

/// <summary>
/// Which build of the connector is running, and whether it is the one in the published package.
/// The commit comes from the SDK's source revision in the informational version ("1.0.0+&lt;sha&gt;"),
/// the published one from the package's version.txt written by Publish-To-Dropbox.bat.
/// </summary>
public static class BuildInfo
{
    public const string PackageVersionFile = "version.txt";

    private const int ShortCommitLength = 7;

    private static readonly Regex CommitInParentheses = new(@"\(([0-9a-fA-F]{7,40})\)", RegexOptions.Compiled);

    /// <summary>Short commit from an informational version such as "1.0.0+bd4b933dc7…"; null when it carries none.</summary>
    public static string? CommitOf(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return null;
        var plus = informationalVersion!.IndexOf('+');
        if (plus < 0) return null;
        var revision = informationalVersion.Substring(plus + 1).Trim();
        if (revision.Length < ShortCommitLength || !revision.All(Uri.IsHexDigit)) return null;
        return revision.Substring(0, ShortCommitLength).ToLowerInvariant();
    }

    /// <summary>Commits named in version.txt, one "(hash)" per line; a partial publish adds a line.</summary>
    public static List<string> PackageCommits(string? versionText)
    {
        var commits = new List<string>();
        if (string.IsNullOrWhiteSpace(versionText)) return commits;
        foreach (Match match in CommitInParentheses.Matches(versionText))
        {
            var commit = match.Groups[1].Value.Substring(0, ShortCommitLength).ToLowerInvariant();
            if (!commits.Contains(commit)) commits.Add(commit);
        }
        return commits;
    }

    /// <summary>
    /// True when the running commit is one the package names, false when it is not, null when either side is unknown.
    /// </summary>
    public static bool? MatchesPackage(string? commit, IReadOnlyCollection<string> packageCommits)
    {
        if (string.IsNullOrEmpty(commit) || packageCommits.Count == 0) return null;
        return packageCommits.Contains(commit!);
    }

    /// <summary>
    /// Build facts of one component. <paramref name="levelsUpToPackage"/> is how many folders above the
    /// assembly the package root is (Bridge\ = 1, Addin\2026\ = 2).
    /// </summary>
    public static Dictionary<string, object?> Describe(Assembly assembly, int levelsUpToPackage, string restartHint)
    {
        var commit = CommitOf(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        var info = new Dictionary<string, object?> { ["commit"] = commit };

        string? location = null;
        try { location = assembly.Location; } catch { /* dynamic assembly */ }
        if (string.IsNullOrEmpty(location) || !File.Exists(location))
        {
            // Loaded from bytes (e.g. a hot reload): there is no file, so no package to compare with.
            info["matchesPackage"] = null;
            return info;
        }

        info["path"] = location;
        info["built"] = File.GetLastWriteTime(location).ToString("yyyy-MM-dd HH:mm");

        var packageText = ReadPackageVersion(location!, levelsUpToPackage);
        var packageCommits = PackageCommits(packageText);
        var matches = MatchesPackage(commit, packageCommits);
        info["package"] = packageText;
        info["matchesPackage"] = matches;
        if (packageText == null)
            info["note"] = "Not running from a published package (no version.txt next to it), e.g. a development build.";
        else if (matches == false)
            info["note"] = $"The published package is a different build. {restartHint}";
        return info;
    }

    private static string? ReadPackageVersion(string assemblyPath, int levelsUp)
    {
        try
        {
            var folder = Path.GetDirectoryName(assemblyPath);
            for (var i = 0; i < levelsUp && folder != null; i++) folder = Path.GetDirectoryName(folder);
            if (folder == null) return null;
            var file = Path.Combine(folder, PackageVersionFile);
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
