using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dextromethorphan.App.Diagnostics;
using Dextromethorphan.App.UI;
using Dextromethorphan.Infrastructure.Library;
using Dextromethorphan.Infrastructure.Settings;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Tests;

public sealed class ArtworkImageInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dextromethorphan.Tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(ArtworkImageFormat.Png, ".png")]
    [InlineData(ArtworkImageFormat.Jpeg, ".jpg")]
    [InlineData(ArtworkImageFormat.Gif, ".gif")]
    [InlineData(ArtworkImageFormat.Bmp, ".bmp")]
    [InlineData(ArtworkImageFormat.Tiff, ".tiff")]
    public void DetectsSupportedWicFormatsAndDimensions(
        ArtworkImageFormat format,
        string extension)
    {
        var bytes = Encode(format, 320, 180);

        var accepted = ArtworkImageInspector.TryInspect(bytes, out var info, out var rejection);

        Assert.True(accepted);
        Assert.Equal(ArtworkImageRejectionReason.None, rejection);
        Assert.Equal(format, info.Format);
        Assert.Equal(extension, info.Extension);
        Assert.Equal(320, info.Width);
        Assert.Equal(180, info.Height);
    }

    [Fact]
    public void RejectsTruncatedAndUnknownPayloads()
    {
        var png = Encode(ArtworkImageFormat.Png, 64, 64);

        Assert.False(ArtworkImageInspector.TryInspect(
            png.AsSpan(0, 32),
            out _,
            out var truncated));
        Assert.Equal(ArtworkImageRejectionReason.CorruptStructure, truncated);

        Assert.False(ArtworkImageInspector.TryInspect(
            "not an image"u8,
            out _,
            out var unknown));
        Assert.Equal(ArtworkImageRejectionReason.UnsupportedFormat, unknown);
    }

    [Fact]
    public void RejectsDecompressionBombDimensionsBeforeBitmapDecode()
    {
        var png = Encode(ArtworkImageFormat.Png, 64, 64);
        png[16] = 0;
        png[17] = 0;
        png[18] = 0x40;
        png[19] = 0x01;

        Assert.False(ArtworkImageInspector.TryInspect(png, out _, out var rejection));
        Assert.Equal(ArtworkImageRejectionReason.DimensionLimit, rejection);
    }

    [Fact]
    public void RejectsOversizedFileBeforeAllocatingItsContents()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "oversized.png");
        using (var file = File.Create(path))
            file.SetLength(ArtworkImageInspector.MaximumEncodedBytes + 1);

        Assert.False(ArtworkImageInspector.TryInspectFile(
            path,
            out _,
            out var rejection,
            TestContext.Current.CancellationToken));
        Assert.Equal(ArtworkImageRejectionReason.EncodedSizeLimit, rejection);
    }

    [Fact]
    public async Task CacheUsesDetectedExtensionAndRejectsCorruptArtwork()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(_root);
        var settings = new JsonSettingsService(paths);
        await settings.InitializeAsync(cancellationToken);
        var cache = new ArtworkCache(paths, settings);
        var modified = DateTimeOffset.UtcNow;

        var jpeg = await cache.StoreAsync(
            Path.Combine(_root, "valid.flac"),
            modified,
            Encode(ArtworkImageFormat.Jpeg, 300, 300),
            cancellationToken);
        var corrupt = await cache.StoreAsync(
            Path.Combine(_root, "corrupt.flac"),
            modified,
            new byte[] { 1, 2, 3, 4, 5 },
            cancellationToken);

        Assert.NotNull(jpeg);
        Assert.Equal(".jpg", Path.GetExtension(jpeg));
        Assert.Null(corrupt);
    }

    [Fact]
    public async Task CacheLookupKeepsLegacyArtFilesReadable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(_root);
        var settings = new JsonSettingsService(paths);
        await settings.InitializeAsync(cancellationToken);
        var cache = new ArtworkCache(paths, settings);
        var media = Path.Combine(_root, "legacy.flac");
        await File.WriteAllBytesAsync(media, [0x00], cancellationToken);
        var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(media), TimeSpan.Zero);
        var stored = await cache.StoreAsync(
            media,
            modified,
            Encode(ArtworkImageFormat.Png, 64, 64),
            cancellationToken);
        Assert.NotNull(stored);
        var legacy = Path.ChangeExtension(stored, ".art");
        File.Move(stored!, legacy);

        var found = await cache.GetOrCreateAsync(media, cancellationToken);

        Assert.Equal(legacy, found);
    }

    [Fact]
    public void PersistentStoreRejectsBombBeforeWpfDecode()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "bomb.png");
        var png = Encode(ArtworkImageFormat.Png, 64, 64);
        png[16] = 0;
        png[17] = 0;
        png[18] = 0x40;
        png[19] = 0x01;
        File.WriteAllBytes(source, png);
        var store = new PersistentArtworkThumbnailStore(
            new AppPaths(_root),
            new DeveloperDiagnostics());

        var result = store.GetOrCreate(source, 256, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result.Value.SourceRejected);
        Assert.Equal(ArtworkImageRejectionReason.DimensionLimit, result.Value.Rejection);
        Assert.Equal(0, store.GetMetrics().SourceDecodes);
        Assert.Equal(1, store.GetMetrics().Failures);
    }

    private static byte[] Encode(ArtworkImageFormat format, int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];
        Array.Fill<byte>(pixels, 0xA7);
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 0xFF;
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);

        BitmapEncoder encoder = format switch
        {
            ArtworkImageFormat.Png => new PngBitmapEncoder(),
            ArtworkImageFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = 90 },
            ArtworkImageFormat.Gif => new GifBitmapEncoder(),
            ArtworkImageFormat.Bmp => new BmpBitmapEncoder(),
            ArtworkImageFormat.Tiff => new TiffBitmapEncoder(),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
