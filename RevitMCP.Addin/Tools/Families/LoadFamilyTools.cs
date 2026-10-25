using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Families;

/// <summary>revit_preview_load_family (#102): which files would load and which policy applies, without opening them.</summary>
public sealed class PreviewLoadFamilyTool : IRevitMcpTool
{
    public string Name => "revit_preview_load_family";

    public string Description =>
        "Previews loading .rfa family files, without changing the model. Same arguments as revit_load_family: paths, or " +
        "folder + pattern (default *.rfa, recursive optional), ifExists, activateTypes. Checks each file cheaply (exists, " +
        ".rfa, the file access policy, size, Revit backups) and whether a family of the same name (the file name) is " +
        "already loaded, and states per file whether it would load, be skipped or overwrite the loaded family. The files " +
        "are not opened, so a damaged file or one from a newer Revit only fails at load time.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(FamilyLoadExecutor.Load(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_load_family (#102): loads family files, one transaction per file, one undo step.</summary>
public sealed class LoadFamilyTool : IRevitMcpTool
{
    public string Name => "revit_load_family";

    public string Description =>
        "Loads .rfa family files into the document. Requires approval. paths (up to 50 .rfa files) or folder + pattern " +
        "(default *.rfa; recursive=true includes subfolders). ifExists for a family of the same name already loaded: " +
        "skip (default) | overwrite (the file's version, the project's parameter values kept) | overwriteParameters (the " +
        "file's version and its parameter values) | overwriteAll (as overwriteParameters, and shared nested families " +
        "also from the file). activateTypes (default true) activates every type so placement tools can use it at once. " +
        "Each file loads in its own transaction, so a bad file never undoes the others; all loads are one undo step. " +
        "Returns per file {path, family, status: loaded | skipped | overwritten | failed, types, reason} and remembers the " +
        "path for revit_reload_family. Run revit_preview_load_family first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(FamilyLoadExecutor.Load(uiapp, request, apply: true, cancellationToken));
}

/// <summary>revit_reload_family (#102): reloads a loaded family from its file.</summary>
public sealed class ReloadFamilyTool : IRevitMcpTool
{
    public string Name => "revit_reload_family";

    public string Description =>
        "Reloads a loaded family from its .rfa file. Requires approval. family (name or element id); path optional — " +
        "default is the file revit_load_family last loaded it from (Revit does not keep the path; without a known path " +
        "the tool asks for one). The file name must match the family name, or Revit would load a second family. ifExists: " +
        "overwrite (default: the project's parameter values kept) | overwriteParameters | overwriteAll. activateTypes " +
        "(default true). Returns the types added and removed, and the instance count.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(FamilyLoadExecutor.Reload(uiapp, request, cancellationToken));
}

internal static class FamilyLoadExecutor
{
    public const string LoadTransactionName = "Revit MCP - Load Families";

