using System.IO;
using System.Text.RegularExpressions;
using RevitMCP.Addin.FileSystem;

namespace RevitMCP.Addin.Families;

/// <summary>What to do when a family of the same name is already loaded (#102).</summary>
public enum FamilyIfExists
{
    /// <summary>Leave the loaded family alone; the file is not loaded.</summary>
    Skip,
    /// <summary>Load the file's version and keep the project's parameter values (Revit: "Overwrite the existing version").</summary>
    Overwrite,
    /// <summary>Load the file's version and its parameter values (Revit: "Overwrite the existing version and its parameter values").</summary>
    OverwriteParameters,
    /// <summary>As OverwriteParameters, and shared nested families also come from the file instead of the project.</summary>
    OverwriteAll
}

/// <summary>How the Revit load callbacks answer for one policy. Pure: FamilyLoadOptions maps it to the Revit enums.</summary>
public readonly struct FamilyLoadDecision
{
    public FamilyLoadDecision(bool load, bool overwriteParameterValues, bool sharedFromFile)
    {
        Load = load;
        OverwriteParameterValues = overwriteParameterValues;
        SharedFromFile = sharedFromFile;
    }

    /// <summary>The return value of OnFamilyFound / OnSharedFamilyFound: false cancels the load of that family.</summary>
    public bool Load { get; }
    public bool OverwriteParameterValues { get; }
    /// <summary>Shared nested families: true = FamilySource.Family (the file's version), false = FamilySource.Project.</summary>
    public bool SharedFromFile { get; }
}

/// <summary>One family file a load request names, after validation.</summary>
public sealed class FamilyFileCandidate
{
    public string Path { get; set; } = string.Empty;
    /// <summary>The file name without extension: the name Revit gives the loaded family.</summary>
    public string FamilyName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    /// <summary>ok | invalid | duplicate | backup.</summary>
    public string Status { get; set; } = "ok";
    public string? Reason { get; set; }
    public bool IsValid => Status == "ok";
}

/// <summary>The files a load request resolves to, and why the rest were dropped.</summary>
public sealed class FamilyFileSelection
{
    public List<FamilyFileCandidate> Files { get; } = new();
    public List<string> Warnings { get; } = new();
    public string? Error { get; set; }
    public IEnumerable<FamilyFileCandidate> Valid => Files.Where(f => f.IsValid);
}

/// <summary>
/// Argument parsing and file validation of revit_preview_load_family / revit_load_family / revit_reload_family (#102).
/// No Revit API: unit tested in RevitMCP.Tests.
/// </summary>
public static class FamilyLoadPolicy
{
    public const int MaxFilesPerCall = 50;
    public const string DefaultPattern = "*.rfa";

