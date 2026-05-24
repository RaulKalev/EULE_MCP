using System.IO;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Delivery;

/// <summary>
/// Policy options that control what optional checks are performed on a delivery scan.
/// All checks are opt-in by default so existing behaviour is unchanged unless the caller
/// passes a configured <see cref="DeliveryFolderPolicy"/>.
/// </summary>
public sealed class DeliveryFolderPolicy
{
    /// <summary>When true, checks that each folder in <see cref="RequiredFolders"/> is present.</summary>
    public bool CheckRequiredFolders { get; set; } = false;

    /// <summary>Sub-folder names (not paths) that must exist inside the delivery folder.</summary>
    public List<string> RequiredFolders { get; set; } = [];

    /// <summary>When true, flags files matching temp/lock/backup patterns.</summary>
    public bool CheckTempFiles { get; set; } = true;

    /// <summary>
    /// Glob-style patterns (case-insensitive) for files to flag as temporary.
    /// Defaults include Office lock files, classic temp/backup extensions, and AutoCAD lock files.
    /// </summary>
    public List<string> TempFilePatterns { get; set; } = ["~$*", "*.bak", "*.tmp", "*.dwl", "*.dwl2", "*.lck", "*.~*"];

    /// <summary>When true, flags files with extensions not in the expected or allowed lists.</summary>
    public bool CheckSuspiciousExtensions { get; set; } = false;

    /// <summary>Extensions (e.g. ".pdf", ".dwg") that are allowed beyond <c>requiredExtensions</c>.</summary>
    public List<string> AllowedExtraExtensions { get; set; } = [];

    /// <summary>When true, detects multiple revisions of the same sheet and flags the older ones.</summary>
    public bool CheckOldRevisions { get; set; } = true;

    /// <summary>Patterns to ignore (e.g. "Thumbs.db"). Matched case-insensitively against file names.</summary>
    public List<string> IgnoredPatterns { get; set; } = ["thumbs.db", "desktop.ini", ".ds_store"];

    /// <summary>If non-empty, at least one file per extension must be present in the delivery folder.</summary>
    public List<string> RequiredProjectFileExtensions { get; set; } = [];

    /// <summary>The primary expected delivery extensions (e.g. ".pdf", ".dwg"). Used for suspicious-extension check.</summary>
    public List<string> RequiredExtensions { get; set; } = [];
}

/// <summary>
/// Performs optional folder-policy checks against an already-scanned delivery.
/// Contains no Revit API dependencies and is safe for unit testing.
/// </summary>
public static class DeliveryFolderPolicyChecker
{
    /// <summary>
    /// Runs all enabled policy checks and returns the resulting issues.
    /// </summary>
    public static List<IssueDto> Check(
        DeliveryScanResult scan,
        DeliveryFolderPolicy policy,
        string runId,
        ref int seqStart)
    {
        var issues = new List<IssueDto>();

        CheckRequiredFolders(scan, policy, runId, ref seqStart, issues);
        CheckTempFiles(scan, policy, runId, ref seqStart, issues);
        CheckSuspiciousExtensions(scan, policy, runId, ref seqStart, issues);
        CheckOldRevisions(scan, policy, runId, ref seqStart, issues);
        CheckRequiredProjectFiles(scan, policy, runId, ref seqStart, issues);

        return issues;
    }

    // ── Required folders ──────────────────────────────────────────────────────

    private static void CheckRequiredFolders(
        DeliveryScanResult scan,
        DeliveryFolderPolicy policy,
        string runId, ref int seq,
        List<IssueDto> issues)
    {
        if (!policy.CheckRequiredFolders || policy.RequiredFolders.Count == 0) return;

        var existing = new HashSet<string>(
            scan.SubFolders ?? [],
            StringComparer.OrdinalIgnoreCase);

        foreach (var folder in policy.RequiredFolders)
        {
            if (!existing.Contains(folder))
                issues.Add(DeliveryIssueBuilder.MissingRequiredFolder(runId, seq++, folder));
        }
    }

    // ── Temp / lock files ─────────────────────────────────────────────────────

    private static void CheckTempFiles(
        DeliveryScanResult scan,
        DeliveryFolderPolicy policy,
        string runId, ref int seq,
        List<IssueDto> issues)
    {
        if (!policy.CheckTempFiles) return;

        var ignored = new HashSet<string>(policy.IgnoredPatterns, StringComparer.OrdinalIgnoreCase);

        foreach (var file in scan.Files)
        {
            var name = Path.GetFileName(file.FileName);
            if (IsIgnored(name, ignored)) continue;
            if (MatchesAnyPattern(name, policy.TempFilePatterns))
                issues.Add(DeliveryIssueBuilder.TempFile(runId, seq++, file.FileName));
        }
    }

