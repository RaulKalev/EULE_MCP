using System.IO;

namespace RevitMCP.Addin.Memory;

/// <summary>The outcome of one submitted proposal.</summary>
public sealed class ProposalOutcome
{
    public int Index { get; set; }
    public string? Id { get; set; }
    public string? Project { get; set; }
    public List<string> Problems { get; set; } = new();
}

/// <summary>
/// Runs the consolidation pass on plain files: the bridge calls it directly, so it needs no Revit open (memory is
/// files). Loads the project memory folders under the given project roots, reports, stores proposals and records the
/// run. Pure — no Revit API.
/// </summary>
public sealed class MemoryConsolidationRunner
{
    private readonly MemorySettings _settings;

    public MemoryConsolidationRunner(MemorySettings settings) => _settings = settings;

    public string FolderOf(string root) => Path.Combine(root, _settings.ProjectFolderName);

    /// <summary>Each root's memory with tallies and lifecycle applied. Missing folders are reported in <paramref name="warnings"/>.</summary>
    public List<ProjectMemory> Load(IEnumerable<string> roots, DateTimeOffset now, List<string> warnings)
    {
        var projects = new List<ProjectMemory>();
        foreach (var raw in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string root;
            try { root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                warnings.Add($"Not a folder path: {raw}");
                continue;
            }
            var folder = FolderOf(root);
            if (!Directory.Exists(folder))
            {
                warnings.Add($"No project memory in {root} ({folder} does not exist).");
                continue;
            }

            using var cache = new MemoryCache(new MemoryStore(folder), watch: false);
            var snapshot = cache.Read();
            foreach (var error in snapshot.Errors) warnings.Add($"{root}: skipped an unreadable entry: {error}");
            MemoryLifecycle.Apply(snapshot.Entries, now, _settings);
            projects.Add(new ProjectMemory
            {
                Root = root,
                Folder = folder,
                Project = snapshot.Entries.Select(e => e.Project).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? Path.GetFileName(root),
                Entries = snapshot.Entries,
                Proposals = new MemoryProposalStore(folder).List(),
                LastRun = MemoryConsolidation.ReadLastRun(folder)
            });
        }
        return projects;
    }

    /// <summary>
    /// The report since the oldest last run of the projects (each project's own last run filters its changes), or
    /// everything with <paramref name="full"/> or an explicit <paramref name="since"/>.
    /// </summary>
    public ConsolidationReport Read(List<ProjectMemory> projects, DateTimeOffset? since, bool full, DateTimeOffset now)
    {
        if (full || since != null) return MemoryConsolidation.Analyze(projects, full ? null : since, now);

        // Per project since its own last run, merged into one report.
        var merged = new ConsolidationReport { ReadAt = now, Since = projects.Select(p => p.LastRun).Min() };
        foreach (var p in projects)
        {
            var part = MemoryConsolidation.Analyze(new[] { p }, p.LastRun, now);
            merged.Changed.AddRange(part.Changed);
            merged.Duplicates.AddRange(part.Duplicates);
            merged.FlipFlops.AddRange(part.FlipFlops);
            merged.Observed.AddRange(part.Observed);
            merged.Dormant.AddRange(part.Dormant);
            merged.PendingProposals += part.PendingProposals;
        }
        merged.CrossProject = MemoryConsolidation.CrossProject(projects);
        return merged;
    }

    /// <summary>Validates and stores proposals in the project folder that holds their first entry.</summary>
    public List<ProposalOutcome> Propose(List<ProjectMemory> projects, IReadOnlyList<MemoryProposal> proposals, string user, string? client, DateTimeOffset now)
    {
        var byId = new Dictionary<string, (MemoryEntry Entry, ProjectMemory Project)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in projects)
            foreach (var e in p.Entries)
                byId[e.Id] = (e, p);
        var entries = byId.ToDictionary(k => k.Key, v => v.Value.Entry, StringComparer.OrdinalIgnoreCase);

        var outcomes = new List<ProposalOutcome>();
        for (var i = 0; i < proposals.Count; i++)
        {
            var proposal = proposals[i];
            var outcome = new ProposalOutcome { Index = i };
            outcomes.Add(outcome);
            outcome.Problems.AddRange(MemoryProposalRules.Check(proposal, entries, _settings.ValueMaxLength));
            // Only promotions may span projects; a merge or rewrite stays inside one project's memory.
            if (outcome.Problems.Count == 0 && proposal.Kind != MemoryProposalKinds.Promote &&
                proposal.EntryIds.Select(id => byId[id].Project.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                outcome.Problems.Add("A merge, rewrite or retire proposal must name entries of one project.");
            if (outcome.Problems.Count > 0) continue;

            var home = byId[proposal.EntryIds[0]].Project;
            proposal.Id = Guid.NewGuid().ToString("N").Substring(0, 12);
            proposal.Status = MemoryProposalStatus.Pending;
            proposal.CreatedAt = now;
            proposal.CreatedBy = user;
            proposal.Client = client;
            proposal.DecidedAt = null;
            proposal.DecidedBy = null;
            proposal.Result = null;
            try
            {
                new MemoryProposalStore(home.Folder).Save(proposal);
                outcome.Id = proposal.Id;
                outcome.Project = home.Project;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcome.Problems.Add($"Could not write the proposal in {home.Folder}: {ex.Message}");
            }
        }
        return outcomes;
    }

    /// <summary>Records the run, so the next one reads only what changed after <paramref name="readAt"/>.</summary>
    public List<string> Done(List<ProjectMemory> projects, DateTimeOffset readAt, string user)
    {
        var problems = new List<string>();
        foreach (var p in projects)
        {
            try { MemoryConsolidation.WriteLastRun(p.Folder, readAt, user); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{p.Root}: {ex.Message}");
            }
        }
        return problems;
    }
}