    /// <summary>Revit's own backup copies: Name.0001.rfa.</summary>
    private static readonly Regex BackupFile = new(@"\.\d{4}\.rfa$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Describe(FamilyIfExists policy) => policy switch
    {
        FamilyIfExists.Skip => "skip",
        FamilyIfExists.Overwrite => "overwrite",
        FamilyIfExists.OverwriteParameters => "overwriteParameters",
        _ => "overwriteAll"
    };

    /// <summary>Parses ifExists. Empty means <paramref name="fallback"/>.</summary>
    public static bool TryParseIfExists(string? value, FamilyIfExists fallback, out FamilyIfExists policy, out string? error)
    {
        error = null;
        policy = fallback;
        var text = (value ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        switch (text)
        {
            case "": return true;
            case "skip": policy = FamilyIfExists.Skip; return true;
            case "overwrite": policy = FamilyIfExists.Overwrite; return true;
            case "overwriteparameters": case "overwriteparametervalues": policy = FamilyIfExists.OverwriteParameters; return true;
            case "overwriteall": policy = FamilyIfExists.OverwriteAll; return true;
            default:
                error = $"ifExists '{value}' is not one of skip, overwrite, overwriteParameters, overwriteAll.";
                return false;
        }
    }

    /// <summary>How the load callbacks answer when Revit finds the family (or a shared nested family) already loaded.</summary>
    public static FamilyLoadDecision Decide(FamilyIfExists policy, bool shared) => policy switch
    {
        FamilyIfExists.Skip => new FamilyLoadDecision(load: false, overwriteParameterValues: false, sharedFromFile: false),
        FamilyIfExists.Overwrite => new FamilyLoadDecision(true, false, false),
        FamilyIfExists.OverwriteParameters => new FamilyLoadDecision(true, true, false),
        _ => new FamilyLoadDecision(true, true, shared)
    };

    /// <summary>Preview status of one file: willLoad (new family) | willSkip | willOverwrite.</summary>
    public static string PlannedStatus(bool alreadyLoaded, FamilyIfExists policy) =>
        !alreadyLoaded ? "willLoad" : policy == FamilyIfExists.Skip ? "willSkip" : "willOverwrite";

    /// <summary>
    /// Resolves paths[] or folder+pattern to family files: existence, the .rfa extension, the read policy (network
    /// paths need AllowNetworkPaths), Revit backups (Name.0001.rfa) and two files that would load as the same family.
    /// At most <see cref="MaxFilesPerCall"/> files; more is an error, not a silent cut.
    /// </summary>
    public static FamilyFileSelection SelectFiles(IReadOnlyList<string> paths, string? folder, string? pattern, bool recursive, FilePathPolicy policy)
    {
        var selection = new FamilyFileSelection();
        var requested = new List<string>();
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
            requested.Add(path.Trim());

        if (!string.IsNullOrWhiteSpace(folder))
        {
            var (fullFolder, normalizeError) = FilePathPolicy.NormalizePath(folder!);
            var folderError = normalizeError ?? policy.ValidateRead(folder!);
            if (folderError != null)
            {
                selection.Error = $"folder '{folder}': {folderError}";
                return selection;
            }
            if (!Directory.Exists(fullFolder))
            {
                selection.Error = $"folder '{folder}' does not exist.";
                return selection;
            }
            var filePattern = string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern!.Trim();
            try
            {
                requested.AddRange(Directory.GetFiles(fullFolder!, filePattern,
                        recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                selection.Error = $"folder '{folder}' could not be listed: {ex.Message}";
                return selection;
            }
        }

        if (requested.Count == 0)
        {
            selection.Error = string.IsNullOrWhiteSpace(folder)
                ? "Pass paths (.rfa files) or folder (+ pattern, default *.rfa); nothing to load was named."
                : $"No file in folder '{folder}' matches '{(string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern)}'.";
            return selection;
        }

        var seenNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in requested)
        {
            var candidate = new FamilyFileCandidate { Path = raw, FamilyName = SafeStem(raw) };
            var (full, normalizeError) = FilePathPolicy.NormalizePath(raw);
            if (full != null) candidate.Path = full;

            if (full != null && !seenPaths.Add(full))
                continue; // the same file named twice: load it once

            if (normalizeError != null)
                Invalid(candidate, normalizeError);
            else if (BackupFile.IsMatch(candidate.Path))
            {
                candidate.Status = "backup";
                candidate.Reason = "A Revit backup copy (Name.0001.rfa), not loaded.";
            }
            else if (!string.Equals(System.IO.Path.GetExtension(full), ".rfa", StringComparison.OrdinalIgnoreCase))
                Invalid(candidate, "Not a family file: the extension must be .rfa.");
            else if (policy.ValidateRead(full!) is { } readError)
                Invalid(candidate, readError);
            else if (!File.Exists(full))
                Invalid(candidate, "The file does not exist.");
            else
            {
                try { candidate.SizeBytes = new FileInfo(full!).Length; }
                catch (Exception ex) { Invalid(candidate, $"The file cannot be read: {ex.Message}"); }

                if (candidate.IsValid && candidate.SizeBytes == 0)
                    Invalid(candidate, "The file is empty.");
                if (candidate.IsValid && seenNames.TryGetValue(candidate.FamilyName, out var first))
                {
                    candidate.Status = "duplicate";
                    candidate.Reason = $"Another file in this call loads the same family name ('{first}'); only the first one is loaded.";
                }
                else if (candidate.IsValid)
                    seenNames[candidate.FamilyName] = candidate.Path;
            }
            selection.Files.Add(candidate);
        }

        var valid = selection.Files.Count(f => f.IsValid);
        if (valid > MaxFilesPerCall)
        {
            selection.Error = $"{valid} family files were named; at most {MaxFilesPerCall} load per call. Narrow the pattern or split the call.";
            return selection;
        }

        foreach (var dropped in selection.Files.Where(f => !f.IsValid))
            selection.Warnings.Add($"{dropped.Path}: {dropped.Reason}");
        return selection;
    }

    /// <summary>The family name Revit gives a file it loads: the file name without its extension.</summary>
    public static string SafeStem(string path)
    {
        try { return System.IO.Path.GetFileNameWithoutExtension(path.Trim()) ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static void Invalid(FamilyFileCandidate candidate, string reason)
    {
        candidate.Status = "invalid";
        candidate.Reason = reason;
    }
}
