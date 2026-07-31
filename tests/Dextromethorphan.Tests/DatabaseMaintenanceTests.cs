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
