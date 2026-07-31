using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.App.Diagnostics;

public sealed class StructuredApplicationLog : IApplicationLog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly AppPaths _paths;
    private readonly long _maximumFileBytes;
    private readonly int _retainedFiles;
    private readonly Channel<LogEntry> _events;
    private readonly Task _writer;
    private int _completed;

    public StructuredApplicationLog(
        AppPaths paths,
        long maximumFileBytes = 5L * 1024 * 1024,
        int retainedFiles = 10)
    {
        _paths = paths;
        _maximumFileBytes = Math.Max(64 * 1024, maximumFileBytes);
        _retainedFiles = Math.Clamp(retainedFiles, 2, 50);
        _events = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(8_192)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _writer = WriteLoopAsync();
    }

    public void Write(
        ApplicationLogLevel level,
        string category,
        string operation,
        IReadOnlyDictionary<string, object?>? data = null,
        Exception? exception = null)
    {
        if (Volatile.Read(ref _completed) != 0) return;
        _events.Writer.TryWrite(new LogEntry(
            DateTimeOffset.UtcNow,
            level.ToString(),
            category,
            operation,
            Environment.CurrentManagedThreadId,
            data,
            exception?.GetBaseException().Message,
            exception?.ToString()));
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        _events.Writer.TryComplete();
        await _writer.WaitAsync(cancellationToken);
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        DateOnly currentDate = default;
        try
        {
            await foreach (var entry in _events.Reader.ReadAllAsync())
            {
                var date = DateOnly.FromDateTime(entry.Timestamp.LocalDateTime);
                if (writer is null
                    || date != currentDate
                    || writer.BaseStream.Length >= _maximumFileBytes)
                {
                    if (writer is not null)
                    {
                        await writer.FlushAsync();
                        await writer.DisposeAsync();
                    }
                    _paths.EnsureCreated();
                    currentDate = date;
                    writer = OpenWriter(date);
                    Prune();
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(entry, Json));
            }
        }
        catch
        {
            // Logging must never take down the application.
        }
        finally
        {
            if (writer is not null)
            {
                try
                {
                    await writer.FlushAsync();
                    await writer.DisposeAsync();
                }
                catch { }
            }
        }
    }

    private StreamWriter OpenWriter(DateOnly date)
    {
        for (var sequence = 0; sequence < 1_000; sequence++)
        {
            var path = Path.Combine(
                _paths.Logs,
                $"app-{date:yyyyMMdd}-{sequence:D3}.jsonl");
            var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                32 * 1024,
                true);
            if (stream.Length < _maximumFileBytes)
                return new StreamWriter(stream);
            stream.Dispose();
        }
        throw new IOException("No application log rotation slot is available.");
    }

    private void Prune()
    {
        try
        {
            foreach (var file in new DirectoryInfo(_paths.Logs)
                         .EnumerateFiles("app-*.jsonl")
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(_retainedFiles))
                file.Delete();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record LogEntry(
        DateTimeOffset Timestamp,
        string Level,
        string Category,
        string Operation,
        int ThreadId,
        IReadOnlyDictionary<string, object?>? Data,
        string? Error,
        string? Exception);
}
