using Microsoft.Extensions.Logging;

namespace LFHub.Tests.Fakes;

/// Keeps every formatted log line, for asserting on what is (and is not) logged.
internal sealed class ListLogger : ILogger
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines) return [.. _lines];
        }
    }

    public string Text => string.Join("\n", Lines);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_lines) _lines.Add($"{logLevel}: {formatter(state, exception)}");
    }
}

internal sealed class ListLoggerFactory : ILoggerFactory
{
    public ListLogger Logger { get; } = new();

    public ILogger CreateLogger(string categoryName) => Logger;

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }
}
