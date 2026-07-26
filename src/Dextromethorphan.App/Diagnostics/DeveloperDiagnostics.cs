using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Windows;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.App.Diagnostics;

internal sealed record DeveloperDiagnosticsOptions(
    bool Enabled,
    bool Verbose,
    string? OutputDirectory,
    string SessionName)
{
    public static DeveloperDiagnosticsOptions Parse(IReadOnlyList<string> args, Performance.PerformanceBenchmarkOptions? benchmark)
    {
        var enabled = IsEnabled(Environment.GetEnvironmentVariable("DEXTROMETHORPHAN_DIAGNOSTICS"));
        var verbose = IsEnabled(Environment.GetEnvironmentVariable("DEXTROMETHORPHAN_DIAGNOSTICS_VERBOSE"));
        var output = Environment.GetEnvironmentVariable("DEXTROMETHORPHAN_DIAGNOSTICS_OUTPUT");
        var session = Environment.GetEnvironmentVariable("DEXTROMETHORPHAN_DIAGNOSTICS_SESSION") ?? "interactive";

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index].ToLowerInvariant())
            {
                case "--diagnostics":
                    enabled = true;
                    break;
                case "--diagnostics-verbose":
                    enabled = true;
                    verbose = true;
                    break;
                case "--diagnostics-output":
                    enabled = true;
                    output = RequireValue(args, ref index, "--diagnostics-output");
                    break;
                case "--diagnostics-session":
                    enabled = true;
                    session = RequireValue(args, ref index, "--diagnostics-session");
                    break;
            }
        }

        if (benchmark is not null)
        {
            enabled = true;
            output ??= Path.Combine(Path.GetDirectoryName(benchmark.OutputPath)!, "diagnostics");
            session = $"benchmark-{benchmark.RunKind}";
        }
        return new DeveloperDiagnosticsOptions(enabled, verbose, output, SanitizeSessionName(session));
    }

    private static bool IsEnabled(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string name)
    {
        if (++index >= args.Count) throw new ArgumentException($"{name} requires a value.");
        return args[index];
    }

    private static string SanitizeSessionName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "session" : sanitized[..Math.Min(60, sanitized.Length)];
    }
}

