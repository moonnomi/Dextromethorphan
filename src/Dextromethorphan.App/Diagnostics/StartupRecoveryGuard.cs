using System.IO;
using System.Text.Json;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.App.Diagnostics;

internal sealed class StartupRecoveryGuard(AppPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };
    private readonly object _gate = new();
    private StartupRecoveryState _state = new();

    public bool Begin(bool explicitlyRequested)
    {
        lock (_gate)
        {
            paths.EnsureCreated();
            _state = Read();
            var now = DateTimeOffset.UtcNow;
            var priorFailure = _state.InProgress
                && now - _state.LastStartedAt < TimeSpan.FromDays(1);
            _state = _state with
            {
                InProgress = true,
                ConsecutiveFailures = priorFailure
                    ? _state.ConsecutiveFailures + 1
                    : 0,
                LastStartedAt = now
            };
            Write(_state);
            return explicitlyRequested || _state.ConsecutiveFailures >= 2;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _state = _state with
            {
                InProgress = false,
                ConsecutiveFailures = 0,
                LastCompletedAt = DateTimeOffset.UtcNow
            };
            Write(_state);
        }
    }

    private StartupRecoveryState Read()
    {
        try
        {
            if (!File.Exists(paths.StartupStateFile))
                return new StartupRecoveryState();
            return JsonSerializer.Deserialize<StartupRecoveryState>(
                       File.ReadAllText(paths.StartupStateFile),
                       JsonOptions)
                   ?? new StartupRecoveryState();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException)
        {
            return new StartupRecoveryState();
        }
    }

    private void Write(StartupRecoveryState state)
    {
        var temporary = paths.StartupStateFile + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(state, JsonOptions));
        if (File.Exists(paths.StartupStateFile))
            File.Move(temporary, paths.StartupStateFile, overwrite: true);
        else
            File.Move(temporary, paths.StartupStateFile);
    }

    private sealed record StartupRecoveryState
    {
        public bool InProgress { get; init; }
        public int ConsecutiveFailures { get; init; }
        public DateTimeOffset LastStartedAt { get; init; }
        public DateTimeOffset? LastCompletedAt { get; init; }
    }
}
