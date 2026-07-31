using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace Dextromethorphan.App.WindowsIntegration;

internal sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private Mutex? _mutex;
    private Task? _listener;
    private bool _ownsMutex;

    public SingleInstanceCoordinator(string? instanceName = null)
    {
        var suffix = string.IsNullOrWhiteSpace(instanceName)
            ? $"Dextromethorphan-{Environment.UserName}"
            : instanceName;
        var safe = new string(suffix
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        _mutexName = $@"Local\{safe}";
        _pipeName = safe;
    }

    public event EventHandler<IReadOnlyList<string>>? ArgumentsReceived;

    public async Task<bool> AcquireOrForwardAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        _mutex = new Mutex(true, _mutexName, out var created);
        if (created)
        {
            _ownsMutex = true;
            _listener = ListenAsync(_lifetime.Token);
            return true;
        }

        _mutex.Dispose();
        _mutex = null;
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(3), cancellationToken);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                arguments.Take(128).ToArray()));
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            if (exception is OperationCanceledException) throw;
            // The primary can be between acquiring the mutex and opening the
            // first pipe. A later launch can retry; never start a second app
            // process against the same writable database.
        }
        return false;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe);
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line)) continue;
                var arguments = JsonSerializer.Deserialize<string[]>(line);
                if (arguments is { Length: > 0 })
                    ArgumentsReceived?.Invoke(this, arguments);
                else
                    ArgumentsReceived?.Invoke(this, Array.Empty<string>());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (JsonException)
            {
                // Ignore malformed messages from unrelated local clients.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_listener is not null)
        {
            try { await _listener; }
            catch (OperationCanceledException) { }
        }
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _mutex?.Dispose();
        _lifetime.Dispose();
    }
}

internal static class LaunchTargetParser
{
    private static readonly HashSet<string> OptionsWithValues =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "--performance-benchmark",
            "--benchmark-kind",
            "--benchmark-scan-files",
            "--gallery-capture-directory",
            "--diagnostics-output",
            "--diagnostics-session"
        };

    public static IReadOnlyList<string> Extract(IReadOnlyList<string> arguments)
    {
        var result = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var value = arguments[index];
            if (OptionsWithValues.Contains(value))
            {
                index++;
                continue;
            }
            if (value.StartsWith("-", StringComparison.Ordinal))
                continue;
            try
            {
                var path = Path.GetFullPath(value);
                if (File.Exists(path) || Directory.Exists(path))
                    result.Add(path);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Not a filesystem launch target.
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
