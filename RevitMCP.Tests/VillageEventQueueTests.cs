using RevitMCP.Village;
using Xunit;

namespace RevitMCP.Tests;

public class VillageEventQueueTests
{
    private static VillageEvent Read(int seq) => new()
    {
        EventType = VillageEventTypes.ToolCompleted,
        Activity = VillageActivities.Inspect,
        Area = VillageAreas.Elements,
        ToolName = "revit_get_elements_info",
        Sequence = seq
    };

    private static VillageEvent Failure(int seq) => new()
    {
        EventType = VillageEventTypes.ToolFailed,
        Activity = VillageActivities.Error,
        Area = VillageAreas.Elements,
        ToolName = "revit_set_parameter",
        Sequence = seq
    };

    private static VillageEvent Write(int seq) => new()
    {
        EventType = VillageEventTypes.ToolCompleted,
        Activity = VillageActivities.Modify,
        Area = VillageAreas.Elements,
        ToolName = "revit_set_parameter",
        Sequence = seq
    };

    [Fact]
    public void Priority_KeepsErrorsTransitionsAndWrites()
    {
        Assert.Equal(VillagePriority.Low, VillagePriority.Of(Read(1)));
        Assert.Equal(VillagePriority.High, VillagePriority.Of(Failure(1)));
        Assert.Equal(VillagePriority.High, VillagePriority.Of(Write(1)));
        Assert.Equal(VillagePriority.High, VillagePriority.Of(new VillageEvent { EventType = VillageEventTypes.ProjectChanged }));
        Assert.Equal(VillagePriority.High, VillagePriority.Of(new VillageEvent { EventType = VillageEventTypes.ToolDeferred }));
        Assert.Equal(VillagePriority.High, VillagePriority.Of(new VillageEvent { EventType = VillageEventTypes.ToolCompleted, Activity = VillageActivities.BuildGraph }));
        Assert.Equal(VillagePriority.Low, VillagePriority.Of(new VillageEvent { EventType = VillageEventTypes.ToolStarted, Activity = VillageActivities.Search }));
    }

    [Fact]
    public void Enqueue_IsFifoAndBounded()
    {
        var q = new VillageEventQueue(capacity: 3, maxEventsPerSecond: 1000, nowMs: () => 0);
        Assert.True(q.TryEnqueue(Read(1)));
        Assert.True(q.TryEnqueue(Read(2)));
        Assert.True(q.TryEnqueue(Read(3)));
        Assert.False(q.TryEnqueue(Read(4))); // full, low priority → dropped
        Assert.Equal(3, q.Count);

        Assert.True(q.TryDequeue(out var first));
        Assert.Equal(1, first!.Sequence);
        var rest = new List<VillageEvent>();
        Assert.Equal(2, q.DrainTo(rest, 10));
        Assert.Equal(new[] { 2L, 3L }, rest.Select(e => e.Sequence).ToArray());
        Assert.False(q.TryDequeue(out _));

        var stats = q.Stats;
        Assert.Equal(3, stats.Enqueued);
        Assert.Equal(3, stats.Dequeued);
        Assert.Equal(1, stats.Dropped);
        Assert.Equal(3, stats.HighWater);
        Assert.Equal(3, stats.Capacity);
        Assert.Equal(0, stats.Depth);
    }

    [Fact]
    public void FullQueue_HighPriorityEvictsOldestLowPriority()
    {
        var q = new VillageEventQueue(3, 1000, () => 0);
        q.TryEnqueue(Read(1));
        q.TryEnqueue(Write(2));
        q.TryEnqueue(Read(3));

        Assert.True(q.TryEnqueue(Failure(4)));
        var drained = new List<VillageEvent>();
        q.DrainTo(drained, 10);
        Assert.Equal(new[] { 2L, 3L, 4L }, drained.Select(e => e.Sequence).ToArray());
        Assert.Equal(1, q.Stats.Evicted);
    }

    [Fact]
    public void FullQueue_OfHighPriorityDropsTheOldest()
    {
        var q = new VillageEventQueue(2, 1000, () => 0);
        q.TryEnqueue(Failure(1));
        q.TryEnqueue(Failure(2));
        Assert.True(q.TryEnqueue(Failure(3)));

        var drained = new List<VillageEvent>();
        q.DrainTo(drained, 10);
        Assert.Equal(new[] { 2L, 3L }, drained.Select(e => e.Sequence).ToArray());
        Assert.Equal(1, q.Stats.Evicted);
    }

    [Fact]
    public void RateLimit_DropsOnlyLowPriorityBurst()
    {
        long now = 0;
        var q = new VillageEventQueue(10000, maxEventsPerSecond: 10, nowMs: () => now);

        var accepted = 0;
        for (var i = 0; i < 50; i++)
            if (q.TryEnqueue(Read(i))) accepted++;
        Assert.Equal(10, accepted);
        Assert.Equal(40, q.Stats.RateLimited);

        // High-priority events pass even with an empty bucket.
        Assert.True(q.TryEnqueue(Failure(100)));
        Assert.True(q.TryEnqueue(Write(101)));

        // Half a second later, five more tokens are available.
        now = 500;
        accepted = 0;
        for (var i = 0; i < 50; i++)
            if (q.TryEnqueue(Read(200 + i))) accepted++;
        Assert.Equal(5, accepted);
    }

    [Fact]
    public void Enqueue_NullIsIgnored()
    {
        var q = new VillageEventQueue(10, 100, () => 0);
        Assert.False(q.TryEnqueue(null!));
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public async Task WaitForItems_WakesConsumerAndTimesOut()
    {
        var q = new VillageEventQueue(10, 100);
        Assert.False(await q.WaitForItemsAsync(50, CancellationToken.None));

        var waiter = q.WaitForItemsAsync(2000, CancellationToken.None);
        q.TryEnqueue(Read(1));
        Assert.True(await waiter);

        // Only the 0→1 transition signals, so wake-ups never accumulate beyond one.
        q.TryEnqueue(Read(2));
        q.TryEnqueue(Read(3));
        var list = new List<VillageEvent>();
        q.DrainTo(list, 10);
        Assert.Equal(3, list.Count);
        Assert.False(await q.WaitForItemsAsync(10, CancellationToken.None)); // nothing buffered

        // With items already queued the wait returns immediately without consuming a signal.
        q.TryEnqueue(Read(4));
        Assert.True(await q.WaitForItemsAsync(10, CancellationToken.None));
        Assert.True(await q.WaitForItemsAsync(10, CancellationToken.None));
    }

    [Fact]
    public void Enqueue_IsSafeFromManyThreads()
    {
        var q = new VillageEventQueue(500, 1_000_000, () => 0);
        Parallel.For(0, 4000, i =>
        {
            q.TryEnqueue(i % 3 == 0 ? Failure(i) : Read(i));
        });

        Assert.True(q.Count <= 500);
        var stats = q.Stats;
        Assert.Equal(4000, stats.Enqueued + stats.Dropped + stats.RateLimited);
        var drained = new List<VillageEvent>();
        q.DrainTo(drained, 10000);
        Assert.Equal(stats.Depth, drained.Count);
    }
}
