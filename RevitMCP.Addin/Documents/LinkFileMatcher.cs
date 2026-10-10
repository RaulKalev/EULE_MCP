namespace RevitMCP.Addin.Documents;

public enum LinkKind
{
    Dwg,
    Ifc,
    Rvt,
    OtherCad
}

/// <summary>An existing link in a project document (no Revit types, unit-testable).</summary>
public sealed class LinkCandidate
{
    public long TypeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public LinkKind Kind { get; set; }
    public string Path { get; set; } = string.Empty;
}

/// <summary>One "reload this link from that file" request as given in chat.</summary>
public sealed class LinkReloadRequest
{
    /// <summary>Existing link name or type id; optional — omitted means "match by file type and name".</summary>
    public string? Link { get; set; }
    public string Path { get; set; } = string.Empty;
}

public sealed class LinkReloadPair
{
    public const string Matched = "matched";
    public const string Ambiguous = "ambiguous";
    public const string Unmatched = "unmatched";
    public const string Invalid = "invalid";

    public int RequestIndex { get; set; }
    public string Path { get; set; } = string.Empty;
    public LinkKind? Kind { get; set; }
    public string Status { get; set; } = Unmatched;
    public string Reason { get; set; } = string.Empty;

    /// <summary>The matched link, or for an ambiguous request the proposed one.</summary>
    public LinkCandidate? Link { get; set; }
    public List<LinkCandidate> Alternatives { get; set; } = new();
}

/// <summary>
/// Pairs chat-provided files with existing links for revit_reload_links_from (#90). Pure.
/// A request naming its link is matched by name or type id. Requests without a link are matched among
/// the links of the same file type: one link and one file pair directly; otherwise the file name has to
/// point clearly at one link. Anything less certain comes back as <see cref="LinkReloadPair.Ambiguous"/>
/// with a proposed pairing, so the agent asks before reloading.
/// </summary>
public static class LinkFileMatcher
{
    private const double MinimumScore = 0.5;
    private const double MinimumMargin = 0.2;

