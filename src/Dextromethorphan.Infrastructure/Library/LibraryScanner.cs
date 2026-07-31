using System.Collections.Concurrent;
using System.Threading.Channels;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Infrastructure.Library;

public sealed class LibraryScanner(
    ILibraryRepository repository,
    ITrackMetadataReader metadataReader,
    IArtworkCache artworkCache,
    AppPaths paths) : ILibraryScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".m4a", ".mp4", ".alac", ".wav", ".wave", ".aif", ".aiff", ".dsf", ".dff", ".ogg", ".opus", ".aac", ".wma"
    };
    private static readonly HashSet<string> ArtworkExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp"
    };
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounce = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateGate = new();
    private readonly AsyncPauseGate _pause = new();
    private readonly ScanCheckpointStore _checkpoints = new(paths);
    private readonly Dictionary<string, LibrarySourceStatus> _sourceStatuses = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _activeScan;
    private ScanLifecycleState _state;
    private int _scanning;

    public bool IsScanning => Volatile.Read(ref _scanning) == 1;
    public ScanLifecycleState State
    {
        get
        {
            lock (_stateGate) return _state;
        }
    }
    public IReadOnlyList<LibrarySourceStatus> SourceStatuses
    {
        get
        {
            lock (_stateGate)
                return _sourceStatuses.Values.OrderBy(x => x.Root, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
    public event EventHandler<ScanProgress>? ProgressChanged;
    public event EventHandler? SourceStatusesChanged;
    public event EventHandler<LibraryFilesChangedEventArgs>? FilesChanged;
    public event Action<string>? ArtworkChanged;

    public async Task ScanAsync(IEnumerable<string> roots, IEnumerable<string>? excluded = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) throw new InvalidOperationException("A library scan is already running.");
        var configuredRoots = CollapseRoots(roots);
        var exclusions = NormalizePaths(excluded ?? []);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_stateGate)
        {
            _activeScan = linked;
            _state = ScanLifecycleState.Running;
        }
        _pause.Resume();
        var token = linked.Token;
        try
        {
            var validRoots = configuredRoots.Where(Directory.Exists).ToArray();
            foreach (var root in configuredRoots)
                UpdateSourceStatus(root, Directory.Exists(root), IsWatched(root), null, Directory.Exists(root) ? null : "Source is offline.");

            var priorCheckpoint = await _checkpoints.LoadAsync(token);
            var resumed = priorCheckpoint?.Matches(configuredRoots, exclusions) == true;
            var startedAt = resumed ? priorCheckpoint!.StartedAt : DateTimeOffset.UtcNow;
            var processed = 0;
            var discovered = 0;
            var added = 0;
            var updated = 0;
            var failed = 0;
            string? lastPath = null;
            var fileIndex = await repository.GetFileIndexAsync(token);
            foreach (var offlineRoot in configuredRoots.Except(
                         validRoots,
                         StringComparer.OrdinalIgnoreCase))
            {
                var trackCount = await repository.CountUnderRootAsync(
                    offlineRoot,
                    token);
                UpdateSourceStatus(
                    offlineRoot,
                    false,
                    IsWatched(offlineRoot),
                    null,
                    "Source is offline.",
                    trackCount);
            }
            var directoryArtwork = new ConcurrentDictionary<string, ExternalArtworkSelection>(
                StringComparer.OrdinalIgnoreCase);
            var progress = new ScanProgressCoalescer(
                value => ProgressChanged?.Invoke(this, value),
                TimeSpan.FromMilliseconds(100),
                () => State);
            void Report(string? path = null, bool force = false)
            {
                if (path is not null) Volatile.Write(ref lastPath, path);
                progress.Report(
                    new ScanProgress(
                        Volatile.Read(ref discovered),
                        Volatile.Read(ref processed),
                        Volatile.Read(ref added),
                        Volatile.Read(ref updated),
                        Volatile.Read(ref failed),
                        Volatile.Read(ref lastPath),
                        false,
                        State,
                        resumed),
                    force);
            }
            Report(force: true);
            await _checkpoints.SaveAsync(
                new ScanCheckpoint(
                    ScanCheckpoint.CurrentVersion,
                    configuredRoots,
                    exclusions,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    0,
                    0,
                    null),
                token);

            using var checkpointLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            var checkpointTask = PersistCheckpointsAsync(
                configuredRoots,
                exclusions,
                startedAt,
                () => Volatile.Read(ref discovered),
                () => Volatile.Read(ref processed),
                () => Volatile.Read(ref lastPath),
                checkpointLifetime.Token);

            var pending = Channel.CreateBounded<(Track Track, bool IsNew)>(new BoundedChannelOptions(500)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            var writerTask = Task.Run(async () =>
            {
                var batch = new List<(Track Track, bool IsNew)>(250);
                async Task FlushAsync()
                {
                    if (batch.Count == 0) return;
                    try
                    {
                        await repository.UpsertBatchAsync(batch.Select(x => x.Track).ToArray(), token);
                        Interlocked.Add(ref added, batch.Count(x => x.IsNew));
                        Interlocked.Add(ref updated, batch.Count(x => !x.IsNew));
                        Interlocked.Add(ref processed, batch.Count);
                    }
                    catch
                    {
                        foreach (var item in batch)
                        {
                            try
                            {
                                await repository.UpsertAsync(item.Track, token);
                                if (item.IsNew) Interlocked.Increment(ref added); else Interlocked.Increment(ref updated);
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                            catch { Interlocked.Increment(ref failed); }
                            Interlocked.Increment(ref processed);
                        }
                    }
                    Report(batch[^1].Track.Path);
                    batch.Clear();
                }

                await foreach (var item in pending.Reader.ReadAllAsync(token))
                {
                    await _pause.WaitAsync(token);
                    batch.Add(item);
                    if (batch.Count >= 250) await FlushAsync();
                }
                await FlushAsync();
            }, token);

            var files = Channel.CreateBounded<SourceFile>(new BoundedChannelOptions(1_024)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            var incompleteRoots = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var producerTask = Task.Run(async () =>
            {
                var seenFiles = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var root in validRoots)
                    {
                        foreach (var path in EnumerateFiles(
                                     root,
                                     exclusions,
                                     message => incompleteRoots.TryAdd(root, message)))
                        {
                            if (!seenFiles.Add(path)) continue;
                            await _pause.WaitAsync(token);
                            await files.Writer.WriteAsync(new SourceFile(root, path), token);
                            Interlocked.Increment(ref discovered);
                            Report(path);
                        }
                    }
                    files.Writer.TryComplete();
                }
                catch (Exception exception)
                {
                    files.Writer.TryComplete(exception);
                    throw;
                }
            }, token);
            var sourceLimiters = validRoots.ToDictionary(
                root => root,
                root => new SemaphoreSlim(
                    ScanConcurrencyPolicy.MetadataWorkers(
                        ScanConcurrencyPolicy.Classify(root),
                        Environment.ProcessorCount)),
                StringComparer.OrdinalIgnoreCase);
            var workerCount = Math.Clamp(Environment.ProcessorCount, 2, 8);
            var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
            {
                await foreach (var item in files.Reader.ReadAllAsync(token))
                {
                    await _pause.WaitAsync(token);
                    var limiter = sourceLimiters[item.Root];
                    await limiter.WaitAsync(token);
                    try
                    {
                        await ProcessFileAsync(
                            item.Path,
                            fileIndex,
                            directoryArtwork,
                            pending.Writer,
                            () =>
                            {
                                Interlocked.Increment(ref processed);
                                Report(item.Path);
                            },
                            () =>
                            {
                                Interlocked.Increment(ref failed);
                                Interlocked.Increment(ref processed);
                                Report(item.Path);
                            },
                            token);
                    }
                    finally
                    {
                        limiter.Release();
                    }
                }
            }, token)).ToArray();

            try
            {
                await producerTask;
                await Task.WhenAll(workers);
            }
            finally
            {
                pending.Writer.TryComplete();
                foreach (var limiter in sourceLimiters.Values) limiter.Dispose();
            }
            await writerTask;
            var fullyEnumeratedRoots = validRoots
                .Where(root => !incompleteRoots.ContainsKey(root))
                .ToArray();
            await repository.RemoveMissingAsync(fullyEnumeratedRoots, token);
            foreach (var root in validRoots)
            {
                var error = incompleteRoots.TryGetValue(root, out var value) ? value : null;
                var trackCount = await repository.CountUnderRootAsync(root, token);
                UpdateSourceStatus(
                    root,
                    true,
                    IsWatched(root),
                    error is null ? DateTimeOffset.UtcNow : null,
                    error,
                    trackCount);
            }
            checkpointLifetime.Cancel();
            try { await checkpointTask; } catch (OperationCanceledException) { }
            _checkpoints.Complete();
            progress.Report(
                new ScanProgress(
                    discovered,
                    processed,
                    added,
                    updated,
                    failed,
                    null,
                    true,
                    ScanLifecycleState.Idle,
                    resumed),
                force: true);
            FilesChanged?.Invoke(
                this,
                new LibraryFilesChangedEventArgs(
                    [new LibraryFileChange(
                        LibraryFileChangeKind.FullRefresh,
                        string.Empty)]));
        }
        catch
        {
            linked.Cancel();
            throw;
        }
        finally
        {
            _pause.Resume();
            lock (_stateGate)
            {
                _activeScan = null;
                _state = ScanLifecycleState.Idle;
            }
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    public void Pause()
    {
        lock (_stateGate)
        {
            if (_state != ScanLifecycleState.Running) return;
            _state = ScanLifecycleState.Paused;
        }
        _pause.Pause();
    }

    public void Resume()
    {
        lock (_stateGate)
        {
            if (_state != ScanLifecycleState.Paused) return;
            _state = ScanLifecycleState.Running;
        }
        _pause.Resume();
    }

    public void Cancel()
    {
        CancellationTokenSource? active;
        lock (_stateGate)
        {
            if (_state is ScanLifecycleState.Idle or ScanLifecycleState.Cancelling) return;
            _state = ScanLifecycleState.Cancelling;
            active = _activeScan;
        }
        _pause.Resume();
        active?.Cancel();
    }

    public void StartWatching(IEnumerable<string> roots)
    {
        StopWatching();
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = true
                };
                watcher.Created += OnChanged;
                watcher.Changed += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += (_, _) => QueueRootRescan(root);
                _watchers.Add(watcher);
                UpdateSourceStatus(root, true, true, null, null);
            }
            catch (IOException exception)
            {
                UpdateSourceStatus(root, Directory.Exists(root), false, null, exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                UpdateSourceStatus(root, Directory.Exists(root), false, null, exception.Message);
            }
        }
    }

    public void StopWatching()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        lock (_stateGate)
        {
            foreach (var (root, status) in _sourceStatuses.ToArray())
                _sourceStatuses[root] = status with { IsWatching = false };
        }
        SourceStatusesChanged?.Invoke(this, EventArgs.Empty);
        foreach (var source in _debounce.Values) { source.Cancel(); source.Dispose(); }
        _debounce.Clear();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (SupportedExtensions.Contains(Path.GetExtension(e.FullPath))) QueueFileUpdate(e.FullPath);
        else if (ArtworkExtensions.Contains(Path.GetExtension(e.FullPath))) ArtworkChanged?.Invoke(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (SupportedExtensions.Contains(Path.GetExtension(e.FullPath)))
            QueueRenameUpdate(e.OldFullPath, e.FullPath);
        else if (SupportedExtensions.Contains(Path.GetExtension(e.OldFullPath)))
            QueueFileUpdate(e.OldFullPath);
        else if (ArtworkExtensions.Contains(Path.GetExtension(e.FullPath))) ArtworkChanged?.Invoke(e.FullPath);
        if (ArtworkExtensions.Contains(Path.GetExtension(e.OldFullPath))) ArtworkChanged?.Invoke(e.OldFullPath);
    }

    private void QueueFileUpdate(string path)
    {
        var source = new CancellationTokenSource();
        _debounce.AddOrUpdate(path, source, (_, old) => { old.Cancel(); old.Dispose(); return source; });
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(800, source.Token);
                if (File.Exists(path))
                {
                    var preferredArtwork = ExternalArtworkResolver.FindPreferredForMedia(
                        path,
                        source.Token);
                    var track = await CacheArtworkAsync(
                        await metadataReader.ReadAsync(path, source.Token),
                        preferredArtwork,
                        source.Token);
                    await repository.UpsertAsync(track, source.Token);
                    FilesChanged?.Invoke(
                        this,
                        new LibraryFilesChangedEventArgs(
                            [new LibraryFileChange(
                                LibraryFileChangeKind.AddedOrUpdated,
                                path)]));
                }
                else
                {
                    await repository.MarkMissingAsync([path], source.Token);
                    FilesChanged?.Invoke(
                        this,
                        new LibraryFilesChangedEventArgs(
                            [new LibraryFileChange(
                                LibraryFileChangeKind.Missing,
                                path)]));
                }
                await RefreshContainingSourceStatusAsync(path, source.Token);
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { _debounce.TryRemove(new KeyValuePair<string, CancellationTokenSource>(path, source)); source.Dispose(); }
        });
    }

    private void QueueRenameUpdate(string previousPath, string path)
    {
        var key = previousPath + "\0" + path;
        var source = new CancellationTokenSource();
        _debounce.AddOrUpdate(
            key,
            source,
            (_, old) =>
            {
                old.Cancel();
                old.Dispose();
                return source;
            });
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, source.Token);
                if (!File.Exists(path))
                {
                    await repository.MarkMissingAsync([previousPath], source.Token);
                    FilesChanged?.Invoke(
                        this,
                        new LibraryFilesChangedEventArgs(
                            [new LibraryFileChange(
                                LibraryFileChangeKind.Missing,
                                previousPath)]));
                    return;
                }
                var preferredArtwork = ExternalArtworkResolver.FindPreferredForMedia(
                    path,
                    source.Token);
                var replacement = await CacheArtworkAsync(
                    await metadataReader.ReadAsync(path, source.Token),
                    preferredArtwork,
                    source.Token);
                await repository.RelinkAsync(
                    previousPath,
                    replacement,
                    source.Token);
                FilesChanged?.Invoke(
                    this,
                    new LibraryFilesChangedEventArgs(
                        [new LibraryFileChange(
                            LibraryFileChangeKind.Relinked,
                            path,
                            previousPath)]));
                await RefreshContainingSourceStatusAsync(path, source.Token);
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                _debounce.TryRemove(
                    new KeyValuePair<string, CancellationTokenSource>(
                        key,
                        source));
                source.Dispose();
            }
        });
    }

    private void QueueRootRescan(string root) => _ = Task.Run(async () => { try { await ScanAsync([root]); } catch { } });

    private async Task ProcessFileAsync(
        string path,
        IReadOnlyDictionary<string, LibraryFileStamp> fileIndex,
        ConcurrentDictionary<string, ExternalArtworkSelection> directoryArtwork,
        ChannelWriter<(Track Track, bool IsNew)> pending,
        Action unchanged,
        Action failed,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            fileIndex.TryGetValue(path, out var existing);
            var preferredArtwork = PreferredArtwork(path, directoryArtwork, cancellationToken);
            if (existing is not null
                && existing.ModifiedAt.ToUnixTimeMilliseconds()
                    == new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds()
                && existing.Size == info.Length)
            {
                if (preferredArtwork is not null
                    && !preferredArtwork.Equals(existing.ArtworkPath, StringComparison.OrdinalIgnoreCase))
                {
                    var persisted = await repository.GetByPathAsync(path, cancellationToken);
                    if (persisted is not null)
                    {
                        await pending.WriteAsync(
                            (persisted with { ArtworkPath = preferredArtwork, Artwork = null }, false),
                            cancellationToken);
                        return;
                    }
                }
                else if (preferredArtwork is null
                         && existing.ArtworkPath is { Length: > 0 } staleArtwork
                         && !File.Exists(staleArtwork))
                {
                    var refreshed = await CacheArtworkAsync(
                        await metadataReader.ReadAsync(path, cancellationToken),
                        preferredArtwork,
                        cancellationToken);
                    await pending.WriteAsync((refreshed, false), cancellationToken);
                    return;
                }
                unchanged();
                return;
            }
            var track = await CacheArtworkAsync(
                await metadataReader.ReadAsync(path, cancellationToken),
                preferredArtwork,
                cancellationToken);
            await pending.WriteAsync((track, existing is null), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            failed();
        }
    }

    private async Task PersistCheckpointsAsync(
        IReadOnlyList<string> roots,
        IReadOnlyList<string> excluded,
        DateTimeOffset startedAt,
        Func<int> discovered,
        Func<int> processed,
        Func<string?> lastPath,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            await _checkpoints.SaveAsync(
                new ScanCheckpoint(
                    ScanCheckpoint.CurrentVersion,
                    roots,
                    excluded,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    discovered(),
                    processed(),
                    lastPath()),
                cancellationToken);
        }
    }

    private void UpdateSourceStatus(
        string root,
        bool online,
        bool watching,
        DateTimeOffset? successfulScan,
        string? error,
        long? trackCount = null)
    {
        var fullRoot = Path.GetFullPath(root);
        lock (_stateGate)
        {
            _sourceStatuses.TryGetValue(fullRoot, out var existing);
            _sourceStatuses[fullRoot] = new LibrarySourceStatus(
                fullRoot,
                ScanConcurrencyPolicy.Classify(fullRoot),
                online,
                watching,
                successfulScan ?? existing?.LastSuccessfulScan,
                error,
                trackCount ?? existing?.TrackCount ?? 0);
        }
        SourceStatusesChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshContainingSourceStatusAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string[] roots;
        lock (_stateGate)
            roots = _sourceStatuses.Keys
                .Where(root => IsWithin(path, root))
                .ToArray();
        foreach (var root in roots)
        {
            var count = await repository.CountUnderRootAsync(
                root,
                cancellationToken);
            UpdateSourceStatus(
                root,
                Directory.Exists(root),
                IsWatched(root),
                null,
                Directory.Exists(root) ? null : "Source is offline.",
                count);
        }
    }

    private bool IsWatched(string root) =>
        _watchers.Any(watcher => watcher.Path.Equals(root, StringComparison.OrdinalIgnoreCase));

    private async Task<Track> CacheArtworkAsync(
        Track track,
        string? preferredArtwork,
        CancellationToken cancellationToken)
    {
        if (preferredArtwork is not null)
            return track with { ArtworkPath = preferredArtwork, Artwork = null };
        if (track.Artwork is not { Length: > 0 }) return track;
        var cached = await artworkCache.StoreAsync(track.Path, track.FileModifiedAt, track.Artwork, cancellationToken);
        return track with { ArtworkPath = cached, Artwork = null };
    }

    private static string? PreferredArtwork(
        string mediaPath,
        ConcurrentDictionary<string, ExternalArtworkSelection> selections,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(mediaPath) ?? string.Empty;
        return selections.GetOrAdd(
            directory,
            path => new ExternalArtworkSelection(
                ExternalArtworkResolver.FindPreferredInDirectory(path, cancellationToken))).Path;
    }

    private static IEnumerable<string> EnumerateFiles(
        string root,
        IReadOnlyList<string> exclusions,
        Action<string> reportIncomplete)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (exclusions.Any(x => IsWithin(directory, x))) continue;
            string canonical;
            try
            {
                canonical = CanonicalPath.Normalize(directory);
                if (!visited.Add(canonical)) continue;
                var attributes = File.GetAttributes(canonical);
                if ((attributes & FileAttributes.ReparsePoint) != 0 && !canonical.Equals(root, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                reportIncomplete($"{directory}: {exception.Message}");
                continue;
            }

            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(canonical);
                directories = Directory.GetDirectories(canonical);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                reportIncomplete($"{directory}: {exception.Message}");
                continue;
            }
            foreach (var file in files)
                if (SupportedExtensions.Contains(Path.GetExtension(file)))
                    yield return CanonicalPath.Normalize(file);
            foreach (var child in directories)
                pending.Push(child);
        }
    }

    private static string[] CollapseRoots(IEnumerable<string> roots)
    {
        var normalized = NormalizePaths(roots)
            .OrderBy(path => path.Length)
            .ToArray();
        var result = new List<string>(normalized.Length);
        foreach (var root in normalized)
        {
            if (result.Any(parent => IsWithin(root, parent))) continue;
            result.Add(root);
        }
        return result.ToArray();
    }

    private static string[] NormalizePaths(IEnumerable<string> paths) =>
        paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(CanonicalPath.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative);
    }

    public ValueTask DisposeAsync()
    {
        Cancel();
        StopWatching();
        return ValueTask.CompletedTask;
    }

    private readonly record struct SourceFile(string Root, string Path);
    private readonly record struct ExternalArtworkSelection(string? Path);
}
