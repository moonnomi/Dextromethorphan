using System.Security.Cryptography;
using System.Text;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Infrastructure.Library;

public sealed class ArtworkCache(AppPaths paths, ISettingsService settings) : IArtworkCache
{
    private static readonly string[] StoredExtensions =
        [".jpg", ".png", ".gif", ".bmp", ".tiff", ".webp", ".art"];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;

    public async Task<string?> StoreAsync(string mediaPath, DateTimeOffset modifiedAt, ReadOnlyMemory<byte> artwork, CancellationToken cancellationToken = default)
    {
        if (artwork.IsEmpty) return null;
        if (!ArtworkImageInspector.TryInspect(artwork.Span, out var image, out _)) return null;
        paths.EnsureCreated();
        var target = CachePath(mediaPath, modifiedAt, artwork.Span, image.Extension);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(target))
            {
                var temporary = target + ".tmp";
                await File.WriteAllBytesAsync(temporary, artwork.ToArray(), cancellationToken);
                File.Move(temporary, target, true);
            }
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
        }
        finally { _gate.Release(); }
        if (DateTimeOffset.UtcNow >= _nextPrune)
        {
            _nextPrune = DateTimeOffset.UtcNow.AddMinutes(1);
            await PruneAsync(cancellationToken);
        }
        return target;
    }

    public async Task<string?> GetOrCreateAsync(string mediaPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(mediaPath)) return null;
        var external = await Task.Run(
            () => ExternalArtworkResolver.FindPreferredForMedia(mediaPath, cancellationToken),
            cancellationToken);
        if (external is not null) return external;
        var modifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(mediaPath), TimeSpan.Zero);
        var existing = FindExistingCachePath(mediaPath, modifiedAt);
        if (existing is not null)
        {
            try { File.SetLastWriteTimeUtc(existing, DateTime.UtcNow); } catch (IOException) { }
            return existing;
        }
        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var file = TagLib.File.Create(mediaPath);
                var bytes = file.Tag.Pictures.FirstOrDefault()?.Data.Data;
                return bytes is { Length: > 0 } ? await StoreAsync(mediaPath, modifiedAt, bytes, cancellationToken) : null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { return null; }
        }, cancellationToken);
    }

    public async Task PruneAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            DeleteExpiredTemporaryFiles(cancellationToken);
            var files = new DirectoryInfo(paths.ArtworkCache)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(x => !x.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .ThenBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var limit = settings.Current.ArtworkCacheMegabytes * 1024L * 1024L;
            long retained = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                retained += file.Length;
                if (retained <= limit) continue;
                try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<ArtworkCacheStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            long bytes = 0;
            var originals = 0;
            var thumbnails = 0;
            var temporary = 0;
            foreach (var file in new DirectoryInfo(paths.ArtworkCache).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    temporary++;
                    continue;
                }
                bytes += file.Length;
                if (file.DirectoryName?.Equals(Path.Combine(paths.ArtworkCache, "thumbnails"), StringComparison.OrdinalIgnoreCase) == true)
                    thumbnails++;
                else
                    originals++;
            }
            return new ArtworkCacheStats(bytes, originals, thumbnails, temporary);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var file in new DirectoryInfo(paths.ArtworkCache).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { file.Delete(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _nextPrune = DateTimeOffset.MinValue;
        }
        finally { _gate.Release(); }
    }

    private void DeleteExpiredTemporaryFiles(CancellationToken cancellationToken)
    {
        foreach (var temporary in new DirectoryInfo(paths.ArtworkCache).EnumerateFiles("*.tmp", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (temporary.LastWriteTimeUtc >= DateTime.UtcNow.AddHours(-1)) continue;
            try { temporary.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private string? FindExistingCachePath(string mediaPath, DateTimeOffset modifiedAt)
    {
        var prefix = CachePathPrefix(mediaPath, modifiedAt);
        var legacy = StoredExtensions
            .Select(extension => prefix + extension)
            .FirstOrDefault(File.Exists);
        if (legacy is not null) return legacy;
        return new DirectoryInfo(paths.ArtworkCache)
            .EnumerateFiles(Path.GetFileName(prefix) + "-*", SearchOption.TopDirectoryOnly)
            .Where(file => StoredExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    private string CachePath(
        string mediaPath,
        DateTimeOffset modifiedAt,
        ReadOnlySpan<byte> artwork,
        string extension)
    {
        var contentHash = Convert.ToHexString(SHA256.HashData(artwork)).ToLowerInvariant();
        return $"{CachePathPrefix(mediaPath, modifiedAt)}-{contentHash}{extension}";
    }

    private string CachePathPrefix(string mediaPath, DateTimeOffset modifiedAt)
    {
        var identity = $"{Path.GetFullPath(mediaPath).ToUpperInvariant()}|{modifiedAt.UtcTicks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(paths.ArtworkCache, hash);
    }
}