    public static LinkKind? KindFromFilePath(string path)
    {
        var p = path.Trim().Trim('"');
        if (p.EndsWith(".ifc.rvt", StringComparison.OrdinalIgnoreCase)) return null;
        if (p.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) return LinkKind.Dwg;
        if (p.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase)) return LinkKind.Ifc;
        if (p.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) return LinkKind.Rvt;
        return null;
    }

    /// <summary>Kind of an existing Revit (RVT/IFC) link type from its name and stored path.</summary>
    public static LinkKind ClassifyRevitLink(string name, string path)
    {
        if (path.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".ifc.rvt", StringComparison.OrdinalIgnoreCase) ||
            name.IndexOf(".ifc", StringComparison.OrdinalIgnoreCase) >= 0)
            return LinkKind.Ifc;
        return LinkKind.Rvt;
    }

    /// <summary>Kind of an existing CAD link type from its stored path or name.</summary>
    public static LinkKind ClassifyCadLink(string name, string path)
    {
        var source = path.Length > 0 ? path : name;
        return source.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) ? LinkKind.Dwg : LinkKind.OtherCad;
    }

    public static List<LinkReloadPair> Match(IReadOnlyList<LinkCandidate> links, IReadOnlyList<LinkReloadRequest> requests)
    {
        var pairs = new LinkReloadPair[requests.Count];
        var claimed = new HashSet<long>();

        // 1. Requests that name their link.
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            var kind = KindFromFilePath(request.Path);
            if (kind == null)
            {
                pairs[i] = Invalid(i, request, "Unsupported file type. Give a .dwg, .ifc or .rvt file (for IFC the .ifc itself, not the .ifc.RVT cache).");
                continue;
            }

            if (string.IsNullOrWhiteSpace(request.Link)) continue;

            var ofKind = links.Where(l => l.Kind == kind).ToList();
            var named = FindByName(ofKind, request.Link!.Trim());
            var pair = new LinkReloadPair { RequestIndex = i, Path = request.Path, Kind = kind };
            if (named.Count == 1)
            {
                pair.Status = LinkReloadPair.Matched;
                pair.Link = named[0];
                pair.Reason = "named in the request";
                claimed.Add(named[0].TypeId);
            }
            else if (named.Count == 0)
            {
                pair.Status = LinkReloadPair.Unmatched;
                pair.Reason = $"No {Label(kind)} link matches '{request.Link}'. {Describe(ofKind, kind.Value)}";
                pair.Alternatives = ofKind;
            }
            else
            {
                pair.Status = LinkReloadPair.Ambiguous;
                pair.Reason = $"'{request.Link}' matches {named.Count} {Label(kind)} links; name one exactly or pass its type id.";
                pair.Alternatives = named;
            }
            pairs[i] = pair;
        }

        // 2. Requests without a link, per file type.
        var open = Enumerable.Range(0, requests.Count).Where(i => pairs[i] == null).ToList();
        foreach (var group in open.GroupBy(i => KindFromFilePath(requests[i].Path)!.Value))
        {
            var kind = group.Key;
            var indexes = group.ToList();
            var candidates = links.Where(l => l.Kind == kind && !claimed.Contains(l.TypeId)).ToList();

            if (candidates.Count == 0)
            {
                foreach (var i in indexes)
                    pairs[i] = new LinkReloadPair
                    {
                        RequestIndex = i, Path = requests[i].Path, Kind = kind, Status = LinkReloadPair.Unmatched,
                        Reason = $"The document has no (unclaimed) {Label(kind)} link to reload from this file."
                    };
                continue;
            }

            if (indexes.Count == 1 && candidates.Count == 1)
            {
                var only = indexes[0];
                pairs[only] = new LinkReloadPair
                {
                    RequestIndex = only, Path = requests[only].Path, Kind = kind, Status = LinkReloadPair.Matched,
                    Link = candidates[0], Reason = $"the only {Label(kind)} link"
                };
                claimed.Add(candidates[0].TypeId);
                continue;
            }

            MatchByName(requests, indexes, candidates, kind, pairs, claimed);
        }

        return pairs.ToList();
    }

    private static void MatchByName(
        IReadOnlyList<LinkReloadRequest> requests,
        List<int> indexes,
        List<LinkCandidate> candidates,
        LinkKind kind,
        LinkReloadPair[] pairs,
        HashSet<long> claimed)
    {
        var scores = indexes.ToDictionary(
            i => i,
            i => candidates.Select(c => (Link: c, Score: Similarity(requests[i].Path, c))).OrderByDescending(x => x.Score).ToList());

        // A request is clear when its best link scores well, beats the runner-up by a margin,
        // and no other request has the same link as its best.
        var bestOf = indexes.ToDictionary(i => i, i => scores[i][0]);
        var clear = new HashSet<int>(indexes.Where(i =>
        {
            var ranked = scores[i];
            var best = ranked[0];
            var margin = ranked.Count > 1 ? best.Score - ranked[1].Score : 1.0;
            var contested = indexes.Any(j => j != i && bestOf[j].Link.TypeId == best.Link.TypeId);
            return best.Score >= MinimumScore && margin >= MinimumMargin && !contested;
        }));

        // When every request is clear, pair them; otherwise propose a greedy pairing for confirmation.
        var allClear = clear.Count == indexes.Count && indexes.Count <= candidates.Count;
        var taken = new HashSet<long>(claimed);
        foreach (var i in indexes.OrderByDescending(i => bestOf[i].Score))
        {
            var proposal = scores[i].FirstOrDefault(x => !taken.Contains(x.Link.TypeId));
            var pair = new LinkReloadPair { RequestIndex = i, Path = requests[i].Path, Kind = kind };
            if (proposal.Link == null)
            {
                pair.Status = LinkReloadPair.Unmatched;
                pair.Reason = $"More {Label(kind)} files than {Label(kind)} links; no link left for this file.";
                pair.Alternatives = candidates;
            }
            else if (allClear)
            {
                pair.Status = LinkReloadPair.Matched;
                pair.Link = proposal.Link;
                pair.Reason = "matched by file name";
                taken.Add(proposal.Link.TypeId);
            }
            else
            {
                pair.Status = LinkReloadPair.Ambiguous;
                pair.Link = proposal.Link;
                pair.Reason = $"Several {Label(kind)} links could match; proposed '{proposal.Link.Name}'. Confirm by passing link for each file.";
                pair.Alternatives = candidates;
                taken.Add(proposal.Link.TypeId);
            }
            pairs[i] = pair;
        }

        if (allClear)
            foreach (var i in indexes)
                if (pairs[i].Link != null) claimed.Add(pairs[i].Link!.TypeId);
    }

    private static List<LinkCandidate> FindByName(List<LinkCandidate> links, string selector)
    {
        if (long.TryParse(selector, out var id))
        {
            var byId = links.Where(l => l.TypeId == id).ToList();
            if (byId.Count > 0) return byId;
        }

        var exact = links.Where(l => string.Equals(l.Name, selector, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;

        var stem = Stem(selector);
        var byStem = links.Where(l => string.Equals(Stem(l.Name), stem, StringComparison.OrdinalIgnoreCase) ||
                                      (l.Path.Length > 0 && string.Equals(Stem(FileName(l.Path)), stem, StringComparison.OrdinalIgnoreCase))).ToList();
        if (byStem.Count > 0) return byStem;

        return links.Where(l => l.Name.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
    }

    /// <summary>0..1 similarity between a new file and an existing link (same path → above 1).</summary>
    public static double Similarity(string filePath, LinkCandidate link)
    {
        if (link.Path.Length > 0 && DocumentTargetMatcher.NormalizePath(link.Path) == DocumentTargetMatcher.NormalizePath(filePath))
            return 2.0;

        var fileStem = Stem(FileName(filePath));
        var linkStems = new[] { Stem(link.Name), link.Path.Length > 0 ? Stem(FileName(link.Path)) : string.Empty }
            .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var best = 0.0;
        foreach (var linkStem in linkStems)
        {
            if (string.Equals(fileStem, linkStem, StringComparison.OrdinalIgnoreCase)) return 1.0;
            var tokenScore = Jaccard(Tokens(fileStem), Tokens(linkStem));
            if (fileStem.IndexOf(linkStem, StringComparison.OrdinalIgnoreCase) >= 0 ||
                linkStem.IndexOf(fileStem, StringComparison.OrdinalIgnoreCase) >= 0)
                tokenScore = Math.Max(tokenScore, 0.8);
            best = Math.Max(best, tokenScore);
        }
        return best;
    }

    /// <summary>Name tokens that carry meaning: digits-only tokens (dates, revisions) are ignored.</summary>
    private static HashSet<string> Tokens(string stem)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new System.Text.StringBuilder();
        foreach (var ch in stem + " ")
        {
            if (char.IsLetterOrDigit(ch)) { current.Append(char.ToLowerInvariant(ch)); continue; }
            if (current.Length > 0 && current.ToString().Any(char.IsLetter)) tokens.Add(current.ToString());
            current.Clear();
        }
        return tokens;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Count(b.Contains);
        return (double)intersection / (a.Count + b.Count - intersection);
    }

    public static string Stem(string name)
    {
        var s = name.Trim();
        // Link type names may carry a ": instance" suffix or a location label; keep the file part.
        var colon = s.IndexOf(" : ", StringComparison.Ordinal);
        if (colon > 0) s = s.Substring(0, colon);
        bool stripped;
        do
        {
            stripped = false;
            foreach (var ext in new[] { ".rvt", ".ifc", ".dwg" })
                if (s.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    s = s.Substring(0, s.Length - ext.Length);
                    stripped = true;
                }
        } while (stripped);
        return s;
    }

    private static string FileName(string path)
    {
        var normalized = path.Trim().Trim('"').Replace('/', '\\');
        var slash = normalized.LastIndexOf('\\');
        return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
    }

    private static string Label(LinkKind? kind) => kind?.ToString().ToUpperInvariant() ?? string.Empty;

    private static LinkReloadPair Invalid(int index, LinkReloadRequest request, string reason) => new()
    {
        RequestIndex = index, Path = request.Path, Status = LinkReloadPair.Invalid, Reason = reason
    };

    private static string Describe(List<LinkCandidate> links, LinkKind kind) =>
        links.Count == 0
            ? $"The document has no {Label(kind)} links."
            : $"{Label(kind)} links: {string.Join(", ", links.Select(l => $"'{l.Name}' (type {l.TypeId})"))}.";
}
