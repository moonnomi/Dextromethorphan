using System.Text.Json;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Library;
using Microsoft.Data.Sqlite;

namespace Dextromethorphan.Infrastructure.Storage;

public sealed class DatabaseRecoveryService(
    AppPaths paths,
    SqliteLibraryRepository library,
    ILibraryScanner scanner,
    ISettingsService settings,
    IApplicationLog applicationLog)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    public IReadOnlyList<string> AvailableBackups =>
        SqliteDatabaseMaintenance.ListBackups(paths);

    public async Task<DatabaseRecoveryResult> RestoreLatestBackupAsync(
        CancellationToken cancellationToken = default)
    {
        var backup = AvailableBackups.FirstOrDefault()
                     ?? throw new InvalidOperationException(
                         "No database backup is available.");
        await SqliteDatabaseMaintenance.RestoreBackupAsync(
            paths,
            backup,
            cancellationToken);
        await library.InitializeAsync(cancellationToken);
        var health = await library.CheckIntegrityAsync(cancellationToken);
        applicationLog.Write(
            ApplicationLogLevel.Information,
            "database",
            "recovery-backup-restored",
            new Dictionary<string, object?>
            {
                ["schemaVersion"] = health.SchemaVersion,
                ["healthy"] = health.IsHealthy
            });
        return new DatabaseRecoveryResult(
            "Backup restored",
            backup,
            null,
            0,
            0);
    }

    public async Task<DatabaseRecoveryResult> RebuildFromFilesAsync(
        CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var state = await CaptureRecoverableStateAsync(
            cancellationToken);
        var stamp = DateTimeOffset.UtcNow.ToString(
            "yyyyMMdd-HHmmss-fffffff");
        var snapshotPath = Path.Combine(
            paths.DatabaseBackups,
            $"recovery-state-{stamp}.json");
        await WriteRecoveryStateAsync(
            snapshotPath,
            state,
            cancellationToken);
        var quarantinePath = Path.Combine(
            paths.DatabaseBackups,
            $"corrupt-library-{stamp}.db");
        QuarantineDatabase(quarantinePath);

        await library.InitializeAsync(cancellationToken);
        await scanner.ScanAsync(
            settings.Current.LibraryFolders,
            settings.Current.ExcludedFolders,
            cancellationToken);
        var restored = await RestoreRecoverableStateAsync(
            state,
            cancellationToken);
        applicationLog.Write(
            ApplicationLogLevel.Information,
            "database",
            "recovery-rebuild-completed",
            new Dictionary<string, object?>
            {
                ["capturedTracks"] = state.Tracks.Count,
                ["restoredTracks"] = restored.Tracks,
                ["capturedPlaylists"] = state.Playlists.Count,
                ["restoredPlaylists"] = restored.Playlists,
                ["quarantine"] = quarantinePath,
                ["snapshot"] = snapshotPath
            });
        return new DatabaseRecoveryResult(
            "Library rebuilt from files",
            quarantinePath,
            snapshotPath,
            restored.Tracks,
            restored.Playlists);
    }

    private async Task<RecoverableUserState> CaptureRecoverableStateAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.DatabaseFile))
            return new RecoverableUserState();
        try
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = paths.DatabaseFile,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                    DefaultTimeout = 2
                }.ToString());
            await connection.OpenAsync(cancellationToken);
            var tracks = await TryCaptureTracksAsync(
                connection,
                cancellationToken);
            var playlists = await TryCapturePlaylistsAsync(
                connection,
                cancellationToken);
            return new RecoverableUserState
            {
                CapturedAt = DateTimeOffset.UtcNow,
                Tracks = tracks,
                Playlists = playlists
            };
        }
        catch (Exception exception) when (
            exception is SqliteException
                or IOException
                or UnauthorizedAccessException)
        {
            applicationLog.Write(
                ApplicationLogLevel.Warning,
                "database",
                "recovery-state-unreadable",
                exception: exception);
            return new RecoverableUserState
            {
                CapturedAt = DateTimeOffset.UtcNow
            };
        }
    }

    private static async Task<IReadOnlyList<RecoverableTrackState>>
        TryCaptureTracksAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT t.path,t.rating,t.loved,t.play_count,
                       t.last_played_at,b.position_ms
                FROM tracks t
                LEFT JOIN bookmarks b ON b.track_id=t.id
                """;
            var result = new List<RecoverableTrackState>();
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new RecoverableTrackState
                {
                    Path = reader.GetString(0),
                    Rating = reader.GetInt32(1),
                    IsLoved = reader.GetInt32(2) != 0,
                    PlayCount = reader.GetInt64(3),
                    LastPlayedAt = reader.IsDBNull(4)
                        ? null
                        : DateTimeOffset.FromUnixTimeMilliseconds(
                            reader.GetInt64(4)),
                    BookmarkMilliseconds = reader.IsDBNull(5)
                        ? null
                        : reader.GetInt64(5)
                });
            return result;
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<RecoverablePlaylistState>>
        TryCapturePlaylistsAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT p.id,p.name,p.kind,p.rules_json,t.path,pt.position
                FROM playlists p
                LEFT JOIN playlist_tracks pt ON pt.playlist_id=p.id
                LEFT JOIN tracks t ON t.id=pt.track_id
                ORDER BY p.id,pt.position
                """;
            var builders =
                new Dictionary<long, RecoverablePlaylistBuilder>();
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                if (!builders.TryGetValue(id, out var builder))
                {
                    builder = new RecoverablePlaylistBuilder(
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.IsDBNull(3)
                            ? null
                            : reader.GetString(3));
                    builders.Add(id, builder);
                }
                if (!reader.IsDBNull(4))
                    builder.Paths.Add(reader.GetString(4));
            }
            return builders.Values.Select(builder =>
                new RecoverablePlaylistState
                {
                    Name = builder.Name,
                    Kind = builder.Kind,
                    RulesJson = builder.RulesJson,
                    TrackPaths = builder.Paths
                }).ToArray();
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    private async Task<(int Tracks, int Playlists)>
        RestoreRecoverableStateAsync(
            RecoverableUserState state,
            CancellationToken cancellationToken)
    {
        await using var connection =
            await library.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        var restoredTracks = 0;
        foreach (var track in state.Tracks)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE tracks
                SET rating=$rating,loved=$loved,play_count=$plays,
                    last_played_at=$last
                WHERE path=$path COLLATE NOCASE
                """;
            update.Parameters.AddWithValue(
                "$path",
                CanonicalPath.Normalize(track.Path));
            update.Parameters.AddWithValue("$rating", track.Rating);
            update.Parameters.AddWithValue(
                "$loved",
                track.IsLoved ? 1 : 0);
            update.Parameters.AddWithValue("$plays", track.PlayCount);
            update.Parameters.AddWithValue(
                "$last",
                (object?)track.LastPlayedAt
                    ?.ToUnixTimeMilliseconds()
                ?? DBNull.Value);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                continue;
            restoredTracks++;
            if (track.BookmarkMilliseconds is null) continue;
            await using var bookmark = connection.CreateCommand();
            bookmark.Transaction = transaction;
            bookmark.CommandText = """
                INSERT INTO bookmarks(track_id,position_ms,updated_at)
                SELECT id,$position,$now FROM tracks
                WHERE path=$path COLLATE NOCASE
                ON CONFLICT(track_id) DO UPDATE SET
                  position_ms=excluded.position_ms,
                  updated_at=excluded.updated_at
                """;
            bookmark.Parameters.AddWithValue(
                "$path",
                CanonicalPath.Normalize(track.Path));
            bookmark.Parameters.AddWithValue(
                "$position",
                track.BookmarkMilliseconds.Value);
            bookmark.Parameters.AddWithValue(
                "$now",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await bookmark.ExecuteNonQueryAsync(cancellationToken);
        }

        var restoredPlaylists = 0;
        foreach (var playlist in state.Playlists)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO playlists(
                  name,kind,rules_json,created_at,updated_at)
                VALUES($name,$kind,$rules,$now,$now);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$name", playlist.Name);
            insert.Parameters.AddWithValue("$kind", playlist.Kind);
            insert.Parameters.AddWithValue(
                "$rules",
                (object?)playlist.RulesJson ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "$now",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var playlistId = Convert.ToInt64(
                await insert.ExecuteScalarAsync(cancellationToken));
            var position = 0;
            foreach (var path in playlist.TrackPaths)
            {
                await using var membership = connection.CreateCommand();
                membership.Transaction = transaction;
                membership.CommandText = """
                    INSERT OR IGNORE INTO playlist_tracks(
                      playlist_id,track_id,position)
                    SELECT $playlist,id,$position FROM tracks
                    WHERE path=$path COLLATE NOCASE
                    """;
                membership.Parameters.AddWithValue(
                    "$playlist",
                    playlistId);
                membership.Parameters.AddWithValue(
                    "$position",
                    position++);
                membership.Parameters.AddWithValue(
                    "$path",
                    CanonicalPath.Normalize(path));
                await membership.ExecuteNonQueryAsync(cancellationToken);
            }
            restoredPlaylists++;
        }
        await transaction.CommitAsync(cancellationToken);
        return (restoredTracks, restoredPlaylists);
    }

    private void QuarantineDatabase(string destination)
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(paths.DatabaseFile))
            File.Move(paths.DatabaseFile, destination, overwrite: false);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = paths.DatabaseFile + suffix;
            if (File.Exists(sidecar))
                File.Move(
                    sidecar,
                    destination + suffix,
                    overwrite: false);
        }
    }

    private static async Task WriteRecoveryStateAsync(
        string path,
        RecoverableUserState state,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(
                         temporary,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         32 * 1024,
                         true))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                state,
                JsonOptions,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path);
    }

    private sealed record RecoverablePlaylistBuilder(
        string Name,
        string Kind,
        string? RulesJson)
    {
        public List<string> Paths { get; } = [];
    }
}

public sealed record DatabaseRecoveryResult(
    string Message,
    string? DatabaseBackupOrQuarantine,
    string? UserStateSnapshot,
    int RestoredTracks,
    int RestoredPlaylists);

public sealed record RecoverableUserState
{
    public DateTimeOffset CapturedAt { get; init; }
    public IReadOnlyList<RecoverableTrackState> Tracks { get; init; } = [];
    public IReadOnlyList<RecoverablePlaylistState> Playlists { get; init; } = [];
}

public sealed record RecoverableTrackState
{
    public required string Path { get; init; }
    public int Rating { get; init; }
    public bool IsLoved { get; init; }
    public long PlayCount { get; init; }
    public DateTimeOffset? LastPlayedAt { get; init; }
    public long? BookmarkMilliseconds { get; init; }
}

public sealed record RecoverablePlaylistState
{
    public required string Name { get; init; }
    public string Kind { get; init; } = "manual";
    public string? RulesJson { get; init; }
    public IReadOnlyList<string> TrackPaths { get; init; } = [];
}
