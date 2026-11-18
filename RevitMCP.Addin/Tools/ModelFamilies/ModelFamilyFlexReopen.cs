using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Tools.Families;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// The <c>reopenCopy</c> pass of revit_flex_model_family (#176, "save/reopen a test copy"): after the first trial has
/// returned, a temporary copy of the family is written, reopened as a path target and flexed again (type cases only),
/// and the two runs are compared per type. Never saves a document the user has open, never saves inside the session's
/// transaction group, and deletes the copy afterwards.
/// </summary>
internal static class ModelFamilyFlexReopen
{
    /// <summary>What is known about the target before the trial runs.</summary>
    internal sealed class Before
    {
        /// <summary>Why no copy can be compared, decided before the trial; null when a copy can be made.</summary>
        public string? SkipReason { get; init; }
        /// <summary>The target's file is open in Revit: the copy gets its own file name so it does not clash with it.</summary>
        public bool OpenInRevit { get; init; }
    }

    /// <summary>
    /// Called before the trial: whether the document the first pass reads has unsaved changes. Decided here, not
    /// afterwards, so the trial's rolled-back transactions cannot be what makes the document look modified.
    /// </summary>
    public static Before Prepare(UIApplication uiapp, McpToolRequest request)
    {
        try
        {
            var problems = new List<string>();
            var target = request.Arguments.TryGetValue(ModelFamilySession.TargetArg, out var raw) ? raw : null;
            var spec = ModelFamilyTargetSpec.Parse(target, ModelFamilySessionMode.Read, problems);
            if (problems.Count > 0) return new Before { SkipReason = string.Join(" ", problems) };
            switch (spec.Kind)
            {
                case ModelFamilyTargetKind.File:
                {
                    var open = OpenAt(uiapp, spec.Path);
                    if (open != null && SafeModified(open))
                        return new Before { SkipReason = $"'{spec.Path}' is open in Revit with unsaved changes; the first pass read those, a copy of the file would not have them. Save it there, or run without reopenCopy." };
                    return new Before { OpenInRevit = open != null };
                }
                case ModelFamilyTargetKind.OpenDocument:
                {
                    var document = OpenFamilyDocument(uiapp, spec.FamilyDocument);
                    if (document == null) return new Before { SkipReason = $"the open family document '{spec.FamilyDocument}' was not found." };
                    if (string.IsNullOrEmpty(document.PathName))
                        return new Before { SkipReason = $"'{document.Title}' has never been saved, so there is no file to copy; it is never saved by this tool. Save it, or run without reopenCopy." };
                    if (SafeModified(document))
                        return new Before { SkipReason = $"'{document.Title}' has unsaved changes; the first pass read those, a copy of its file would not have them. It is never saved by this tool: save it there, or run without reopenCopy." };
                    return new Before { OpenInRevit = true };
                }
                default:
                    return new Before();
            }
        }
        catch (Exception ex)
        {
            return new Before { SkipReason = $"the target could not be inspected: {ex.Message.TrimEnd('.')}." };
        }
    }

