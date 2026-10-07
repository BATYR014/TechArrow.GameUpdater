using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
namespace TechArrow.GameUpdater.Infrastructure.Services;
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message)
{
    public override string ToString() => $"[{Timestamp:HH:mm:ss}] [{Level}] {Category}: {Message}";
}
public sealed class LoggingService : ILoggerProvider, IAsyncDisposable
{
    private readonly Channel<LogEntry> _queue = Channel.CreateUnbounded<LogEntry>(new() { SingleReader = true });
    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private readonly Task _writer;
    private readonly AppPaths _paths;
    private string? _writeError;
    public string? WriteError => Volatile.Read(ref _writeError);
    public IReadOnlyList<LogEntry> Snapshot => _recent.ToArray();
    public LoggingService(AppPaths paths) { _paths = paths; _writer = Task.Run(WriteLoopAsync); }
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    private void Add(LogEntry entry)
    {
        _recent.Enqueue(entry);
        while (_recent.Count > 1000) _recent.TryDequeue(out _);
        _queue.Writer.TryWrite(entry);
    }
    private async Task WriteLoopAsync()
    {
        await foreach (var entry in _queue.Reader.ReadAllAsync())
        {
            try
            {
                Directory.CreateDirectory(_paths.LogsDirectory);
                var file = Path.Combine(_paths.LogsDirectory, $"{entry.Timestamp:yyyy-MM-dd}.log");
                await File.AppendAllTextAsync(file, entry + Environment.NewLine).ConfigureAwait(false);
                Volatile.Write(ref _writeError, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Volatile.Write(ref _writeError, "Не удалось записать лог: " + ex.Message);
                System.Diagnostics.Trace.TraceError("Log write failed: {0}", ex);
            }
        }
    }
    public void Dispose() => _queue.Writer.TryComplete();
    public async ValueTask DisposeAsync() { Dispose(); await _writer.ConfigureAwait(false); }
    private sealed class FileLogger(LoggingService owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner.Add(new(DateTimeOffset.Now, logLevel, category,
                formatter(state, exception) + (exception is null ? "" : Environment.NewLine + exception)));
        }
    }
}