public sealed class DeveloperDiagnostics
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly ConcurrentDictionary<string, OperationAggregate> _aggregates = new(StringComparer.Ordinal);
    private readonly object _configurationGate = new();
    private Channel<DiagnosticEvent>? _channel;
    private Task? _writerTask;
    private string? _summaryPath;
    private int _droppedEvents;
    private bool _completed;

    public DeveloperDiagnostics() => Current = this;

    internal static DeveloperDiagnostics? Current { get; private set; }
    internal bool Enabled { get; private set; }
    internal bool Verbose { get; private set; }
    internal string? EventLogPath { get; private set; }
    internal string? SummaryPath => _summaryPath;

    internal void Configure(DeveloperDiagnosticsOptions options, AppPaths paths)
    {
        lock (_configurationGate)
        {
            if (Enabled || !options.Enabled) return;
            var directory = Path.GetFullPath(string.IsNullOrWhiteSpace(options.OutputDirectory) ? paths.Logs : options.OutputDirectory);
            Directory.CreateDirectory(directory);
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            EventLogPath = Path.Combine(directory, $"diagnostics-{stamp}-{options.SessionName}.jsonl");
            _summaryPath = Path.Combine(directory, $"diagnostics-{stamp}-{options.SessionName}-summary.json");
            Verbose = options.Verbose;
            Enabled = true;
            _channel = Channel.CreateBounded<DiagnosticEvent>(new BoundedChannelOptions(16_384)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            _writerTask = WriteEventsAsync(_channel.Reader, EventLogPath);
        }
        Mark("diagnostics", "session.started", new Dictionary<string, object?>
        {
            ["verbose"] = Verbose,
            ["eventLog"] = EventLogPath
        });
    }

    internal DiagnosticOperationScope Measure(string category, string operation, IReadOnlyDictionary<string, object?>? data = null) =>
        Enabled ? new DiagnosticOperationScope(this, category, operation, data) : default;

    internal Task<T> MeasureAsync<T>(string category, string operation, Func<Task<T>> action, IReadOnlyDictionary<string, object?>? data = null)
    {
        if (!Enabled) return action();
        return MeasureCoreAsync(category, operation, action, data);
    }

    private async Task<T> MeasureCoreAsync<T>(string category, string operation, Func<Task<T>> action, IReadOnlyDictionary<string, object?>? data)
    {
        using var scope = Measure(category, operation, data);
        try { return await action(); }
        catch (Exception exception)
        {
            scope.Fail(exception);
            throw;
        }
    }

    internal Task MeasureAsync(string category, string operation, Func<Task> action, IReadOnlyDictionary<string, object?>? data = null)
    {
        if (!Enabled) return action();
        return MeasureCoreAsync(category, operation, action, data);
    }

    private async Task MeasureCoreAsync(string category, string operation, Func<Task> action, IReadOnlyDictionary<string, object?>? data)
    {
        using var scope = Measure(category, operation, data);
        try { await action(); }
        catch (Exception exception)
        {
            scope.Fail(exception);
            throw;
        }
    }

    internal void RecordDuration(string category, string operation, TimeSpan duration, IReadOnlyDictionary<string, object?>? data = null, Exception? error = null)
    {
        if (!Enabled) return;
        var milliseconds = duration.TotalMilliseconds;
        _aggregates.GetOrAdd($"{category}.{operation}", _ => new OperationAggregate(category, operation)).Add(milliseconds, error is not null);
        if (Verbose || milliseconds >= 2 || error is not null)
            Enqueue(new DiagnosticEvent(DateTimeOffset.UtcNow, error is null ? "timing" : "error", category, operation,
                Math.Round(milliseconds, 3), Environment.CurrentManagedThreadId, IsUiThread(), data,
                error?.GetBaseException().Message, error?.ToString()));
    }

    internal void Mark(string category, string operation, IReadOnlyDictionary<string, object?>? data = null) =>
        Enqueue(new DiagnosticEvent(DateTimeOffset.UtcNow, "breadcrumb", category, operation, null,
            Environment.CurrentManagedThreadId, IsUiThread(), data, null, null));

    internal void Error(string category, string operation, Exception exception, IReadOnlyDictionary<string, object?>? data = null) =>
        Enqueue(new DiagnosticEvent(DateTimeOffset.UtcNow, "error", category, operation, null,
            Environment.CurrentManagedThreadId, IsUiThread(), data,
            exception.GetBaseException().Message, exception.ToString()));

    internal async Task CompleteAsync()
    {
        lock (_configurationGate)
        {
            if (!Enabled || _completed) return;
            Enqueue(new DiagnosticEvent(DateTimeOffset.UtcNow, "breadcrumb", "diagnostics", "session.completed", null,
                Environment.CurrentManagedThreadId, IsUiThread(), null, null, null));
            _completed = true;
        }
        _channel!.Writer.TryComplete();
        try
        {
            if (_writerTask is not null) await _writerTask;
            await WriteSummaryAsync();
        }
        catch
        {
            // Diagnostics must never prevent the application from closing.
        }
    }

    private void Enqueue(DiagnosticEvent value)
    {
        if (!Enabled || _completed || _channel is null) return;
        if (!_channel.Writer.TryWrite(value)) Interlocked.Increment(ref _droppedEvents);
    }

    private static bool IsUiThread()
    {
        try { return Application.Current?.Dispatcher.CheckAccess() == true; }
        catch { return false; }
    }

    private static async Task WriteEventsAsync(ChannelReader<DiagnosticEvent> reader, string path)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true);
        await using var writer = new StreamWriter(stream);
        await foreach (var value in reader.ReadAllAsync())
            await writer.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions));
        await writer.FlushAsync();
    }

    private async Task WriteSummaryAsync()
    {
        if (_summaryPath is null) return;
        var operations = _aggregates.Values
            .Select(value => value.Snapshot())
            .OrderByDescending(value => value.MaximumMs)
            .ThenBy(value => value.Category)
            .ThenBy(value => value.Operation)
            .ToArray();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var summary = new
        {
            schemaVersion = 1,
            capturedAt = DateTimeOffset.UtcNow,
            eventLog = EventLogPath,
            droppedEvents = _droppedEvents,
            process = new
            {
                workingSetBytes = process.WorkingSet64,
                peakWorkingSetBytes = process.PeakWorkingSet64,
                privateMemoryBytes = process.PrivateMemorySize64,
                managedHeapBytes = GC.GetTotalMemory(false),
                gen0Collections = GC.CollectionCount(0),
                gen1Collections = GC.CollectionCount(1),
                gen2Collections = GC.CollectionCount(2)
            },
            operations
        };
        await File.WriteAllTextAsync(_summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
    }

    internal readonly struct DiagnosticOperationScope : IDisposable
    {
        private readonly DeveloperDiagnostics? _owner;
        private readonly string? _category;
        private readonly string? _operation;
        private readonly IReadOnlyDictionary<string, object?>? _data;
        private readonly long _started;
        private readonly FailureState? _failure;

        internal DiagnosticOperationScope(DeveloperDiagnostics owner, string category, string operation, IReadOnlyDictionary<string, object?>? data)
        {
            _owner = owner;
            _category = category;
            _operation = operation;
            _data = data;
            _started = Stopwatch.GetTimestamp();
            _failure = new FailureState();
        }

        public void Fail(Exception exception)
        {
            if (_failure is not null) _failure.Exception = exception;
        }

        public void Dispose()
        {
            if (_owner is null || _category is null || _operation is null) return;
            _owner.RecordDuration(_category, _operation, Stopwatch.GetElapsedTime(_started), _data, _failure?.Exception);
        }

        private sealed class FailureState
        {
            public Exception? Exception { get; set; }
        }
    }

    private sealed class OperationAggregate(string category, string operation)
    {
        private const int MaximumSamples = 4_096;
        private readonly object _gate = new();
        private readonly List<double> _samples = [];
        private long _count;
        private long _errors;
        private double _total;
        private double _maximum;

        public void Add(double milliseconds, bool error)
        {
            lock (_gate)
            {
                _count++;
                if (error) _errors++;
                _total += milliseconds;
                _maximum = Math.Max(_maximum, milliseconds);
                if (_samples.Count < MaximumSamples) _samples.Add(milliseconds);
            }
        }

        public OperationSummary Snapshot()
        {
            lock (_gate)
            {
                var sorted = _samples.Order().ToArray();
                return new OperationSummary(
                    category,
                    operation,
                    _count,
                    _errors,
                    Math.Round(_count == 0 ? 0 : _total / _count, 3),
                    Percentile(sorted, 0.50),
                    Percentile(sorted, 0.95),
                    Percentile(sorted, 0.99),
                    Math.Round(_maximum, 3),
                    sorted.Length,
                    _count > sorted.Length);
            }
        }

        private static double Percentile(IReadOnlyList<double> sorted, double percentile)
        {
            if (sorted.Count == 0) return 0;
            var index = (int)Math.Ceiling((sorted.Count - 1) * percentile);
            return Math.Round(sorted[Math.Clamp(index, 0, sorted.Count - 1)], 3);
        }
    }

    private sealed record DiagnosticEvent(
        DateTimeOffset Timestamp,
        string Kind,
        string Category,
        string Operation,
        double? DurationMs,
        int ThreadId,
        bool UiThread,
        IReadOnlyDictionary<string, object?>? Data,
        string? Error,
        string? Exception);

    private sealed record OperationSummary(
        string Category,
        string Operation,
        long Count,
        long Errors,
        double AverageMs,
        double P50Ms,
        double P95Ms,
        double P99Ms,
        double MaximumMs,
        int Sampled,
        bool SamplesTruncated);
}
