using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Parameters;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Families;

/// <summary>revit_preview_edit_detail_family: checks an edit of an existing Detail Item family; changes nothing.</summary>
public sealed class PreviewEditDetailFamilyTool : IRevitMcpTool
{
    public string Name => "revit_preview_edit_detail_family";

    public string Description =>
        "Previews editing an existing Detail Item family, without changing the model or any file. Same arguments as " +
        "revit_edit_detail_family. Opens the family (read-only, closed without saving) to check what would be added and " +
        "removed: the element ids remove names, parameters that a formula or a linked element still uses, type values, " +
        "line styles, filled region types and label families against the project, and the save path.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(DetailFamilyEditExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_edit_detail_family: adds to and removes from an existing Detail Item family, then saves or reloads it.</summary>
public sealed class EditDetailFamilyTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_edit_detail_family";

    public string Description =>
        "Edits an existing Detail Item family. Requires approval. Target: family (loaded in the project; reloaded into it " +
        "afterwards as one undo step, keeping the project's parameter values unless overwriteParameterValues) or path (an " +
        ".rfa; saved back with overwrite=true, or to savePath). Adds with the same arguments as revit_create_detail_family " +
        "(lines, rectangles, arcs, circles, lineStyle, lineStyles, filledRegions, filledRegionTypes, parameters, labels, " +
        "labelFamily, types); remove {curves?, filledRegions?, labels?, textNotes?: true | [ids], parameters?: [names]} " +
        "runs first. purge defaults to false. A failed edit leaves the file and the loaded family as they were. Run " +
        "revit_preview_edit_detail_family first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(DetailFamilyEditExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => DetailFamilyEditExecutor.Summary(request);
}

/// <summary>What an opened family document holds, as the edit's checks need it.</summary>
internal sealed class FamilyEditState
{
    public Dictionary<string, KnownFamilyParameter> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> TypeNames { get; set; } = new();
    public List<string> NestedFamilyNames { get; } = new();
    /// <summary>Removable element ids by remove key: curves, filledRegions, labels, textNotes.</summary>
    public Dictionary<string, List<long>> Elements { get; } = new(StringComparer.Ordinal);
    public string CategoryName { get; set; } = string.Empty;
    public bool IsDetailItem { get; set; }
}

internal static class DetailFamilyEditExecutor
{
    private static readonly Dictionary<string, string> KindLabels = new(StringComparer.Ordinal)
    {
        ["curves"] = "a detail line",
        ["filledRegions"] = "a filled region",
        ["labels"] = "a nested annotation (label) instance",
        ["textNotes"] = "a text note"
    };

