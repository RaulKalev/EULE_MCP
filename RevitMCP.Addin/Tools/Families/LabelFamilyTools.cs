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
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Families;

/// <summary>The label family tools (docs/label-families.md). App.OnStartup calls <see cref="Register"/> once.</summary>
public static class LabelFamilyToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new ListLabelFamiliesTool());
        handler.RegisterTool(new PreviewCreateLabelFamilyTool());
        handler.RegisterTool(new CreateLabelFamilyTool());
    }
}

/// <summary>revit_list_label_families: Generic Annotation families that can serve as label families, with their parameters.</summary>
public sealed class ListLabelFamiliesTool : IRevitMcpTool
{
    public string Name => "revit_list_label_families";

    public string Description =>
        "Lists the Generic Annotation families loaded in the document that can serve as label families (labelFamily of " +
        "revit_create_detail_family): name, id, types, editable. The first `limit` (default 5, max 25) of them, or those " +
        "named in families, are opened read-only for their editable user-defined parameters (the labelParameter " +
        "candidates: name, dataType, instance, shared), the labels Revit's API can see (hasLabel) and the labels' text " +
        "types. inspect=false opens none. path lists one .rfa file instead.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(LabelFamilyExecutor.List(uiapp, request, cancellationToken));
}

/// <summary>revit_preview_create_label_family: what revit_create_label_family would make, checked against the seed.</summary>
public sealed class PreviewCreateLabelFamilyTool : IRevitMcpTool
{
    public string Name => "revit_preview_create_label_family";

    public string Description =>
        "Previews revit_create_label_family without changing the model or any file. Same arguments. Opens the seed " +
        "read-only and returns ready, problems, the seed's types and parameters, the type plan (keep | rename | create | " +
        "delete), the parameter changes, the label text types the appearance goes to, the save path and the load status.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(LabelFamilyExecutor.Execute(uiapp, request, apply: false));
}

/// <summary>revit_create_label_family: saves a variant of a seed label family as a new .rfa, and optionally loads it.</summary>
public sealed class CreateLabelFamilyTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_create_label_family";

    public string Description =>
        "Makes a variant of a label family (a Generic Annotation family with a label bound to one of its parameters) " +
        "and saves it as a new .rfa. Requires approval. The seed (a loaded family's name or a .rfa path) is never changed: " +
        "a copy is edited and saved. savePath (folder with familyName, or the .rfa path) is required. types [{name, " +
        "textSize? (paper mm), font?, bold?, italic?, underline?, widthFactor?, color?}] become the family's types (seed " +
        "types not listed are deleted); the text fields set the label's text type, the same for every type. parameter " +
        "(default: the only editable one), renameParameter {from?, to}, makeInstance true|false, sampleValue (its value in " +
        "every type). loadIntoProject (ifExists: skip | overwrite). Run revit_preview_create_label_family first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(LabelFamilyExecutor.Execute(uiapp, request, apply: true));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => LabelFamilyExecutor.Summary(request);
}

internal static class LabelFamilyExecutor
{
    private const double MmPerFoot = 304.8;

    /// <summary>What was read from an open family document.</summary>
    private sealed class FamilyContents
    {
        public string Category { get; set; } = string.Empty;
        public List<string> Types { get; set; } = new();
        public List<LabelFamilyParameterInfo> Parameters { get; set; } = new();
        public Dictionary<string, FamilyParameter> ParameterElements { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<TextElement> Labels { get; set; } = new();
        public List<TextElementType> LabelTextTypes { get; set; } = new();
    }

    // ── revit_list_label_families ───────────────────────────────────────────