    // ── Suspicious extensions ─────────────────────────────────────────────────

    private static void CheckSuspiciousExtensions(
        DeliveryScanResult scan,
        DeliveryFolderPolicy policy,
        string runId, ref int seq,
        List<IssueDto> issues)
    {
        if (!policy.CheckSuspiciousExtensions) return;

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ext in policy.RequiredExtensions)
            allowed.Add(ext.StartsWith('.') ? ext : "." + ext);
        foreach (var ext in policy.AllowedExtraExtensions)
            allowed.Add(ext.StartsWith('.') ? ext : "." + ext);
        foreach (var ext in policy.RequiredProjectFileExtensions)
            allowed.Add(ext.StartsWith('.') ? ext : "." + ext);

        var ignored = new HashSet<string>(policy.IgnoredPatterns, StringComparer.OrdinalIgnoreCase);

        foreach (var file in scan.Files)
        {
            var name = Path.GetFileName(file.FileName);
            if (IsIgnored(name, ignored)) continue;
            if (MatchesAnyPattern(name, policy.TempFilePatterns)) continue; // already flagged as temp

            var ext = Path.GetExtension(name);
            if (!string.IsNullOrEmpty(ext) && !allowed.Contains(ext))
                issues.Add(DeliveryIssueBuilder.SuspiciousExtension(runId, seq++, file.FileName, ext));
        }
    }

    // ── Old revision detection ────────────────────────────────────────────────

    private static void CheckOldRevisions(
        DeliveryScanResult scan,
        DeliveryFolderPolicy policy,
        string runId, ref int seq,
        List<IssueDto> issues)
    {
        if (!policy.CheckOldRevisions) return;

        // Group parsed files by (SheetNumber, Extension) and flag non-latest revisions
        var groups = scan.Files
            .Where(f => f.Parsed != null)
            .GroupBy(f => (
                Sheet: f.Parsed!.SheetNumber,
                Ext: f.Parsed.Extension.ToLowerInvariant()));

        foreach (var group in groups)
        {
            var withRevision = group
                .Where(f => !string.IsNullOrEmpty(f.Parsed!.Revision))
                .ToList();

            if (withRevision.Count <= 1) continue;

            // Sort alphabetically desc so the first item is the "latest" (e.g. Rev02 > Rev01)
            var sorted = withRevision
                .OrderByDescending(f => f.Parsed!.Revision, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Flag everything except the latest
            foreach (var old in sorted.Skip(1))
            {
                issues.Add(DeliveryIssueBuilder.OldRevision(
                    runId, seq++,
                    old.FileName,
                    old.Parsed!.SheetNumber,
                    old.Parsed.Revision!));
            }
        }
    }

    // ── Required project files ────────────────────────────────────────────────

    private static void CheckRequiredProjectFiles(
        DeliveryScanResult scan,
        DeliveryFolderPolicy policy,
        string runId, ref int seq,
        List<IssueDto> issues)
    {
        if (policy.RequiredProjectFileExtensions.Count == 0) return;

        var allExtensions = new HashSet<string>(
            scan.Files.Select(f => Path.GetExtension(f.FileName)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var required in policy.RequiredProjectFileExtensions)
        {
            var ext = required.StartsWith('.') ? required : "." + required;
            if (!allExtensions.Contains(ext))
                issues.Add(DeliveryIssueBuilder.MissingProjectFile(runId, seq++, ext));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsIgnored(string fileName, HashSet<string> ignoredPatterns)
        => ignoredPatterns.Any(pattern =>
            MatchesGlobPattern(fileName, pattern));

    private static bool MatchesAnyPattern(string fileName, IEnumerable<string> patterns)
        => patterns.Any(p => MatchesGlobPattern(fileName, p));

    /// <summary>
    /// Simple glob matching supporting leading '*' (matches-any-prefix) and trailing '*' (matches-any-suffix).
    /// Case-insensitive.
    /// </summary>
    internal static bool MatchesGlobPattern(string fileName, string pattern)
    {
        var name = fileName.ToLowerInvariant();
        var pat = pattern.ToLowerInvariant();

        if (pat.StartsWith('*') && pat.EndsWith('*') && pat.Length > 2)
            return name.Contains(pat[1..^1]);
        if (pat.StartsWith('*'))
            return name.EndsWith(pat[1..]);
        if (pat.EndsWith('*'))
            return name.StartsWith(pat[..^1]);

        return name == pat;
    }
}
