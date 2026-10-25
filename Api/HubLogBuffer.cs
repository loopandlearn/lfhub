using System.Collections.Concurrent;

namespace LFHub.Api;

public sealed record HubLogLine(DateTime At, LogLevel Level, string Category, string Message)
{
    public override string ToString() =>
        $"{At:yyyy-MM-ddTHH:mm:ss}Z {Level.ToString().ToUpperInvariant()[..4]} {Category}: {Message}";
}

/// <summary>
/// Keeps the latest log lines in memory so a device can fetch the ones about
/// it (GET /v1/device/log) and attach them to LoopFollow's shared logs. Lines
/// are lost on restart; the container log keeps everything.
/// </summary>
public sealed class HubLogBuffer : ILoggerProvider
{
    private const int Capacity = 20_000;
    private readonly ConcurrentQueue<HubLogLine> _lines = new();
    private int _count;

    public ILogger CreateLogger(string categoryName) => new BufferLogger(this, ShortName(categoryName));

    public IReadOnlyList<HubLogLine> Since(DateTime fromUtc) => _lines.Where(l => l.At >= fromUtc).ToList();

    public void Dispose() { }

    private void Add(HubLogLine line)
    {
        _lines.Enqueue(line);
        if (Interlocked.Increment(ref _count) <= Capacity) return;
        if (_lines.TryDequeue(out _)) Interlocked.Decrement(ref _count);
    }

    private static string ShortName(string category) =>
        category.StartsWith("LFHub.") ? category[(category.LastIndexOf('.') + 1)..] : category;

    private sealed class BufferLogger(HubLogBuffer buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= (category.StartsWith("Microsoft") || category.StartsWith("System") ? LogLevel.Warning : LogLevel.Information);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null) message += $" ({exception.GetBaseException().Message})";
            buffer.Add(new HubLogLine(DateTime.UtcNow, logLevel, category, message));
        }
    }
}
