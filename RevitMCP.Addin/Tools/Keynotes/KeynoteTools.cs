using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Keynotes;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Keynotes;

/// <summary>revit_get_keynotes: the keynote table's entries, its file and, optionally, which elements carry each key.</summary>
public sealed class GetKeynotesTool : IRevitMcpTool
{
    public string Name => "revit_get_keynotes";

    public string Description =>
        "Reads the keynote table: entries {key, text, parent, children} in key order with search (key or text), parent " +
        "(direct children of a key; \"root\" = top level) and offset/limit paging (default 100, max 1000); the table's " +
        "file path, path type and load status. usage=true also counts, per listed key, the types, materials and other " +
        "elements whose Keynote parameter holds it, and lists keys used in the model that the table lacks.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;

        KeynoteTable table;
        try { table = KeynoteTable.GetKeynoteTable(doc); }
        catch (Exception ex) { return Task.FromResult(KeynoteSupport.Fail(request, $"'{doc.Title}' has no readable keynote table: {ex.Message}")); }

        var entries = KeynoteSupport.Entries(table);
        var source = KeynoteSupport.Source(table);
        var search = ToolArguments.GetString(request.Arguments, "search");
        // Absent = any level; "" or "root" = the top level.
        string? parent = request.Arguments.TryGetValue("parent", out var rawParent) && rawParent != null
            ? ToolArguments.GetString(request.Arguments, "parent")
            : null;
        var (offset, limit) = KeynoteLogic.Page(ToolArguments.GetInt(request.Arguments, "offset", 0), ToolArguments.GetInt(request.Arguments, "limit", KeynoteLogic.DefaultLimit));

        var matching = KeynoteLogic.Filter(entries, search, parent);
        var page = matching.Skip(offset).Take(limit).ToList();
        var children = KeynoteLogic.ChildCounts(entries);
        var warnings = new List<string>();

