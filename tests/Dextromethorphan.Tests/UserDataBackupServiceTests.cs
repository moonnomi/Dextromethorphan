using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Settings;
using Dextromethorphan.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Dextromethorphan.Tests;

public sealed class UserDataBackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dextromethorphan.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BackupRestorePreservesSettingsAndUserLibraryState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(_root);
        var settings = new JsonSettingsService(paths);
        await settings.InitializeAsync(cancellationToken);
        await settings.UpdateAsync(
            value =>
            {
                value.Theme = "Amoled";
                value.AccentColor = "#FF123456";
            },
            cancellationToken);
        var library = new SqliteLibraryRepository(paths);
        var playlists = new SqlitePlaylistRepository(library);
        await library.InitializeAsync(cancellationToken);
        await library.UpsertAsync(
            new Track
            {
                Path = Path.Combine(_root, "song.flac"),
                Title = "Song",
                Artist = "Artist",
                Album = "Album",
                FileModifiedAt = DateTimeOffset.UtcNow,
                FileSize = 12
            },
            cancellationToken);
        var track = Assert.Single(
            await library.GetAllAsync(cancellationToken));
        await library.SetRatingAsync(
            track.Id,
            5,
            true,
            cancellationToken);
        await library.SaveBookmarkAsync(
            track.Id,
            TimeSpan.FromSeconds(42),
            cancellationToken);
        var playlistId = await playlists.CreateManualAsync(
            "Keep me",
            cancellationToken);
        await playlists.AddTracksAsync(
            playlistId,
            [track.Id],
            cancellationToken);

        var archive = Path.Combine(_root, "user-backup.dexbackup");
        var service = new UserDataBackupService(
            paths,
            settings,
            library);
        await service.ExportAsync(archive, cancellationToken);

        await settings.UpdateAsync(
            value =>
            {
                value.Theme = "Light";
                value.AccentColor = "#FFFFFFFF";
            },
            cancellationToken);
        await library.SetRatingAsync(
            track.Id,
            1,
            false,
            cancellationToken);
        await playlists.DeleteAsync(
            playlistId,
            cancellationToken);

        await service.RestoreAsync(archive, cancellationToken);

        var restored = Assert.Single(
            await library.GetAllAsync(cancellationToken));
        Assert.Equal("Amoled", settings.Current.Theme);
        Assert.Equal("#FF123456", settings.Current.AccentColor);
        Assert.Equal(5, restored.Rating);
        Assert.True(restored.IsLoved);
        Assert.Equal(
            TimeSpan.FromSeconds(42),
            await library.GetBookmarkAsync(
                restored.Id,
                cancellationToken));
        Assert.Equal(
            "Keep me",
            Assert.Single(
                await playlists.GetAllAsync(cancellationToken)).Name);
    }

    [Fact]
    public async Task SettingsImportNormalizesAndScopedResetKeepsLibraryRoots()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(_root);
        var settings = new JsonSettingsService(paths);
        await settings.InitializeAsync(cancellationToken);
        var offlineRoot = Path.Combine(_root, "offline-source");
        await settings.UpdateAsync(
            value =>
            {
                value.LibraryFolders = [offlineRoot];
                value.Theme = "Amoled";
                value.Volume = 0.1;
            },
            cancellationToken);
        var export = Path.Combine(_root, "settings-export.json");
        await settings.ExportAsync(export, cancellationToken);
        await settings.UpdateAsync(
            value =>
            {
                value.Theme = "Light";
                value.LibraryFolders.Clear();
            },
            cancellationToken);

        await settings.ImportAsync(export, cancellationToken);
        await settings.ResetAsync(
            SettingsResetScope.Appearance,
            cancellationToken);

        Assert.Equal("Dark", settings.Current.Theme);
        Assert.Equal(0.1, settings.Current.Volume);
        Assert.Equal(
            Path.GetFullPath(offlineRoot),
            Assert.Single(settings.Current.LibraryFolders));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
