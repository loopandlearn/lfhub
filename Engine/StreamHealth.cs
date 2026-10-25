namespace LFHub.Engine;

/// <summary>
/// Whether a patient's Nightscout socket counts as healthy: authorized, or not
/// authorized for less than the grace since it last was (or since following began).
/// Failed reconnect attempts do not restart the grace.
/// </summary>
internal sealed class StreamHealth(DateTime startedAt)
{
    private readonly Lock _lock = new();
    private bool _live;
    private DateTime _downSince = startedAt;

    /// The server confirmed the socket's authorization.
    public void Authorized()
    {
        lock (_lock) _live = true;
    }

    /// A session ended, or a connection attempt failed.
    public void Closed(DateTime now)
    {
        lock (_lock)
        {
            if (!_live) return;
            _live = false;
            _downSince = now;
        }
    }

    public bool IsHealthy(DateTime now, TimeSpan grace)
    {
        lock (_lock) return _live || now - _downSince < grace;
    }
}
