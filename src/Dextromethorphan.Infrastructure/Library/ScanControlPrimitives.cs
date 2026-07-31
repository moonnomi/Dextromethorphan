using System.Diagnostics;
using System.Text.Json;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Infrastructure.Library;

internal sealed class AsyncPauseGate
{
    private readonly object _gate = new();
    private TaskCompletionSource _resume = Completed();
    private bool _paused;

    public bool IsPaused
    {
        get
        {
            lock (_gate) return _paused;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_paused) return;
            _paused = true;
            _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource? resume = null;
        lock (_gate)
        {
            if (!_paused) return;
            _paused = false;
            resume = _resume;
        }
        resume.TrySetResult();
    }

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            return _paused
                ? _resume.Task.WaitAsync(cancellationToken)
                : Task.CompletedTask;
    }

    private static TaskCompletionSource Completed()
    {
        var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        value.SetResult();
        return value;
    }
}

internal sealed class ScanProgressCoalescer(
    Action<ScanProgress> publish,
    TimeSpan minimumInterval,
    Func<ScanLifecycleState> state)
{
    private readonly object _gate = new();
    private long _lastPublished = Stopwatch.GetTimestamp();
    private ScanProgress? _pending;

    public void Report(ScanProgress progress, bool force = false)
    {
        ScanProgress? value = null;
        lock (_gate)
        {
            _pending = progress.IsComplete ? progress : progress with { State = state() };
            var elapsed = Stopwatch.GetElapsedTime(_lastPublished);
            if (!force && elapsed < minimumInterval) return;
            value = _pending;
            _pending = null;
            _lastPublished = Stopwatch.GetTimestamp();
        }
        publish(value);
    }

    public void Flush()
    {
        ScanProgress? value = null;
        lock (_gate)
        {
            if (_pending is null) return;
            value = _pending.IsComplete ? _pending : _pending with { State = state() };
            _pending = null;
            _lastPublished = Stopwatch.GetTimestamp();
        }
        publish(value);
    }
}

internal static class ScanConcurrencyPolicy
{
    public static LibrarySourceKind Classify(string root)
    {
        if (root.StartsWith(@"\\", StringComparison.Ordinal)
            || Uri.TryCreate(root, UriKind.Absolute, out var uri)
            && uri.IsUnc)
            return LibrarySourceKind.Network;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            return drive.DriveType switch
            {
                DriveType.Network => LibrarySourceKind.Network,
                DriveType.Removable or DriveType.CDRom => LibrarySourceKind.Removable,
                DriveType.Fixed => LibrarySourceKind.Local,
                _ => LibrarySourceKind.Unknown
            };
        }
        catch
        {
            return LibrarySourceKind.Unknown;
        }
    }

    public static int MetadataWorkers(LibrarySourceKind kind, int processorCount) => kind switch
    {
        LibrarySourceKind.Local => Math.Clamp(processorCount, 2, 8),
        LibrarySourceKind.Network => 2,
        LibrarySourceKind.Removable => 2,
        _ => 2
    };
}

internal sealed class ScanCheckpointStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public async Task<ScanCheckpoint?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.ScanCheckpointFile)) return null;
        try
        {
            await using var stream = new FileStream(
                paths.ScanCheckpointFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                8 * 1024,
                true);
            return await JsonSerializer.DeserializeAsync<ScanCheckpoint>(
                stream,
                Json,
                cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task SaveAsync(ScanCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        var temporary = paths.ScanCheckpointFile + ".tmp";
        await using (var stream = new FileStream(
                         temporary,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         8 * 1024,
                         true))
        {
            await JsonSerializer.SerializeAsync(stream, checkpoint, Json, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporary, paths.ScanCheckpointFile, true);
    }

    public void Complete()
    {
        try
        {
            if (File.Exists(paths.ScanCheckpointFile))
                File.Delete(paths.ScanCheckpointFile);
            var temporary = paths.ScanCheckpointFile + ".tmp";
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed record ScanCheckpoint(
    int Version,
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> Excluded,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    int Discovered,
    int Processed,
    string? LastPath)
{
    public const int CurrentVersion = 1;

    public bool Matches(IReadOnlyList<string> roots, IReadOnlyList<string> excluded) =>
        Version == CurrentVersion
        && Roots.SequenceEqual(roots, StringComparer.OrdinalIgnoreCase)
        && Excluded.SequenceEqual(excluded, StringComparer.OrdinalIgnoreCase);
}
