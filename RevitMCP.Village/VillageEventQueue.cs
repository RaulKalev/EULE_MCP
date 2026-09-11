using System.Diagnostics;
using Newtonsoft.Json;

namespace RevitMCP.Village;

/// <summary>Priority used by the queue's drop policy. Errors and transitions are kept; routine reads go first.</summary>
public static class VillagePriority
{
    public const int Low = 0;
    public const int High = 1;

    public static int Of(VillageEvent e)
    {
        switch (e.EventType)
        {
            case VillageEventTypes.ToolFailed:
            case VillageEventTypes.ToolDeferred:
            case VillageEventTypes.ProjectChanged:
            case VillageEventTypes.SessionStarted:
            case VillageEventTypes.GraphRefreshed:
            case VillageEventTypes.BridgeConnected:
            case VillageEventTypes.BridgeDisconnected:
                return High;
        }

        if (e.Activity == VillageActivities.BuildGraph || VillageActivities.IsWrite(e.Activity))
            return High;

        return Low;
    }
}

/// <summary>Counters exposed in the diagnostics panel.</summary>
public sealed class VillageQueueStats
{
    [JsonProperty("enqueued")] public long Enqueued { get; set; }
    [JsonProperty("dequeued")] public long Dequeued { get; set; }
    /// <summary>New low-priority events refused because the queue was full.</summary>
    [JsonProperty("dropped")] public long Dropped { get; set; }
    /// <summary>Queued low-priority events evicted to make room for a high-priority one.</summary>
    [JsonProperty("evicted")] public long Evicted { get; set; }
    /// <summary>Low-priority events refused by the per-second rate limit.</summary>
    [JsonProperty("rate_limited")] public long RateLimited { get; set; }
    [JsonProperty("depth")] public int Depth { get; set; }
    [JsonProperty("high_water")] public int HighWater { get; set; }
    [JsonProperty("capacity")] public int Capacity { get; set; }
}

/// <summary>
/// Bounded, thread-safe queue between the connector hooks (any thread, including the Revit API
/// thread) and the single village consumer. Enqueue is O(1) amortised, never blocks and never throws.
/// When full, a high-priority event evicts the oldest low-priority one; a low-priority event is
/// dropped. A token bucket caps low-priority events per second so a burst of thousands of element
/// queries cannot flood the consumer. No Revit API dependency.
/// </summary>
public sealed class VillageEventQueue
{
    private readonly object _gate = new();
    private readonly LinkedList<VillageEvent> _items = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Func<long> _nowMs;
    private readonly int _capacity;
    private readonly int _maxPerSecond;
    private readonly VillageQueueStats _stats = new();

    private double _tokens;
    private long _lastRefillMs;
    private int _lowCount;

    public VillageEventQueue(int capacity, int maxEventsPerSecond, Func<long>? nowMs = null)
    {
        _capacity = capacity < 1 ? 1 : capacity;
        _maxPerSecond = maxEventsPerSecond < 1 ? 1 : maxEventsPerSecond;
        _nowMs = nowMs ?? DefaultClock;
        _tokens = _maxPerSecond;
        _lastRefillMs = _nowMs();
        _stats.Capacity = _capacity;
    }

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static long DefaultClock() => Clock.ElapsedMilliseconds;

    public int Capacity => _capacity;

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>Snapshot of the counters (a copy; safe to serialize).</summary>
    public VillageQueueStats Stats
    {
        get
        {
            lock (_gate)
            {
                return new VillageQueueStats
                {
                    Enqueued = _stats.Enqueued,
                    Dequeued = _stats.Dequeued,
                    Dropped = _stats.Dropped,
                    Evicted = _stats.Evicted,
                    RateLimited = _stats.RateLimited,
                    Depth = _items.Count,
                    HighWater = _stats.HighWater,
                    Capacity = _capacity
                };
            }
        }
    }

    /// <summary>Returns false when the event was dropped (queue full or rate limited). Never throws.</summary>
    public bool TryEnqueue(VillageEvent e)
    {
        if (e == null) return false;
        var priority = VillagePriority.Of(e);
        var signal = false;

        lock (_gate)
        {
            Refill();
            if (priority == VillagePriority.Low)
            {
                if (_tokens < 1)
                {
                    _stats.RateLimited++;
                    return false;
                }
                _tokens -= 1;
            }
            else if (_tokens >= 1)
            {
                _tokens -= 1; // high-priority events still consume a token but are never refused by the limiter
            }

            if (_items.Count >= _capacity)
            {
                if (priority == VillagePriority.Low)
                {
                    _stats.Dropped++;
                    return false;
                }

                if (!EvictOldestLow())
                {
                    // Only high-priority events are queued: drop the oldest so the newest error/transition survives.
                    _items.RemoveFirst();
                    _stats.Evicted++;
                }
            }

            _items.AddLast(e);
            if (priority == VillagePriority.Low) _lowCount++;
            _stats.Enqueued++;
            if (_items.Count > _stats.HighWater) _stats.HighWater = _items.Count;
            signal = _items.Count == 1;
        }

        if (signal) Signal();
        return true;
    }

    public bool TryDequeue(out VillageEvent? e)
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                e = null;
                return false;
            }
            e = _items.First!.Value;
            _items.RemoveFirst();
            if (VillagePriority.Of(e) == VillagePriority.Low) _lowCount--;
            _stats.Dequeued++;
            return true;
        }
    }

    /// <summary>Moves up to <paramref name="max"/> events into <paramref name="target"/> in FIFO order.</summary>
    public int DrainTo(List<VillageEvent> target, int max)
    {
        var moved = 0;
        while (moved < max && TryDequeue(out var e))
        {
            target.Add(e!);
            moved++;
        }
        return moved;
    }

    /// <summary>Waits until at least one event is queued (or the token is cancelled).</summary>
    public async Task WaitForItemsAsync(CancellationToken cancellationToken)
    {
        if (Count > 0) return;
        await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits with a timeout. Returns true when items are (probably) available.</summary>
    public async Task<bool> WaitForItemsAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        if (Count > 0) return true;
        return await _signal.WaitAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    private void Signal()
    {
        try
        {
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending; that is all the consumer needs.
        }
    }

    private void Refill()
    {
        var now = _nowMs();
        var elapsed = now - _lastRefillMs;
        if (elapsed <= 0) return;
        _tokens = Math.Min(_maxPerSecond, _tokens + elapsed * (_maxPerSecond / 1000.0));
        _lastRefillMs = now;
    }

    private bool EvictOldestLow()
    {
        if (_lowCount == 0) return false;
        var node = _items.First;
        while (node != null)
        {
            if (VillagePriority.Of(node.Value) == VillagePriority.Low)
            {
                _items.Remove(node);
                _lowCount--;
                _stats.Evicted++;
                return true;
            }
            node = node.Next;
        }
        _lowCount = 0;
        return false;
    }
}
