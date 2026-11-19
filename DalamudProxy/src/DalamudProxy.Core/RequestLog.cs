using System.Collections.Concurrent;

namespace DalamudProxy.Core;

public sealed class RequestLog
{
    private const int Capacity = 200;
    private readonly ConcurrentQueue<RequestLogEntry> entries = new();
    private int count;

    public event Action? Changed;

    public IReadOnlyList<RequestLogEntry> Snapshot() => this.entries.ToArray();

    public void Add(RequestLogEntry entry)
    {
        this.entries.Enqueue(entry);
        if (Interlocked.Increment(ref this.count) <= Capacity)
        {
            this.Changed?.Invoke();
            return;
        }

        while (Volatile.Read(ref this.count) > Capacity && this.entries.TryDequeue(out _))
            Interlocked.Decrement(ref this.count);

        this.Changed?.Invoke();
    }

    public void Clear()
    {
        while (this.entries.TryDequeue(out _))
        {
        }

        Volatile.Write(ref this.count, 0);
        this.Changed?.Invoke();
    }
}
