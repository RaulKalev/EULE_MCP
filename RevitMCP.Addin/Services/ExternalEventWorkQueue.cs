using System.Collections.Concurrent;

namespace RevitMCP.Addin.Services;

/// <summary>
/// Thread-safe bounded queue for work dispatched to the Revit API thread.
/// </summary>
public sealed class ExternalEventWorkQueue
{
    private readonly ConcurrentQueue<ExternalEventWorkItem> _queue = new();
    private readonly object _gate = new();
    private readonly int _capacity;
    private int _count;

    public ExternalEventWorkQueue(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count => Volatile.Read(ref _count);
    public bool HasItems => Count > 0;

    public bool TryEnqueue(ExternalEventWorkItem item)
    {
        lock (_gate)
        {
            if (_count >= _capacity)
                return false;
            _queue.Enqueue(item);
            _count++;
            return true;
        }
    }

    public bool TryDequeue(out ExternalEventWorkItem? item)
    {
        lock (_gate)
        {
            if (!_queue.TryDequeue(out item))
                return false;
            _count--;
            return true;
        }
    }

    public bool TryCancel(string requestId, string message, string status)
    {
        lock (_gate)
        {
            foreach (var item in _queue)
            {
                if (item.Request.RequestId != requestId)
                    continue;

                item.Cancel(message, status);
                return true;
            }
        }

        return false;
    }

    public void Drain(string message, string status)
    {
        while (TryDequeue(out var item))
        {
            item!.Cancel(message, status);
            item.Dispose();
        }
    }
}
