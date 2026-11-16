namespace RevitMCP.Addin.LinkOperations;

/// <summary>What the live document says about a link, read on the Revit API thread (no Revit types here).</summary>
public sealed class LinkVerificationFacts
{
    /// <summary>False when the target document is not open, so nothing can be checked.</summary>
    public bool DocumentAvailable { get; set; } = true;
    public bool LinkTypeFound { get; set; }
    public long? LinkTypeId { get; set; }
    /// <summary>The path Revit reports for the link type.</summary>
    public string? LinkPath { get; set; }
    /// <summary>Revit's linked-file status for the link type, e.g. Loaded, Unloaded, NotFound.</summary>
    public string? LinkStatus { get; set; }
    public List<long> InstanceIds { get; set; } = new();
    /// <summary>Elements in the linked document; null when it could not be read.</summary>
    public int? LinkedElementCount { get; set; }
    public string Kind { get; set; } = LinkKinds.Ifc;
    public LinkCacheFacts? Cache { get; set; }
}

public sealed class LinkVerdict
{
    /// <summary>completed | failed | outcome_unknown.</summary>
    public string Outcome { get; set; } = LinkOperationStates.OutcomeUnknown;
    public bool Verified => Outcome == LinkOperationStates.Completed;
    public string Reason { get; set; } = string.Empty;
    public List<string> Checks { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

/// <summary>
/// Turns facts read from the live document into a verdict (#163): the link type exists, points at the requested file,
/// is loaded, has an instance and holds elements. A non-empty cache file alone proves nothing; an empty one is a
/// partial conversion. Pure.
/// </summary>
public static class LinkVerification
{
    public static LinkVerdict Decide(LinkVerificationFacts facts, string requestedSource)
    {
        var verdict = new LinkVerdict();

        if (!facts.DocumentAvailable)
        {
            verdict.Outcome = LinkOperationStates.OutcomeUnknown;
            verdict.Reason = "The target document is not open, so the link could not be checked. Open it and reconcile.";
            return verdict;
        }

        if (!facts.LinkTypeFound)
        {
            verdict.Outcome = LinkOperationStates.Failed;
            verdict.Reason = "No link to this file exists in the document.";
            verdict.Checks.Add("link type: missing");
            AddCacheNote(verdict, facts);
            return verdict;
        }
        verdict.Checks.Add($"link type: found (id {facts.LinkTypeId})");

        if (!LinkSourcePath.SameSource(facts.LinkPath, requestedSource))
        {
            verdict.Outcome = LinkOperationStates.Failed;
            verdict.Reason = $"The link points at '{facts.LinkPath}', not at the requested file.";
            verdict.Checks.Add("path: differs from the request");
            return verdict;
        }
        verdict.Checks.Add("path: matches the requested file");

        if (!string.Equals(facts.LinkStatus, "Loaded", StringComparison.OrdinalIgnoreCase))
        {
            verdict.Outcome = LinkOperationStates.Failed;
            verdict.Reason = $"The link exists but Revit reports it as '{facts.LinkStatus ?? "unknown"}', not Loaded.";
            verdict.Checks.Add($"status: {facts.LinkStatus ?? "unknown"}");
            return verdict;
        }
        verdict.Checks.Add("status: Loaded");

        if (facts.InstanceIds.Count == 0)
        {
            verdict.Outcome = LinkOperationStates.Failed;
            verdict.Reason = "The link type is loaded but has no instance in the model (partial result).";
            verdict.Checks.Add("instance: none");
            return verdict;
        }
        verdict.Checks.Add($"instances: {facts.InstanceIds.Count}");

        if (facts.LinkedElementCount is 0)
        {
            verdict.Outcome = LinkOperationStates.Failed;
            verdict.Reason = "The link is loaded but its document holds no elements (an empty conversion).";
            verdict.Checks.Add("content: no elements");
            return verdict;
        }
        verdict.Checks.Add(facts.LinkedElementCount is { } count
            ? $"content: {count} element(s)"
            : "content: element count not readable");
        if (facts.LinkedElementCount == null)
            verdict.Notes.Add("The linked document's element count could not be read, so its content was not checked.");

        if (facts.Kind == LinkKinds.Ifc && facts.Cache is { Exists: true, Bytes: 0 })
        {
            verdict.Outcome = LinkOperationStates.Failed;
            verdict.Reason = "The IFC cache file is empty (a partial conversion).";
            verdict.Checks.Add("cache: empty");
            return verdict;
        }
        AddCacheNote(verdict, facts);

        verdict.Outcome = LinkOperationStates.Completed;
        verdict.Reason = "Link found, loaded, pointing at the requested file, with an instance in the model.";
        return verdict;
    }

    private static void AddCacheNote(LinkVerdict verdict, LinkVerificationFacts facts)
    {
        if (facts.Kind != LinkKinds.Ifc || facts.Cache == null) return;
        verdict.Notes.Add(facts.Cache.Exists
            ? $"IFC cache {facts.Cache.Path}: {facts.Cache.Bytes} bytes (kept; never deleted by recovery)."
            : $"No IFC cache found at {facts.Cache.Path}.");
    }
}
