using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dextromethorphan.App.Diagnostics;
using Dextromethorphan.App.UI;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Tests;

public sealed class PersistentArtworkThumbnailStoreTests
{
    [Theory]
    [InlineData(32, 64)]
    [InlineData(64, 64)]
    [InlineData(65, 256)]
    [InlineData(96, 256)]
    [InlineData(256, 256)]
    [InlineData(257, 640)]
    [InlineData(640, 640)]
    [InlineData(641, 1024)]
    [InlineData(900, 1024)]
    [InlineData(1200, 1024)]
    public void RequestedWidthsMapToFourStableVariants(int requestedWidth, int expectedWidth)
    {
        Assert.Equal(expectedWidth, ArtworkThumbnailVariant.ForRequestedWidth(requestedWidth).PixelWidth);
    }

    [Fact]
    public void EachVariantIsGeneratedOnceAndNextProcessUsesPersistentFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "Dextromethorphan.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "source.png");
            Directory.CreateDirectory(root);
            WriteSourceArtwork(source);
            var paths = new AppPaths(root);

            var firstProcess = new PersistentArtworkThumbnailStore(paths, new DeveloperDiagnostics());
            var first = firstProcess.GetOrCreate(source, 64, TestContext.Current.CancellationToken);

            Assert.NotNull(first);
            Assert.False(first.Value.PersistentCacheHit);
            Assert.Equal(64, first.Value.Variant.PixelWidth);
            Assert.NotNull(firstProcess.GetOrCreate(source, 96, TestContext.Current.CancellationToken));
            Assert.NotNull(firstProcess.GetOrCreate(source, 640, TestContext.Current.CancellationToken));
            Assert.NotNull(firstProcess.GetOrCreate(source, 900, TestContext.Current.CancellationToken));

            var thumbnails = Directory.EnumerateFiles(
                Path.Combine(paths.ArtworkCache, "thumbnails"),
                "*.png",
                SearchOption.TopDirectoryOnly).ToArray();
            Assert.Equal(4, thumbnails.Length);
            Assert.Equal([64, 256, 640, 1024], thumbnails.Select(ReadPixelWidth).Order().ToArray());

            var generated = firstProcess.GetMetrics();
            Assert.Equal(4, generated.SourceDecodes);
            Assert.Equal(4, generated.VariantsGenerated);
            Assert.Equal(0, generated.Failures);

            var nextProcess = new PersistentArtworkThumbnailStore(paths, new DeveloperDiagnostics());
            var reused = nextProcess.GetOrCreate(source, 900, TestContext.Current.CancellationToken);

            Assert.NotNull(reused);
            Assert.True(reused.Value.PersistentCacheHit);
            Assert.Equal(1024, reused.Value.Variant.PixelWidth);
            var warm = nextProcess.GetMetrics();
            Assert.Equal(1, warm.Hits);
            Assert.Equal(0, warm.SourceDecodes);
            Assert.Equal(0, warm.VariantsGenerated);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void WriteSourceArtwork(string path)
    {
        const int width = 1200;
        const int height = 800;
        const int bytesPerPixel = 4;
        var pixels = new byte[width * height * bytesPerPixel];
        for (var index = 0; index < pixels.Length; index += bytesPerPixel)
        {
            pixels[index] = 0xCA;
            pixels[index + 1] = 0x57;
            pixels[index + 2] = 0x2A;
            pixels[index + 3] = 0xFF;
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * bytesPerPixel, 0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static int ReadPixelWidth(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapFrame.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).PixelWidth;
    }
}