    public static string Summary(McpToolRequest request)
    {
        var errors = new List<string>();
        var edit = DetailFamilySpecParser.ParseEdit(request.Arguments, errors);
        var document = ToolArguments.GetString(request.Arguments, "document").Trim();
        var target = edit.IsPathTarget
            ? $"the family file '{edit.Path}'"
            : $"the family '{edit.Family}' loaded in {(document.Length > 0 ? $"'{document}'" : "the active document")}";
        var parts = new List<string>();
        var removals = edit.Remove.ElementKinds().Where(k => k.Removal.Any)
            .Select(k => k.Removal.All ? $"all {k.Key}" : $"{k.Key} {string.Join(", ", k.Removal.Ids.Take(10))}{(k.Removal.Ids.Count > 10 ? ", …" : string.Empty)}")
            .ToList();
        if (edit.Remove.Parameters.Count > 0) removals.Add($"parameters {string.Join(", ", edit.Remove.Parameters.Select(p => $"'{p}'"))}");
        if (removals.Count > 0) parts.Add($"remove {string.Join("; ", removals)}");
        if (edit.AddsAnything) parts.Add($"add {DetailFamilyExecutor.DescribeContent(edit.Content)}");
        var save = edit.IsPathTarget
            ? edit.SavePath.Length > 0 ? $"save it to '{edit.SavePath}'{(edit.Overwrite ? " (replacing an existing file)" : string.Empty)}" : $"save it back to '{edit.Path}', replacing it"
            : $"load it back into the project as one undo step ({(edit.OverwriteParameterValues ? "with the family's parameter values" : "keeping the project's parameter values")})" +
              (edit.SavePath.Length > 0 ? $" and save a copy to '{edit.SavePath}'" : string.Empty);
        return $"Edit {target}: {string.Join("; ", parts)}{(edit.Content.Purge ? "; purge unused elements" : string.Empty)}; then {save}.";
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var resolved = ToolDocument.Resolve(uiapp, request, out var failure);
        if (resolved == null) return failure!;
        var doc = resolved.Document;
        if (doc.IsFamilyDocument)
            return DetailFamilyExecutor.Fail(request, "The target is a family document: run this from a project, whose line styles, filled region types and label families the edit copies, and into which a loaded family is reloaded.");

        var problems = new List<string>();
        var warnings = new List<string>();
        var edit = DetailFamilySpecParser.ParseEdit(request.Arguments, problems);
        var spec = edit.Content;

        // The target: a loaded family, or a file that is not open in Revit.
        Family? family = null;
        var familyName = string.Empty;
        if (!edit.IsPathTarget && edit.Family.Length > 0)
        {
            family = ResolveFamily(doc, edit.Family);
            if (family == null)
            {
                var loaded = FamilyInventory.LoadedByName(doc);
                var close = DetailFamilySpecParser.SuggestNames(edit.Family,
                    loaded.Where(f => IsDetailItem(f.Value.FamilyCategory)).Select(f => f.Key));
                problems.Add($"family: '{edit.Family}' is not a family loaded in '{doc.Title}'" +
                             $"{(close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : ". Use revit_list_families to find it, or pass path.")}");
            }
            else
            {
                familyName = FamilyTypeSupport.SafeName(family);
                if (SafeInPlace(family)) problems.Add($"'{familyName}' is an in-place family; only loadable families can be edited.");
                else if (!family.IsEditable) problems.Add($"'{familyName}' cannot be opened for editing.");
                else if (!IsDetailItem(family.FamilyCategory))
                    problems.Add($"'{familyName}' is a {FamilyInventory.CategoryOf(family)} family; this tool edits Detail Item families.");
                if (OpenFamilyDocument(uiapp, familyName) is { } open)
                    problems.Add($"'{familyName}' is open in the Family Editor ('{open}'): close it first. The tool edits its own copy and must not touch your open window.");
                if (doc.IsReadOnly) problems.Add($"'{doc.Title}' is read-only; the edited family cannot be loaded back into it.");
            }
        }
        else if (edit.IsPathTarget && edit.Path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(edit.Path))
        {
            var policy = new FilePathPolicy();
            var readError = policy.ValidateRead(edit.Path);
            if (readError != null) problems.Add($"path: {readError}");
            else if (!File.Exists(edit.Path)) problems.Add($"path '{edit.Path}' does not exist.");
            else
            {
                familyName = DetailFamilySpecParser.FamilyNameOfPath(edit.Path);
                if (edit.SavePath.Length == 0 && policy.ValidateWrite(edit.Path) is { } writeError)
                    problems.Add($"path: saving back is not allowed: {writeError}");
                if (OpenDocumentAt(uiapp, edit.Path) is { } open)
                    problems.Add($"'{edit.Path}' is open in Revit ('{open}'): close it first, or edit the loaded family with family=.");
                FileVersionWarning(uiapp, edit.Path, warnings);
            }
        }
        spec.FamilyName = familyName;

        // Where the edited family is saved.
        string? savePath = null;
        var saveExists = false;
        if (edit.SavePath.Length > 0 && familyName.Length > 0)
        {
            var (path, _, error) = DetailFamilySpecParser.ResolveEditSavePath(edit.SavePath, familyName, keepName: !edit.IsPathTarget);
            if (error != null) problems.Add($"savePath: {error}");
            else
            {
                savePath = path!;
                var writeError = new FilePathPolicy().ValidateWrite(savePath);
                if (writeError != null) problems.Add($"savePath: {writeError}");
                saveExists = File.Exists(savePath);
                if (saveExists && !edit.Overwrite)
                    problems.Add($"'{savePath}' already exists: pass overwrite=true to replace it, or choose another name or folder.");
                var folder = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                    warnings.Add($"The folder '{folder}' does not exist yet; it will be created.");
            }
        }
        var finalPath = edit.IsPathTarget ? savePath ?? edit.Path : savePath;

        if (familyName.Length == 0 || problems.Any(IsTargetProblem))
            return Refuse(request, apply, problems, warnings, familyName, edit, sw);

        Document? familyDoc = null;
        var keepOpen = false;
        var tempFolder = Path.Combine(Path.GetTempPath(), "RevitMCP", "DetailFamilyEdit", Guid.NewGuid().ToString("N"));
        using var dialogs = new ModalDialogGuard(uiapp);
        try
        {
            var openBefore = uiapp.Application.Documents.Cast<Document>().ToList();
            familyDoc = family != null ? doc.EditFamily(family) : uiapp.Application.OpenDocumentFile(edit.Path);
            if (familyDoc == null)
                return DetailFamilyExecutor.Fail(request, $"Revit could not open '{(family != null ? familyName : edit.Path)}'.", warnings);
            if (openBefore.Any(d => d.Equals(familyDoc)))
            {
                keepOpen = true;
                return DetailFamilyExecutor.Fail(request, $"'{familyName}' was already open in Revit: close it first. Nothing was changed.", warnings);
            }

            var state = ReadState(familyDoc);
            if (!state.IsDetailItem)
                problems.Add($"'{familyName}' is a {(state.CategoryName.Length > 0 ? state.CategoryName : "category-less")} family; this tool edits Detail Item families.");
            var view = DetailFamilyBuilder.FindSketchView(familyDoc);
            if (view == null && (spec.Curves.Count > 0 || spec.FilledRegions.Count > 0 || spec.Labels.Count > 0))
                problems.Add($"'{familyName}' has no plan or drafting view to draw in.");

            // What remove names, then whether what is added refers to parameters that stay or are added.
            var removalsByKind = new Dictionary<string, List<long>>(StringComparer.Ordinal);
            var removedIds = new HashSet<long>();
            foreach (var (key, removal) in edit.Remove.ElementKinds())
            {
                var ids = removal.Any
                    ? DetailFamilySpecParser.ResolveElementRemoval(removal, $"remove.{key}", KindLabels[key], state.Elements[key],
                        id => Describe(familyDoc, id), problems)
                    : new List<long>();
                removalsByKind[key] = ids;
                removedIds.UnionWith(ids);
            }
            var parameterRemovals = DetailFamilySpecParser.PlanParameterRemovals(edit.Remove.Parameters, state.Parameters, removedIds, problems);
            var removedNames = new HashSet<string>(edit.Remove.Parameters, StringComparer.OrdinalIgnoreCase);
            DetailFamilySpecParser.CheckParameterReferences(spec, problems, state.Parameters, removedNames);
            DetailFamilySpecParser.CheckTypeValues(spec, problems, state.Parameters, removedNames);
            foreach (var parameter in spec.Parameters.Where(p => p.Name.Length > 0 && state.Parameters.ContainsKey(p.Name) && !removedNames.Contains(p.Name)))
            {
                var known = state.Parameters[parameter.Name];
                if (parameter.Spec?.Friendly != null && known.Friendly != null && parameter.Spec.Friendly != known.Friendly)
                    problems.Add($"{parameter.Source}: the family already has '{known.Name}' as a {known.DataTypeLabel} parameter, not {parameter.DataType}. " +
                                 "Remove it in the same edit (remove.parameters) to add it again with the new type.");
                else if (known.Formula.Length > 0 && parameter.Value != null)
                    problems.Add($"{parameter.Source}: the family's parameter '{known.Name}' has the formula '{known.Formula}'; it takes no value.");
                else
                    warnings.Add($"{parameter.Source}: the family already has '{known.Name}'; it is kept as it is and only its value or formula set.");
            }

            var plan = DetailFamilyBuilder.Plan(uiapp, doc, spec, problems, warnings, state.NestedFamilyNames);
            if (!edit.IsPathTarget && !edit.OverwriteParameterValues)
            {
                var existingTypes = spec.Types.Where(t => state.TypeNames.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).Select(t => t.Name).ToList();
                if (existingTypes.Count > 0)
                    warnings.Add($"Type(s) {string.Join(", ", existingTypes.Select(n => $"'{n}'"))} already exist: the reload keeps the project's parameter " +
                                 "values for types the project has, so their new values reach the project only with overwriteParameterValues=true.");
            }

            if (!apply)
            {
                sw.Stop();
                return new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = true,
                    Message = problems.Count == 0
                        ? $"Preview: ready to edit '{familyName}': remove {removedIds.Count} element(s) and {parameterRemovals.Count} parameter(s); " +
                          $"add {spec.Curves.Count} curve(s), {spec.FilledRegions.Count} filled region(s), {spec.Labels.Count} label(s), " +
                          $"{spec.Parameters.Count} parameter(s), {spec.Types.Count} type(s); then {(edit.IsPathTarget ? $"save to '{finalPath}'" : "reload it into the project")}."
                        : $"Preview: {problems.Count} problem(s) to fix before editing the family.",
                    Data = new
                    {
                        ready = problems.Count == 0,
                        problems,
                        family = familyName,
                        target = edit.IsPathTarget ? "path" : "family",
                        familyId = family?.Id.Value,
                        path = edit.IsPathTarget ? edit.Path : null,
                        savePath = finalPath,
                        fileExists = edit.IsPathTarget && savePath == null || saveExists,
                        willReplaceFile = edit.IsPathTarget && savePath == null || saveExists && edit.Overwrite,
                        load = edit.IsPathTarget ? null : new
                        {
                            status = "willReload",
                            documentTitle = doc.Title,
                            parameterValues = edit.OverwriteParameterValues ? "family" : "project"
                        },
                        existing = new
                        {
                            types = state.TypeNames,
                            parameters = state.Parameters.Values.Where(p => !p.IsBuiltIn).Select(p => new
                            {
                                name = p.Name,
                                dataType = p.DataTypeLabel,
                                instance = p.IsInstance,
                                shared = p.IsShared,
                                formula = p.Formula.Length > 0 ? p.Formula : null,
                                linkedElements = p.AssociatedElementIds.Count
                            }).ToList(),
                            elements = state.Elements.ToDictionary(e => e.Key, e => e.Value.Count)
                        },
                        remove = new
                        {
                            curves = removalsByKind["curves"],
                            filledRegions = removalsByKind["filledRegions"],
                            labels = removalsByKind["labels"],
                            textNotes = removalsByKind["textNotes"],
                            parameters = parameterRemovals.Select(p => new { name = p.Name, linkedElementsRemoved = p.UnlinkedElementIds }).ToList()
                        },
                        add = new
                        {
                            curves = spec.CountsByKind(),
                            lineStyles = DetailFamilyBuilder.DescribeLineStyles(plan),
                            parameters = plan.Parameters.Items.Select(DetailFamilyExtrasRevit.DescribePlan).ToList(),
                            sharedParameterFile = plan.Parameters.SharedFilePath,
                            filledRegions = DetailFamilyBuilder.DescribeFilledRegions(spec, plan),
                            labels = DetailFamilyBuilder.DescribeLabels(plan),
                            parameterLabels = DetailFamilyBuilder.DescribeParameterLabels(spec, plan),
                            visibility = DetailFamilyBuilder.DescribeVisibility(spec)
                        },
                        types = DetailFamilyBuilder.DescribeTypes(spec, state.TypeNames),
                        purge = spec.Purge
                    },
                    Warnings = warnings,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            if (problems.Count > 0)
                return Invalid(request, problems, warnings, sw);

            Directory.CreateDirectory(tempFolder);

            // 1. Remove: elements first (their links go with them), then the parameters, in order.
            var removed = Remove(familyDoc, removalsByKind, parameterRemovals, warnings);

            // 2. Add, with the same steps as revit_create_detail_family.
            var drawn = DetailFamilyBuilder.Draw(uiapp, doc, familyDoc, view!, spec, plan, tempFolder, warnings, cancellationToken);
            if (drawn.NothingDrawn)
                return new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Status = "transaction_failed",
                    Message = "Revit drew none of the curves; nothing was saved or reloaded.",
                    Errors = drawn.CurveFailures,
                    Warnings = warnings,
                    DurationMs = sw.ElapsedMilliseconds
                };

            // 3. Purge (only when asked: an edit keeps what the family had).
            var purged = spec.Purge ? DetailFamilyBuilder.Purge(familyDoc, drawn.ProtectedIds, warnings, cancellationToken) : new Dictionary<string, int>();

            // 4. Save to a temporary file, then into place; a loaded family is reloaded into the project.
            string? tempPath = null;
            if (finalPath != null)
            {
                tempPath = Path.Combine(tempFolder, Path.GetFileName(finalPath));
                familyDoc.SaveAs(tempPath, new SaveAsOptions { OverwriteExistingFile = true });
            }

            object? load = null;
            if (family != null)
            {
                var (loadResult, loadError) = Reload(doc, familyDoc, family, familyName, edit.OverwriteParameterValues);
                if (loadError != null)
                    return DetailFamilyExecutor.Fail(request, $"{loadError} The project and its loaded family are unchanged; no file was written.", warnings);
                load = loadResult;
            }

            string? savedTo = null;
            if (finalPath != null)
            {
                familyDoc.Close(false);
                familyDoc = null;
                if (!edit.IsPathTarget || savePath != null)
                {
                    if (File.Exists(finalPath) && !edit.Overwrite)
                        return FailAfterLoad(request, family != null, $"'{finalPath}' appeared while the family was being edited; it was not replaced.", load, warnings);
                }
                try
                {
                    PlaceFile(tempPath!, finalPath);
                    savedTo = finalPath;
                }
                catch (Exception ex)
                {
                    return FailAfterLoad(request, family != null, $"Saving to '{finalPath}' failed: {ex.Message}", load, warnings);
                }
                if (family != null && savePath != null)
                {
                    var storeError = FamilyPathStore.Default().Remember(CheckpointChangeTracker.ModelIdOf(doc), new List<(string, string)> { (familyName, savePath) });
                    if (storeError != null) warnings.Add(storeError);
                }
            }

            warnings.AddRange(dialogs.Dismissed.Distinct().Select(d => $"Closed a Revit dialog: {d}"));
            if (drawn.CurveFailures.Count > 0)
                warnings.Add($"{drawn.CurveFailures.Count} curve(s) were not drawn: {string.Join("; ", drawn.CurveFailures.Take(5))}");

            sw.Stop();
            var removedCount = removed.ElementsRemoved;
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Edited '{familyName}': removed {removedCount} element(s) and {removed.ParametersRemoved} parameter(s); added " +
                          $"{drawn.CurvesDrawn} of {spec.Curves.Count} curve(s)" +
                          $"{(spec.FilledRegions.Count > 0 ? $", {drawn.RegionsDrawn} of {spec.FilledRegions.Count} filled region(s)" : string.Empty)}" +
                          $"{(drawn.Parameters.Count > 0 ? $", {drawn.Parameters.Count} parameter(s)" : string.Empty)}" +
                          $"{(plan.TextLabelCount > 0 ? $", {plan.TextLabelCount} text label(s)" : string.Empty)}" +
                          $"{(drawn.ParameterLabels.Count > 0 ? $", {drawn.ParameterLabels.Count(r => DetailFamilyBuilder.StatusOf(r) == "placed")} of {plan.ParameterLabelCount} parameter label(s)" : string.Empty)}" +
                          $"{(drawn.Types.Count > 0 ? $", {drawn.Types.Count} type(s)" : string.Empty)}" +
                          $"{(spec.Purge ? $"; purged {purged.Values.Sum()} unused element(s)" : string.Empty)}" +
                          $"{(load != null ? "; reloaded into the project (one undo step)" : string.Empty)}" +
                          $"{(savedTo != null ? $"; saved to '{savedTo}'" : string.Empty)}.",
                Data = new
                {
                    family = familyName,
                    target = edit.IsPathTarget ? "path" : "family",
                    path = edit.IsPathTarget ? edit.Path : null,
                    savedTo,
                    replacedFile = edit.IsPathTarget && savePath == null || saveExists,
                    removed = removed.Data,
                    curvesDrawn = drawn.CurvesDrawn,
                    curvesFailed = drawn.CurveFailures,
                    lineStyles = drawn.LineStyles,
                    parameters = drawn.Parameters,
                    sharedParameterFile = plan.Parameters.AnyShared ? plan.Parameters.SharedFilePath : null,
                    filledRegionTypes = drawn.RegionTypes,
                    filledRegions = drawn.Regions,
                    labels = drawn.Labels,
                    parameterLabels = drawn.ParameterLabels,
                    visibility = drawn.Visibility,
                    types = drawn.Types,
                    purged,
                    load
                },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            return DetailFamilyExecutor.Fail(request, "The request was cancelled; nothing was saved or reloaded.", warnings);
        }
        catch (Exception ex)
        {
            return DetailFamilyExecutor.Fail(request, $"{(apply ? "Editing" : "Checking")} the family failed: {ex.Message.TrimEnd('.')}. Nothing was saved or reloaded.", warnings);
        }
        finally
        {
            if (!keepOpen)
            {
                try { familyDoc?.Close(false); } catch { /* already closed */ }
            }
            try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, recursive: true); } catch { /* temp only */ }
        }
    }

    // ── Target ───────────────────────────────────────────────────────────────

    /// <summary>Problems that mean the family cannot (or must not) be opened at all.</summary>
    private static bool IsTargetProblem(string problem) =>
        problem.StartsWith("family:", StringComparison.Ordinal) || problem.StartsWith("path", StringComparison.Ordinal) ||
        problem.Contains("is open in") || problem.Contains("in-place family") || problem.Contains("cannot be opened for editing") ||
        problem.Contains("this tool edits Detail Item families") || problem.StartsWith("Give family", StringComparison.Ordinal) ||
        problem.StartsWith("Nothing to edit", StringComparison.Ordinal);

    private static Family? ResolveFamily(Document doc, string value)
    {
        if (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
            return doc.GetElement(new ElementId(id)) as Family;
        return FamilyInventory.LoadedByName(doc).TryGetValue(value, out var family) ? family : null;
    }

    private static bool SafeInPlace(Family family)
    {
        try { return family.IsInPlace; }
        catch { return false; }
    }

    private static bool IsDetailItem(Category? category)
    {
        try { return category != null && category.Id.Value == (long)BuiltInCategory.OST_DetailComponents; }
        catch { return false; }
    }

    /// <summary>The title of an open family document of that family, if the user has it open in the Family Editor.</summary>
    private static string? OpenFamilyDocument(UIApplication uiapp, string familyName)
    {
        foreach (Document open in uiapp.Application.Documents)
        {
            if (!open.IsFamilyDocument) continue;
            var title = open.Title ?? string.Empty;
            var fileName = string.IsNullOrEmpty(open.PathName) ? string.Empty : Path.GetFileNameWithoutExtension(open.PathName);
            if (string.Equals(title, familyName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(title, familyName + ".rfa", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, familyName, StringComparison.OrdinalIgnoreCase))
                return title;
        }
        return null;
    }

    /// <summary>The title of an open document saved at <paramref name="path"/>, if any.</summary>
    private static string? OpenDocumentAt(UIApplication uiapp, string path)
    {
        string Full(string p)
        {
            try { return Path.GetFullPath(p); }
            catch { return p; }
        }
        var wanted = Full(path);
        foreach (Document open in uiapp.Application.Documents)
        {
            if (string.IsNullOrEmpty(open.PathName)) continue;
            if (string.Equals(Full(open.PathName), wanted, StringComparison.OrdinalIgnoreCase)) return open.Title;
        }
        return null;
    }

    /// <summary>A file of an older Revit is upgraded when it is opened; saving writes it in this version.</summary>
    private static void FileVersionWarning(UIApplication uiapp, string path, List<string> warnings)
    {
        try
        {
            var format = BasicFileInfo.Extract(path).Format;
            var version = uiapp.Application.VersionNumber;
            if (!string.IsNullOrEmpty(format) && !string.Equals(format, version, StringComparison.Ordinal))
                warnings.Add($"'{Path.GetFileName(path)}' is a Revit {format} file; it is upgraded when opened and saved as Revit {version}, " +
                             $"which Revit {format} cannot open.");
        }
        catch
        {
            // The version is only a warning.
        }
    }

    // ── State of the opened family ───────────────────────────────────────────

    private static FamilyEditState ReadState(Document familyDoc)
    {
        var state = new FamilyEditState();
        var category = familyDoc.OwnerFamily?.FamilyCategory;
        state.CategoryName = category?.Name ?? string.Empty;
        state.IsDetailItem = IsDetailItem(category);
        state.TypeNames = DetailFamilyBuilder.TypeNames(familyDoc);

        var manager = familyDoc.FamilyManager;
        var byId = new Dictionary<long, KnownFamilyParameter>();
        foreach (FamilyParameter parameter in manager.Parameters)
        {
            try
            {
                var definition = parameter.Definition;
                ForgeTypeId? dataType = null;
                try { dataType = definition.GetDataType(); }
                catch { /* some built-in parameters have none */ }
                var known = new KnownFamilyParameter
                {
                    Name = definition.Name,
                    Friendly = ParameterRevitTypes.FriendlyOf(dataType),
                    DataTypeLabel = ParameterRevitTypes.SpecLabel(dataType),
                    IsInstance = parameter.IsInstance,
                    IsShared = parameter.IsShared,
                    IsBuiltIn = parameter.Id.Value < 0 || definition is InternalDefinition { BuiltInParameter: not BuiltInParameter.INVALID },
                    Formula = parameter.Formula ?? string.Empty
                };
                foreach (Parameter linked in parameter.AssociatedParameters)
                {
                    var element = linked.Element;
                    if (element != null) known.AssociatedElementIds.Add(element.Id.Value);
                }
                if (!state.Parameters.ContainsKey(known.Name)) state.Parameters[known.Name] = known;
                byId[parameter.Id.Value] = known;
            }
            catch
            {
                // A parameter Revit cannot describe is left out; removing it by name then reports it as unknown.
            }
        }
        foreach (var dimension in new FilteredElementCollector(familyDoc).OfClass(typeof(Dimension)).Cast<Dimension>())
        {
            try
            {
                var label = dimension.FamilyLabel;
                if (label != null && byId.TryGetValue(label.Id.Value, out var known)) known.AssociatedElementIds.Add(dimension.Id.Value);
            }
            catch
            {
                // Not every dimension can carry a label.
            }
        }

        foreach (var nested in new FilteredElementCollector(familyDoc).OfClass(typeof(Family)).Cast<Family>())
        {
            var name = FamilyTypeSupport.SafeName(nested);
            if (name.Length > 0) state.NestedFamilyNames.Add(name);
        }

        // Curves drawn in a filled region's (or any) sketch belong to it: they are not removable detail lines.
        var sketched = new HashSet<long>();
        foreach (var sketch in new FilteredElementCollector(familyDoc).OfClass(typeof(Sketch)).Cast<Sketch>())
        {
            try { foreach (var id in sketch.GetAllElements()) sketched.Add(id.Value); }
            catch { /* older sketches report nothing */ }
        }
        state.Elements["curves"] = new FilteredElementCollector(familyDoc).OfClass(typeof(CurveElement)).Cast<CurveElement>()
            .Where(c => c.CurveElementType is CurveElementType.DetailCurve or CurveElementType.SymbolicCurve && !sketched.Contains(c.Id.Value))
            .Select(c => c.Id.Value).ToList();
        state.Elements["filledRegions"] = new FilteredElementCollector(familyDoc).OfClass(typeof(FilledRegion)).ToElementIds().Select(id => id.Value).ToList();
        state.Elements["labels"] = new FilteredElementCollector(familyDoc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
            .Where(i => IsGenericAnnotation(i))
            .Select(i => i.Id.Value).ToList();
        state.Elements["textNotes"] = new FilteredElementCollector(familyDoc).OfClass(typeof(TextNote)).ToElementIds().Select(id => id.Value).ToList();
        return state;
    }

    private static bool IsGenericAnnotation(FamilyInstance instance)
    {
        try { return instance.Symbol?.Family?.FamilyCategory?.Id.Value == (long)BuiltInCategory.OST_GenericAnnotation; }
        catch { return false; }
    }

    private static string? Describe(Document familyDoc, long id)
    {
        var element = familyDoc.GetElement(new ElementId(id));
        if (element == null) return null;
        var category = element.Category?.Name;
        return $"a {element.GetType().Name}{(string.IsNullOrEmpty(category) ? string.Empty : $" ({category})")}";
    }

    // ── Remove ───────────────────────────────────────────────────────────────

    private sealed class RemoveResult
    {
        public int ElementsRemoved { get; set; }
        public int ParametersRemoved { get; set; }
        public object Data { get; set; } = new();
    }

    /// <summary>Deletes the elements and then the parameters, each in its own sub-transaction, so one refusal is reported and the rest go on.</summary>
    private static RemoveResult Remove(Document familyDoc, Dictionary<string, List<long>> elements, List<ParameterRemovalPlan> parameters, List<string> warnings)
    {
        var result = new RemoveResult();
        var byKind = new Dictionary<string, object>(StringComparer.Ordinal);
        var parameterResults = new List<object>();
        if (elements.Values.All(ids => ids.Count == 0) && parameters.Count == 0)
        {
            result.Data = new { curves = new List<long>(), filledRegions = new List<long>(), labels = new List<long>(), textNotes = new List<long>(), parameters = parameterResults };
            return result;
        }

        using var tx = new Transaction(familyDoc, "Revit MCP - Remove from family");
        tx.Start();
        foreach (var pair in elements)
        {
            var removed = new List<long>();
            var failed = new List<string>();
            foreach (var id in pair.Value)
            {
                using var sub = new SubTransaction(familyDoc);
                sub.Start();
                try
                {
                    familyDoc.Delete(new ElementId(id));
                    sub.Commit();
                    removed.Add(id);
                }
                catch (Exception ex)
                {
                    sub.RollBack();
                    failed.Add($"{id}: {ex.Message}");
                }
            }
            if (failed.Count > 0) warnings.Add($"remove.{pair.Key}: {failed.Count} element(s) were not removed: {string.Join("; ", failed.Take(5))}");
            result.ElementsRemoved += removed.Count;
            byKind[pair.Key] = removed;
        }

        var manager = familyDoc.FamilyManager;
        foreach (var plan in parameters)
        {
            var parameter = manager.get_Parameter(plan.Name);
            if (parameter == null)
            {
                parameterResults.Add(new { name = plan.Name, status = "failed", reason = "The family no longer has this parameter." });
                continue;
            }
            using var sub = new SubTransaction(familyDoc);
            sub.Start();
            try
            {
                manager.RemoveParameter(parameter);
                sub.Commit();
                result.ParametersRemoved++;
                parameterResults.Add(new { name = plan.Name, status = "removed" });
            }
            catch (Exception ex)
            {
                sub.RollBack();
                parameterResults.Add(new { name = plan.Name, status = "failed", reason = ex.Message });
                warnings.Add($"Parameter '{plan.Name}' was not removed: {ex.Message}");
            }
        }
        TransactionCommitGuard.CommitOrThrow(tx);

        result.Data = new
        {
            curves = byKind.TryGetValue("curves", out var c) ? c : new List<long>(),
            filledRegions = byKind.TryGetValue("filledRegions", out var r) ? r : new List<long>(),
            labels = byKind.TryGetValue("labels", out var l) ? l : new List<long>(),
            textNotes = byKind.TryGetValue("textNotes", out var t) ? t : new List<long>(),
            parameters = parameterResults
        };
        return result;
    }

    // ── Reload and save ──────────────────────────────────────────────────────

    /// <summary>
    /// Loads the edited family document back into the project. Revit records Document.LoadFamily(Document,
    /// IFamilyLoadOptions) as one undo step of its own; it must run with no transaction open in the project.
    /// </summary>
    private static (object? Result, string? Error) Reload(Document projectDoc, Document familyDoc, Family family, string familyName, bool overwriteParameterValues)
    {
        var before = new HashSet<string>(FamilyInventory.Symbols(projectDoc, family).Select(FamilyTypeSupport.SafeName), StringComparer.OrdinalIgnoreCase);
        var options = new FamilyLoadOptions(overwriteParameterValues ? FamilyIfExists.OverwriteParameters : FamilyIfExists.Overwrite);
        Family? loaded;
        try { loaded = familyDoc.LoadFamily(projectDoc, options); }
        catch (Exception ex) { return (null, $"Revit did not load the edited family back into '{projectDoc.Title}': {ex.Message}."); }
        if (loaded == null) return (null, $"Revit did not load the edited family back into '{projectDoc.Title}'.");

        var types = FamilyInventory.Symbols(projectDoc, loaded);
        return (new
        {
            status = "reloaded",
            familyId = loaded.Id.Value,
            parameterValues = overwriteParameterValues ? "family" : "project",
            types = types.Select(s => new { id = s.Id.Value, name = FamilyTypeSupport.SafeName(s), isNew = !before.Contains(FamilyTypeSupport.SafeName(s)) }).ToList(),
            note = types.Any(s => !before.Contains(FamilyTypeSupport.SafeName(s)))
                ? "New types are not activated by the reload; placing one activates it."
                : null
        }, null);
    }

    /// <summary>Copies the saved file next to its destination, then swaps it in, so a failure never leaves a half-written family.</summary>
    private static void PlaceFile(string source, string destination)
    {
        var folder = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(folder);
        var staging = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(destination)}.{Guid.NewGuid():N}.tmp");
        File.Copy(source, staging, overwrite: true);
        try
        {
            if (!File.Exists(destination)) File.Move(staging, destination);
            else
            {
                try { File.Replace(staging, destination, null); }
                catch (IOException) { File.Copy(staging, destination, overwrite: true); }
            }
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); } catch { /* staging only */ }
        }
    }

    // ── Results ──────────────────────────────────────────────────────────────

    private static McpToolResult Refuse(McpToolRequest request, bool apply, List<string> problems, List<string> warnings,
        string familyName, DetailFamilyEditSpec edit, Stopwatch sw)
    {
        if (apply) return Invalid(request, problems, warnings, sw);
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preview: {problems.Count} problem(s) to fix before editing the family.",
            Data = new
            {
                ready = false,
                problems,
                family = familyName.Length > 0 ? familyName : null,
                target = edit.IsPathTarget ? "path" : "family",
                path = edit.IsPathTarget ? edit.Path : null,
                note = "The family was not opened: fix the target first; the rest of the edit is checked once it can be opened."
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static McpToolResult Invalid(McpToolRequest request, List<string> problems, List<string> warnings, Stopwatch sw) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Status = "invalid_arguments",
        Message = $"Nothing was changed: {problems.Count} problem(s). {string.Join(" ", problems.Take(3))}",
        Errors = problems,
        Warnings = warnings,
        DurationMs = sw.ElapsedMilliseconds
    };

    private static McpToolResult FailAfterLoad(McpToolRequest request, bool loaded, string message, object? load, List<string> warnings)
    {
        if (!loaded) return DetailFamilyExecutor.Fail(request, $"{message} The original file is unchanged.", warnings);
        warnings.Add(message);
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = false,
            Status = "partial",
            Message = $"The edited family was reloaded into the project (one undo step), but {char.ToLowerInvariant(message[0])}{message.Substring(1)}",
            Data = new { load },
            Errors = [message],
            Warnings = warnings
        };
    }
}