    public static McpToolResult Load(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;
        var args = request.Arguments;

        if (!FamilyLoadPolicy.TryParseIfExists(ToolArguments.GetString(args, "ifExists"), FamilyIfExists.Skip, out var policy, out var policyError))
            return Fail(request, policyError!);
        var activateTypes = ToolArguments.GetBool(args, "activateTypes", true);

        var selection = FamilyLoadPolicy.SelectFiles(
            ToolArguments.GetStringArray(args, "paths"),
            ToolArguments.GetString(args, "folder"),
            ToolArguments.GetString(args, "pattern"),
            ToolArguments.GetBool(args, "recursive"),
            new FilePathPolicy());
        if (selection.Error != null) return Fail(request, selection.Error, selection.Warnings);

        var loaded = FamilyInventory.LoadedByName(doc);
        var warnings = new List<string>(selection.Warnings);

        if (!apply)
        {
            var counts = FamilyTypeSupport.CountInstancesByType(doc);
            var files = selection.Files.Select(file =>
            {
                loaded.TryGetValue(file.FamilyName, out var existing);
                var symbols = existing == null ? new List<FamilySymbol>() : FamilyInventory.Symbols(doc, existing);
                return new
                {
                    path = file.Path,
                    family = file.FamilyName,
                    sizeKb = Math.Round(file.SizeBytes / 1024.0, 1),
                    status = file.IsValid ? FamilyLoadPolicy.PlannedStatus(existing != null, policy) : file.Status,
                    alreadyLoaded = existing != null,
                    loadedFamilyId = existing?.Id.Value,
                    loadedTypeCount = existing == null ? (int?)null : symbols.Count,
                    loadedInstanceCount = existing == null ? (int?)null : symbols.Sum(s => counts.TryGetValue(s.Id.Value, out var c) ? c : 0),
                    reason = file.Reason
                };
            }).ToList();

            sw.Stop();
            var willLoad = files.Count(f => f.status == "willLoad");
            var willOverwrite = files.Count(f => f.status == "willOverwrite");
            var willSkip = files.Count(f => f.status == "willSkip");
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Preview: {willLoad} new famil{(willLoad == 1 ? "y" : "ies")} would load, {willOverwrite} would overwrite " +
                          $"the loaded version, {willSkip} would be skipped (ifExists={FamilyLoadPolicy.Describe(policy)}), " +
                          $"{files.Count - willLoad - willOverwrite - willSkip} file(s) cannot be loaded.",
                Data = new
                {
                    documentTitle = doc.Title,
                    ifExists = FamilyLoadPolicy.Describe(policy),
                    activateTypes,
                    files,
                    note = "The files are not opened by the preview; a damaged file, one from a newer Revit version or one " +
                           "that is not a family fails only at load time, without undoing the others."
                },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        var results = new List<FileLoadResult>();
        var valid = selection.Valid.ToList();
        foreach (var file in selection.Files.Where(f => !f.IsValid))
            results.Add(new FileLoadResult { Path = file.Path, Family = file.FamilyName, Status = "failed", Reason = file.Reason });

        var toLoad = new List<FamilyFileCandidate>();
        foreach (var file in valid)
        {
            if (policy == FamilyIfExists.Skip && loaded.ContainsKey(file.FamilyName))
                results.Add(new FileLoadResult
                {
                    Path = file.Path, Family = file.FamilyName, Status = "skipped",
                    FamilyId = loaded[file.FamilyName].Id.Value,
                    Types = FamilyInventory.Symbols(doc, loaded[file.FamilyName]).Select(FamilyTypeSupport.SafeName).ToList(),
                    Reason = "A family of this name is already loaded (ifExists=skip)."
                });
            else
                toLoad.Add(file);
        }

        string? groupError = null;
        if (toLoad.Count > 0)
        {
            using var transactions = ItemTransactions.Start(doc, LoadTransactionName, out var startError);
            if (transactions == null) return Fail(request, startError!, warnings);
            foreach (var file in toLoad)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(LoadOne(doc, transactions, file, policy, activateTypes, loaded.ContainsKey(file.FamilyName)));
            }
            groupError = transactions.Finish();
            warnings.AddRange(transactions.Warnings.Select(w => "Revit: " + w));
            if (groupError != null)
            {
                foreach (var result in results.Where(r => r.Status is "loaded" or "overwritten"))
                {
                    result.Status = "failed";
                    result.Reason = groupError;
                }
            }
        }

        var remembered = results.Where(r => r.Status is "loaded" or "overwritten").Select(r => (r.Family, r.Path)).ToList();
        var storeError = FamilyPathStore.Default().Remember(CheckpointChangeTracker.ModelIdOf(doc), remembered);
        if (storeError != null) warnings.Add(storeError);

        // Keep the order the caller named the files in.
        var order = selection.Files.Select((f, i) => (f.Path, i)).GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        results = results.OrderBy(r => order.TryGetValue(r.Path, out var i) ? i : int.MaxValue).ToList();

        sw.Stop();
        var loadedCount = results.Count(r => r.Status == "loaded");
        var overwritten = results.Count(r => r.Status == "overwritten");
        var skipped = results.Count(r => r.Status == "skipped");
        var failed = results.Count(r => r.Status == "failed");
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = loadedCount + overwritten + skipped > 0 && groupError == null,
            Status = loadedCount + overwritten + skipped == 0 ? "transaction_failed" : null,
            Message = $"Loaded {loadedCount}, overwrote {overwritten}, skipped {skipped}, failed {failed} of {results.Count} family file(s)" +
                      $"{(groupError != null ? $": {groupError}" : ".")}",
            Data = new
            {
                documentTitle = doc.Title,
                ifExists = FamilyLoadPolicy.Describe(policy),
                activateTypes,
                loaded = loadedCount,
                overwritten,
                skipped,
                failed,
                files = results.Select(r => r.ToPayload()).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static FileLoadResult LoadOne(Document doc, ItemTransactions transactions, FamilyFileCandidate file, FamilyIfExists policy,
        bool activateTypes, bool existedBefore)
    {
        var result = new FileLoadResult { Path = file.Path, Family = file.FamilyName };
        var options = new FamilyLoadOptions(policy);
        Family? family = null;
        var notLoaded = false;
        var error = transactions.Run($"Revit MCP - Load Family {file.FamilyName}", () =>
        {
            var ok = doc.LoadFamily(file.Path, options, out family);
            if (!ok || family == null)
            {
                notLoaded = true;
                return options.FamilyFound
                    ? "Revit kept the loaded family (the load options declined the overwrite)."
                    : existedBefore
                        ? "Revit loaded nothing: the loaded family already matches the file."
                        : "Revit did not load the file: it may be from a newer Revit version, damaged, or not a loadable family.";
            }
            if (activateTypes) ActivateAll(doc, family);
            return null;
        });

        if (error != null)
        {
            // Declined by the skip policy, or the loaded family is identical to the file: nothing failed.
            result.Status = notLoaded && ((options.FamilyFound && policy == FamilyIfExists.Skip) || (!options.FamilyFound && existedBefore))
                ? "skipped"
                : "failed";
            if (result.Status == "skipped" && family == null && FamilyInventory.LoadedByName(doc).TryGetValue(file.FamilyName, out var kept))
            {
                result.FamilyId = kept.Id.Value;
                result.Types = FamilyInventory.Symbols(doc, kept).Select(FamilyTypeSupport.SafeName).ToList();
            }
            result.Reason = error;
            return result;
        }

        result.FamilyId = family!.Id.Value;
        result.Family = FamilyTypeSupport.SafeName(family);
        result.Status = existedBefore || options.FamilyFound ? "overwritten" : "loaded";
        result.Types = FamilyInventory.Symbols(doc, family).Select(FamilyTypeSupport.SafeName).ToList();
        result.SharedFamilies = options.SharedFamiliesFound.Where(n => n.Length > 0).Distinct().ToList();
        if (result.Status == "overwritten")
            result.Reason = $"The loaded version was replaced (ifExists={FamilyLoadPolicy.Describe(policy)}).";
        return result;
    }

    private static void ActivateAll(Document doc, Family family)
    {
        foreach (var symbol in FamilyInventory.Symbols(doc, family))
        {
            try { if (!symbol.IsActive) symbol.Activate(); }
            catch { /* an annotation or profile type has nothing to activate */ }
        }
    }

    // ── Reload ───────────────────────────────────────────────────────────────

    public static McpToolResult Reload(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;
        var args = request.Arguments;
        var warnings = new List<string>();

        if (!FamilyLoadPolicy.TryParseIfExists(ToolArguments.GetString(args, "ifExists"), FamilyIfExists.Overwrite, out var policy, out var policyError))
            return Fail(request, policyError!);
        if (policy == FamilyIfExists.Skip)
            return Fail(request, "ifExists=skip would leave the family as it is; a reload takes overwrite, overwriteParameters or overwriteAll.");
        var activateTypes = ToolArguments.GetBool(args, "activateTypes", true);

        var familyArgument = ToolArguments.GetString(args, "family").Trim();
        if (familyArgument.Length == 0) return Fail(request, "family is required: the family name or its element id (revit_list_families).");
        var family = ResolveFamily(doc, familyArgument);
        if (family == null) return Fail(request, $"No loaded family '{familyArgument}'. Use revit_list_families to find it.");
        var familyName = FamilyTypeSupport.SafeName(family);
        if (SafeInPlace(family)) return Fail(request, $"'{familyName}' is an in-place family; it has no file to reload from.");

        var modelId = CheckpointChangeTracker.ModelIdOf(doc);
        var store = FamilyPathStore.Default();
        var path = ToolArguments.GetString(args, "path").Trim();
        var pathSource = "argument";
        if (path.Length == 0)
        {
            var entry = store.Get(modelId, familyName);
            if (entry == null)
                return Fail(request, $"No known file for family '{familyName}': Revit does not keep a family's source path and it " +
                                     "was not loaded by revit_load_family. Pass path=<the .rfa file>.");
            path = entry.Path;
            pathSource = $"family path store (loaded {entry.LoadedUtc:yyyy-MM-dd HH:mm} UTC)";
        }

        var selection = FamilyLoadPolicy.SelectFiles(new[] { path }, null, null, false, new FilePathPolicy());
        var file = selection.Files.FirstOrDefault();
        if (selection.Error != null || file == null || !file.IsValid)
            return Fail(request, $"{path}: {selection.Error ?? file?.Reason ?? "not a family file"}");
        if (!string.Equals(file.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
            return Fail(request, $"The file '{System.IO.Path.GetFileName(file.Path)}' loads as family '{file.FamilyName}', not '{familyName}': Revit " +
                                 "names a loaded family after its file, so this would add a second family instead of reloading. " +
                                 "Rename the file or pass the right path.");

        var before = FamilyInventory.Symbols(doc, family).Select(s => (Id: s.Id.Value, Name: FamilyTypeSupport.SafeName(s))).ToList();
        var counts = FamilyTypeSupport.CountInstancesByType(doc);
        var instances = before.Sum(t => counts.TryGetValue(t.Id, out var c) ? c : 0);

        cancellationToken.ThrowIfCancellationRequested();
        using var transactions = ItemTransactions.Start(doc, "Revit MCP - Reload Family", out var startError);
        if (transactions == null) return Fail(request, startError!);
        var result = LoadOne(doc, transactions, file, policy, activateTypes, existedBefore: true);
        var groupError = transactions.Finish();
        warnings.AddRange(transactions.Warnings.Select(w => "Revit: " + w));
        if (groupError != null)
        {
            result.Status = "failed";
            result.Reason = groupError;
        }

        if (result.Status != "overwritten")
        {
            sw.Stop();
            var upToDate = result.Status == "skipped";
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = upToDate,
                Status = upToDate ? null : "transaction_failed",
                Message = upToDate ? $"'{familyName}' already matches {file.Path}; nothing was reloaded." : $"'{familyName}' was not reloaded: {result.Reason}",
                Data = new { family = familyName, familyId = family.Id.Value, path = file.Path, pathSource, result = result.ToPayload() },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        var storeError = store.Remember(modelId, new[] { (familyName, file.Path) });
        if (storeError != null) warnings.Add(storeError);

        var reloaded = doc.GetElement(new ElementId(result.FamilyId ?? family.Id.Value)) as Family ?? family;
        var after = FamilyInventory.Symbols(doc, reloaded).Select(s => (Id: s.Id.Value, Name: FamilyTypeSupport.SafeName(s))).ToList();
        var added = after.Where(a => before.All(b => !string.Equals(b.Name, a.Name, StringComparison.OrdinalIgnoreCase))).Select(a => new { typeId = a.Id, name = a.Name }).ToList();
        var removed = before.Where(b => after.All(a => !string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))).Select(b => new { typeId = b.Id, name = b.Name }).ToList();
        if (removed.Count > 0)
            warnings.Add($"{removed.Count} type(s) are no longer in the file; Revit keeps loaded types that the file dropped only while instances use them. Check: {string.Join(", ", removed.Select(r => r.name))}.");

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Reloaded '{familyName}' from {file.Path} (ifExists={FamilyLoadPolicy.Describe(policy)}): {after.Count} type(s), " +
                      $"{added.Count} added, {removed.Count} removed, {instances} instance(s).",
            Data = new
            {
                documentTitle = doc.Title,
                family = familyName,
                familyId = reloaded.Id.Value,
                path = file.Path,
                pathSource,
                ifExists = FamilyLoadPolicy.Describe(policy),
                instanceCount = instances,
                types = after.Select(a => new { typeId = a.Id, name = a.Name }).ToList(),
                typesAdded = added,
                typesRemoved = removed,
                sharedFamilies = result.SharedFamilies
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

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

    private static McpToolResult Fail(McpToolRequest request, string message, List<string>? warnings = null) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Status = "validation_failed",
        Message = message,
        Warnings = warnings ?? new List<string>()
    };

    private sealed class FileLoadResult
    {
        public string Path { get; set; } = string.Empty;
        public string Family { get; set; } = string.Empty;
        public long? FamilyId { get; set; }
        public string Status { get; set; } = string.Empty;
        public List<string> Types { get; set; } = new();
        public List<string> SharedFamilies { get; set; } = new();
        public string? Reason { get; set; }

        public object ToPayload() => new
        {
            path = Path,
            family = Family,
            familyId = FamilyId,
            status = Status,
            types = Types,
            sharedNestedFamilies = SharedFamilies.Count > 0 ? SharedFamilies : null,
            reason = Reason
        };
    }
}
