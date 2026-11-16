using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.LinkOperations;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Documents;

/// <summary>
/// The Revit side of IFC-first model linking (#163): plan context from the live document, the blocking conversion and
/// link, the live verification and the reconcile. Everything that touches the Revit API is here; the decisions
/// (state machine, admission, verdict) are in <c>LinkOperations\</c> and unit tested.
/// The run path never retries and never reacts to the caller's cancellation token once it entered the conversion:
/// the Revit call cannot be interrupted safely, so it is always followed by its verification.
/// </summary>
internal static class LinkModelsExecutor
{
    // ----- planning --------------------------------------------------------------------------------------------

    public static LinkPlanContext BuildContext(Document doc, LinkOperationService? service)
    {
        var policy = new FilePathPolicy();
        var existing = new List<ExistingLinkInfo>();
        try
        {
            foreach (var link in LinkInventory.Collect(doc))
            {
                var kind = link.Kind == LinkKind.Ifc ? LinkKinds.Ifc : link.Kind == LinkKind.Rvt ? LinkKinds.Rvt : null;
                if (kind == null) continue;
                existing.Add(new ExistingLinkInfo
                {
                    TypeId = link.TypeId,
                    Name = link.Name,
                    Path = link.Path,
                    Kind = kind,
                    Status = link.Status,
                    InstanceCount = link.Instances.Count
                });
            }
        }
        catch (Exception)
        {
            // An unreadable link list must not stop a preview; admission still guards duplicates.
        }

        return new LinkPlanContext
        {
            TargetKey = TargetKeyOf(doc),
            FileExists = File.Exists,
            ValidateRead = policy.ValidateRead,
            CacheOf = CacheFactsOf,
            ExistingLinks = existing,
            Operations = service?.List() ?? new List<LinkOperation>()
        };
    }

    public static string TargetKeyOf(Document doc) => LinkSourcePath.TargetKey(doc.PathName, doc.Title);

    public static string TargetSelectorOf(Document doc) => string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;

    public static LinkCacheFacts CacheFactsOf(string cachePath)
    {
        var facts = new LinkCacheFacts { Path = cachePath };
        try
        {
            var info = new FileInfo(cachePath);
            if (!info.Exists) return facts;
            facts.Exists = true;
            facts.Bytes = info.Length;
            facts.LastWriteUtc = info.LastWriteTimeUtc;
            var ifc = cachePath.EndsWith(LinkSourcePath.CacheSuffix, StringComparison.OrdinalIgnoreCase)
                ? cachePath.Substring(0, cachePath.Length - LinkSourcePath.CacheSuffix.Length)
                : cachePath;
            facts.Fresh = !File.Exists(ifc) || info.LastWriteTimeUtc >= File.GetLastWriteTimeUtc(ifc);
        }
        catch (Exception)
        {
            // Facts are informational.
        }
        return facts;
    }

    // ----- the run tool ----------------------------------------------------------------------------------------

