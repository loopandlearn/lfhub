namespace LFHub.Api;

/// <summary>
/// Accepts at most <c>limit</c> requests per key within any <c>window</c>; a request
/// it turns away does not count.
/// </summary>
public sealed class SlidingWindowLimiter<TKey>(int limit, TimeSpan window, TimeProvider time) where TKey : notnull
{
    /// Above this many keys, keys with nothing left in the window are dropped.
    private const int SweepAbove = 1024;

    private readonly Dictionary<TKey, Queue<DateTime>> _recent = [];
    private readonly Lock _lock = new();

    public bool TryTake(TKey key)
    {
        lock (_lock)
        {
            var now = time.GetUtcNow().UtcDateTime;
            if (!_recent.TryGetValue(key, out var accepted)) _recent[key] = accepted = new Queue<DateTime>();
            while (accepted.Count > 0 && now - accepted.Peek() > window) accepted.Dequeue();
            if (accepted.Count >= limit) return false;
            accepted.Enqueue(now);
            if (_recent.Count > SweepAbove)
                foreach (var (k, q) in _recent.ToList())
                    if (q.Count == 0 || now - q.Last() > window) _recent.Remove(k);
            return true;
        }
    }
}
