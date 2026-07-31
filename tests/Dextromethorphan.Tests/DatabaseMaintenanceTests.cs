using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Dextromethorphan.Tests;

public sealed class DatabaseMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dextromethorphan.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task NumberedMigrationCreatesBackupAndRepairsSearchIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(_root);
        var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(cancellationToken);
        await repository.UpsertAsync(Track("legacy.flac", "Legacy"), cancellationToken);
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabaseFile}"))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TRIGGER tracks_ai;
                DROP TRIGGER tracks_ad;
                DROP TRIGGER tracks_au;
                DROP TABLE tracks_fts;
                PRAGMA user_version=2;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await repository.InitializeAsync(cancellationToken);

        Assert.Equal(
            "Legacy",
            Assert.Single(await repository.SearchAsync(
                "legacy",
                cancellationToken: cancellationToken)).Title);
        Assert.Single(SqliteDatabaseMaintenance.ListBackups(paths));
        Assert.Equal(
            SqliteLibraryRepository.CurrentSchemaVersion,
            (await repository.CheckIntegrityAsync(cancellationToken)).SchemaVersion);
    }

    [Fact]
    public async Task VersionZeroLegacyDatabaseMigratesWithoutTouchingTrackData()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabaseFile}"))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE tracks(
                  id INTEGER PRIMARY KEY,
                  path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                  title TEXT NOT NULL,
                  artist TEXT NOT NULL DEFAULT '',
                  album_artist TEXT NOT NULL DEFAULT '',
                  album TEXT NOT NULL DEFAULT '',
                  genre TEXT NOT NULL DEFAULT '',
                  comment TEXT NOT NULL DEFAULT '',
                  year INTEGER NOT NULL DEFAULT 0,
                  track_number INTEGER NOT NULL DEFAULT 0,
                  disc_number INTEGER NOT NULL DEFAULT 0,
                  duration_ms INTEGER NOT NULL DEFAULT 0,
                  bitrate INTEGER NOT NULL DEFAULT 0,
                  sample_rate INTEGER NOT NULL DEFAULT 0,
                  bits_per_sample INTEGER NOT NULL DEFAULT 0,
                  channels INTEGER NOT NULL DEFAULT 0,
                  codec TEXT NOT NULL DEFAULT '',
                  replaygain_track REAL,
                  replaygain_album REAL,
                  replay_peak REAL,
                  rating INTEGER NOT NULL DEFAULT 0,
                  loved INTEGER NOT NULL DEFAULT 0,
                  play_count INTEGER NOT NULL DEFAULT 0,
                  last_played_at INTEGER,
                  file_modified_at INTEGER NOT NULL,
                  file_size INTEGER NOT NULL,
                  lyrics TEXT NOT NULL DEFAULT '',
                  added_at INTEGER NOT NULL,
                  updated_at INTEGER NOT NULL);
                INSERT INTO tracks(
                  path,title,file_modified_at,file_size,added_at,updated_at)
                VALUES('C:\Music\untouched.flac','Untouched',1,42,1,1);
                PRAGMA user_version=0;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(cancellationToken);

        var migrated = Assert.Single(await repository.SearchAsync(
            "Untouched",
            cancellationToken: cancellationToken));
        Assert.Equal(@"C:\Music\untouched.flac", migrated.Path);
        Assert.Equal(42, migrated.FileSize);
        Assert.False(migrated.IsMissing);
        Assert.Equal(
            SqliteLibraryRepository.CurrentSchemaVersion,
            (await repository.CheckIntegrityAsync(cancellationToken)).SchemaVersion);
    }

    [Fact]
    public async Task BackupRestoreReturnsUserStateAtomically()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(_root);
        var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(cancellationToken);
        await repository.UpsertAsync(Track("rated.flac", "Rated"), cancellationToken);
        var track = Assert.Single(await repository.GetAllAsync(cancellationToken));
        await repository.SetRatingAsync(track.Id, 5, true, cancellationToken);
        var backup = await SqliteDatabaseMaintenance.CreateBackupAsync(
            paths,
            cancellationToken: cancellationToken);
        Assert.NotNull(backup);
        await repository.SetRatingAsync(track.Id, 1, false, cancellationToken);

        await SqliteDatabaseMaintenance.RestoreBackupAsync(
            paths,
            backup!,
            cancellationToken);
        var restoredRepository = new SqliteLibraryRepository(paths);
        await restoredRepository.InitializeAsync(cancellationToken);
        var restored = Assert.Single(await restoredRepository.GetAllAsync(cancellationToken));

        Assert.Equal(5, restored.Rating);
        Assert.True(restored.IsLoved);
    }

    [Fact]
    public async Task InvalidDatabaseIsReportedAsCorruption()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(
            paths.DatabaseFile,
            "not a sqlite database",
            cancellationToken);

        var exception = await Assert.ThrowsAsync<DatabaseCorruptionException>(
            () => new SqliteLibraryRepository(paths).InitializeAsync(cancellationToken));

        Assert.Equal(paths.DatabaseFile, exception.DatabasePath);
    }

    private Track Track(string file, string title)
    {
        var path = Path.Combine(_root, file);
        return new Track
        {
            Path = path,
            Title = title,
            Artist = "Migration",
            AlbumArtist = "Migration",
            Album = "Recovery",
            Genre = "Tests",
            Duration = TimeSpan.FromMinutes(1),
            Codec = "FLAC",
            FileModifiedAt = DateTimeOffset.UtcNow,
            FileSize = 1
        };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
