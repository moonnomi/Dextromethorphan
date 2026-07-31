using System.Text;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Library;
using Microsoft.Data.Sqlite;

namespace Dextromethorphan.Infrastructure.Storage;

public sealed class SqliteLibraryRepository(
    AppPaths paths,
    IApplicationLog? applicationLog = null) : ILibraryRepository
{
    public const int CurrentSchemaVersion = 4;
    internal string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = paths.DatabaseFile,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var databaseExisted = File.Exists(paths.DatabaseFile)
            && new FileInfo(paths.DatabaseFile).Length > 0;
        string? backup = null;
        var fromVersion = 0;
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await ExecuteScriptAsync(
                connection,
                "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;",
                cancellationToken);
            fromVersion = await GetSchemaVersionAsync(connection, cancellationToken);
            if (fromVersion > CurrentSchemaVersion)
                throw new InvalidOperationException(
                    $"Library schema {fromVersion} is newer than this application supports ({CurrentSchemaVersion}).");
            if (databaseExisted && fromVersion < CurrentSchemaVersion)
                backup = await SqliteDatabaseMaintenance.CreateBackupAsync(
                    paths,
                    cancellationToken: cancellationToken);
            for (var version = fromVersion + 1; version <= CurrentSchemaVersion; version++)
                await ApplyMigrationAsync(connection, version, cancellationToken);
            await RepairRequiredSchemaObjectsAsync(connection, cancellationToken);
            var health = await SqliteDatabaseMaintenance.CheckIntegrityAsync(
                connection,
                cancellationToken);
            if (!health.IsHealthy)
                throw new DatabaseCorruptionException(
                    health.Message,
                    paths.DatabaseFile);
            applicationLog?.Write(
                ApplicationLogLevel.Information,
                "database",
                "initialized",
                new Dictionary<string, object?>
                {
                    ["fromVersion"] = fromVersion,
                    ["schemaVersion"] = health.SchemaVersion,
                    ["backupCreated"] = backup is not null
                });
        }
        catch (DatabaseCorruptionException exception)
        {
            applicationLog?.Write(
                ApplicationLogLevel.Critical,
                "database",
                "integrity-check-failed",
                new Dictionary<string, object?> { ["database"] = paths.DatabaseFile },
                exception);
            throw;
        }
        catch (SqliteException exception) when (
            exception.SqliteErrorCode is 11 or 26)
        {
            var corruption = new DatabaseCorruptionException(
                exception.Message,
                paths.DatabaseFile);
            applicationLog?.Write(
                ApplicationLogLevel.Critical,
                "database",
                "open-corrupt",
                new Dictionary<string, object?> { ["database"] = paths.DatabaseFile },
                corruption);
            throw corruption;
        }
        catch (Exception exception)
        {
            SqliteConnection.ClearAllPools();
            if (backup is not null)
            {
                try
                {
                    await SqliteDatabaseMaintenance.RestoreBackupAsync(
                        paths,
                        backup,
                        cancellationToken);
                }
                catch
                {
                    backup = null;
                }
            }
            applicationLog?.Write(
                ApplicationLogLevel.Error,
                "database",
                "migration-failed",
                new Dictionary<string, object?>
                {
                    ["fromVersion"] = fromVersion,
                    ["targetVersion"] = CurrentSchemaVersion,
                    ["restoredBackup"] = backup
                },
                exception);
            throw new DatabaseMigrationException(
                fromVersion,
                CurrentSchemaVersion,
                backup,
                exception);
        }
    }

    public async Task<DatabaseIntegrityResult> CheckIntegrityAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await SqliteDatabaseMaintenance.CheckIntegrityAsync(
            connection,
            cancellationToken);
    }

    private static async Task<int> GetSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ApplyMigrationAsync(
        SqliteConnection connection,
        int version,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (SqliteTransaction)transaction;
        switch (version)
        {
            case 1:
                await ExecuteScriptAsync(
                    connection,
                    BaseSchema,
                    cancellationToken,
                    sqliteTransaction);
                break;
            case 2:
                await EnsureColumnAsync(
                    connection,
                    "tracks",
                    "artwork_path",
                    "TEXT",
                    cancellationToken,
                    sqliteTransaction);
                break;
            case 3:
            {
                var hadSearchIndex = await ObjectExistsAsync(
                    connection,
                    "table",
                    "tracks_fts",
                    cancellationToken,
                    sqliteTransaction);
                await ExecuteScriptAsync(
                    connection,
                    SearchSchema,
                    cancellationToken,
                    sqliteTransaction);
                if (!hadSearchIndex)
                    await ExecuteScriptAsync(
                        connection,
                        "INSERT INTO tracks_fts(tracks_fts) VALUES('rebuild');",
                        cancellationToken,
                        sqliteTransaction);
                break;
            }
            case 4:
                await EnsureColumnAsync(
                    connection,
                    "tracks",
                    "is_missing",
                    "INTEGER NOT NULL DEFAULT 0",
                    cancellationToken,
                    sqliteTransaction);
                await ExecuteScriptAsync(
                    connection,
                    "CREATE INDEX IF NOT EXISTS idx_tracks_missing ON tracks(is_missing, path COLLATE NOCASE);",
                    cancellationToken,
                    sqliteTransaction);
                break;
            default:
                throw new InvalidOperationException($"Unknown database migration {version}.");
        }
        await ExecuteScriptAsync(
            connection,
            $"PRAGMA user_version={version};",
            cancellationToken,
            sqliteTransaction);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task RepairRequiredSchemaObjectsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (SqliteTransaction)transaction;
        await EnsureColumnAsync(
            connection,
            "tracks",
            "artwork_path",
            "TEXT",
            cancellationToken,
            sqliteTransaction);
        await EnsureColumnAsync(
            connection,
            "tracks",
            "is_missing",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken,
            sqliteTransaction);
        await ExecuteScriptAsync(
            connection,
            "CREATE INDEX IF NOT EXISTS idx_tracks_missing ON tracks(is_missing, path COLLATE NOCASE);",
            cancellationToken,
            sqliteTransaction);
        var hadSearchIndex = await ObjectExistsAsync(
            connection,
            "table",
            "tracks_fts",
            cancellationToken,
            sqliteTransaction);
        await ExecuteScriptAsync(
            connection,
            SearchSchema,
            cancellationToken,
            sqliteTransaction);
        if (!hadSearchIndex)
            await ExecuteScriptAsync(
                connection,
                "INSERT INTO tracks_fts(tracks_fts) VALUES('rebuild');",
                cancellationToken,
                sqliteTransaction);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Track?> GetByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM tracks WHERE path = $path COLLATE NOCASE LIMIT 1";
        command.Parameters.AddWithValue("$path", CanonicalPath.Normalize(path));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTrack(reader) : null;
    }

    public async Task<IReadOnlyDictionary<string, LibraryFileStamp>> GetFileIndexAsync(CancellationToken cancellationToken = default)
    {
        var index = new Dictionary<string, LibraryFileStamp>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, path, file_modified_at, file_size, artwork_path FROM tracks";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            index[reader.GetString(1)] = new LibraryFileStamp(
                reader.GetInt64(0),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4));
        return index;
    }

    public Task UpsertAsync(Track track, CancellationToken cancellationToken = default) =>
        UpsertBatchAsync([track], cancellationToken);

    public async Task UpsertBatchAsync(IReadOnlyCollection<Track> tracks, CancellationToken cancellationToken = default)
    {
        if (tracks.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = UpsertSql;
        foreach (var track in tracks)
        {
            command.Parameters.Clear();
            AddTrackParameters(command, track);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveMissingAsync(IReadOnlyCollection<string> roots, CancellationToken cancellationToken = default)
    {
        if (roots.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT id, path FROM tracks";
        var missing = new List<long>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var path = reader.GetString(1);
                if (roots.Any(root => IsWithin(path, root)) && !File.Exists(path)) missing.Add(reader.GetInt64(0));
            }
        }
        if (missing.Count == 0) return;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText = "UPDATE tracks SET is_missing=1, updated_at=$now WHERE id=$id";
        update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var idParameter = update.Parameters.Add("$id", SqliteType.Integer);
        foreach (var id in missing)
        {
            idParameter.Value = id;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkMissingAsync(
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE tracks
            SET is_missing=1, updated_at=$now
            WHERE path=$path COLLATE NOCASE
            """;
        command.Parameters.AddWithValue(
            "$now",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var pathParameter = command.Parameters.Add("$path", SqliteType.Text);
        foreach (var path in paths)
        {
            pathParameter.Value = CanonicalPath.Normalize(path);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RelinkAsync(
        string previousPath,
        Track replacement,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var priorId = await FindTrackIdAsync(
            connection,
            previousPath,
            (SqliteTransaction)transaction,
            cancellationToken);
        if (priorId is null)
        {
            await UpsertTrackAsync(
                connection,
                replacement,
                (SqliteTransaction)transaction,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        await MergePathCollisionAsync(
            connection,
            priorId.Value,
            replacement.Path,
            (SqliteTransaction)transaction,
            cancellationToken);
        await UpdateRelinkedTrackAsync(
            connection,
            priorId.Value,
            replacement,
            (SqliteTransaction)transaction,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RelinkMissingAsync(
        long trackId,
        Track replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(trackId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await MergePathCollisionAsync(
            connection,
            trackId,
            replacement.Path,
            (SqliteTransaction)transaction,
            cancellationToken);
        await UpdateRelinkedTrackAsync(
            connection,
            trackId,
            replacement,
            (SqliteTransaction)transaction,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveTracksAsync(
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        if (trackIds.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM tracks WHERE id=$id";
        var idParameter = command.Parameters.Add("$id", SqliteType.Integer);
        foreach (var id in trackIds.Where(id => id > 0).Distinct())
        {
            idParameter.Value = id;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Track>> GetMissingAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM tracks
            WHERE is_missing=1
            ORDER BY path COLLATE NOCASE
            """;
        return await ReadManyAsync(command, cancellationToken);
    }

    public async Task<long> CountUnderRootAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        var normalized = CanonicalPath.Normalize(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM tracks
            WHERE is_missing=0
              AND (path=$root COLLATE NOCASE OR path LIKE $prefix ESCAPE '\' COLLATE NOCASE)
            """;
        command.Parameters.AddWithValue("$root", normalized);
        command.Parameters.AddWithValue(
            "$prefix",
            EscapeLike(normalized + Path.DirectorySeparatorChar) + "%");
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<Track>> SearchAsync(string query, int limit = 250, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        if (string.IsNullOrWhiteSpace(query))
        {
            command.CommandText = "SELECT * FROM tracks ORDER BY album_artist, album, disc_number, track_number, title LIMIT $limit";
        }
        else
        {
            command.CommandText = """
                SELECT t.* FROM tracks_fts
                JOIN tracks t ON t.id = tracks_fts.rowid
                WHERE tracks_fts MATCH $query
                ORDER BY bm25(tracks_fts, 8.0, 5.0, 4.0, 3.0, 2.0, 1.0, 1.0), t.title COLLATE NOCASE
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$query", BuildSearchExpression(query));
        }
        return await ReadManyAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<Track>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM tracks ORDER BY album_artist COLLATE NOCASE, album COLLATE NOCASE, disc_number, track_number, title COLLATE NOCASE";
        return await ReadManyAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<Track>> GetRecentlyAddedAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM tracks ORDER BY added_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        return await ReadManyAsync(command, cancellationToken);
    }

    public async Task<LibraryStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COUNT(DISTINCT NULLIF(album, '')), COUNT(DISTINCT NULLIF(artist, '')), COALESCE(SUM(duration_ms), 0) FROM tracks WHERE is_missing=0";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new LibraryStats(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), TimeSpan.FromMilliseconds(reader.GetInt64(3)));
    }

    public Task SetRatingAsync(long trackId, int rating, bool loved, CancellationToken cancellationToken = default) =>
        ExecuteAsync("UPDATE tracks SET rating=$rating, loved=$loved WHERE id=$id", [("$rating", Math.Clamp(rating, 0, 5)), ("$loved", loved ? 1 : 0), ("$id", trackId)], cancellationToken);

    public Task RecordPlayAsync(long trackId, CancellationToken cancellationToken = default) =>
        ExecuteAsync("UPDATE tracks SET play_count=play_count+1, last_played_at=$now WHERE id=$id", [("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$id", trackId)], cancellationToken);

    public Task SaveBookmarkAsync(long trackId, TimeSpan position, CancellationToken cancellationToken = default) =>
        ExecuteAsync("INSERT INTO bookmarks(track_id, position_ms, updated_at) VALUES($id,$position,$now) ON CONFLICT(track_id) DO UPDATE SET position_ms=excluded.position_ms, updated_at=excluded.updated_at", [("$id", trackId), ("$position", (long)position.TotalMilliseconds), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())], cancellationToken);

    public async Task<TimeSpan?> GetBookmarkAsync(long trackId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT position_ms FROM bookmarks WHERE track_id=$id";
        command.Parameters.AddWithValue("$id", trackId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long milliseconds ? TimeSpan.FromMilliseconds(milliseconds) : null;
    }

    private async Task ExecuteAsync(string sql, IEnumerable<(string Name, object Value)> parameters, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    internal static async Task<IReadOnlyList<Track>> ReadManyAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var tracks = new List<Track>();
        var strings = new TrackStringPool();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            tracks.Add(strings.Compact(ReadTrack(reader)));
        return tracks;
    }

    internal static Track ReadTrack(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")), Path = r.GetString(r.GetOrdinal("path")), Title = r.GetString(r.GetOrdinal("title")),
        Artist = r.GetString(r.GetOrdinal("artist")), AlbumArtist = r.GetString(r.GetOrdinal("album_artist")), Album = r.GetString(r.GetOrdinal("album")), Genre = r.GetString(r.GetOrdinal("genre")), Comment = r.GetString(r.GetOrdinal("comment")),
        Year = r.GetInt32(r.GetOrdinal("year")), TrackNumber = r.GetInt32(r.GetOrdinal("track_number")), DiscNumber = r.GetInt32(r.GetOrdinal("disc_number")), Duration = TimeSpan.FromMilliseconds(r.GetInt64(r.GetOrdinal("duration_ms"))),
        Bitrate = r.GetInt32(r.GetOrdinal("bitrate")), SampleRate = r.GetInt32(r.GetOrdinal("sample_rate")), BitsPerSample = r.GetInt32(r.GetOrdinal("bits_per_sample")), Channels = r.GetInt32(r.GetOrdinal("channels")), Codec = r.GetString(r.GetOrdinal("codec")),
        ReplayGainTrackDb = NullableDouble(r, "replaygain_track"), ReplayGainAlbumDb = NullableDouble(r, "replaygain_album"), ReplayPeak = NullableDouble(r, "replay_peak"), Rating = r.GetInt32(r.GetOrdinal("rating")), IsLoved = r.GetInt32(r.GetOrdinal("loved")) != 0,
        PlayCount = r.GetInt64(r.GetOrdinal("play_count")), LastPlayedAt = NullableDate(r, "last_played_at"), AddedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("added_at"))), FileModifiedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("file_modified_at"))), FileSize = r.GetInt64(r.GetOrdinal("file_size")), ArtworkPath = NullableString(r, "artwork_path"), Lyrics = r.GetString(r.GetOrdinal("lyrics")),
        IsMissing = r.GetInt32(r.GetOrdinal("is_missing")) != 0
    };

    private static double? NullableDouble(SqliteDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : r.GetDouble(i); }
    private static DateTimeOffset? NullableDate(SqliteDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(i)); }
    private static string? NullableString(SqliteDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : r.GetString(i); }

    private sealed class TrackStringPool
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Track Compact(Track track) => track with
        {
            Artist = Shared(track.Artist),
            AlbumArtist = Shared(track.AlbumArtist),
            Album = Shared(track.Album),
            Genre = Shared(track.Genre),
            Codec = Shared(track.Codec),
            ArtworkPath = track.ArtworkPath is null ? null : Shared(track.ArtworkPath)
        };

        private string Shared(string value)
        {
            if (value.Length == 0) return string.Empty;
            if (_values.TryGetValue(value, out var existing)) return existing;
            _values.Add(value, value);
            return value;
        }
    }

    private static async Task<long?> FindTrackIdAsync(
        SqliteConnection connection,
        string path,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id FROM tracks
            WHERE path=$path COLLATE NOCASE
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$path", CanonicalPath.Normalize(path));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static async Task UpsertTrackAsync(
        SqliteConnection connection,
        Track track,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertSql;
        AddTrackParameters(command, track);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MergePathCollisionAsync(
        SqliteConnection connection,
        long retainedId,
        string replacementPath,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var collisionId = await FindTrackIdAsync(
            connection,
            replacementPath,
            transaction,
            cancellationToken);
        if (collisionId is null || collisionId == retainedId) return;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO playlist_tracks(playlist_id,track_id,position)
            SELECT playlist_id,$retained,position
            FROM playlist_tracks
            WHERE track_id=$collision;

            INSERT OR IGNORE INTO bookmarks(track_id,position_ms,updated_at)
            SELECT $retained,position_ms,updated_at
            FROM bookmarks
            WHERE track_id=$collision;

            DELETE FROM tracks WHERE id=$collision;
            """;
        command.Parameters.AddWithValue("$retained", retainedId);
        command.Parameters.AddWithValue("$collision", collisionId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateRelinkedTrackAsync(
        SqliteConnection connection,
        long trackId,
        Track track,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE tracks SET
              path=$path,title=$title,artist=$artist,album_artist=$album_artist,
              album=$album,genre=$genre,comment=$comment,year=$year,
              track_number=$track_number,disc_number=$disc_number,
              duration_ms=$duration_ms,bitrate=$bitrate,sample_rate=$sample_rate,
              bits_per_sample=$bits_per_sample,channels=$channels,codec=$codec,
              replaygain_track=$replaygain_track,replaygain_album=$replaygain_album,
              replay_peak=$replay_peak,file_modified_at=$file_modified_at,
              file_size=$file_size,artwork_path=$artwork_path,lyrics=$lyrics,
              is_missing=0,updated_at=$now
            WHERE id=$id
            """;
        AddTrackParameters(command, track);
        command.Parameters.AddWithValue("$id", trackId);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0)
            throw new InvalidOperationException(
                $"Library track {trackId} no longer exists.");
    }

    private static void AddTrackParameters(SqliteCommand c, Track t)
    {
        var values = new Dictionary<string, object?>
        {
            ["$path"] = CanonicalPath.Normalize(t.Path), ["$title"] = t.Title, ["$artist"] = t.Artist, ["$album_artist"] = t.AlbumArtist, ["$album"] = t.Album, ["$genre"] = t.Genre, ["$comment"] = t.Comment,
            ["$year"] = t.Year, ["$track_number"] = t.TrackNumber, ["$disc_number"] = t.DiscNumber, ["$duration_ms"] = (long)t.Duration.TotalMilliseconds, ["$bitrate"] = t.Bitrate, ["$sample_rate"] = t.SampleRate,
            ["$bits_per_sample"] = t.BitsPerSample, ["$channels"] = t.Channels, ["$codec"] = t.Codec, ["$replaygain_track"] = t.ReplayGainTrackDb, ["$replaygain_album"] = t.ReplayGainAlbumDb,
            ["$replay_peak"] = t.ReplayPeak, ["$rating"] = t.Rating, ["$loved"] = t.IsLoved ? 1 : 0, ["$play_count"] = t.PlayCount, ["$last_played_at"] = t.LastPlayedAt?.ToUnixTimeMilliseconds(),
            ["$file_modified_at"] = t.FileModifiedAt.ToUnixTimeMilliseconds(), ["$file_size"] = t.FileSize, ["$artwork_path"] = t.ArtworkPath, ["$lyrics"] = t.Lyrics, ["$now"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        foreach (var (name, value) in values) c.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static string BuildSearchExpression(string query)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => $"\"{term.Replace("\"", "\"\"")}\"*");
        return string.Join(" AND ", terms);
    }

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(
            CanonicalPath.Normalize(root),
            CanonicalPath.Normalize(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative);
    }

    private static async Task<bool> ObjectExistsAsync(
        SqliteConnection connection,
        string type,
        string name,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type=$type AND name=$name)";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async Task ExecuteScriptAsync(
        SqliteConnection connection,
        string script,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = script;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string declaration,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = $"PRAGMA table_info({table})";
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration}";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    internal const string BaseSchema = """
        CREATE TABLE IF NOT EXISTS tracks(
          id INTEGER PRIMARY KEY, path TEXT NOT NULL COLLATE NOCASE UNIQUE, title TEXT NOT NULL, artist TEXT NOT NULL DEFAULT '', album_artist TEXT NOT NULL DEFAULT '', album TEXT NOT NULL DEFAULT '', genre TEXT NOT NULL DEFAULT '', comment TEXT NOT NULL DEFAULT '',
          year INTEGER NOT NULL DEFAULT 0, track_number INTEGER NOT NULL DEFAULT 0, disc_number INTEGER NOT NULL DEFAULT 0, duration_ms INTEGER NOT NULL DEFAULT 0, bitrate INTEGER NOT NULL DEFAULT 0, sample_rate INTEGER NOT NULL DEFAULT 0,
          bits_per_sample INTEGER NOT NULL DEFAULT 0, channels INTEGER NOT NULL DEFAULT 0, codec TEXT NOT NULL DEFAULT '', replaygain_track REAL, replaygain_album REAL, replay_peak REAL, rating INTEGER NOT NULL DEFAULT 0, loved INTEGER NOT NULL DEFAULT 0,
          play_count INTEGER NOT NULL DEFAULT 0, last_played_at INTEGER, file_modified_at INTEGER NOT NULL, file_size INTEGER NOT NULL, artwork_path TEXT, lyrics TEXT NOT NULL DEFAULT '', is_missing INTEGER NOT NULL DEFAULT 0, added_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS idx_tracks_artist ON tracks(artist COLLATE NOCASE); CREATE INDEX IF NOT EXISTS idx_tracks_album ON tracks(album COLLATE NOCASE); CREATE INDEX IF NOT EXISTS idx_tracks_added ON tracks(added_at DESC);
        CREATE TABLE IF NOT EXISTS bookmarks(track_id INTEGER PRIMARY KEY REFERENCES tracks(id) ON DELETE CASCADE, position_ms INTEGER NOT NULL, updated_at INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS playlists(id INTEGER PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL DEFAULT 'manual', rules_json TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS playlist_tracks(playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE, track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE, position INTEGER NOT NULL, PRIMARY KEY(playlist_id, track_id));
        CREATE INDEX IF NOT EXISTS idx_playlist_tracks_order ON playlist_tracks(playlist_id, position);
        """;

    internal const string SearchSchema = """
        CREATE VIRTUAL TABLE IF NOT EXISTS tracks_fts USING fts5(title, artist, album, album_artist, genre, path, comment, content='tracks', content_rowid='id', tokenize='unicode61 remove_diacritics 2', prefix='2 3 4');
        CREATE TRIGGER IF NOT EXISTS tracks_ai AFTER INSERT ON tracks BEGIN
          INSERT INTO tracks_fts(rowid,title,artist,album,album_artist,genre,path,comment) VALUES(new.id,new.title,new.artist,new.album,new.album_artist,new.genre,new.path,new.comment);
        END;
        CREATE TRIGGER IF NOT EXISTS tracks_ad AFTER DELETE ON tracks BEGIN
          INSERT INTO tracks_fts(tracks_fts,rowid,title,artist,album,album_artist,genre,path,comment) VALUES('delete',old.id,old.title,old.artist,old.album,old.album_artist,old.genre,old.path,old.comment);
        END;
        CREATE TRIGGER IF NOT EXISTS tracks_au AFTER UPDATE ON tracks BEGIN
          INSERT INTO tracks_fts(tracks_fts,rowid,title,artist,album,album_artist,genre,path,comment) VALUES('delete',old.id,old.title,old.artist,old.album,old.album_artist,old.genre,old.path,old.comment);
          INSERT INTO tracks_fts(rowid,title,artist,album,album_artist,genre,path,comment) VALUES(new.id,new.title,new.artist,new.album,new.album_artist,new.genre,new.path,new.comment);
        END;
        """;

    private const string UpsertSql = """
        INSERT INTO tracks(path,title,artist,album_artist,album,genre,comment,year,track_number,disc_number,duration_ms,bitrate,sample_rate,bits_per_sample,channels,codec,replaygain_track,replaygain_album,replay_peak,rating,loved,play_count,last_played_at,file_modified_at,file_size,artwork_path,lyrics,is_missing,added_at,updated_at)
        VALUES($path,$title,$artist,$album_artist,$album,$genre,$comment,$year,$track_number,$disc_number,$duration_ms,$bitrate,$sample_rate,$bits_per_sample,$channels,$codec,$replaygain_track,$replaygain_album,$replay_peak,$rating,$loved,$play_count,$last_played_at,$file_modified_at,$file_size,$artwork_path,$lyrics,0,$now,$now)
        ON CONFLICT(path) DO UPDATE SET title=excluded.title,artist=excluded.artist,album_artist=excluded.album_artist,album=excluded.album,genre=excluded.genre,comment=excluded.comment,year=excluded.year,track_number=excluded.track_number,disc_number=excluded.disc_number,duration_ms=excluded.duration_ms,bitrate=excluded.bitrate,sample_rate=excluded.sample_rate,bits_per_sample=excluded.bits_per_sample,channels=excluded.channels,codec=excluded.codec,replaygain_track=excluded.replaygain_track,replaygain_album=excluded.replaygain_album,replay_peak=excluded.replay_peak,file_modified_at=excluded.file_modified_at,file_size=excluded.file_size,artwork_path=excluded.artwork_path,lyrics=excluded.lyrics,is_missing=0,updated_at=excluded.updated_at;
        """;
}