    public static McpToolResult Run(UIApplication uiapp, McpToolRequest request)
    {
        var service = LinkOperationService.Current;
        if (service == null)
            return DocumentToolSupport.Fail(request, "The link operation service is not available in this Revit session.", "not_available");

        var id = ToolArguments.GetString(request.Arguments, "operationId");
        var token = ToolArguments.GetString(request.Arguments, "dispatchToken");
        var op = service.ClaimForRun(id, token);
        if (op == null)
            return DocumentToolSupport.Fail(request,
                $"Operation '{id}' is not waiting to run (wrong state or token, or it was cancelled). Nothing was done.", "validation_failed");

        var target = RevitDocumentResolver.Resolve(uiapp, op.TargetSelector, out var documentError);
        if (target == null)
        {
            service.Fail(id, "The target document is not open any more: " + documentError);
            return DocumentToolSupport.Fail(request, "The target document is not open: " + documentError, "validation_failed");
        }
        var doc = target.Document;

        var blocked = DocumentToolSupport.CheckNoOpenTransaction(doc);
        if (blocked != null)
        {
            service.Fail(id, "Not started: " + blocked);
            return DocumentToolSupport.Fail(request, blocked, "transaction_failed");
        }
        if (!File.Exists(op.SourcePath))
        {
            service.Fail(id, "Not started: the file no longer exists: " + op.SourcePath);
            return DocumentToolSupport.Fail(request, "File not found: " + op.SourcePath, "validation_failed");
        }

        var existing = FindLink(doc, op);
        if (op.Type == LinkOperationTypes.Link && existing != null)
        {
            // Someone linked it after the preview. Nothing to convert: verify what is there.
            service.BeginConversion(id, "a link to this file already exists; checking it");
            service.BeginVerification(id);
            var already = Verify(doc, op, existing);
            var finished = service.Finish(id, already.Verdict, r => Fill(r, already.Facts), null);
            return ResultOf(request, finished, "A link to this file already existed; it was verified, not created again.");
        }

        var phase = op.Type == LinkOperationTypes.Refresh
            ? "reloading the existing link"
            : op.Kind == LinkKinds.Ifc ? "converting the IFC to a Revit cache (Revit does not answer meanwhile)" : "loading the RVT link";
        // Persist BEFORE the blocking call: from here on a lost answer is outcome_unknown, never a retry.
        if (!service.BeginConversion(id, phase))
            return DocumentToolSupport.Fail(request, "The operation could not move to converting; nothing was done.", "validation_failed");

        var notes = new List<string>();
        string? error = null;
        try
        {
            if (op.Type == LinkOperationTypes.Refresh) Refresh(uiapp, doc, op, notes);
            else if (op.Kind == LinkKinds.Ifc) LinkIfc(uiapp, doc, op, service, notes);
            else LinkRvt(doc, op, service, notes);
        }
        catch (Exception ex)
        {
            // Not rethrown and not a retry: the verification below reads what the document really holds.
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        service.BeginVerification(id);
        var verification = Verify(doc, op, FindLink(doc, op));
        var verdict = verification.Verdict;
        if (error != null)
        {
            verdict.Notes.Add("Revit reported: " + error);
            if (verdict.Outcome == LinkOperationStates.Failed && verdict.Reason.StartsWith("No link", StringComparison.Ordinal))
                verdict.Reason = "Revit did not create the link: " + error;
        }
        verdict.Notes.AddRange(notes);
        var done = service.Finish(id, verdict, r => Fill(r, verification.Facts));
        return ResultOf(request, done, null);
    }

    private static McpToolResult ResultOf(McpToolRequest request, LinkOperation? op, string? note)
    {
        if (op == null)
            return DocumentToolSupport.Fail(request, "The operation record could not be updated.", "tool_execution_failed");
        var success = op.State == LinkOperationStates.Completed;
        var view = LinkOperationStatusView.Describe(op, DateTimeOffset.Now, new LinkOperationLiveness { EffectiveState = op.State }, null);
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = success,
            Status = success ? null : "operation_" + op.State,
            Message = (note == null ? string.Empty : note + " ") + $"Operation {op.Id}: {op.State}. {op.Message}",
            Data = view
        };
    }

