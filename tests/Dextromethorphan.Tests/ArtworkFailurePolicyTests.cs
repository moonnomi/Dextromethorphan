using System.IO;
using Dextromethorphan.App.Diagnostics;
using Dextromethorphan.App.UI;
using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Tests;

public sealed class ArtworkFailurePolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dextromethorphan.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ClassifiesIoFailuresForRetryAndContentFailuresAsPermanent()
    {
        Assert.Equal(
            ArtworkFailureKind.Transient,
            ArtworkFailurePolicy.Classify(new IOException("offline")));
        Assert.Equal(
            ArtworkFailureKind.Transient,
            ArtworkFailurePolicy.Classify(new UnauthorizedAccessException("busy")));
        Assert.Equal(
            ArtworkFailureKind.Permanent,
            ArtworkFailurePolicy.Classify(new FileFormatException("corrupt")));
        Assert.Equal(
            ArtworkFailureKind.Permanent,
            ArtworkFailurePolicy.Classify(new NotSupportedException("codec")));
    }

    [Fact]
    public void RetryBackoffIsBoundedAndIncreasing()
    {
        var first = ArtworkFailurePolicy.RetryDelay(1);
        var second = ArtworkFailurePolicy.RetryDelay(2);
        var final = ArtworkFailurePolicy.RetryDelay(3);

        Assert.True(first < second);
        Assert.True(second < final);
        Assert.Equal(final, ArtworkFailurePolicy.RetryDelay(100));
        Assert.Equal(3, ArtworkFailurePolicy.MaximumAutomaticAttempts);
    }

    [Fact]
    public async Task PermanentFailureIsSuppressedWithoutAnotherDecode()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "corrupt.png");
        await File.WriteAllBytesAsync(
            source,
            "not an image"u8.ToArray(),
            TestContext.Current.CancellationToken);
        using var service = CreateService();

        Assert.Null(await service.GetAsync(
            source,
            256,
            ArtworkRequestPriority.Visible,
            TestContext.Current.CancellationToken));
        var afterFirst = service.GetRuntimeMetrics();
        Assert.Equal(ArtworkFailureKind.Permanent, service.GetFailure(source, 256).Kind);

        Assert.Null(await service.GetAsync(
            source,
            256,
            ArtworkRequestPriority.Visible,
            TestContext.Current.CancellationToken));
        Assert.Equal(afterFirst.Decodes, service.GetRuntimeMetrics().Decodes);
    }

    [Fact]
    public async Task TransientMissingFileRecoversAfterRetryDelay()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "arrives-later.png");
        using var service = CreateService();

        Assert.Null(await service.GetAsync(
            source,
            256,
            ArtworkRequestPriority.Visible,
            TestContext.Current.CancellationToken));
        var failure = service.GetFailure(source, 256);
        Assert.Equal(ArtworkFailureKind.Transient, failure.Kind);
        Assert.True(failure.RetryAfter > TimeSpan.Zero);

        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(source, png, TestContext.Current.CancellationToken);
        await Task.Delay(
            failure.RetryAfter + TimeSpan.FromMilliseconds(75),
            TestContext.Current.CancellationToken);

        var recovered = await service.GetAsync(
            source,
            256,
            ArtworkRequestPriority.Visible,
            TestContext.Current.CancellationToken);
        Assert.NotNull(recovered);
        Assert.Equal(ArtworkFailureKind.None, service.GetFailure(source, 256).Kind);
    }

    [Fact]
    public async Task ExternalSourceVersionChangeBypassesStaleRamAndDiskVariants()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "external-cover.png");
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(source, png, TestContext.Current.CancellationToken);
        using var service = CreateService();

        var first = await service.GetAsync(
            source,
            256,
            ArtworkRequestPriority.Visible,
            TestContext.Current.CancellationToken);
        var firstDecodes = service.GetRuntimeMetrics().Decodes;
        File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddSeconds(2));

        var changed = await service.GetAsync(
            source,
            256,
            ArtworkRequestPriority.Visible,
            TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(changed);
        Assert.NotSame(first, changed);
        Assert.True(service.GetRuntimeMetrics().Decodes > firstDecodes);
    }

    private ArtworkImageService CreateService()
    {
        var diagnostics = new DeveloperDiagnostics();
        var updates = new ArtworkPropertyUpdateBatcher(update => update());
        var thumbnails = new PersistentArtworkThumbnailStore(new AppPaths(_root), diagnostics);
        return new ArtworkImageService(diagnostics, updates, thumbnails);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
