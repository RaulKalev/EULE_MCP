using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin.Graph;

/// <summary>
/// Captures the "current model version" used for graph freshness. Must run on the Revit API thread.
///
/// Signal used, in order:
///  1. Workshared file-based models: <c>BasicFileInfo.Extract(path).LatestCentralVersion</c> +
///     <c>LatestCentralEpisodeGUID</c>, read from the central file when reachable, otherwise from
///     the local file header (which reflects the last sync).
///  2. Everything else (non-workshared, cloud/server workshared, or when the header read fails):
///     <c>Document.GetDocumentVersion(doc)</c> → <c>NumberOfSaves</c> + <c>VersionGUID</c>.
///  3. Unsaved documents: no version; the element count is the only freshness signal.
///
/// Limitations: neither signal changes for unsaved in-session edits — the element count is the
/// only hint for those. The local header only advances when the user syncs, so a stale local of a
/// file-based central whose central file is unreachable reports the last synced version.
/// </summary>
public static class RevitModelVersionReader
{
    public static ModelVersionSignal Read(UIApplication uiapp, Document doc)
    {
        var signal = new ModelVersionSignal();
        try { signal.RevitVersion = uiapp.Application.VersionNumber; } catch { }
        try { signal.Username = uiapp.Application.Username; } catch { }
        try { signal.IsWorkshared = doc.IsWorkshared; } catch { }

        var centralUserPath = string.Empty;
        if (signal.IsWorkshared)
        {
            try
            {
                var modelPath = doc.GetWorksharingCentralModelPath();
                if (modelPath != null)
                    centralUserPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath) ?? string.Empty;
            }
            catch { }
        }

        var localPath = string.Empty;
        try { localPath = doc.PathName ?? string.Empty; } catch { }

        signal.ModelPath = centralUserPath.Length > 0 ? centralUserPath : localPath;
        signal.ModelName = DeriveModelName(doc, centralUserPath, localPath);
        signal.ProjectKey = ReadProjectKey(doc);
        signal.ElementCount = CountElements(doc);

        var (value, source) = ReadVersion(doc, signal.IsWorkshared, centralUserPath, localPath);
        signal.Value = value;
        signal.Source = source;
        return signal;
    }

    /// <summary>Cheap native count of all non-type elements; used both for meta and for freshness.</summary>
    public static long CountElements(Document doc)
    {
        try
        {
            return new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();
        }
        catch
        {
            return 0;
        }
    }

    private static (string Value, string Source) ReadVersion(Document doc, bool isWorkshared, string centralUserPath, string localPath)
    {
        if (isWorkshared)
        {
            foreach (var (candidate, label) in new[] { (centralUserPath, "central file"), (localPath, "local file header") })
            {
                if (!IsReadableFile(candidate)) continue;
                try
                {
                    var info = BasicFileInfo.Extract(candidate);
                    if (info == null) continue;
                    var version = info.LatestCentralVersion;
                    var episode = info.LatestCentralEpisodeGUID;
                    if (version > 0)
                    {
                        return (
                            $"central:{version.ToString(CultureInfo.InvariantCulture)}:{episode:N}",
                            $"BasicFileInfo.LatestCentralVersion of the {label}");
                    }
                }
                catch
                {
                    // Header unreadable (locked, newer format, cloud path) — try the next candidate.
                }
            }
        }

        try
        {
            var dv = Document.GetDocumentVersion(doc);
            if (dv != null)
            {
                var suffix = isWorkshared ? "; local copy of a workshared model — advances on save/sync" : "; advances on each save";
                return (
                    $"saves:{dv.NumberOfSaves.ToString(CultureInfo.InvariantCulture)}:{dv.VersionGUID:N}",
                    "DocumentVersion (NumberOfSaves + VersionGUID" + suffix + ")");
            }
        }
        catch { }

        return (string.Empty, "unavailable (document has not been saved yet)");
    }

    private static bool IsReadableFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Path.IsPathRooted(path) && File.Exists(path); }
        catch { return false; }
    }

    private static string DeriveModelName(Document doc, string centralUserPath, string localPath)
    {
        var candidate = centralUserPath.Length > 0 ? centralUserPath : localPath;
        if (candidate.Length > 0)
        {
            var name = FileStem(candidate);
            if (name.Length > 0) return name;
        }
        try { return doc.Title ?? string.Empty; } catch { return string.Empty; }
    }

    private static string FileStem(string path)
    {
        try
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(stem)) return stem;
        }
        catch { }

        // Cloud/server paths may contain characters .NET Framework rejects — fall back to a manual split.
        var slash = path.LastIndexOfAny(new[] { '/', '\\' });
        var file = slash >= 0 ? path.Substring(slash + 1) : path;
        var dot = file.LastIndexOf('.');
        return dot > 0 ? file.Substring(0, dot) : file;
    }

    private static string ReadProjectKey(Document doc)
    {
        try
        {
            var info = doc.ProjectInformation;
            if (info == null) return string.Empty;
            var number = info.Number;
            if (!string.IsNullOrWhiteSpace(number)) return number.Trim();
            var name = info.Name;
            return string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}
