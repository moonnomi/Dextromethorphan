using System.Security.Cryptography;
using System.Text;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Infrastructure.Library;

public sealed class ArtworkCache(AppPaths paths, ISettingsService settings) : IArtworkCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;

    public async Task<string?> StoreAsync(string mediaPath, DateTimeOffset modifiedAt, ReadOnlyMemory<byte> artwork, CancellationToken cancellationToken = default)
    {
        if (artwork.IsEmpty) return null;
        paths.EnsureCreated();
        var target = CachePath(mediaPath, modifiedAt);
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
        var modifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(mediaPath), TimeSpan.Zero);
        var target = CachePath(mediaPath, modifiedAt);
        if (File.Exists(target))
        {
            try { File.SetLastWriteTimeUtc(target, DateTime.UtcNow); } catch (IOException) { }
            return target;
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
            var files = new DirectoryInfo(paths.ArtworkCache)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(x => !x.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.LastWriteTimeUtc).ToArray();
            var limit = settings.Current.ArtworkCacheMegabytes * 1024L * 1024L;
            long retained = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                retained += file.Length;
                if (retained <= limit) continue;
                try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            foreach (var temporary in new DirectoryInfo(paths.ArtworkCache).EnumerateFiles("*.tmp", SearchOption.AllDirectories))
            {
                if (temporary.LastWriteTimeUtc >= DateTime.UtcNow.AddHours(-1)) continue;
                try { temporary.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        finally { _gate.Release(); }
    }

    private string CachePath(string mediaPath, DateTimeOffset modifiedAt)
    {
        var identity = $"{Path.GetFullPath(mediaPath).ToUpperInvariant()}|{modifiedAt.UtcTicks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(paths.ArtworkCache, hash + ".art");
    }
}