    public static JObject Run(UIApplication uiapp, McpToolRequest request, JObject firstResult, Before before, CancellationToken cancellationToken)
    {
        if (before.SkipReason != null) return Skipped(before.SkipReason);
        var problems = new List<string>();
        var target = request.Arguments.TryGetValue(ModelFamilySession.TargetArg, out var raw) ? raw : null;
        var spec = ModelFamilyTargetSpec.Parse(target, ModelFamilySessionMode.Read, problems);
        if (problems.Count > 0) return Skipped(string.Join(" ", problems));

        var policy = new FilePathPolicy();
        var folder = Path.Combine(Path.GetTempPath(), "RevitMCP", "ModelFamilyFlex", Guid.NewGuid().ToString("N"));
        string? copyPath = null;
        var location = "temp";
        try
        {
            switch (spec.Kind)
            {
                case ModelFamilyTargetKind.File:
                {
                    (copyPath, location) = CopyFile(spec.Path, folder, policy, before.OpenInRevit, out var error);
                    if (copyPath == null) return Skipped(error!);
                    break;
                }
                case ModelFamilyTargetKind.OpenDocument:
                {
                    var document = OpenFamilyDocument(uiapp, spec.FamilyDocument);
                    if (document == null || string.IsNullOrEmpty(document.PathName))
                        return Skipped($"the open family document '{spec.FamilyDocument}' was not found again.");
                    (copyPath, location) = CopyFile(document.PathName, folder, policy, before.OpenInRevit, out var error);
                    if (copyPath == null) return Skipped(error!);
                    break;
                }
                case ModelFamilyTargetKind.LoadedFamily:
                {
                    var (path, error) = SaveLoadedFamily(uiapp, request, spec.Family, folder, policy);
                    if (path == null) return Skipped(error!);
                    copyPath = path;
                    break;
                }
                default:
                    return Skipped("only a family, path or familyDocument target can be copied.");
            }

            // The second pass: the same types and checks on the copy, without the ranges and explicit cases.
            var arguments = new Dictionary<string, object?>(request.Arguments, StringComparer.Ordinal)
            {
                [ModelFamilySession.TargetArg] = new JObject { ["path"] = copyPath }
            };
            arguments.Remove(ModelFamilyFlexArgs.Ranges);
            arguments.Remove(ModelFamilyFlexArgs.Cases);
            arguments.Remove(ModelFamilyFlexArgs.Combine);
            arguments.Remove(ModelFamilyFlexArgs.ReopenCopy);
            var copyRequest = new McpToolRequest
            {
                RequestId = request.RequestId,
                ToolName = request.ToolName,
                Permission = request.Permission,
                ClientName = request.ClientName,
                Arguments = arguments
            };
            var second = ModelFamilySession.Trial(uiapp, copyRequest, "flex the reopened copy", ModelFamilyFlexExecutor.Body, cancellationToken);
            var secondCases = (second.Data as JObject)?["result"]?["cases"] as JArray;
            if (!second.Success || secondCases == null)
                return new JObject
                {
                    ["status"] = "failed",
                    ["copy"] = location,
                    ["message"] = $"The reopened copy could not be flexed: {second.Message}",
                    ["errors"] = new JArray(second.Errors)
                };
            var types = ModelFamilyFlexRun.Compare(firstResult["cases"] as JArray ?? new JArray(), secondCases);
            return new JObject
            {
                ["status"] = "compared",
                ["copy"] = location,
                ["equal"] = types.All(t => (bool?)t["equal"] == true),
                ["types"] = types,
                ["copySummary"] = (second.Data as JObject)?["result"]?["summary"]?.DeepClone(),
                ["note"] = "The family was saved to a temporary copy, reopened and flexed again (type cases only); the copy was deleted."
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Skipped($"the copy could not be made or flexed: {ex.Message.TrimEnd('.')}.");
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch { /* temp only */ }
            if (location == "besideOriginal" && copyPath != null)
            {
                try { if (File.Exists(copyPath)) File.Delete(copyPath); } catch { /* reported nowhere: the name says what it is */ }
            }
        }
    }

    private static JObject Skipped(string reason) => new() { ["status"] = "skipped", ["reason"] = reason };

    /// <summary>
    /// Copies a family file for the second pass: into a temporary folder under its own name (the family name stays),
    /// or as <c>&lt;name&gt;.flexcopy.rfa</c> when the original is open in Revit (two open documents with the same file
    /// name may clash); when the file access policy refuses that folder, beside the original as
    /// <c>&lt;name&gt;.flexcopy.&lt;guid&gt;.rfa</c>.
    /// </summary>
    private static (string? Path, string Location) CopyFile(string source, string folder, FilePathPolicy policy, bool openInRevit, out string? error)
    {
        error = null;
        var temp = Path.Combine(folder, openInRevit ? $"{Path.GetFileNameWithoutExtension(source)}.flexcopy.rfa" : Path.GetFileName(source));
        if (policy.ValidateRead(temp) == null)
        {
            Directory.CreateDirectory(folder);
            File.Copy(source, temp, overwrite: true);
            return (temp, "temp");
        }
        var beside = Path.Combine(Path.GetDirectoryName(source) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(source)}.flexcopy.{Guid.NewGuid():N}.rfa");
        var refused = policy.ValidateRead(beside) ?? policy.ValidateWrite(beside);
        if (refused != null)
        {
            error = $"the file access policy allows neither the temporary folder nor a copy beside the original ({refused}).";
            return (null, "none");
        }
        File.Copy(source, beside, overwrite: false);
        return (beside, "besideOriginal");
    }

    /// <summary>A loaded family: opened as a copy (Document.EditFamily), saved to the temporary folder and closed.</summary>
    private static (string? Path, string? Error) SaveLoadedFamily(UIApplication uiapp, McpToolRequest request, string selector, string folder, FilePathPolicy policy)
    {
        var project = RevitDocumentResolver.Resolve(uiapp, request, out var documentError)?.Document;
        if (project == null) return (null, documentError ?? "no project document is open.");
        Family? family = null;
        if (long.TryParse(selector, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
            family = project.GetElement(new ElementId(id)) as Family;
        else if (FamilyInventory.LoadedByName(project).TryGetValue(selector, out var byName))
            family = byName;
        if (family == null) return (null, $"the family '{selector}' was not found again in '{project.Title}'.");
        var name = FamilyTypeSupport.SafeName(family);
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        var path = Path.Combine(folder, name + ".rfa");
        if (policy.ValidateRead(path) is { } readError)
            return (null, $"the file access policy refuses the temporary folder ({readError}), and a loaded family has no file to put a copy beside. Allow %TEMP% for reading, or flex the family's file with target.path.");
        var familyDoc = FamilyDocumentGuard.EditFamily(project, family, out var owned);
        if (familyDoc == null) return (null, $"Revit could not open '{name}' for a copy.");
        try
        {
            if (!owned) return (null, $"'{name}' is open in the Family Editor; it is never saved by this tool. Close it, or flex that window with familyDocument.");
            Directory.CreateDirectory(folder);
            familyDoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            return (path, null);
        }
        finally
        {
            FamilyDocumentGuard.CloseIfOwned(familyDoc, owned);
        }
    }

    private static Document? OpenAt(UIApplication uiapp, string path)
    {
        string Full(string p)
        {
            try { return Path.GetFullPath(p); }
            catch { return p; }
        }
        var wanted = Full(path);
        foreach (Document open in uiapp.Application.Documents)
        {
            try
            {
                if (!string.IsNullOrEmpty(open.PathName) && string.Equals(Full(open.PathName), wanted, StringComparison.OrdinalIgnoreCase)) return open;
            }
            catch { /* a document being closed */ }
        }
        return null;
    }

    private static Document? OpenFamilyDocument(UIApplication uiapp, string selector)
    {
        if (string.Equals(selector, "active", StringComparison.OrdinalIgnoreCase)) return RevitDocumentResolver.ActiveFamilyDocument(uiapp);
        var matches = ModelFamilySession.OpenFamilyDocuments(uiapp).Where(d =>
            string.Equals(d.Title, selector, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ModelFamilySession.TitleName(d), selector, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ModelFamilySession.TitleName(d) + ".rfa", selector, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(d.PathName) && string.Equals(d.PathName, selector, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool SafeModified(Document document)
    {
        try { return document.IsModified; }
        catch { return true; }
    }
}
