using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;
using RevitMCP.Addin.Transactions;

namespace RevitMCP.Addin.Documents;

/// <summary>Outcome of reloading one link.</summary>
public sealed class LinkReloadOutcome
{
    public string Link { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public long OldTypeId { get; set; }
    public long NewTypeId { get; set; }
    public string OldPath { get; set; } = string.Empty;
    public string NewPath { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Error { get; set; }
    public List<long> NewInstanceIds { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

/// <summary>
/// Reloads links from new files (#90). DWG links reload in place (<c>CADLinkType.LoadFrom</c>) and keep their
/// instances and graphic overrides; RVT links reload in place (<c>RevitLinkType.LoadFrom</c>). An IFC link is first
/// updated in place (<c>RevitLinkType.UpdateFromIFC</c> re-points the link type at the new IFC and its .ifc.RVT
/// cache). When that does not work it is replaced: the new IFC is converted to its .ifc.RVT, linked as a new link
/// type, its instances are placed like the old ones (position, rotation, workset, pin, per-view hidden state and
/// graphic overrides), and the old link type is deleted.
/// Link loads must not run inside an API transaction; each step that needs one opens its own.
/// </summary>
public static class LinkReloadService
{
    public const string PlacementMatchExisting = "matchExisting";
    public const string PlacementOrigin = "origin";
    public const string PlacementShared = "shared";

    /// <summary>IFC: try the in-place update, fall back to replacing the link.</summary>
    public const string IfcMethodAuto = "auto";
    /// <summary>IFC: only the in-place update (RevitLinkType.UpdateFromIFC).</summary>
    public const string IfcMethodUpdate = "update";
    /// <summary>IFC: link the new file as a new link and delete the old one.</summary>
    public const string IfcMethodReplace = "replace";

    public static LinkReloadOutcome Reload(
        Application app,
        Document doc,
        LinkEntry link,
        string newPath,
        string placement,
        string ifcMethod,
        bool regenerateIfcCache,
        CancellationToken cancellationToken)
    {
        var outcome = new LinkReloadOutcome
        {
            Link = link.Name,
            Kind = link.Kind.ToString().ToUpperInvariant(),
            OldTypeId = link.TypeId,
            NewTypeId = link.TypeId,
            OldPath = link.Path,
            NewPath = newPath
        };

        try
        {
            if (!File.Exists(newPath))
                return Fail(outcome, "file_not_found", $"File not found: {newPath}");

            cancellationToken.ThrowIfCancellationRequested();
            switch (link.Kind)
            {
                case LinkKind.Dwg:
                    ReloadCad(doc, (CADLinkType)link.Type, newPath, outcome);
                    break;
                case LinkKind.Rvt:
                    ReloadRvt(doc, (RevitLinkType)link.Type, newPath, outcome);
                    break;
                case LinkKind.Ifc:
                    ReloadIfc(app, doc, link, newPath, placement, ifcMethod, regenerateIfcCache, outcome, cancellationToken);
                    break;
                default:
                    return Fail(outcome, "unsupported", $"Only DWG, IFC and RVT links can be reloaded ('{link.Name}' is {link.Kind}).");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Fail(outcome, "failed", $"{ex.GetType().Name}: {ex.Message}");
        }

        return outcome;
    }

    private static void ReloadCad(Document doc, CADLinkType type, string newPath, LinkReloadOutcome outcome)
    {
        outcome.Method = "CADLinkType.LoadFrom";
        LinkLoadResult? result = null;
        RunLinkStep(doc, "Revit MCP - Reload DWG link", () => result = type.LoadFrom(newPath));
        ApplyLoadResult(outcome, result);
        outcome.NewPath = LinkInventory.GetPath(type);
    }

    private static void ReloadRvt(Document doc, RevitLinkType type, string newPath, LinkReloadOutcome outcome)
    {
        outcome.Method = "RevitLinkType.LoadFrom";
        var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(newPath);
        LinkLoadResult? result = null;
        RunLinkStep(doc, "Revit MCP - Reload RVT link", () => result = type.LoadFrom(modelPath, new WorksetConfiguration()));
        ApplyLoadResult(outcome, result);
        outcome.NewPath = LinkInventory.GetPath(type);
    }

    private static void ReloadIfc(
        Application app,
        Document doc,
        LinkEntry link,
        string ifcPath,
        string placement,
        string ifcMethod,
        bool regenerateCache,
        LinkReloadOutcome outcome,
        CancellationToken cancellationToken)
    {
        var fullIfcPath = Path.GetFullPath(ifcPath);
        var cachePath = fullIfcPath + ".RVT";

        // In place first: the existing link type is re-pointed at the new IFC and keeps its ids and overrides.
        if (ifcMethod != IfcMethodReplace)
        {
            var recreate = regenerateCache || !IsCacheFresh(fullIfcPath, cachePath);
            string? updateError = null;
            var updated = false;
            try
            {
                RunLinkStep(doc, "Revit MCP - Update IFC link", () =>
                    updated = ((RevitLinkType)link.Type).UpdateFromIFC(doc, fullIfcPath, cachePath, recreate));
                if (!updated) updateError = "RevitLinkType.UpdateFromIFC returned false.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                updateError = $"{ex.GetType().Name}: {ex.Message}";
            }

            var newPath = LinkInventory.GetPath(link.Type);
            if (updated && IsSameIfc(newPath, fullIfcPath))
            {
                outcome.Method = "RevitLinkType.UpdateFromIFC";
                outcome.Success = true;
                outcome.Status = "LinkLoaded";
                outcome.NewPath = newPath;
                if (!recreate) outcome.Notes.Add($"Reused the existing IFC cache {cachePath} (newer than the IFC).");
                return;
            }

            if (updated)
                updateError = $"UpdateFromIFC succeeded but the link still points at '{newPath}'.";
            if (ifcMethod == IfcMethodUpdate)
            {
                outcome.Method = "RevitLinkType.UpdateFromIFC";
                Fail(outcome, "update_failed", updateError ?? "The IFC link was not updated.");
                return;
            }
            outcome.Notes.Add($"In-place update did not work ({updateError}); replaced the link instead.");
        }

        outcome.Method = "replace: CreateFromIFC + delete old link";
        EnsureIfcCache(app, fullIfcPath, cachePath, regenerateCache, outcome);
        cancellationToken.ThrowIfCancellationRequested();

        LinkLoadResult? created = null;
        RunLinkStep(doc, "Revit MCP - Link IFC", () =>
            created = RevitLinkType.CreateFromIFC(doc, fullIfcPath, cachePath, false, new RevitLinkOptions(false)));
        if (created == null || created.ElementId == ElementId.InvalidElementId)
        {
            Fail(outcome, created?.LoadResult.ToString() ?? "create_failed", "Revit did not create the new IFC link type.");
            return;
        }

        var newTypeId = created.ElementId;
        outcome.NewTypeId = newTypeId.Value;
        var oldInstances = link.Instances.OfType<RevitLinkInstance>().ToList();
        var newInstanceIds = new List<long>();
        var notes = new List<string>();

        var transaction = RevitTransactionRunner.Run(doc, "Revit MCP - Replace IFC link", () =>
        {
            if (oldInstances.Count == 0)
            {
                var instance = CreateInstance(doc, newTypeId, placement == PlacementShared ? PlacementShared : PlacementOrigin);
                newInstanceIds.Add(instance.Id.Value);
            }

            foreach (var old in oldInstances)
            {
                var instance = CreateInstance(doc, newTypeId, placement == PlacementShared ? PlacementShared : PlacementOrigin);
                if (placement == PlacementMatchExisting)
                    MatchPlacement(doc, old, instance);
                CopyInstanceState(doc, old, instance, notes);
                newInstanceIds.Add(instance.Id.Value);
            }

            doc.Delete(link.Type.Id);
        });

        if (!transaction.Success)
        {
            // The new link type exists without instances; remove it so the document is as before.
            RevitTransactionRunner.Run(doc, "Revit MCP - Undo IFC link", () => doc.Delete(newTypeId));
            Fail(outcome, "replace_failed",
                $"Replacing the link failed and was rolled back: {transaction.Diagnostics.OriginalError ?? "unknown transaction failure"}");
            return;
        }

        outcome.Success = true;
        outcome.Status = created.LoadResult.ToString();
        outcome.NewInstanceIds = newInstanceIds;
        outcome.NewPath = fullIfcPath;
        outcome.Notes.AddRange(notes);
        outcome.Notes.Add("The IFC link was replaced: element ids of the link type and instances changed.");
    }

    private static void EnsureIfcCache(Application app, string ifcPath, string cachePath, bool regenerate, LinkReloadOutcome outcome)
    {
        if (!regenerate && IsCacheFresh(ifcPath, cachePath))
        {
            outcome.Notes.Add($"Reused the existing IFC cache {cachePath} (newer than the IFC).");
            return;
        }

        var options = new IFCImportOptions { Intent = IFCImportIntent.Reference, Action = IFCImportAction.Open };
        var ifcDoc = app.OpenIFCDocument(ifcPath, options)
                     ?? throw new InvalidOperationException($"Revit could not open the IFC file {ifcPath}.");
        try
        {
            ifcDoc.SaveAs(cachePath, new SaveAsOptions { OverwriteExistingFile = true });
        }
        finally
        {
            ifcDoc.Close(false);
        }
        outcome.Notes.Add($"Converted the IFC to {cachePath}.");
    }

    private static bool IsCacheFresh(string ifcPath, string cachePath) =>
        File.Exists(cachePath) && File.GetLastWriteTimeUtc(cachePath) >= File.GetLastWriteTimeUtc(ifcPath);

    private static RevitLinkInstance CreateInstance(Document doc, ElementId typeId, string placement) =>
        placement == PlacementShared
            ? RevitLinkInstance.Create(doc, typeId, ImportPlacement.Shared)
            : RevitLinkInstance.Create(doc, typeId, ImportPlacement.Origin);

    /// <summary>Moves an origin-placed instance onto the old instance's transform (rotation about Z, then translation).</summary>
    private static void MatchPlacement(Document doc, RevitLinkInstance old, RevitLinkInstance created)
    {
        var target = old.GetTotalTransform();
        var current = created.GetTotalTransform();

        var targetAngle = Math.Atan2(target.BasisX.Y, target.BasisX.X);
        var currentAngle = Math.Atan2(current.BasisX.Y, current.BasisX.X);
        var rotation = targetAngle - currentAngle;
        if (Math.Abs(rotation) > 1e-9)
        {
            var axis = Line.CreateBound(current.Origin, current.Origin + XYZ.BasisZ);
            ElementTransformUtils.RotateElement(doc, created.Id, axis, rotation);
        }

        var moved = created.GetTotalTransform();
        var translation = target.Origin - moved.Origin;
        if (!translation.IsZeroLength())
            ElementTransformUtils.MoveElement(doc, created.Id, translation);
    }

    private static void CopyInstanceState(Document doc, RevitLinkInstance old, RevitLinkInstance created, List<string> notes)
    {
        try
        {
            if (doc.IsWorkshared)
            {
                var oldWorkset = old.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                var newWorkset = created.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (oldWorkset != null && newWorkset != null && !newWorkset.IsReadOnly)
                    newWorkset.Set(oldWorkset.AsInteger());
            }
        }
        catch (Exception ex)
        {
            notes.Add($"Workset not copied: {ex.Message}");
        }

        var hiddenIn = 0;
        var overridden = 0;
        foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
        {
            if (view.IsTemplate) continue;
            try
            {
                if (old.IsHidden(view) && created.CanBeHidden(view))
                {
                    view.HideElements(new List<ElementId> { created.Id });
                    hiddenIn++;
                }

                var overrides = view.GetElementOverrides(old.Id);
                if (overrides != null && !IsEmpty(overrides))
                {
                    view.SetElementOverrides(created.Id, overrides);
                    overridden++;
                }
            }
            catch
            {
                // Views that cannot host the link (schedules, sheets, ...) are skipped.
            }
        }

        if (hiddenIn > 0) notes.Add($"Hidden in {hiddenIn} view(s) like the old link.");
        if (overridden > 0) notes.Add($"Copied graphic overrides in {overridden} view(s).");

        try
        {
            if (old.Pinned) created.Pinned = true;
        }
        catch
        {
            // Pinning is cosmetic.
        }
    }

    private static bool IsEmpty(OverrideGraphicSettings settings)
    {
        try
        {
            var clean = new OverrideGraphicSettings();
            return settings.Halftone == clean.Halftone &&
                   settings.Transparency == clean.Transparency &&
                   settings.DetailLevel == clean.DetailLevel &&
                   !settings.ProjectionLineColor.IsValid &&
                   !settings.CutLineColor.IsValid &&
                   settings.ProjectionLineWeight == clean.ProjectionLineWeight &&
                   settings.CutLineWeight == clean.CutLineWeight &&
                   settings.ProjectionLinePatternId == clean.ProjectionLinePatternId &&
                   settings.SurfaceForegroundPatternId == clean.SurfaceForegroundPatternId &&
                   settings.SurfaceBackgroundPatternId == clean.SurfaceBackgroundPatternId &&
                   settings.CutForegroundPatternId == clean.CutForegroundPatternId &&
                   settings.CutBackgroundPatternId == clean.CutBackgroundPatternId;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSameIfc(string existingPath, string ifcPath)
    {
        if (existingPath.Length == 0) return false;
        var existing = DocumentTargetMatcher.NormalizePath(existingPath);
        var wanted = DocumentTargetMatcher.NormalizePath(ifcPath);
        return existing == wanted || existing == wanted + ".RVT";
    }

    /// <summary>
    /// Runs a link operation outside a transaction (link loads require that); when Revit reports that the
    /// operation needs an open transaction, runs it again inside one.
    /// </summary>
    private static void RunLinkStep(Document doc, string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ModificationOutsideTransactionException
                                       or Autodesk.Revit.Exceptions.InternalException)
        {
            // Some link loads (seen with CADLinkType.LoadFrom) fail with an internal error outside a transaction.
            var result = RevitTransactionRunner.Run(doc, name, action);
            if (!result.Success)
                throw new InvalidOperationException(
                    $"{ex.GetType().Name} outside a transaction ({ex.Message}); inside one: " +
                    (result.Diagnostics.OriginalError ?? $"{name} failed."));
        }
    }

    private static void ApplyLoadResult(LinkReloadOutcome outcome, LinkLoadResult? result)
    {
        var status = result?.LoadResult ?? LinkLoadResultType.LinkNotLoadedOtherError;
        outcome.Status = status.ToString();
        outcome.Success = status == LinkLoadResultType.LinkLoaded;
        if (!outcome.Success)
            outcome.Error = status is LinkLoadResultType.UsedExisting or LinkLoadResultType.LinkExists
                ? $"Revit reported {status}: another link in this document already uses that file, so nothing was reloaded."
                : $"Revit reported {status} while loading the link.";
    }

    private static LinkReloadOutcome Fail(LinkReloadOutcome outcome, string status, string error)
    {
        outcome.Success = false;
        outcome.Status = status;
        outcome.Error = error;
        return outcome;
    }
}