        Dictionary<string, KeynoteUsage>? usage = null;
        List<KeynoteUsage>? unknown = null;
        if (ToolArguments.GetBool(request.Arguments, "usage", false))
        {
            try
            {
                usage = KeynoteLogic.CountUsage(KeynoteSupport.Carriers(doc, cancellationToken), entries.Select(e => e.Key), out unknown);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add($"Usage could not be counted: {ex.Message}");
            }
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = entries.Count == 0
                ? $"The keynote table of '{doc.Title}' is empty{(source.Path == null ? " and has no file" : string.Empty)}."
                : $"{matching.Count} of {entries.Count} keynote(s) match; {page.Count} returned.",
            Data = new
            {
                document = doc.Title,
                table = source,
                total = entries.Count,
                matching = matching.Count,
                offset,
                limit,
                hasMore = offset + page.Count < matching.Count,
                entries = page.Select(e => new
                {
                    key = e.Key,
                    text = e.Text,
                    parent = e.ParentKey.Length > 0 ? e.ParentKey : null,
                    children = children.TryGetValue(e.Key, out var n) ? n : 0,
                    usage = usage == null ? null : usage.TryGetValue(e.Key, out var u)
                        ? new { types = u.Types, materials = u.Materials, elements = u.Elements, sampleIds = u.SampleIds }
                        : new { types = 0, materials = 0, elements = 0, sampleIds = new List<long>() }
                }).ToList(),
                usage = usage == null ? null : new
                {
                    keysUsed = usage.Count,
                    tableKeysUsed = usage.Keys.Count(k => entries.Any(e => e.Key == k)),
                    unknownKeys = unknown!.Take(50).Select(u => new { key = u.Key, types = u.Types, materials = u.Materials, elements = u.Elements, sampleIds = u.SampleIds }).ToList(),
                    unknownKeyCount = unknown!.Count
                }
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>revit_preview_reload_keynote_table: what reloading the keynote table would change.</summary>
public sealed class PreviewReloadKeynoteTableTool : IRevitMcpTool
{
    public string Name => "revit_preview_reload_keynote_table";

    public string Description =>
        "Previews revit_reload_keynote_table without changing the model. Same arguments. Reads the keynote file itself " +
        "(key TAB text TAB parent) and compares it with the table now in the model: added, removed and changed keys, " +
        "lines it could not read, and problems to fix first (a path outside the file access policy, a missing file). " +
        "Revit's own read on reload is what counts.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(KeynoteReloadExecutor.Execute(uiapp, request, apply: false));
}

/// <summary>revit_reload_keynote_table: reloads the keynote table from its file, or loads it from another file.</summary>
public sealed class ReloadKeynoteTableTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_reload_keynote_table";

    public string Description =>
        "Reloads the keynote table from its current file, or loads it from path (a local keynote .txt file, which the " +
        "table then points to). Requires approval. Reports the entries before and after: added, removed and changed " +
        "keys, and the errors Revit reported. One transaction, one undo. Run revit_preview_reload_keynote_table first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(KeynoteReloadExecutor.Execute(uiapp, request, apply: true));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var document = ToolArguments.GetString(request.Arguments, "document").Trim();
        var path = ToolArguments.GetString(request.Arguments, "path").Trim();
        string? current = null;
        try
        {
            var target = ToolDocument.Resolve(uiapp, request, out _);
            if (target != null)
            {
                document = target.Document.Title;
                current = KeynoteSupport.Source(KeynoteTable.GetKeynoteTable(target.Document)).Path;
            }
        }
        catch
        {
            // The arguments alone.
        }
        return KeynoteLogic.ApprovalSummary(document, path.Length > 0 ? path : null, current);
    }
}

/// <summary>Where the keynote table comes from.</summary>
internal sealed class KeynoteSource
{
    public string? Path { get; set; }
    public string? PathType { get; set; }
    public string? Status { get; set; }
    public bool FileExists { get; set; }
}

internal static class KeynoteSupport
{
    public static List<KeynoteEntryInfo> Entries(KeynoteTable table)
    {
        var result = new List<KeynoteEntryInfo>();
        using var entries = table.GetKeyBasedTreeEntries();
        foreach (KeyBasedTreeEntry entry in entries)
        {
            result.Add(new KeynoteEntryInfo
            {
                Key = entry.Key ?? string.Empty,
                Text = (entry as KeynoteEntry)?.KeynoteText ?? string.Empty,
                ParentKey = entry.ParentKey ?? string.Empty
            });
        }
        return result;
    }

    public static KeynoteSource Source(KeynoteTable table)
    {
        var source = new KeynoteSource();
        try
        {
            if (table.IsExternalFileReference())
            {
                var file = table.GetExternalFileReference();
                source.Path = ModelPathUtils.ConvertModelPathToUserVisiblePath(file.GetAbsolutePath());
                source.PathType = file.PathType.ToString();
                source.Status = file.GetLinkedFileStatus().ToString();
            }
        }
        catch
        {
            // Fall back to the external resource reference below.
        }
        if (string.IsNullOrWhiteSpace(source.Path))
        {
            try
            {
                var reference = table.GetExternalResourceReference(ExternalResourceTypes.BuiltInExternalResourceTypes.KeynoteTable);
                if (reference != null)
                {
                    source.Path = reference.InSessionPath;
                    source.Status ??= reference.GetResourceVersionStatus().ToString();
                }
            }
            catch
            {
                // No reference: the table has no file.
            }
        }
        if (string.IsNullOrWhiteSpace(source.Path)) source.Path = null;
        try { source.FileExists = source.Path != null && File.Exists(source.Path); }
        catch { source.FileExists = false; }
        return source;
    }

    /// <summary>(key, kind, id) of every type, material and other element whose Keynote parameter has a value.</summary>
    public static IEnumerable<(string Key, string Kind, long Id)> Carriers(Document doc, CancellationToken cancellationToken)
    {
        var result = new List<(string, string, long)>();
        var filter = new ElementParameterFilter(ParameterFilterRuleFactory.CreateHasValueParameterRule(new ElementId(BuiltInParameter.KEYNOTE_PARAM)));
        foreach (var e in new FilteredElementCollector(doc).WhereElementIsElementType().WherePasses(filter))
            Add(e, "type");
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(filter))
            Add(e, e is Material ? "material" : "element");
        return result;