    private static LinkEntry? FindLink(Document doc, LinkOperation op)
    {
        try
        {
            return LinkInventory.Collect(doc).FirstOrDefault(l =>
                (op.ExistingLinkTypeId.HasValue && l.TypeId == op.ExistingLinkTypeId.Value) ||
                ((l.Kind == LinkKind.Ifc && op.Kind == LinkKinds.Ifc || l.Kind == LinkKind.Rvt && op.Kind == LinkKinds.Rvt) &&
                 LinkSourcePath.SameSource(l.Path, op.SourcePath)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ----- the conversions --------------------------------------------------------------------------------------

    private static void LinkIfc(UIApplication uiapp, Document doc, LinkOperation op, LinkOperationService service, List<string> notes)
    {
        var ifc = Path.GetFullPath(op.SourcePath);
        var cache = LinkSourcePath.CachePathOf(ifc);
        var before = CacheFactsOf(cache);

        // The long part: Revit opens the IFC, converts it and saves <ifc>.RVT. No cancellation token is read from here on.
        var scratch = new LinkReloadOutcome();
        LinkReloadService.EnsureIfcCache(uiapp.Application, ifc, cache, op.RegenerateCache, scratch);
        notes.AddRange(scratch.Notes);
        if (before.Exists && !op.RegenerateCache && scratch.Notes.Any(n => n.StartsWith("Reused", StringComparison.Ordinal)))
            notes.Add("The existing cache was newer than the IFC and was reused.");

        service.SetPhase(op.Id, "linking the converted IFC into the document");
        LinkLoadResult? created = null;
        LinkReloadService.RunLinkStep(doc, "Revit MCP - Link IFC", () =>
            created = RevitLinkType.CreateFromIFC(doc, ifc, cache, false, new RevitLinkOptions(false)));
        PlaceInstance(doc, op, created, "IFC", notes);
    }

    private static void LinkRvt(Document doc, LinkOperation op, LinkOperationService service, List<string> notes)
    {
        service.SetPhase(op.Id, "loading the RVT link");
        var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(op.SourcePath);
        LinkLoadResult? created = null;
        LinkReloadService.RunLinkStep(doc, "Revit MCP - Link RVT", () =>
            created = RevitLinkType.Create(doc, modelPath, new RevitLinkOptions(false)));
        PlaceInstance(doc, op, created, "RVT", notes);
    }

    private static void PlaceInstance(Document doc, LinkOperation op, LinkLoadResult? created, string label, List<string> notes)
    {
        if (created == null || created.ElementId == ElementId.InvalidElementId)
            throw new InvalidOperationException($"Revit did not create the {label} link type ({created?.LoadResult.ToString() ?? "no result"}).");
        if (created.LoadResult is LinkLoadResultType.UsedExisting or LinkLoadResultType.LinkExists)
            throw new InvalidOperationException($"Revit reported {created.LoadResult}: a link to this file already exists, so none was created.");

        var typeId = created.ElementId;
        var placement = op.Positioning == "shared" ? LinkReloadService.PlacementShared : LinkReloadService.PlacementOrigin;
        var transaction = RevitTransactionRunner.Run(doc, "Revit MCP - Place " + label + " link", () =>
        {
            var instance = LinkReloadService.CreateInstance(doc, typeId, placement);
            if (op.Pin) instance.Pinned = true;
        });
        if (!transaction.Success)
        {
            // The type exists without an instance (a partial result): remove what this operation created, nothing else.
            var cleanup = RevitTransactionRunner.Run(doc, "Revit MCP - Undo " + label + " link", () => doc.Delete(typeId));
            notes.Add(cleanup.Success
                ? "The new link type was removed again because it could not be placed."
                : "The new link type could not be removed after the placement failed; check the model.");
            throw new InvalidOperationException(
                $"The {label} link could not be placed: {transaction.Diagnostics.OriginalError ?? "unknown transaction failure"}");
        }
        notes.Add($"Placed {(placement == LinkReloadService.PlacementShared ? "at shared coordinates" : "origin to origin")}{(op.Pin ? ", pinned" : string.Empty)}.");
    }

    private static void Refresh(UIApplication uiapp, Document doc, LinkOperation op, List<string> notes)
    {
        var link = FindLink(doc, op) ?? throw new InvalidOperationException("The link to refresh no longer exists in the document.");
        var outcome = LinkReloadService.Reload(uiapp.Application, doc, link, op.SourcePath,
            LinkReloadService.PlacementMatchExisting, LinkReloadService.IfcMethodAuto, op.RegenerateCache, CancellationToken.None);
        notes.AddRange(outcome.Notes);
        if (!outcome.Success)
            throw new InvalidOperationException(outcome.Error ?? $"The reload ended with '{outcome.Status}'.");
    }

    // ----- verification -----------------------------------------------------------------------------------------

    internal sealed class Verification
    {
        public LinkVerificationFacts Facts { get; set; } = new();
        public LinkVerdict Verdict { get; set; } = new();
    }

    public static Verification Verify(Document? doc, LinkOperation op, LinkEntry? link)
    {
        var facts = Collect(doc, op, link);
        return new Verification { Facts = facts, Verdict = LinkVerification.Decide(facts, op.SourcePath) };
    }

    public static LinkVerificationFacts Collect(Document? doc, LinkOperation op, LinkEntry? link)
    {
        var facts = new LinkVerificationFacts
        {
            DocumentAvailable = doc != null,
            Kind = op.Kind,
            Cache = op.Kind == LinkKinds.Ifc ? CacheFactsOf(LinkSourcePath.CachePathOf(op.SourcePath)) : null
        };
        if (doc == null || link == null) return facts;

        facts.LinkTypeFound = true;
        facts.LinkTypeId = link.TypeId;
        facts.LinkPath = link.Path;
        facts.LinkStatus = link.Status;
        facts.InstanceIds = link.Instances.Select(i => i.Id.Value).ToList();
        try
        {
            if (link.Instances.OfType<RevitLinkInstance>().FirstOrDefault()?.GetLinkDocument() is { } linked)
                facts.LinkedElementCount = new FilteredElementCollector(linked).WhereElementIsNotElementType().GetElementCount();
        }
        catch (Exception)
        {
            facts.LinkedElementCount = null;
        }
        return facts;
    }

    private static void Fill(LinkOperationResult result, LinkVerificationFacts facts)
    {
        result.LinkTypeId = facts.LinkTypeId;
        result.InstanceIds = facts.InstanceIds;
        result.LinkPath = facts.LinkPath;
        result.LinkStatus = facts.LinkStatus;
        result.LinkedElementCount = facts.LinkedElementCount;
        result.Cache = facts.Cache;
    }

    /// <summary>
    /// Settles the operation behind a revit_reload_links_from pair (#163): verification reads the live document, so a reload
    /// that reported success but left the link missing or not loaded is recorded as failed, and one that threw while
    /// the link is in fact fine is recorded as completed. Never retries.
    /// </summary>
    public static void FinishReload(LinkOperationService service, Document doc, string operationId, LinkReloadOutcome? outcome, string? exceptionMessage)
    {
        var op = service.Store.Get(operationId);
        if (op == null) return;
        service.BeginVerification(operationId);
        LinkEntry? link = null;
        try
        {
            link = LinkInventory.Collect(doc).FirstOrDefault(l =>
                (outcome != null && outcome.NewTypeId != 0 && l.TypeId == outcome.NewTypeId) ||
                (op.ExistingLinkTypeId.HasValue && l.TypeId == op.ExistingLinkTypeId.Value) ||
                LinkSourcePath.SameSource(l.Path, op.SourcePath));
        }
        catch (Exception)
        {
            // The verdict below says the document could not be read.
        }
        var verification = Verify(doc, op, link);
        var error = exceptionMessage ?? (outcome is { Success: false } ? outcome.Error ?? outcome.Status : null);
        if (error != null) verification.Verdict.Notes.Add("The reload reported: " + error);
        if (outcome != null) verification.Verdict.Notes.AddRange(outcome.Notes);
        service.Finish(operationId, verification.Verdict, r => Fill(r, verification.Facts));
    }

    // ----- reconcile --------------------------------------------------------------------------------------------

    public static McpToolResult Reconcile(UIApplication uiapp, McpToolRequest request)
    {
        var service = LinkOperationService.Current;
        if (service == null)
            return DocumentToolSupport.Fail(request, "The link operation service is not available in this Revit session.", "not_available");
        var id = ToolArguments.GetString(request.Arguments, "operationId");
        service.RecoverOrphans();
        var op = service.Store.Get(id);
        if (op == null)
            return DocumentToolSupport.Fail(request, $"No link operation '{id}'.", "not_found");
        if (op.State != LinkOperationStates.OutcomeUnknown)
        {
            var settled = LinkOperationStates.IsTerminal(op.State);
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = settled,
                Status = settled ? null : "operation_in_progress",
                Message = settled
                    ? $"Operation {id} already ended ({op.State}); nothing to reconcile."
                    : $"Operation {id} is {op.State}; it is still being handled. Only an outcome_unknown operation is reconciled.",
                Data = LinkOperationStatusView.Describe(op, DateTimeOffset.Now, new LinkOperationLiveness { EffectiveState = op.State }, null)
            };
        }

        // This call is running on the Revit API thread, so no conversion of this session is running now.
        var target = RevitDocumentResolver.Resolve(uiapp, op.TargetSelector, out _);
        var link = target == null ? null : FindLink(target.Document, op);
        var verification = Verify(target?.Document, op, link);
        var updated = service.Finish(id, verification.Verdict, r => Fill(r, verification.Facts), "reconcile");
        if (updated == null)
            return DocumentToolSupport.Fail(request, "The operation record could not be updated.", "tool_execution_failed");

        var warnings = new List<string>();
        if (updated.State == LinkOperationStates.OutcomeUnknown)
            warnings.Add("The outcome still cannot be established: ask the user to check the model. Nothing was started again.");
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Operation {id} reconciled: {updated.State}. {updated.Message}",
            Warnings = warnings,
            Data = LinkOperationStatusView.Describe(updated, DateTimeOffset.Now, new LinkOperationLiveness { EffectiveState = updated.State }, null)
        };
    }
}
