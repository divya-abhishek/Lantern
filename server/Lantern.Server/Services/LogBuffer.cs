using System.Collections.Concurrent;
using Lantern.Core.Dtos;
using Microsoft.Extensions.Logging;

namespace Lantern.Server.Services;

/// <summary>
/// Keeps the last few log entries in memory so <c>GET /api/v1/diagnostics</c> can show
/// what is going wrong on the PC without waiting for a log file to be copied around.
/// Only warnings and above are buffered (errors is what the phone screen is for).
/// </summary>
public sealed class LogBuffer : ILoggerProvider
{
    public const int Capacity = 250;

    private readonly ConcurrentQueue<LogEntryDto> _entries = new();

    public void Add(LogLevel level, string category, string message, string? detail = null)
    {
        _entries.Enqueue(new LogEntryDto
        {
            TimestampUtc = DateTime.UtcNow,
            Level = level.ToString(),
            Category = category,
            Message = Truncate(message, 500) ?? string.Empty,
            Detail = Truncate(detail, 2000),
        });

        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
        }
    }

    /// <summary>Newest entries, returned in chronological order (oldest first).</summary>
    public List<LogEntryDto> Recent(int max = 50) =>
        _entries.Reverse().Take(Math.Max(1, max)).Reverse().ToList();

    public ILogger CreateLogger(string categoryName) => new BufferLogger(this, categoryName);

    public void Dispose()
    {
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + "...";

    private sealed class BufferLogger : ILogger
    {
        private readonly LogBuffer _buffer;
        private readonly string _category;

        public BufferLogger(LogBuffer buffer, string category)
        {
            _buffer = buffer;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning) return;

            var message = formatter(state, exception);
            _buffer.Add(logLevel, _category, message, exception?.ToString());
        }
    }
}
