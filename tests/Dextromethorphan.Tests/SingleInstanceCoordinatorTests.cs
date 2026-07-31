using Dextromethorphan.App.WindowsIntegration;

namespace Dextromethorphan.Tests;

public sealed class SingleInstanceCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dextromethorphan.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LaterLaunchForwardsArgumentsToPrimary()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var name = "Dextromethorphan-Test-" + Guid.NewGuid().ToString("N");
        await using var primary = new SingleInstanceCoordinator(name);
        Assert.True(await primary.AcquireOrForwardAsync([], cancellationToken));
        var received = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ArgumentsReceived += (_, arguments) => received.TrySetResult(arguments);

        await using var secondary = new SingleInstanceCoordinator(name);
        Assert.False(await secondary.AcquireOrForwardAsync(
            [@"C:\Music", @"C:\Music\song.flac"],
            cancellationToken));

        Assert.Equal(
            [@"C:\Music", @"C:\Music\song.flac"],
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
    }

    [Fact]
    public async Task LaunchTargetParserSkipsOptionValues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        var song = Path.Combine(_root, "song.flac");
        await File.WriteAllBytesAsync(song, [0], cancellationToken);
        var diagnostics = Path.Combine(_root, "diagnostics");
        Directory.CreateDirectory(diagnostics);

        var result = LaunchTargetParser.Extract(
        [
            "--diagnostics-output",
            diagnostics,
            "--performance-overlay",
            song
        ]);

        Assert.Equal([song], result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
