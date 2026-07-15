using System.Collections.Concurrent;
using System.Threading.Channels;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Infrastructure.Library;

public sealed class LibraryScanner(ILibraryRepository repository, ITrackMetadataReader metadataReader, IArtworkCache artworkCache) : ILibraryScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".m4a", ".mp4", ".alac", ".wav", ".wave", ".aif", ".aiff", ".dsf", ".dff", ".ogg", ".opus", ".aac", ".wma"
    };
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounce = new(StringComparer.OrdinalIgnoreCase);
    private int _scanning;

    public bool IsScanning => Volatile.Read(ref _scanning) == 1;
    public event EventHandler<ScanProgress>? ProgressChanged;

    public async Task ScanAsync(IEnumerable<string> roots, IEnumerable<string>? excluded = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) throw new InvalidOperationException("A library scan is already running.");
        try
        {
            var validRoots = roots.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var exclusions = (excluded ?? []).Select(Path.GetFullPath).ToArray();
            var files = EnumerateFiles(validRoots, exclusions).ToArray();
            var processed = 0; var added = 0; var updated = 0; var failed = 0;
            var fileIndex = await repository.GetFileIndexAsync(cancellationToken);
            ProgressChanged?.Invoke(this, new ScanProgress(files.Length, 0, 0, 0, 0, null, false));

            var pending = Channel.CreateBounded<(Track Track, bool IsNew)>(new BoundedChannelOptions(500)
            {
                SingleReader = true,
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
                        await repository.UpsertBatchAsync(batch.Select(x => x.Track).ToArray(), cancellationToken);
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
                                await repository.UpsertAsync(item.Track, cancellationToken);
                                if (item.IsNew) Interlocked.Increment(ref added); else Interlocked.Increment(ref updated);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch { Interlocked.Increment(ref failed); }
                            Interlocked.Increment(ref processed);
                        }
                    }
                    ProgressChanged?.Invoke(this, new ScanProgress(files.Length, processed, added, updated, failed, batch[^1].Track.Path, false));
                    batch.Clear();
                }

                await foreach (var item in pending.Reader.ReadAllAsync(cancellationToken))
                {
                    batch.Add(item);
                    if (batch.Count >= 250) await FlushAsync();
                }
                await FlushAsync();
            }, cancellationToken);

            try
            {
                await Parallel.ForEachAsync(files, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8) }, async (path, ct) =>
                {
                    try
                    {
                        var info = new FileInfo(path);
                        fileIndex.TryGetValue(path, out var existing);
                        if (existing is not null && existing.ModifiedAt.UtcDateTime == info.LastWriteTimeUtc && existing.Size == info.Length)
                        {
                            Interlocked.Increment(ref processed);
                            ProgressChanged?.Invoke(this, new ScanProgress(files.Length, processed, added, updated, failed, path, false));
                            return;
                        }
                        var track = await CacheArtworkAsync(await metadataReader.ReadAsync(path, ct), ct);
                        await pending.Writer.WriteAsync((track, existing is null), ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch
                    {
                        Interlocked.Increment(ref failed);
                        Interlocked.Increment(ref processed);
                        ProgressChanged?.Invoke(this, new ScanProgress(files.Length, processed, added, updated, failed, path, false));
                    }
                });
            }
            finally
            {
                pending.Writer.TryComplete();
            }
            await writerTask;
            await repository.RemoveMissingAsync(validRoots, cancellationToken);
            ProgressChanged?.Invoke(this, new ScanProgress(files.Length, processed, added, updated, failed, null, true));
        }
        finally { Interlocked.Exchange(ref _scanning, 0); }
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
                watcher.Renamed += OnRenamed;
                watcher.Error += (_, _) => QueueRootRescan(root);
                _watchers.Add(watcher);
            }
            catch (IOException) { }
        }
    }

    public void StopWatching()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        foreach (var source in _debounce.Values) { source.Cancel(); source.Dispose(); }
        _debounce.Clear();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (SupportedExtensions.Contains(Path.GetExtension(e.FullPath))) QueueFileUpdate(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (SupportedExtensions.Contains(Path.GetExtension(e.FullPath))) QueueFileUpdate(e.FullPath);
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
                if (File.Exists(path)) await repository.UpsertAsync(await CacheArtworkAsync(await metadataReader.ReadAsync(path, source.Token), source.Token), source.Token);
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { _debounce.TryRemove(new KeyValuePair<string, CancellationTokenSource>(path, source)); source.Dispose(); }
        });
    }

    private void QueueRootRescan(string root) => _ = Task.Run(async () => { try { await ScanAsync([root]); } catch { } });

    private async Task<Track> CacheArtworkAsync(Track track, CancellationToken cancellationToken)
    {
        if (track.Artwork is not { Length: > 0 }) return track;
        var cached = await artworkCache.StoreAsync(track.Path, track.FileModifiedAt, track.Artwork, cancellationToken);
        return track with { ArtworkPath = cached, Artwork = null };
    }

    private static IEnumerable<string> EnumerateFiles(IEnumerable<string> roots, IReadOnlyList<string> exclusions)
    {
        foreach (var root in roots)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                if (exclusions.Any(x => IsWithin(directory, x))) continue;
                IEnumerable<string> files;
                IEnumerable<string> directories;
                try { files = Directory.EnumerateFiles(directory); directories = Directory.EnumerateDirectories(directory); }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException) { continue; }
                foreach (var file in files) if (SupportedExtensions.Contains(Path.GetExtension(file))) yield return file;
                foreach (var child in directories) pending.Push(child);
            }
        }
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative);
    }

    public ValueTask DisposeAsync() { StopWatching(); return ValueTask.CompletedTask; }
}