    public static McpToolResult List(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;
        var warnings = new List<string>();
        var path = ToolArguments.GetString(request.Arguments, "path").Trim();
        using var dialogs = new ModalDialogGuard(uiapp);

        if (path.Length > 0)
        {
            var (opened, error) = OpenFile(uiapp, path);
            if (opened == null) return Fail(request, error!);
            object entry;
            try
            {
                var contents = Read(opened);
                entry = Describe(Path.GetFileNameWithoutExtension(path), null, contents.Types, true, contents);
                if (!IsGenericAnnotation(opened.OwnerFamily))
                    warnings.Add($"'{path}' is a {contents.Category} family; a label family must be a Generic Annotation.");
            }
            finally
            {
                try { opened.Close(false); } catch { /* already closed */ }
            }
            warnings.AddRange(dialogs.Dismissed.Distinct().Select(d => $"Closed a Revit dialog: {d}"));
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Read the family file '{path}'.",
                Data = new { document = doc.Title, path, families = new[] { entry }, labelDetection = LabelDetectionNote },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        var annotation = FamilyInventory.Families(doc).Where(IsGenericAnnotation)
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var (listed, inspect) = LabelFamilyVariantParser.PlanListing(annotation.Keys.ToList(),
            ToolArguments.GetStringArray(request.Arguments, "families"),
            ToolArguments.GetInt(request.Arguments, "limit", LabelFamilyVariantParser.DefaultInspectLimit),
            ToolArguments.GetBool(request.Arguments, "inspect", true), errors);
        warnings.AddRange(errors);

        var toInspect = new HashSet<string>(inspect, StringComparer.OrdinalIgnoreCase);
        var entries = new List<object>();
        var inspected = new List<string>();
        foreach (var name in listed)
        {
            var family = annotation[name];
            var types = FamilyInventory.Symbols(doc, family).Select(FamilyTypeSupport.SafeName).ToList();
            FamilyContents? contents = null;
            if (toInspect.Contains(name) && !cancellationToken.IsCancellationRequested)
            {
                if (!family.IsEditable) warnings.Add($"'{name}' cannot be opened for editing; its parameters are not listed.");
                else
                {
                    Document? opened = null;
                    try
                    {
                        opened = doc.EditFamily(family);
                        if (opened != null) { contents = Read(opened); inspected.Add(name); }
                    }
                    catch (Exception ex) { warnings.Add($"'{name}' could not be opened: {ex.Message}"); }
                    finally
                    {
                        try { opened?.Close(false); } catch { /* already closed */ }
                    }
                }
            }
            entries.Add(Describe(name, family.Id.Value, types, family.IsEditable, contents));
        }
        warnings.AddRange(dialogs.Dismissed.Distinct().Select(d => $"Closed a Revit dialog: {d}"));

        var notInspected = listed.Where(n => !inspected.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{listed.Count} Generic Annotation famil{(listed.Count == 1 ? "y" : "ies")} in '{doc.Title}'; {inspected.Count} opened for their parameters." +
                      (notInspected.Count > 0 ? $" Not opened: {notInspected.Count} (name them in families, or raise limit)." : string.Empty),
            Data = new
            {
                document = doc.Title,
                count = listed.Count,
                inspected,
                notInspected = notInspected.Count > 0 ? notInspected : null,
                families = entries,
                labelDetection = LabelDetectionNote
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private const string LabelDetectionNote =
        "hasLabel counts the family's text elements that are not text notes, which is how Revit's API exposes labels. " +
        "The API does not say which parameter a label shows: the parameters listed are the candidates.";

    private static object Describe(string name, long? id, List<string> types, bool editable, FamilyContents? contents) => new
    {
        name,
        id,
        types,
        editable,
        inspected = contents != null,
        hasLabel = contents == null ? (bool?)null : contents.Labels.Count > 0,
        labels = contents?.Labels.Count,
        parameters = contents?.Parameters.Where(p => p.Editable).Select(p => new
        {
            name = p.Name,
            dataType = p.DataType,
            instance = p.IsInstance,
            shared = p.IsShared
        }).ToList(),
        otherParameters = contents?.Parameters.Where(p => !p.Editable).Select(p => p.Name).ToList() is { Count: > 0 } others ? others : null,
        labelTextTypes = contents?.LabelTextTypes.Select(DescribeTextType).ToList()
    };

    // ── revit_(preview_)create_label_family ─────────────────────────────────

    public static string Summary(McpToolRequest request)
    {
        var errors = new List<string>();
        var spec = LabelFamilyVariantParser.Parse(request.Arguments, errors);
        var document = ToolArguments.GetString(request.Arguments, "document").Trim();
        var parts = new List<string>();
        if (spec.Types.Count > 0) parts.Add($"types {string.Join(", ", spec.Types.Select(t => t.Name).Take(10))}{(spec.Types.Count > 10 ? ", …" : string.Empty)} (seed types not listed are deleted)");
        if (spec.Appearance.HasValues) parts.Add($"label text {spec.Appearance.Describe()}");
        if (spec.RenameTo.Length > 0) parts.Add($"parameter {(spec.Parameter.Length > 0 ? $"'{spec.Parameter}' " : string.Empty)}renamed to '{spec.RenameTo}'");
        if (spec.MakeInstance != null) parts.Add($"parameter made {(spec.MakeInstance.Value ? "instance" : "type")}");
        if (spec.SampleValue != null) parts.Add($"sample value '{spec.SampleValue}'");
        return $"Create label family '{spec.FamilyName}' as a copy of '{spec.SeedName}'" +
               $"{(parts.Count > 0 ? $" ({string.Join("; ", parts)})" : string.Empty)} and save it to '{spec.SavePath}'" +
               $"{(spec.Overwrite ? ", replacing an existing file" : string.Empty)}. The seed is not changed" +
               $"{(spec.LoadIntoProject ? $"; then load it into {(document.Length > 0 ? $"'{document}'" : "the active document")} (ifExists={spec.IfExists})" : string.Empty)}." +
               $"{(errors.Count > 0 ? $" The request has {errors.Count} problem(s)." : string.Empty)}";
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;

        var problems = new List<string>();
        var warnings = new List<string>();
        var spec = LabelFamilyVariantParser.Parse(request.Arguments, problems);

        var fileExists = false;
        if (spec.SavePath.Length > 0)
        {
            var policyError = new FilePathPolicy().ValidateWrite(spec.SavePath);
            if (policyError != null) problems.Add($"savePath: {policyError}");
            fileExists = File.Exists(spec.SavePath);
            if (fileExists && !spec.Overwrite)
                problems.Add($"'{spec.SavePath}' already exists: pass overwrite=true to replace it, or choose another name or folder.");
            var folder = Path.GetDirectoryName(spec.SavePath);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                warnings.Add($"The folder '{folder}' does not exist yet; it will be created.");
        }

        var alreadyLoaded = spec.FamilyName.Length > 0 && FamilyInventory.LoadedByName(doc).ContainsKey(spec.FamilyName);
        var overwriteLoaded = spec.IfExists == "overwrite";
        var loadStatus = !spec.LoadIntoProject ? "notRequested" : !alreadyLoaded ? "willLoad" : overwriteLoaded ? "willOverwrite" : "willSkip";

        // The seed, opened as an in-memory copy (EditFamily) or from its file; never saved in place.
        using var dialogs = new ModalDialogGuard(uiapp);
        Document? seedDoc = null;
        string? seedKind = null;
        if (spec.Seed.Length > 0)
        {
            string? openError;
            (seedDoc, seedKind, openError) = OpenSeed(uiapp, doc, spec.Seed);
            if (openError != null) problems.Add(openError);
        }

        var tempFolder = Path.Combine(Path.GetTempPath(), "RevitMCP", "LabelFamily", Guid.NewGuid().ToString("N"));
        try
        {
            FamilyContents? contents = null;
            List<LabelFamilyTypeAction> typePlan = new();
            LabelFamilyParameterInfo? parameter = null;
            if (seedDoc != null)
            {
                contents = Read(seedDoc);
                if (!IsGenericAnnotation(seedDoc.OwnerFamily))
                    problems.Add($"The seed '{spec.SeedName}' is a {contents.Category} family; a label family must be a Generic Annotation.");
                typePlan = LabelFamilyVariantParser.PlanTypes(contents.Types, spec.Types);
                if (typePlan.Count(t => t.Action != "delete") == 0)
                    problems.Add("The variant would have no type.");
                parameter = LabelFamilyVariantParser.ResolveParameter(spec, contents.Parameters, problems);
                if (contents.Labels.Count == 0)
                {
                    if (spec.Appearance.HasValues)
                        problems.Add($"Revit's API sees no label in '{spec.SeedName}', so the text appearance (textSize, font, …) cannot be set. " +
                                     "Leave those fields out, or check the seed holds a label.");
                    else
                        warnings.Add($"Revit's API sees no label in '{spec.SeedName}'; check that it holds a label bound to a parameter.");
                }
                else if (contents.Labels.Count > 1)
                    warnings.Add($"'{spec.SeedName}' has {contents.Labels.Count} labels; the text appearance goes to every label's text type.");
                if (spec.Appearance.HasValues)
                    foreach (var shared in contents.LabelTextTypes)
                    {
                        var notes = new FilteredElementCollector(seedDoc).OfClass(typeof(TextNote)).Count(n => n.GetTypeId() == shared.Id);
                        if (notes > 0) warnings.Add($"The text type '{shared.Name}' is also used by {notes} text note(s) in the seed; they change with the label.");
                    }
            }

            object Plan() => new
            {
                seed = new
                {
                    name = spec.SeedName,
                    source = seedKind,
                    path = spec.SeedIsPath ? spec.Seed : null,
                    category = contents?.Category,
                    types = contents?.Types,
                    labels = contents?.Labels.Count,
                    parameters = contents?.Parameters.Select(p => new { name = p.Name, dataType = p.DataType, instance = p.IsInstance, shared = p.IsShared, editable = p.Editable }).ToList()
                },
                familyName = spec.FamilyName,
                savePath = spec.SavePath,
                fileExists,
                willReplaceFile = fileExists && spec.Overwrite,
                types = typePlan.Select(t => new { name = t.Name, action = t.Action, seedName = t.Action == "rename" ? t.SeedName : null }).ToList(),
                parameter = parameter == null ? null : new
                {
                    name = parameter.Name,
                    dataType = parameter.DataType,
                    rename = spec.RenameTo.Length > 0 && spec.RenameTo != parameter.Name ? new { from = parameter.Name, to = spec.RenameTo } : null,
                    scope = spec.MakeInstance == null ? (parameter.IsInstance ? "instance" : "type")
                        : spec.MakeInstance.Value == parameter.IsInstance ? (parameter.IsInstance ? "instance" : "type")
                        : $"{(parameter.IsInstance ? "instance" : "type")} -> {(spec.MakeInstance.Value ? "instance" : "type")}",
                    sampleValue = spec.SampleValue
                },
                appearance = spec.Appearance.HasValues
                    ? new { requested = spec.Appearance.Describe(), textTypes = contents?.LabelTextTypes.Select(DescribeTextType).ToList() }
                    : null,
                load = new { status = loadStatus, documentTitle = doc.Title, ifExists = spec.IfExists }
            };

            if (!apply)
            {
                warnings.AddRange(dialogs.Dismissed.Distinct().Select(d => $"Closed a Revit dialog: {d}"));
                sw.Stop();
                return new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = true,
                    Message = problems.Count == 0
                        ? $"Preview: ready to save '{spec.FamilyName}' as a variant of '{spec.SeedName}' at '{spec.SavePath}'."
                        : $"Preview: {problems.Count} problem(s) to fix before creating the label family.",
                    Data = new { ready = problems.Count == 0, problems, plan = Plan() },
                    Warnings = warnings,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            if (problems.Count > 0 || seedDoc == null || contents == null)
                return new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Status = "invalid_arguments",
                    Message = $"Nothing was created: {problems.Count} problem(s). {string.Join(" ", problems.Take(3))}",
                    Errors = problems,
                    Warnings = warnings,
                    DurationMs = sw.ElapsedMilliseconds
                };

            // 1. Change the copy: types, the label's parameter, the label's text type. One transaction.
            var textResults = new List<object>();
            using (var tx = new Transaction(seedDoc, "Revit MCP - Label family variant"))
            {
                tx.Start();
                ApplyTypes(seedDoc.FamilyManager, typePlan);
                if (parameter != null)
                    ApplyParameter(seedDoc.FamilyManager, contents.ParameterElements[parameter.Name], spec, warnings);
                if (spec.Appearance.HasValues)
                    foreach (var textType in contents.LabelTextTypes)
                        textResults.Add(ApplyAppearance(textType, spec.Appearance, warnings));
                TransactionCommitGuard.CommitOrThrow(tx);
            }
            var after = Read(seedDoc);

            // 2. Save the copy to a temporary file of the final name, close it, copy into place.
            Directory.CreateDirectory(tempFolder);
            var tempPath = Path.Combine(tempFolder, spec.FamilyName + ".rfa");
            seedDoc.SaveAs(tempPath, new SaveAsOptions { OverwriteExistingFile = true });
            seedDoc.Close(false);
            seedDoc = null;

            Directory.CreateDirectory(Path.GetDirectoryName(spec.SavePath)!);
            if (File.Exists(spec.SavePath) && !spec.Overwrite)
                return Fail(request, $"'{spec.SavePath}' appeared while the family was being made; nothing was replaced.", warnings);
            File.Copy(tempPath, spec.SavePath, overwrite: spec.Overwrite);

            // 3. Load.
            object? loadResult = spec.LoadIntoProject ? Load(doc, spec, alreadyLoaded, overwriteLoaded, warnings) : null;
            warnings.AddRange(dialogs.Dismissed.Distinct().Select(d => $"Closed a Revit dialog: {d}"));

            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Saved label family '{spec.FamilyName}' (a variant of '{spec.SeedName}', {after.Types.Count} type(s)) at '{spec.SavePath}'" +
                          $"{(spec.LoadIntoProject ? $"; load: {loadResult?.GetType().GetProperty("status")?.GetValue(loadResult) ?? "none"}" : string.Empty)}.",
                Data = new
                {
                    familyName = spec.FamilyName,
                    savePath = spec.SavePath,
                    replacedFile = fileExists,
                    seed = new { name = spec.SeedName, source = seedKind, changed = false },
                    types = after.Types,
                    typeChanges = typePlan.Where(t => t.Action != "keep").Select(t => new { name = t.Name, action = t.Action }).ToList(),
                    parameters = after.Parameters.Select(p => new { name = p.Name, dataType = p.DataType, instance = p.IsInstance, shared = p.IsShared }).ToList(),
                    labelTextTypes = textResults.Count > 0 ? textResults : null,
                    load = loadResult
                },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            return Fail(request, $"Making the label family failed and nothing was saved at '{spec.SavePath}': {ex.Message}", warnings);
        }
        finally
        {
            try { seedDoc?.Close(false); } catch { /* already closed */ }
            try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, recursive: true); } catch { /* temp only */ }
        }
    }

    // ── Opening and reading ─────────────────────────────────────────────────

    /// <summary>Opens the seed: a loaded family by name (EditFamily: an in-memory copy) or a .rfa file.</summary>
    private static (Document? Doc, string? Kind, string? Error) OpenSeed(UIApplication uiapp, Document doc, string seed)
    {
        if (seed.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
        {
            var (opened, error) = OpenFile(uiapp, seed);
            return (opened, "file", error);
        }

        var loaded = FamilyInventory.LoadedByName(doc);
        if (!loaded.TryGetValue(seed, out var family))
        {
            var close = LabelFamilyVariantParser.Suggest(seed, loaded.Where(f => IsGenericAnnotation(f.Value)).Select(f => f.Key));
            return (null, "project", $"seed '{seed}' is not a family loaded in '{doc.Title}' (or pass a .rfa path)" +
                                     $"{(close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(s => $"'{s}'"))}?" : ".")}");
        }
        if (!IsGenericAnnotation(family))
            return (null, "project", $"seed '{family.Name}' is a {FamilyInventory.CategoryOf(family)} family; a label family must be a Generic Annotation.");
        if (!family.IsEditable)
            return (null, "project", $"seed '{family.Name}' cannot be opened for editing.");
        try
        {
            var opened = doc.EditFamily(family);
            return opened == null
                ? (null, "project", $"Revit could not open the family '{family.Name}'.")
                : (opened, "project", null);
        }
        catch (Exception ex)
        {
            return (null, "project", $"Revit could not open the family '{family.Name}': {ex.Message}");
        }
    }

    /// <summary>Opens a .rfa file that is not open yet. The caller closes it without saving.</summary>
    private static (Document? Doc, string? Error) OpenFile(UIApplication uiapp, string path)
    {
        if (!path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)) return (null, $"'{path}' is not a .rfa file.");
        var policyError = new FilePathPolicy().ValidateRead(path);
        if (policyError != null) return (null, policyError);
        if (!File.Exists(path)) return (null, $"'{path}' does not exist.");
        foreach (Document open in uiapp.Application.Documents)
        {
            bool same;
            try
            {
                var openPath = open.PathName;
                same = openPath.Length > 0 && string.Equals(Path.GetFullPath(openPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            }
            catch { continue; }
            if (same)
                return (null, $"'{path}' is open in Revit; close it first, or name the loaded family instead.");
        }
        try
        {
            var opened = uiapp.Application.OpenDocumentFile(path);
            if (opened == null) return (null, $"Revit could not open '{path}'.");
            if (!opened.IsFamilyDocument)
            {
                try { opened.Close(false); } catch { /* already closed */ }
                return (null, $"'{path}' is not a family.");
            }
            return (opened, null);
        }
        catch (Exception ex)
        {
            return (null, $"Revit could not open '{path}': {ex.Message}");
        }
    }

    private static FamilyContents Read(Document familyDoc)
    {
        var contents = new FamilyContents { Category = SafeCategory(familyDoc.OwnerFamily) };
        var manager = familyDoc.FamilyManager;
        contents.Types = manager.Types.Cast<FamilyType>().Select(t => t.Name ?? string.Empty)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (FamilyParameter p in manager.Parameters)
        {
            if (p.Id.Value < 0 || p.Definition is InternalDefinition { BuiltInParameter: not BuiltInParameter.INVALID }) continue;
            var name = p.Definition.Name;
            contents.ParameterElements[name] = p;
            ForgeTypeId? dataType = null;
            try { dataType = p.Definition.GetDataType(); } catch { /* none */ }
            contents.Parameters.Add(new LabelFamilyParameterInfo
            {
                Name = name,
                IsInstance = p.IsInstance,
                IsShared = p.IsShared,
                DataType = ParameterRevitTypes.FriendlyOf(dataType) ?? ParameterRevitTypes.SpecLabel(dataType),
                Formula = p.Formula,
                IsReadOnly = p.IsReadOnly || p.IsDeterminedByFormula
            });
        }
        contents.Parameters = contents.Parameters.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // Labels: text elements that are not text notes (the API has no Label class).
        contents.Labels = new FilteredElementCollector(familyDoc).WhereElementIsNotElementType().ToElements()
            .OfType<TextElement>().Where(e => e is not TextNote).ToList();
        contents.LabelTextTypes = contents.Labels.Select(l => l.GetTypeId()).Where(id => id != ElementId.InvalidElementId)
            .Distinct().Select(id => familyDoc.GetElement(id)).OfType<TextElementType>().ToList();
        return contents;
    }

    private static object DescribeTextType(TextElementType type)
    {
        double? Double(BuiltInParameter bip) => type.get_Parameter(bip) is { HasValue: true, StorageType: StorageType.Double } p ? p.AsDouble() : null;
        int? Int(BuiltInParameter bip) => type.get_Parameter(bip) is { HasValue: true, StorageType: StorageType.Integer } p ? p.AsInteger() : null;
        var size = Double(BuiltInParameter.TEXT_SIZE);
        var color = Int(BuiltInParameter.LINE_COLOR);
        return new
        {
            id = type.Id.Value,
            name = type.Name,
            textSizeMm = size == null ? (double?)null : Math.Round(size.Value * MmPerFoot, 3),
            font = type.get_Parameter(BuiltInParameter.TEXT_FONT)?.AsString(),
            bold = Int(BuiltInParameter.TEXT_STYLE_BOLD) is { } b ? b != 0 : (bool?)null,
            italic = Int(BuiltInParameter.TEXT_STYLE_ITALIC) is { } i ? i != 0 : (bool?)null,
            underline = Int(BuiltInParameter.TEXT_STYLE_UNDERLINE) is { } u ? u != 0 : (bool?)null,
            widthFactor = Double(BuiltInParameter.TEXT_WIDTH_SCALE),
            color = color == null ? null : LabelFamilyVariantParser.RevitIntToColor(color.Value)
        };
    }

    // ── Changing the copy ───────────────────────────────────────────────────

    private static void ApplyTypes(FamilyManager manager, List<LabelFamilyTypeAction> plan)
    {
        FamilyType? Find(string name) => manager.Types.Cast<FamilyType>().FirstOrDefault(t => string.Equals(t.Name ?? string.Empty, name, StringComparison.Ordinal));

        foreach (var rename in plan.Where(t => t.Action == "rename"))
        {
            var type = Find(rename.SeedName!) ?? throw new InvalidOperationException($"The seed type '{rename.SeedName}' was not found.");
            manager.CurrentType = type;
            // A change of case only: through a free name, so Revit does not see the new name as taken.
            manager.RenameCurrentType(rename.Name + " (" + Guid.NewGuid().ToString("N").Substring(0, 6) + ")");
            manager.RenameCurrentType(rename.Name);
        }

        // New types copy the first kept type's values.
        var start = plan.Where(t => t.Action is "keep" or "rename").Select(t => Find(t.Name)).FirstOrDefault(t => t != null);
        foreach (var create in plan.Where(t => t.Action == "create"))
        {
            if (start != null) manager.CurrentType = start;
            var created = manager.NewType(create.Name);
            start ??= created;
        }

        foreach (var delete in plan.Where(t => t.Action == "delete"))
        {
            var type = Find(delete.Name);
            if (type == null) continue;
            manager.CurrentType = type;
            manager.DeleteCurrentType();
        }
    }

    private static void ApplyParameter(FamilyManager manager, FamilyParameter parameter, LabelFamilyVariantSpec spec, List<string> warnings)
    {
        if (spec.MakeInstance != null && spec.MakeInstance.Value != parameter.IsInstance)
        {
            if (spec.MakeInstance.Value) manager.MakeInstance(parameter);
            else manager.MakeType(parameter);
        }
        if (spec.RenameTo.Length > 0 && !string.Equals(parameter.Definition.Name, spec.RenameTo, StringComparison.Ordinal))
            manager.RenameParameter(parameter, spec.RenameTo);

        if (spec.SampleValue == null) return;
        foreach (var type in manager.Types.Cast<FamilyType>().ToList())
        {
            manager.CurrentType = type;
            if (parameter.StorageType == StorageType.String) manager.Set(parameter, spec.SampleValue);
            else
            {
                try { manager.SetValueString(parameter, spec.SampleValue); }
                catch (Exception ex)
                {
                    warnings.Add($"sampleValue '{spec.SampleValue}' was not set in type '{type.Name}': {ex.Message}");
                }
            }
        }
    }

    /// <summary>Sets the appearance fields on one label text type; what Revit refuses is reported, not fatal.</summary>
    private static object ApplyAppearance(TextElementType type, LabelTextAppearance appearance, List<string> warnings)
    {
        var applied = new List<string>();
        var refused = new List<string>();
        void Set(string field, BuiltInParameter bip, Action<Parameter> set)
        {
            var parameter = type.get_Parameter(bip);
            if (parameter == null || parameter.IsReadOnly) { refused.Add($"{field} (the text type has no settable {bip})"); return; }
            try { set(parameter); applied.Add(field); }
            catch (Exception ex) { refused.Add($"{field} ({ex.Message})"); }
        }

        if (appearance.SizeMm != null) Set("textSize", BuiltInParameter.TEXT_SIZE, p => p.Set(appearance.SizeMm.Value / MmPerFoot));
        if (appearance.Font != null) Set("font", BuiltInParameter.TEXT_FONT, p => p.Set(appearance.Font));
        if (appearance.Bold != null) Set("bold", BuiltInParameter.TEXT_STYLE_BOLD, p => p.Set(appearance.Bold.Value ? 1 : 0));
        if (appearance.Italic != null) Set("italic", BuiltInParameter.TEXT_STYLE_ITALIC, p => p.Set(appearance.Italic.Value ? 1 : 0));
        if (appearance.Underline != null) Set("underline", BuiltInParameter.TEXT_STYLE_UNDERLINE, p => p.Set(appearance.Underline.Value ? 1 : 0));
        if (appearance.WidthFactor != null) Set("widthFactor", BuiltInParameter.TEXT_WIDTH_SCALE, p => p.Set(appearance.WidthFactor.Value));
        if (appearance.Color != null) Set("color", BuiltInParameter.LINE_COLOR, p => p.Set(LabelFamilyVariantParser.ColorToRevitInt(appearance.Color)));

        if (refused.Count > 0) warnings.Add($"Text type '{type.Name}': not set: {string.Join("; ", refused)}.");
        return new { name = type.Name, applied, refused = refused.Count > 0 ? refused : null, now = DescribeTextType(type) };
    }

    // ── Load ────────────────────────────────────────────────────────────────

    private static object Load(Document doc, LabelFamilyVariantSpec spec, bool alreadyLoaded, bool overwrite, List<string> warnings)
    {
        if (alreadyLoaded && !overwrite)
            return new { status = "skipped", reason = $"A family named '{spec.FamilyName}' is already loaded (ifExists=skip)." };

        Family? family;
        using (var tx = new Transaction(doc, $"Revit MCP - Load Family {spec.FamilyName}"))
        {
            tx.Start();
            var ok = doc.LoadFamily(spec.SavePath, new FamilyLoadOptions(overwrite ? FamilyIfExists.Overwrite : FamilyIfExists.Skip), out family);
            if (!ok || family == null)
            {
                tx.RollBack();
                return new { status = alreadyLoaded ? "skipped" : "failed", reason = alreadyLoaded ? "The loaded family already matches the file." : "Revit did not load the file." };
            }
            foreach (var symbol in FamilyInventory.Symbols(doc, family))
            {
                try { if (!symbol.IsActive) symbol.Activate(); }
                catch { /* nothing to activate */ }
            }
            TransactionCommitGuard.CommitOrThrow(tx);
        }

        var storeError = FamilyPathStore.Default().Remember(CheckpointChangeTracker.ModelIdOf(doc), new List<(string, string)> { (spec.FamilyName, spec.SavePath) });
        if (storeError != null) warnings.Add(storeError);
        return new
        {
            status = alreadyLoaded ? "overwritten" : "loaded",
            familyId = family.Id.Value,
            types = FamilyInventory.Symbols(doc, family).Select(s => new { id = s.Id.Value, name = FamilyTypeSupport.SafeName(s) }).ToList()
        };
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static bool IsGenericAnnotation(Family? family)
    {
        try { return family?.FamilyCategory != null && family.FamilyCategory.Id.Value == (long)BuiltInCategory.OST_GenericAnnotation; }
        catch { return false; }
    }

    private static string SafeCategory(Family? family)
    {
        try { return family?.FamilyCategory?.Name ?? "uncategorised"; }
        catch { return "uncategorised"; }
    }

    private static McpToolResult Fail(McpToolRequest request, string message, List<string>? warnings = null) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Message = message,
        Errors = [message],
        Warnings = warnings ?? new List<string>()
    };
}
