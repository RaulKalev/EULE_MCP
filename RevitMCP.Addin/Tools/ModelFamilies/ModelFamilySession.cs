using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Tools.Families;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>A failure Revit raised while a step of a model-family edit was committed.</summary>
internal sealed class ModelFamilyFailure
{
    public string Step { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<long> ElementIds { get; set; } = new();

    public JObject ToJson() => new()
    {
        ["step"] = Step,
        ["severity"] = Severity,
        ["message"] = Message,
        ["elementIds"] = new JArray(ElementIds)
    };

    public override string ToString() =>
        $"{Message}{(ElementIds.Count > 0 ? $" (elements {string.Join(", ", ElementIds.Take(8))})" : string.Empty)}";
}

/// <summary>A step Revit refused: its transaction was rolled back. Carries what Revit said and which elements it named.</summary>
internal sealed class ModelFamilyStepException : Exception
{
    public ModelFamilyStepException(string step, string message, List<ModelFamilyFailure> failures) : base(message)
    {
        Step = step;
        Failures = failures;
    }

    public string Step { get; }
    public List<ModelFamilyFailure> Failures { get; }
}

/// <summary>Records every failure of a transaction with its elements, deletes warnings and rolls back on an error.</summary>
internal sealed class ModelFamilyFailureCapture : IFailuresPreprocessor
{
    private readonly string _step;

    public ModelFamilyFailureCapture(string step) => _step = step;

    public List<ModelFamilyFailure> Failures { get; } = new();
    public bool HasError { get; private set; }

    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        try
        {
            foreach (var failure in failuresAccessor.GetFailureMessages())
            {
                var severity = failure.GetSeverity();
                string text;
                try { text = failure.GetDescriptionText(); }
                catch { text = failure.GetFailureDefinitionId()?.Guid.ToString() ?? "(unknown failure)"; }
                var ids = new List<long>();
                try
                {
                    ids.AddRange(failure.GetFailingElementIds().Select(id => id.Value));
                    ids.AddRange(failure.GetAdditionalElementIds().Select(id => id.Value));
                }
                catch { /* the elements are diagnostics only */ }
                Failures.Add(new ModelFamilyFailure
                {
                    Step = _step,
                    Severity = severity == FailureSeverity.Warning ? "warning" : "error",
                    Message = text,
                    ElementIds = ids.Distinct().ToList()
                });
                if (severity == FailureSeverity.Warning) failuresAccessor.DeleteWarning(failure);
                else HasError = true;
            }
        }
        catch
        {
            // Diagnostics only: the rollback decision below still applies.
        }
        return HasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}

/// <summary>
/// What a model-family tool's body works with: the opened family document, the project (when there is one) and the
/// lists its findings go to. Bodies change the family only through <see cref="Transact"/> / <see cref="TryTransact"/>.
/// </summary>
internal sealed class ModelFamilyContext
{
    public UIApplication Uiapp { get; init; } = null!;
    /// <summary>The family document being read or edited.</summary>
    public Document FamilyDoc { get; init; } = null!;
    /// <summary>The project of the <c>document</c> argument (or the active one); null when no project is open.</summary>
    public Document? Project { get; init; }
    public McpToolRequest Request { get; init; } = null!;
    public Dictionary<string, object?> Args => Request.Arguments;
    public ModelFamilyTargetSpec Target { get; init; } = null!;
    public string FamilyName { get; init; } = string.Empty;
    /// <summary>False in a preview (and in a read): the session rolls everything back afterwards.</summary>
    public bool Apply { get; init; }
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Reasons the edit must not be applied. Any entry makes the session roll back and report them.</summary>
    public List<string> Problems { get; } = new();
    public List<string> Warnings { get; } = new();
    /// <summary>Warnings (and, for tolerated steps, errors) Revit raised in the steps so far.</summary>
    public List<ModelFamilyFailure> Failures { get; } = new();
    /// <summary>One line of what the edit does or did, for the result message.</summary>
    public string Summary { get; set; } = string.Empty;

    public FamilyManager Manager => FamilyDoc.FamilyManager;

