using System.IO;

namespace RevitMCP.Addin.LinkOperations;

/// <summary>
/// Identity of a link source and of the document it goes into (#163): the key behind exactly-once admission. A
/// path and its <c>.ifc.RVT</c> cache are the same source, and case, slashes and quotes do not make a file new.
/// Pure (System.IO path strings only).
/// </summary>
public static class LinkSourcePath
{
    public const string CacheSuffix = ".RVT";

    /// <summary>Upper-case, backslashes, full path where possible, quotes and long-path prefix removed; an IFC's cache folds onto the IFC.</summary>
    public static string Canonical(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var text = path!.Trim().Trim('"').Trim();
        if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) text = @"\\" + text.Substring(8);
        else if (text.StartsWith(@"\\?\", StringComparison.Ordinal)) text = text.Substring(4);
        text = text.Replace('/', '\\');
        try
        {
            if (Path.IsPathRooted(text)) text = Path.GetFullPath(text);
        }
        catch (Exception)
        {
            // Illegal characters: keep the text; the planner rejects such a path anyway.
        }
        text = text.TrimEnd('\\').ToUpperInvariant();
        if (text.EndsWith(".IFC" + CacheSuffix, StringComparison.Ordinal))
            text = text.Substring(0, text.Length - CacheSuffix.Length);
        return text;
    }

    /// <summary>ifc, rvt, or null when the extension is not one a link operation handles.</summary>
    public static string? KindOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var text = path!.Trim().Trim('"').ToLowerInvariant();
        if (text.EndsWith(".ifc.rvt", StringComparison.Ordinal)) return null;   // the cache, not a source
        if (text.EndsWith(".ifc", StringComparison.Ordinal)) return LinkKinds.Ifc;
        if (text.EndsWith(".rvt", StringComparison.Ordinal)) return LinkKinds.Rvt;
        return null;
    }

    /// <summary>The conversion cache Revit writes next to an IFC: <c>model.ifc.RVT</c>.</summary>
    public static string CachePathOf(string ifcPath) => ifcPath + CacheSuffix;

    /// <summary>The document part of the lock key: its file path, else (unsaved model) its title.</summary>
    public static string TargetKey(string? documentPath, string? documentTitle)
    {
        var path = Canonical(documentPath);
        if (path.Length > 0) return path;
        return "TITLE:" + (documentTitle ?? string.Empty).Trim().ToUpperInvariant();
    }

    /// <summary>True when an existing link's path (the IFC, or its cache) is the requested source.</summary>
    public static bool SameSource(string? existingLinkPath, string? requestedPath)
    {
        var existing = Canonical(existingLinkPath);
        return existing.Length > 0 && existing == Canonical(requestedPath);
    }

    public static bool IsNetworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var text = path!.Trim().Trim('"');
        return text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal);
    }
}
