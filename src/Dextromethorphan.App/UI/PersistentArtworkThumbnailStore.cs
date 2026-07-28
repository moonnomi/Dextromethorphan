using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dextromethorphan.App.Diagnostics;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.App.UI;

public sealed class PersistentArtworkThumbnailStore
{
    private const string ThumbnailDirectoryName = "thumbnails";
    private readonly AppPaths _paths;
    private readonly DeveloperDiagnostics _diagnostics;
    private readonly ConcurrentDictionary<string, object> _generationGates = new(StringComparer.Ordinal);
    private long _requests;
    private long _hits;
    private long _sourceDecodes;
    private long _variantsGenerated;
    private long _failures;

    public PersistentArtworkThumbnailStore(AppPaths paths, DeveloperDiagnostics diagnostics)
    {
        _paths = paths;
        _diagnostics = diagnostics;
    }

    internal ArtworkThumbnailResult? GetOrCreate(
        string sourcePath,
        int requestedWidth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _requests);
        if (!TryDescribeSource(sourcePath, out var source)) return null;

        var variant = ArtworkThumbnailVariant.ForRequestedWidth(requestedWidth);
        var target = VariantPath(source.Identity, variant);
        if (TryUseExisting(target))
        {
            Interlocked.Increment(ref _hits);
            return new ArtworkThumbnailResult(target, variant, true);
        }

        var generationGate = _generationGates.GetOrAdd(source.Identity, static _ => new object());
        try
        {
            lock (generationGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryUseExisting(target))
                {
                    Interlocked.Increment(ref _hits);
                    return new ArtworkThumbnailResult(target, variant, true);
                }

                GenerateVariant(source, variant, cancellationToken);
                return File.Exists(target)
                    ? new ArtworkThumbnailResult(target, variant, false)
                    : null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or InvalidOperationException or FileFormatException)
        {
            Interlocked.Increment(ref _failures);
            _diagnostics.Error("artwork", "thumbnail.variant-generation", exception, Data(sourcePath, variant));
            return null;
        }
        finally
        {
            _generationGates.TryRemove(
                new KeyValuePair<string, object>(source.Identity, generationGate));
        }
    }

    internal PersistentArtworkThumbnailMetrics GetMetrics() => new(
        Interlocked.Read(ref _requests),
        Interlocked.Read(ref _hits),
        Interlocked.Read(ref _sourceDecodes),
        Interlocked.Read(ref _variantsGenerated),
        Interlocked.Read(ref _failures));

    private void GenerateVariant(
        ArtworkSource source,
        ArtworkThumbnailVariant variant,
        CancellationToken cancellationToken)
    {
        _paths.EnsureCreated();
        Directory.CreateDirectory(ThumbnailDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var timer = Stopwatch.StartNew();
        Exception? error = null;
        try
        {
            using var stream = File.Open(source.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoded = new BitmapImage();
            decoded.BeginInit();
            decoded.CacheOption = BitmapCacheOption.OnLoad;
            decoded.CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
            decoded.DecodePixelWidth = variant.PixelWidth;
            decoded.StreamSource = stream;
            decoded.EndInit();
            decoded.Freeze();
            Interlocked.Increment(ref _sourceDecodes);

            cancellationToken.ThrowIfCancellationRequested();
            var target = VariantPath(source.Identity, variant);
            if (File.Exists(target)) return;
            WriteVariant(decoded, target, cancellationToken);
            Interlocked.Increment(ref _variantsGenerated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or InvalidOperationException or FileFormatException)
        {
            error = exception;
            throw;
        }
        finally
        {
            _diagnostics.RecordDuration(
                "artwork",
                "thumbnail.generate-persistent-variant",
                timer.Elapsed,
                new Dictionary<string, object?>
                {
                    ["extension"] = Path.GetExtension(source.Path),
                    ["variant"] = variant.FileLabel,
                    ["width"] = variant.PixelWidth
                },
                error);
        }
    }

    private static void WriteVariant(
        BitmapSource source,
        string target,
        CancellationToken cancellationToken)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        var temporary = target + $".{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
        try
        {
            using (var output = File.Open(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(output);
                output.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private bool TryDescribeSource(string path, out ArtworkSource source)
    {
        source = default;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            if (!info.Exists) return false;

            var identityText = IsManagedOriginal(fullPath)
                ? fullPath.ToUpperInvariant()
                : $"{fullPath.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            var identity = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identityText))).ToLowerInvariant();
            source = new ArtworkSource(fullPath, identity);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            Interlocked.Increment(ref _failures);
            _diagnostics.Error(
                "artwork",
                "thumbnail.variant-source",
                exception,
                new Dictionary<string, object?> { ["extension"] = Path.GetExtension(path) });
            return false;
        }
    }

    private bool IsManagedOriginal(string fullPath)
    {
        if (!Path.GetExtension(fullPath).Equals(".art", StringComparison.OrdinalIgnoreCase)) return false;
        var cacheRoot = Path.GetFullPath(_paths.ArtworkCache)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryUseExisting(string path)
    {
        if (!File.Exists(path)) return false;
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return true;
    }

    private string VariantPath(string identity, ArtworkThumbnailVariant variant) =>
        Path.Combine(ThumbnailDirectory, $"{identity}-{variant.FileLabel}.png");

    private string ThumbnailDirectory => Path.Combine(_paths.ArtworkCache, ThumbnailDirectoryName);

    private static Dictionary<string, object?> Data(string sourcePath, ArtworkThumbnailVariant variant) => new()
    {
        ["extension"] = Path.GetExtension(sourcePath),
        ["variant"] = variant.FileLabel,
        ["width"] = variant.PixelWidth
    };

    private readonly record struct ArtworkSource(string Path, string Identity);
}

internal readonly record struct ArtworkThumbnailResult(
    string Path,
    ArtworkThumbnailVariant Variant,
    bool PersistentCacheHit);

internal readonly record struct ArtworkThumbnailVariant(string FileLabel, int PixelWidth)
{
    internal static readonly ArtworkThumbnailVariant Small = new("64", 64);
    internal static readonly ArtworkThumbnailVariant Gallery = new("256", 256);
    internal static readonly ArtworkThumbnailVariant Detail = new("640", 640);
    internal static readonly ArtworkThumbnailVariant NowPlaying = new("now-playing", 1024);

    internal static ArtworkThumbnailVariant ForRequestedWidth(int requestedWidth) =>
        requestedWidth switch
        {
            <= 64 => Small,
            <= 256 => Gallery,
            <= 640 => Detail,
            _ => NowPlaying
        };
}

internal readonly record struct PersistentArtworkThumbnailMetrics(
    long Requests,
    long Hits,
    long SourceDecodes,
    long VariantsGenerated,
    long Failures)
{
    internal double HitRate => Requests == 0 ? 0 : Hits * 100d / Requests;
}