    /// <summary>
    /// Runs <paramref name="action"/> in its own transaction and regenerates. When Revit posts an error (a constraint
    /// that cannot be satisfied, a form that cannot be made) the transaction is rolled back and a
    /// <see cref="ModelFamilyStepException"/> names the failures and their elements; the session then rolls the whole
    /// edit back. Warnings are recorded in <see cref="Failures"/> and dismissed.
    /// </summary>
    public T Transact<T>(string step, Func<T> action)
    {
        CancellationToken.ThrowIfCancellationRequested();
        var capture = new ModelFamilyFailureCapture(step);
        using var tx = new Transaction(FamilyDoc, $"Revit MCP - {step}");
        var options = tx.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(capture);
        options.SetClearAfterRollback(true);
        tx.SetFailureHandlingOptions(options);
        tx.Start();
        T result;
        try
        {
            result = action();
            FamilyDoc.Regenerate();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            if (ex is ModelFamilyStepException) throw;
            throw new ModelFamilyStepException(step, $"{step}: {ex.Message.TrimEnd('.')}.", capture.Failures);
        }
        var status = tx.Commit();
        if (status != TransactionStatus.Committed)
        {
            var errors = capture.Failures.Where(f => f.Severity == "error").ToList();
            var detail = errors.Count > 0 ? string.Join(" ", errors.Select(f => f.ToString()).Distinct()) : $"Revit did not commit it (status {status})";
            throw new ModelFamilyStepException(step, $"{step}: {detail.TrimEnd('.')}.", capture.Failures);
        }
        Failures.AddRange(capture.Failures);
        return result;
    }

    public void Transact(string step, Action action) => Transact(step, () => { action(); return true; });

    /// <summary>
    /// As <see cref="Transact"/>, but a refused step is only reported: its own changes are rolled back, the failures go
    /// to <see cref="Failures"/> and the edit goes on. Use it for an item of a list that may fail without spoiling the rest.
    /// </summary>
    public bool TryTransact(string step, Action action, out string? error)
    {
        try
        {
            Transact(step, action);
            error = null;
            return true;
        }
        catch (ModelFamilyStepException ex)
        {
            Failures.AddRange(ex.Failures);
            error = ex.Message;
            return false;
        }
    }
}

/// <summary>
/// Opens the family document a model-family tool names, runs the tool's body on it and finishes the way #147 does:
/// a preview changes nothing, a failed edit leaves the file and the loaded family as they were, a file is saved to a
/// temporary file and swapped into place, a loaded family is loaded back as one undo step, and a document the user has
/// open is never closed or saved unless asked. Reads the <c>target</c> and <c>document</c> arguments.
/// </summary>
internal static class ModelFamilySession
{
    public const string TargetArg = "target";

    /// <summary>One line for an approval summary: what is edited and what happens to the result.</summary>
    public static string DescribeTarget(McpToolRequest request, ModelFamilySessionMode mode = ModelFamilySessionMode.Edit)
    {
        var spec = ModelFamilyTargetSpec.Parse(TargetOf(request), mode, new List<string>());
        return $"{spec.Describe()}; then {spec.DescribeOutcome()}";
    }

    /// <summary>Reads a family: nothing is changed, saved or loaded.</summary>
    public static McpToolResult Read(UIApplication uiapp, McpToolRequest request, string what,
        Func<ModelFamilyContext, object?> body, CancellationToken cancellationToken) =>
        Run(uiapp, request, apply: false, what, ModelFamilySessionMode.Read, body, cancellationToken);

    /// <summary>
    /// Edits a family (or, with <see cref="ModelFamilySessionMode.Create"/>, a new one from the template).
    /// <paramref name="apply"/> false is the preview: the body runs the same way and everything is rolled back.
    /// The body returns the tool's result data and reports what stops the edit in <see cref="ModelFamilyContext.Problems"/>.
    /// </summary>
    public static McpToolResult Edit(UIApplication uiapp, McpToolRequest request, bool apply, string what,
        Func<ModelFamilyContext, object?> body, CancellationToken cancellationToken,
        ModelFamilySessionMode mode = ModelFamilySessionMode.Edit) =>
        Run(uiapp, request, apply, what, mode, body, cancellationToken);

    private static object? TargetOf(McpToolRequest request) =>
        request.Arguments != null && request.Arguments.TryGetValue(TargetArg, out var raw) ? raw : null;

