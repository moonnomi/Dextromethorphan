using System.IO.Compression;
using Dextromethorphan.App.Diagnostics;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Settings;
using Dextromethorphan.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Dextromethorphan.Tests;

public sealed class DiagnosticsBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dextromethorphan.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExportRedactsPathsAndNeverIncludesLibraryDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(_root);
        var settings = new JsonSettingsService(paths);
        await settings.InitializeAsync(cancellationToken);
        var libraryRoot = Path.Combine(_root, "private-music");
        await settings.UpdateAsync(
            value => value.LibraryFolders = [libraryRoot],
            cancellationToken);
        var library = new SqliteLibraryRepository(paths);
        await library.InitializeAsync(cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(paths.Logs, "app.jsonl"),
            $"{{\"path\":\"{libraryRoot}\",\"deviceId\":\"secret-device\"}}",
            cancellationToken);
        var destination = Path.Combine(_root, "diagnostics.zip");

        await new DiagnosticsBundleExporter(
                paths,
                settings,
                new StubAudioEngine(),
                library)
            .ExportAsync(destination, cancellationToken);

        using var archive = ZipFile.OpenRead(destination);
        Assert.DoesNotContain(
            archive.Entries,
            entry => entry.FullName.Equals(
                "library.db",
                StringComparison.OrdinalIgnoreCase));
        var logEntry = Assert.Single(
            archive.Entries,
            entry => entry.FullName.StartsWith(
                "logs/",
                StringComparison.Ordinal));
        using var reader = new StreamReader(logEntry.Open());
        var redacted = await reader.ReadToEndAsync(cancellationToken);
        Assert.DoesNotContain(libraryRoot, redacted);
        Assert.DoesNotContain("secret-device", redacted);
        Assert.Contains("<library-root-1>", redacted);
        Assert.Contains("id-", redacted);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class StubAudioEngine : IAudioEngine
    {
        public PlaybackSnapshot Snapshot { get; } = new(
            null,
            PlaybackState.Stopped,
            TimeSpan.Zero,
            TimeSpan.Zero,
            1);
        public AudioDiagnostics? Diagnostics => null;
        public event EventHandler<PlaybackSnapshot>? StateChanged
        {
            add { }
            remove { }
        }
        public event EventHandler<TrackTransitionedEventArgs>? TrackTransitioned
        {
            add { }
            remove { }
        }
        public event EventHandler? PlaybackEnded
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<AudioDeviceInfo>> GetOutputDevicesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AudioDeviceInfo>>(
            [
                new(
                    "secret-device",
                    "Test output",
                    true,
                    "Active")
            ]);

        public Task<AudioDeviceCapabilities> GetDeviceCapabilitiesAsync(
            string deviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task LoadAsync(
            Track track,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task QueueNextAsync(
            Track? track,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task PlayAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task PauseAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task StopAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SeekAsync(
            TimeSpan position,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SetVolumeAsync(
            double volume,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SetPlaybackOptionsAsync(
            AudioPlaybackOptions options,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ConfigureOutputAsync(
            AudioOutputProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
