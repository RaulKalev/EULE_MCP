using RevitMCP.Addin.Checkpoints;

namespace RevitMCP.Addin.Jobs;

/// <summary>
/// The part of a job's checkpoint that the job made (H14). The checkpoint keeps recording after the job ends, and
/// while it runs the user and other sessions keep working, so the report counts only the batches attributed to the
/// job's own requests (client "Job &lt;id&gt;") and the snapshot comparison only for the elements those batches
/// touched. Values are still read live ("now"). Pure — no Revit API.
/// </summary>
public static class JobChangeScope
{
    /// <summary>True for a batch recorded while one of the job's steps ran.</summary>
    public static bool IsJobBatch(ChangeBatch batch, string jobId)
        => string.Equals(ChangeAttribution.AgentClient(batch.Attribution), JobIds.ClientFor(jobId), StringComparison.Ordinal);

    /// <summary>A copy of <paramref name="checkpoint"/> holding only the job's batches (snapshot and identities shared).</summary>
    public static Checkpoint Of(Checkpoint checkpoint, string jobId) => new()
    {
        Id = checkpoint.Id,
        Title = checkpoint.Title,
        ModelId = checkpoint.ModelId,
        DocumentTitle = checkpoint.DocumentTitle,
        CreatedAt = checkpoint.CreatedAt,
        DocumentVersion = checkpoint.DocumentVersion,
        CreatedBy = checkpoint.CreatedBy,
        PlanId = checkpoint.PlanId,
        Warnings = checkpoint.Warnings,
        Scope = checkpoint.Scope,
        Snapshot = checkpoint.Snapshot,
        Batches = checkpoint.Batches.Where(b => IsJobBatch(b, jobId)).ToList(),
        Identities = checkpoint.Identities,
        TrackingComplete = checkpoint.TrackingComplete,
        TrackingNote = checkpoint.TrackingNote
    };

    /// <summary>
    /// The diff of the job's changes: its netted batches plus the snapshot deltas of the elements they touched. When
    /// tracking is incomplete (a Revit restart, the recording cap) the batches may miss the job's changes, so every
    /// snapshot delta is kept, as revit_checkpoint_diff does.
    /// </summary>
    public static CheckpointDiff Diff(Checkpoint jobView, IReadOnlyDictionary<long, ElementDelta> snapshotDeltas)
    {
        if (!jobView.TrackingComplete) return CheckpointDiffBuilder.Build(jobView, snapshotDeltas);
        var touched = new HashSet<long>(jobView.Batches.SelectMany(b => b.Added.Concat(b.Modified).Concat(b.Deleted)));
        var deltas = snapshotDeltas.Where(p => touched.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        return CheckpointDiffBuilder.Build(jobView, deltas);
    }
}