    private static McpToolResult Run(UIApplication uiapp, McpToolRequest request, bool apply, string what,
        ModelFamilySessionMode mode, Func<ModelFamilyContext, object?> body, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var problems = new List<string>();
        var warnings = new List<string>();
        var spec = ModelFamilyTargetSpec.Parse(TargetOf(request), mode, problems);
        var reading = mode == ModelFamilySessionMode.Read;

        // The project: required for a loaded family and for load=true, optional otherwise.
        var documentGiven = ToolArguments.GetString(request.Arguments ?? new Dictionary<string, object?>(), RevitDocumentResolver.ArgumentName).Trim().Length > 0;
        Document? project = null;
        if (documentGiven || spec.Kind == ModelFamilyTargetKind.LoadedFamily || spec.Load)
        {
            var resolved = RevitDocumentResolver.Resolve(uiapp, request, out var documentError);
            if (resolved == null) problems.Add($"target: {documentError ?? "no project document is open"} (a loaded family, and load=true, need a project).");
            else project = resolved.Document;
        }
        else
        {
            try { project = RevitDocumentResolver.Resolve(uiapp, request, out _)?.Document; }
            catch { project = null; }
        }
        if (project != null && !reading && (spec.Kind == ModelFamilyTargetKind.LoadedFamily || spec.Load) && project.IsReadOnly)
            problems.Add($"target: '{project.Title}' is read-only; the family cannot be loaded into it.");

        // The target, without opening anything yet.
        Family? family = null;
        Document? openDocument = null;
        var familyName = string.Empty;
        string? templatePath = null;
        var policy = new FilePathPolicy();
        switch (spec.Kind)
        {
            case ModelFamilyTargetKind.LoadedFamily when project != null:
                family = ResolveFamily(project, spec.Family);
                if (family == null)
                {
                    var close = ModelFamilyJson.Suggest(spec.Family, FamilyInventory.LoadedByName(project).Keys);
                    problems.Add($"target.family: '{spec.Family}' is not a family loaded in '{project.Title}'" +
                                 (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : ". Use revit_list_families to find it, or pass path."));
                    break;
                }
                familyName = FamilyTypeSupport.SafeName(family);
                if (SafeInPlace(family)) problems.Add($"target.family: '{familyName}' is an in-place family; only loadable families can be edited.");
                else if (!family.IsEditable) problems.Add($"target.family: '{familyName}' cannot be opened for editing.");
                if (FindOpenFamilyDocument(uiapp, familyName) is { } openTitle)
                    problems.Add($"target.family: '{familyName}' is open in the Family Editor ('{openTitle}'). Close it first, or edit that window with familyDocument='{openTitle}'.");
                break;

            case ModelFamilyTargetKind.File when problems.Count == 0 || !problems.Any(p => p.StartsWith("target.path", StringComparison.Ordinal)):
                if (policy.ValidateRead(spec.Path) is { } readError) problems.Add($"target.path: {readError}");
                else if (!File.Exists(spec.Path)) problems.Add($"target.path: '{spec.Path}' does not exist.");
                else
                {
                    familyName = Path.GetFileNameWithoutExtension(spec.Path);
                    if (!reading && spec.SavePath.Length == 0 && policy.ValidateWrite(spec.Path) is { } writeError)
                        problems.Add($"target.path: saving back is not allowed: {writeError}");
                    if (FindOpenDocumentAt(uiapp, spec.Path) is { } open)
                    {
                        if (reading) openDocument = open;
                        else problems.Add($"target.path: '{spec.Path}' is open in Revit. Close it first, or edit that window with familyDocument='{open.Title}'.");
                    }
                    FileVersionWarning(uiapp, spec.Path, warnings);
                }
                break;

            case ModelFamilyTargetKind.OpenDocument:
                openDocument = ResolveOpenFamilyDocument(uiapp, spec.FamilyDocument, out var openError);
                if (openDocument == null) problems.Add($"target.familyDocument: {openError}");
                else
                {
                    familyName = TitleName(openDocument);
                    if (!reading)
                    {
                        if (openDocument.IsReadOnly) problems.Add($"target.familyDocument: '{openDocument.Title}' is read-only.");
                        if (openDocument.IsModifiable) problems.Add($"target.familyDocument: '{openDocument.Title}' has a transaction open (a sketch or command in progress); finish or cancel it first.");
                        if (spec.Save && spec.SavePath.Length == 0 && string.IsNullOrEmpty(openDocument.PathName))
                            problems.Add($"target.save: '{openDocument.Title}' has never been saved; pass savePath.");
                    }
                }
                break;

            case ModelFamilyTargetKind.Template:
                (templatePath, var templateError) = DetailFamilySpecParser.ResolveTemplate(spec.TemplatePath, SafeTemplateRoot(uiapp),
                    File.Exists, root => SafeFind(root, ModelFamilyTargetSpec.DefaultTemplateFileName), ModelFamilyTargetSpec.DefaultTemplateFileName);
                if (templateError != null)
                    problems.Add($"target.templatePath: {templateError.Replace("annotationTemplatePath", "target.templatePath")}");
                else if (policy.ValidateRead(templatePath!) is { } templateReadError)
                    problems.Add($"target.templatePath: {templateReadError}");
                break;
        }

        // Where the result is saved.
        string? savePath = null;
        var saveExists = false;
        if (!reading && spec.SavePath.Length > 0 && (familyName.Length > 0 || spec.Kind == ModelFamilyTargetKind.Template))
        {
            var keepName = spec.Kind == ModelFamilyTargetKind.LoadedFamily;
            var (resolvedPath, resolvedName, saveError) = spec.Kind == ModelFamilyTargetKind.Template
                ? DetailFamilySpecParser.ResolveSavePath(spec.SavePath, spec.FamilyName)
                : DetailFamilySpecParser.ResolveEditSavePath(spec.SavePath, spec.FamilyName.Length > 0 && !keepName ? spec.FamilyName : familyName, keepName);
            if (saveError != null) problems.Add($"target.savePath: {saveError}");
            else
            {
                savePath = resolvedPath!;
                if (spec.Kind == ModelFamilyTargetKind.Template) familyName = resolvedName!;
                if (policy.ValidateWrite(savePath) is { } writeError) problems.Add($"target.savePath: {writeError}");
                saveExists = File.Exists(savePath);
                if (saveExists && !spec.Overwrite)
                    problems.Add($"target.savePath: '{savePath}' already exists: pass overwrite=true to replace it, or choose another name or folder.");
                if (FindOpenDocumentAt(uiapp, savePath) is { } openAtSave && !ReferenceEquals(openAtSave, openDocument) && !SameDocument(openAtSave, openDocument))
                    problems.Add($"target.savePath: '{savePath}' is open in Revit ('{openAtSave.Title}'); close it first.");
                var folder = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                    warnings.Add($"The folder '{folder}' does not exist yet; it will be created.");
            }
        }
        var finalPath = spec.Kind switch
        {
            ModelFamilyTargetKind.File => savePath ?? spec.Path,
            _ => savePath
        };

        if (problems.Any(ModelFamilyTargetSpec.IsTargetProblem))
            return Refused(request, apply, reading, what, spec, familyName, problems, warnings, sw);

        Document? familyDoc = null;
        var owned = false;
        var tempFolder = Path.Combine(Path.GetTempPath(), "RevitMCP", "ModelFamily", Guid.NewGuid().ToString("N"));
        using var dialogs = new ModalDialogGuard(uiapp);
        ModelFamilyContext? context = null;
        TransactionGroup? group = null;
        try
        {
            // 1. Open.
            switch (spec.Kind)
            {
                case ModelFamilyTargetKind.LoadedFamily:
                    familyDoc = FamilyDocumentGuard.EditFamily(project!, family!, out owned);
                    if (familyDoc != null && !owned)
                    {
                        if (!reading)
                            return Fail(request, "validation_failed", FamilyDocumentGuard.OpenInEditorMessage(familyName) + " Nothing was changed.", warnings, sw);
                        warnings.Add($"'{familyName}' is open in the Family Editor: that window was read, with any unsaved changes it has.");
                    }
                    break;
                case ModelFamilyTargetKind.File:
                    if (openDocument != null) familyDoc = openDocument;
                    else
                    {
                        familyDoc = uiapp.Application.OpenDocumentFile(spec.Path);
                        owned = familyDoc != null;
                    }
                    break;
                case ModelFamilyTargetKind.OpenDocument:
                    familyDoc = openDocument;
                    break;
                case ModelFamilyTargetKind.Template:
                    familyDoc = uiapp.Application.NewFamilyDocument(templatePath!);
                    owned = familyDoc != null;
                    break;
            }
            if (familyDoc == null)
                return Fail(request, "tool_execution_failed", $"Revit could not open {spec.Describe()}.", warnings, sw);
            if (!familyDoc.IsFamilyDocument)
                return Fail(request, "validation_failed", $"'{familyDoc.Title}' is not a family document.", warnings, sw);

            context = new ModelFamilyContext
            {
                Uiapp = uiapp,
                FamilyDoc = familyDoc,
                Project = project,
                Request = request,
                Target = spec,
                FamilyName = familyName,
                Apply = apply && !reading,
                CancellationToken = cancellationToken
            };
            context.Warnings.AddRange(warnings);
            warnings = context.Warnings;

            // 2. The body, inside one group: a preview and a failure roll all of it back.
            object? data;
            if (reading)
            {
                data = body(context);
            }
            else
            {
                group = new TransactionGroup(familyDoc, $"Revit MCP - {what}");
                group.Start();
                try
                {
                    data = body(context);
                }
                catch (ModelFamilyStepException ex)
                {
                    RollBack(ref group);
                    context.Failures.AddRange(ex.Failures.Where(f => !context.Failures.Contains(f)));
                    return StepFailed(request, apply, what, spec, familyName, ex, context, sw);
                }
                if (!apply || context.Problems.Count > 0) RollBack(ref group);
                else
                {
                    group.Assimilate();
                    group.Dispose();
                    group = null;
                }
            }

            var envelope = Envelope(spec, familyName, project, context, data);
            if (reading)
            {
                sw.Stop();
                return new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = context.Problems.Count == 0,
                    Status = context.Problems.Count == 0 ? null : "validation_failed",
                    Message = context.Problems.Count == 0
                        ? context.Summary.Length > 0 ? context.Summary : $"Read {spec.Describe()}."
                        : $"{context.Problems.Count} problem(s). {string.Join(" ", context.Problems.Take(3))}",
                    Data = envelope,
                    Errors = context.Problems.ToList(),
                    Warnings = warnings,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            if (!apply)
            {
                envelope.AddFirst(new JProperty("problems", new JArray(context.Problems)));
                envelope.AddFirst(new JProperty("ready", context.Problems.Count == 0));
                envelope["outcome"] = spec.DescribeOutcome();
                envelope["savePath"] = finalPath;
                envelope["willReplaceFile"] = spec.Kind == ModelFamilyTargetKind.File && savePath == null || saveExists && spec.Overwrite;
                sw.Stop();
                return new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = true,
                    Message = context.Problems.Count == 0
                        ? $"Preview: ready to {what} in {spec.Describe()}{(context.Summary.Length > 0 ? $": {context.Summary}" : string.Empty)}; then {spec.DescribeOutcome()}. Nothing was changed."
                        : $"Preview: {context.Problems.Count} problem(s) to fix first. {string.Join(" ", context.Problems.Take(3))}",
                    Data = envelope,
                    Warnings = warnings,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            if (context.Problems.Count > 0)
            {
                sw.Stop();
                return new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Status = "validation_failed",
                    Message = $"Nothing was changed: {context.Problems.Count} problem(s). {string.Join(" ", context.Problems.Take(3))}",
                    Data = envelope,
                    Errors = context.Problems.ToList(),
                    Warnings = warnings,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            // 3. Finish: save, load, close.
            string? savedTo = null;
            object? load = null;
            if (spec.Kind == ModelFamilyTargetKind.OpenDocument)
            {
                // The edit is already in the user's document (one undo step): a failed save must say so, not "nothing changed".
                try
                {
                    if (savePath != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
                        familyDoc.SaveAs(savePath, new SaveAsOptions { OverwriteExistingFile = spec.Overwrite });
                        savedTo = savePath;
                    }
                    else if (spec.Save)
                    {
                        familyDoc.Save();
                        savedTo = familyDoc.PathName;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return Partial(request, $"The edit was applied to '{familyDoc.Title}' (one undo step; Undo in that window reverts it), but saving it failed: " +
                                            $"{ex.Message.TrimEnd('.')}. The document is left open and unsaved; nothing was loaded.", envelope, warnings, sw);
                }
                if (spec.Load)
                {
                    var (loadResult, loadError) = LoadDocument(project!, familyDoc, spec.OverwriteParameterValues);
                    if (loadError != null)
                        return Partial(request, $"The edit was applied to '{familyDoc.Title}'{(savedTo != null ? $" and saved to '{savedTo}'" : string.Empty)}, but {Lower(loadError)}", envelope, warnings, sw);
                    load = loadResult;
                }
            }
            else
            {
                Directory.CreateDirectory(tempFolder);
                string? tempPath = null;
                if (finalPath != null)
                {
                    tempPath = Path.Combine(tempFolder, Path.GetFileName(finalPath));
                    familyDoc.SaveAs(tempPath, new SaveAsOptions { OverwriteExistingFile = true });
                }

                if (spec.Kind == ModelFamilyTargetKind.LoadedFamily)
                {
                    var (loadResult, loadError) = LoadDocument(project!, familyDoc, spec.OverwriteParameterValues);
                    if (loadError != null)
                        return Fail(request, "tool_execution_failed", $"{loadError} The project and its loaded family are unchanged; no file was written.", warnings, sw, envelope);
                    load = loadResult;
                }

                FamilyDocumentGuard.CloseIfOwned(familyDoc, owned);
                familyDoc = null;

                if (finalPath != null)
                {
                    var replacingTarget = spec.Kind == ModelFamilyTargetKind.File && savePath == null;
                    if (!replacingTarget && File.Exists(finalPath) && !spec.Overwrite)
                        return AfterLoad(request, load, $"'{finalPath}' appeared while the family was being edited; it was not replaced.", envelope, warnings, sw);
                    try
                    {
                        PlaceFile(tempPath!, finalPath);
                        savedTo = finalPath;
                    }
                    catch (Exception ex)
                    {
                        return AfterLoad(request, load, $"Saving to '{finalPath}' failed: {ex.Message}", envelope, warnings, sw);
                    }
                    if (spec.Kind == ModelFamilyTargetKind.LoadedFamily && project != null)
                    {
                        var storeError = FamilyPathStore.Default().Remember(CheckpointChangeTracker.ModelIdOf(project), new List<(string, string)> { (familyName, finalPath) });
                        if (storeError != null) warnings.Add(storeError);
                    }
                }

                if (spec.Load && spec.Kind != ModelFamilyTargetKind.LoadedFamily && savedTo != null)
                {
                    var (loadResult, loadError) = LoadFile(project!, savedTo, spec.OverwriteParameterValues);
                    if (loadError != null)
                        return Partial(request, $"The family was saved to '{savedTo}', but {Lower(loadError)}", envelope, warnings, sw);
                    load = loadResult;
                }
            }

            warnings.AddRange(dialogs.Dismissed.Distinct().Select(d => $"Closed a Revit dialog: {d}"));
            envelope["savedTo"] = savedTo;
            envelope["replacedFile"] = spec.Kind == ModelFamilyTargetKind.File && savePath == null || saveExists;
            envelope["load"] = load == null ? null : JToken.FromObject(load);
            sw.Stop();
            var done = context.Summary.Length > 0 ? context.Summary : what;
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"{familyName}: {done}" +
                          $"{(load != null ? "; loaded into the project (one undo step)" : string.Empty)}" +
                          $"{(savedTo != null ? $"; saved to '{savedTo}'" : spec.Kind == ModelFamilyTargetKind.OpenDocument ? "; the document is left open and unsaved" : string.Empty)}.",
                Data = envelope,
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            RollBack(ref group);
            return Fail(request, "request_cancelled", "The request was cancelled; nothing was saved or loaded.", warnings, sw);
        }
        catch (Exception ex)
        {
            RollBack(ref group);
            return Fail(request, "tool_execution_failed", $"{what}: {ex.Message.TrimEnd('.')}. Nothing was saved or loaded.", warnings, sw);
        }
        finally
        {
            RollBack(ref group);
            FamilyDocumentGuard.CloseIfOwned(familyDoc, owned);
            try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, recursive: true); } catch { /* temp only */ }
        }
    }

    // ── Results ──────────────────────────────────────────────────────────────

    private static JObject Envelope(ModelFamilyTargetSpec spec, string familyName, Document? project, ModelFamilyContext context, object? data)
    {
        var envelope = new JObject
        {
            ["family"] = familyName,
            ["target"] = spec.Kind switch
            {
                ModelFamilyTargetKind.LoadedFamily => "family",
                ModelFamilyTargetKind.File => "path",
                ModelFamilyTargetKind.OpenDocument => "familyDocument",
                _ => "template"
            },
            ["path"] = spec.Kind == ModelFamilyTargetKind.File ? spec.Path : null,
            ["document"] = project?.Title
        };
        if (data != null) envelope["result"] = data as JToken ?? JToken.FromObject(data);
        if (context.Failures.Count > 0) envelope["revitFailures"] = new JArray(context.Failures.Select(f => f.ToJson()));
        return envelope;
    }

    private static McpToolResult StepFailed(McpToolRequest request, bool apply, string what, ModelFamilyTargetSpec spec, string familyName,
        ModelFamilyStepException ex, ModelFamilyContext context, Stopwatch sw)
    {
        sw.Stop();
        var data = new JObject
        {
            ["family"] = familyName,
            ["ready"] = false,
            ["failedStep"] = ex.Step,
            ["revitFailures"] = new JArray(context.Failures.Select(f => f.ToJson())),
            ["problems"] = new JArray(context.Problems.Concat(new[] { ex.Message }))
        };
        return new McpToolResult
        {
            RequestId = request.RequestId,
            // A preview that finds the edit impossible has done its job; the edit itself failed.
            Success = !apply,
            Status = apply ? "transaction_failed" : null,
            Message = apply
                ? $"Nothing was changed: Revit refused a step and the whole edit of {spec.Describe()} was rolled back. {ex.Message}"
                : $"Preview: Revit would refuse this edit. {ex.Message}",
            Data = data,
            Errors = apply ? new List<string> { ex.Message } : new List<string>(),
            Warnings = context.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static McpToolResult Refused(McpToolRequest request, bool apply, bool reading, string what, ModelFamilyTargetSpec spec,
        string familyName, List<string> problems, List<string> warnings, Stopwatch sw)
    {
        sw.Stop();
        if (apply || reading)
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "validation_failed",
                Message = $"Nothing was {(reading ? "read" : "changed")}: {problems.Count} problem(s). {string.Join(" ", problems.Take(3))}",
                Errors = problems,
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preview: {problems.Count} problem(s) to fix first. {string.Join(" ", problems.Take(3))}",
            Data = new JObject
            {
                ["ready"] = false,
                ["problems"] = new JArray(problems),
                ["family"] = familyName.Length > 0 ? familyName : null,
                ["note"] = "The family was not opened: fix the target first; the rest of the edit is checked once it can be opened."
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static McpToolResult Fail(McpToolRequest request, string status, string message, List<string> warnings, Stopwatch sw, JObject? data = null)
    {
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = false,
            Status = status,
            Message = message,
            Data = data,
            Errors = new List<string> { message },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static McpToolResult Partial(McpToolRequest request, string message, JObject envelope, List<string> warnings, Stopwatch sw)
    {
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = false,
            Status = "partial",
            Message = message,
            Data = envelope,
            Errors = new List<string> { message },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static McpToolResult AfterLoad(McpToolRequest request, object? load, string message, JObject envelope, List<string> warnings, Stopwatch sw)
    {
        if (load == null) return Fail(request, "tool_execution_failed", $"{message} The original file is unchanged.", warnings, sw, envelope);
        envelope["load"] = JToken.FromObject(load);
        return Partial(request, $"The edited family was loaded into the project (one undo step), but {Lower(message)}", envelope, warnings, sw);
    }

    private static string Lower(string message) => message.Length == 0 ? message : char.ToLowerInvariant(message[0]) + message.Substring(1);

    private static void RollBack(ref TransactionGroup? group)
    {
        if (group == null) return;
        try { if (group.GetStatus() == TransactionStatus.Started) group.RollBack(); }
        catch { /* the document may be gone */ }
        try { group.Dispose(); }
        catch { /* already disposed */ }
        group = null;
    }

    // ── Target lookup ────────────────────────────────────────────────────────

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

    /// <summary>A family document's name without the extension Revit shows in some titles.</summary>
    public static string TitleName(Document familyDoc)
    {
        var title = familyDoc.Title ?? string.Empty;
        return title.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) ? title.Substring(0, title.Length - 4) : title;
    }

    /// <summary>The open family documents, for messages and for <c>familyDocument</c>.</summary>
    public static List<Document> OpenFamilyDocuments(UIApplication uiapp)
    {
        var result = new List<Document>();
        foreach (Document open in uiapp.Application.Documents)
        {
            try { if (open.IsFamilyDocument && !open.IsLinked) result.Add(open); }
            catch { /* a document being closed */ }
        }
        return result;
    }

    private static Document? ResolveOpenFamilyDocument(UIApplication uiapp, string selector, out string? error)
    {
        error = null;
        var open = OpenFamilyDocuments(uiapp);
        if (string.Equals(selector, "active", StringComparison.OrdinalIgnoreCase))
        {
            if (RevitDocumentResolver.ActiveFamilyDocument(uiapp) is { } active) return active;
            error = "the active document is not a family document." + OpenList(open);
            return null;
        }
        var matches = open.Where(d =>
            string.Equals(d.Title, selector, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(TitleName(d), selector, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(TitleName(d) + ".rfa", selector, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(d.PathName) && string.Equals(d.PathName, selector, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];
        error = matches.Count == 0
            ? $"no open family document is called '{selector}'." + OpenList(open)
            : $"{matches.Count} open family documents are called '{selector}'; pass the file path of one: {string.Join(", ", matches.Select(d => $"'{d.PathName}'"))}.";
        return null;
    }

    private static string OpenList(List<Document> open) => open.Count == 0
        ? " No family document is open; use path or family."
        : $" Open family documents: {string.Join(", ", open.Select(d => $"'{d.Title}'"))}.";

    private static string? FindOpenFamilyDocument(UIApplication uiapp, string familyName)
    {
        foreach (var open in OpenFamilyDocuments(uiapp))
        {
            var fileName = string.IsNullOrEmpty(open.PathName) ? string.Empty : Path.GetFileNameWithoutExtension(open.PathName);
            if (string.Equals(TitleName(open), familyName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, familyName, StringComparison.OrdinalIgnoreCase))
                return open.Title;
        }
        return null;
    }

    private static Document? FindOpenDocumentAt(UIApplication uiapp, string path)
    {
        static string Full(string p)
        {
            try { return Path.GetFullPath(p); }
            catch { return p; }
        }
        var wanted = Full(path);
        foreach (Document open in uiapp.Application.Documents)
        {
            try
            {
                if (string.IsNullOrEmpty(open.PathName)) continue;
                if (string.Equals(Full(open.PathName), wanted, StringComparison.OrdinalIgnoreCase)) return open;
            }
            catch { /* a document being closed */ }
        }
        return null;
    }

    private static bool SameDocument(Document a, Document? b)
    {
        try { return b != null && a.Equals(b); }
        catch { return false; }
    }

    private static string SafeTemplateRoot(UIApplication uiapp)
    {
        try { return uiapp.Application.FamilyTemplatePath ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static IEnumerable<string> SafeFind(string root, string fileName)
    {
        try { return Directory.Exists(root) ? Directory.GetFiles(root, fileName, SearchOption.AllDirectories) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static void FileVersionWarning(UIApplication uiapp, string path, List<string> warnings)
    {
        try
        {
            var format = BasicFileInfo.Extract(path).Format;
            var version = uiapp.Application.VersionNumber;
            if (!string.IsNullOrEmpty(format) && !string.Equals(format, version, StringComparison.Ordinal))
                warnings.Add($"'{Path.GetFileName(path)}' is a Revit {format} file; it is upgraded when opened, and a save writes it as Revit {version}, which Revit {format} cannot open.");
        }
        catch
        {
            // The version is only a warning.
        }
    }

    // ── Load and save ────────────────────────────────────────────────────────

    /// <summary>
    /// Loads an open family document into the project. Revit records Document.LoadFamily(Document, IFamilyLoadOptions)
    /// as one undo step of its own; it must run with no transaction open in the project.
    /// </summary>
    private static (object? Result, string? Error) LoadDocument(Document project, Document familyDoc, bool overwriteParameterValues)
    {
        var options = new FamilyLoadOptions(overwriteParameterValues ? FamilyIfExists.OverwriteParameters : FamilyIfExists.Overwrite);
        Family? loaded;
        try { loaded = familyDoc.LoadFamily(project, options); }
        catch (Exception ex) { return (null, $"Revit did not load the family into '{project.Title}': {ex.Message.TrimEnd('.')}."); }
        if (loaded == null) return (null, $"Revit did not load the family into '{project.Title}'.");
        return (DescribeLoad(project, loaded, options, overwriteParameterValues), null);
    }

    private static (object? Result, string? Error) LoadFile(Document project, string path, bool overwriteParameterValues)
    {
        var options = new FamilyLoadOptions(overwriteParameterValues ? FamilyIfExists.OverwriteParameters : FamilyIfExists.Overwrite);
        Family? loaded = null;
        try
        {
            using var tx = new Transaction(project, "Revit MCP - Load family");
            tx.Start();
            var ok = project.LoadFamily(path, options, out loaded);
            if (!ok || loaded == null)
            {
                tx.RollBack();
                return (null, $"Revit did not load '{path}' into '{project.Title}'.");
            }
            TransactionCommitGuard.CommitOrThrow(tx);
        }
        catch (Exception ex)
        {
            return (null, $"Revit did not load '{path}' into '{project.Title}': {ex.Message.TrimEnd('.')}.");
        }
        return (DescribeLoad(project, loaded, options, overwriteParameterValues), null);
    }

    private static object DescribeLoad(Document project, Family loaded, FamilyLoadOptions options, bool overwriteParameterValues) => new
    {
        status = options.FamilyFound ? "reloaded" : "loaded",
        familyId = loaded.Id.Value,
        documentTitle = project.Title,
        parameterValues = options.FamilyFound ? overwriteParameterValues ? "family" : "project" : null,
        types = FamilyInventory.Symbols(project, loaded).Select(s => new { id = s.Id.Value, name = FamilyTypeSupport.SafeName(s) }).ToList(),
        note = "Types are not activated by a load; placing one activates it."
    };

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
}