        void Add(Element e, string kind)
        {
            var value = e.get_Parameter(BuiltInParameter.KEYNOTE_PARAM)?.AsString();
            if (!string.IsNullOrWhiteSpace(value)) result.Add((value!.Trim(), kind, e.Id.Value));
        }
    }

    public static McpToolResult Fail(McpToolRequest request, string message, string? status = null, List<string>? errors = null) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Status = status,
        Message = message,
        Errors = errors ?? [message]
    };
}

internal static class KeynoteReloadExecutor
{
    /// <summary>The largest keynote file the preview reads itself.</summary>
    private const long MaxPreviewBytes = 8 * 1024 * 1024;

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;

        KeynoteTable table;
        try { table = KeynoteTable.GetKeynoteTable(doc); }
        catch (Exception ex) { return KeynoteSupport.Fail(request, $"'{doc.Title}' has no keynote table: {ex.Message}"); }

        var problems = new List<string>();
        var warnings = new List<string>();
        var current = KeynoteSupport.Source(table);
        var rawPath = ToolArguments.GetString(request.Arguments, "path").Trim().Trim('"');
        string? newPath = null;
        if (rawPath.Length > 0)
        {
            var (full, normalizeError) = FilePathPolicy.NormalizePath(rawPath);
            var policyError = normalizeError ?? new FilePathPolicy().ValidateRead(full!);
            if (policyError != null) problems.Add($"path '{rawPath}': {policyError}");
            else if (!File.Exists(full)) problems.Add($"path '{full}' does not exist.");
            else
            {
                newPath = full;
                if (!string.Equals(Path.GetExtension(full), ".txt", StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"'{Path.GetFileName(full)}' is not a .txt file; Revit reads keynote tables from tab-separated text files.");
            }
        }
        else if (current.Path == null)
            problems.Add("The keynote table has no file to reload from; give path (Manage > Additional Settings > Keynoting Settings shows it).");
        else if (!current.FileExists)
            warnings.Add($"The table's file '{current.Path}' is not reachable from here; Revit may still read it (a server or relative path).");

        var before = KeynoteSupport.Entries(table);
        object Common() => new
        {
            document = doc.Title,
            currentPath = current.Path,
            pathType = current.PathType,
            status = current.Status,
            newPath,
            entriesBefore = before.Count
        };

        if (!apply)
        {
            var readFrom = newPath ?? (current.FileExists ? current.Path : null);
            object? diff = null;
            List<string>? fileProblems = null;
            int? fileEntries = null;
            string? note;
            if (problems.Count > 0) note = "Fix the problems first.";
            else if (readFrom == null) note = "The file is not reachable from here, so the change cannot be shown before the reload.";
            else
            {
                var (full, normalizeError) = FilePathPolicy.NormalizePath(readFrom);
                var policyError = normalizeError ?? new FilePathPolicy().ValidateRead(full!);
                if (policyError != null) note = $"The connector may not read '{readFrom}' ({policyError}); Revit still can on reload.";
                else if (new FileInfo(full!).Length > MaxPreviewBytes) note = $"'{readFrom}' is larger than {MaxPreviewBytes / (1024 * 1024)} MB; not read for the preview.";
                else
                {
                    var parsed = KeynoteLogic.ParseFile(File.ReadAllText(full!));
                    fileProblems = parsed.Problems.Take(50).ToList();
                    fileEntries = parsed.Entries.Count;
                    diff = KeynoteLogic.Describe(KeynoteLogic.Compare(before, parsed.Entries));
                    note = "Read by the connector (key TAB text TAB parent; '#' lines skipped). Revit's own read on reload is what counts.";
                }
            }
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = problems.Count > 0
                    ? $"Preview: {problems.Count} problem(s) to fix before reloading the keynote table."
                    : $"Preview: the keynote table of '{doc.Title}' would be {(newPath != null ? "loaded from " + newPath : "reloaded from its file")}.",
                Data = new { ready = problems.Count == 0, problems, table = Common(), fileEntries, fileProblems, change = diff, note },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (problems.Count > 0)
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "invalid_arguments",
                Message = $"Nothing was changed: {string.Join(" ", problems)}",
                Errors = problems,
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };

        string status;
        var revitMessages = new List<string>();
        try
        {
            using var tx = new Transaction(doc, "Revit MCP - Reload keynote table");
            tx.Start();
            using var results = new KeyBasedTreeEntriesLoadResults();
            ExternalResourceLoadStatus outcome;
            if (newPath != null)
            {
                var reference = ExternalResourceReference.CreateLocalResource(doc,
                    ExternalResourceTypes.BuiltInExternalResourceTypes.KeynoteTable,
                    ModelPathUtils.ConvertUserVisiblePathToModelPath(newPath), PathType.Absolute);
                if (!table.ServerSupports(reference))
                {
                    tx.RollBack();
                    return KeynoteSupport.Fail(request, $"Nothing was changed: Revit's keynote table cannot load '{newPath}'.", "transaction_failed");
                }
                outcome = table.LoadFrom(reference, results);
            }
            else outcome = table.Reload(results);
            status = outcome.ToString();
            revitMessages.AddRange(Messages(results));

            if (outcome != ExternalResourceLoadStatus.Success && outcome != ExternalResourceLoadStatus.ResourceAlreadyCurrent)
            {
                tx.RollBack();
                return new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Status = "transaction_failed",
                    Message = $"Nothing was changed: Revit could not load the keynote table ({status})." +
                              (revitMessages.Count > 0 ? " " + string.Join(" ", revitMessages.Take(3)) : string.Empty),
                    Errors = revitMessages.Count > 0 ? revitMessages : [status],
                    Warnings = warnings,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }
            TransactionCommitGuard.CommitOrThrow(tx);
        }
        catch (Exception ex)
        {
            return KeynoteSupport.Fail(request, $"Reloading the keynote table failed and nothing was changed: {ex.Message}", "transaction_failed");
        }

        var after = KeynoteSupport.Entries(KeynoteTable.GetKeynoteTable(doc));
        var change = KeynoteLogic.Compare(before, after);
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = status == ExternalResourceLoadStatus.ResourceAlreadyCurrent.ToString()
                ? $"The keynote table of '{doc.Title}' was already current: {after.Count} entries."
                : $"Keynote table reloaded: {before.Count} → {after.Count} entries ({change.Added.Count} added, {change.Removed.Count} removed, {change.Changed.Count} changed).",
            Data = new
            {
                table = Common(),
                loadStatus = status,
                source = KeynoteSupport.Source(KeynoteTable.GetKeynoteTable(doc)),
                entriesAfter = after.Count,
                change = KeynoteLogic.Describe(change),
                revitMessages
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static IEnumerable<string> Messages(KeyBasedTreeEntriesLoadResults results)
    {
        var list = new List<string>();
        try { list.AddRange(results.GetFileReadErrors().Select(e => $"read error: {e}")); } catch { /* none */ }
        try { list.AddRange(results.GetFileSyntaxErrors().Select(e => $"syntax error: {e}")); } catch { /* none */ }
        try
        {
            foreach (var error in results.GetKeyBasedTreeEntryErrors())
            {
                string? key = null;
                try { key = error.GetEntry()?.Key; } catch { /* no entry */ }
                list.Add($"{error.ErrorType}{(key != null ? $": '{key}'" : string.Empty)}");
            }
        }
        catch { /* none */ }
        try { list.AddRange(results.GetFailureMessages().Select(f => f.GetDescriptionText())); } catch { /* none */ }
        return list.Take(50);
    }
}
