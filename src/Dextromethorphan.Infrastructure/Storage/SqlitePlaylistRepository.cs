using System.Text.Json;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Microsoft.Data.Sqlite;

namespace Dextromethorphan.Infrastructure.Storage;

public sealed class SqlitePlaylistRepository(SqliteLibraryRepository library) : IPlaylistRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<Playlist>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await library.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM playlists ORDER BY name COLLATE NOCASE";
        var result = new List<Playlist>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadPlaylist(reader));
        return result;
    }

    public async Task<IReadOnlyList<PlaylistSummary>> GetSummariesAsync(CancellationToken cancellationToken = default)
    {
        var playlists = await GetAllAsync(cancellationToken);
        if (playlists.Count == 0) return [];

        var summaries = new Dictionary<long, PlaylistSummary>();
        await using (var connection = await library.OpenAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT t.*, p.id AS playlist_id, COUNT(pt.track_id) AS track_count
                FROM playlists p
                LEFT JOIN playlist_tracks pt ON pt.playlist_id = p.id
                LEFT JOIN tracks t ON t.id = (
                    SELECT first.track_id
                    FROM playlist_tracks first
                    WHERE first.playlist_id = p.id
                    ORDER BY first.position
                    LIMIT 1
                )
                WHERE p.kind = 'manual'
                GROUP BY p.id
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var playlistId = reader.GetInt64(reader.GetOrdinal("playlist_id"));
                var playlist = playlists.First(x => x.Id == playlistId);
                var idOrdinal = reader.GetOrdinal("id");
                var representative = reader.IsDBNull(idOrdinal) ? null : SqliteLibraryRepository.ReadTrack(reader);
                summaries[playlistId] = new PlaylistSummary(
                    playlist,
                    checked((int)reader.GetInt64(reader.GetOrdinal("track_count"))),
                    representative);
            }
        }

        var smart = playlists.Where(x => x.Kind == PlaylistKind.Smart).ToArray();
        if (smart.Length > 0)
        {
            var smartSummaries = new System.Collections.Concurrent.ConcurrentDictionary<long, PlaylistSummary>();
            await Parallel.ForEachAsync(
                smart,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (playlist, token) => smartSummaries[playlist.Id] = await GetSmartSummaryAsync(playlist, token));
            foreach (var pair in smartSummaries)
                summaries[pair.Key] = pair.Value;
        }

        return playlists
            .Select(playlist => summaries.TryGetValue(playlist.Id, out var summary)
                ? summary
                : new PlaylistSummary(playlist, 0, null))
            .ToArray();
    }

    public async Task<Playlist?> GetAsync(long playlistId, CancellationToken cancellationToken = default)
    {
        await using var connection = await library.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM playlists WHERE id=$id";
        command.Parameters.AddWithValue("$id", playlistId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPlaylist(reader) : null;
    }

    public Task<long> CreateManualAsync(string name, CancellationToken cancellationToken = default) => CreateAsync(name, PlaylistKind.Manual, null, cancellationToken);

    public Task<long> CreateSmartAsync(string name, SmartPlaylistDefinition rules, CancellationToken cancellationToken = default)
    {
        SmartPlaylistSqlCompiler.Compile(rules, DateTimeOffset.UtcNow);
        return CreateAsync(name, PlaylistKind.Smart, JsonSerializer.Serialize(rules, JsonOptions), cancellationToken);
    }

    public async Task UpdateSmartRulesAsync(long playlistId, SmartPlaylistDefinition rules, CancellationToken cancellationToken = default)
    {
        SmartPlaylistSqlCompiler.Compile(rules, DateTimeOffset.UtcNow);
        await using var connection = await library.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE playlists SET rules_json=$rules, updated_at=$now WHERE id=$id AND kind='smart'";
        command.Parameters.AddWithValue("$rules", JsonSerializer.Serialize(rules, JsonOptions));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", playlistId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new InvalidOperationException("The playlist was not found or is not a smart playlist.");
    }

    public async Task RenameAsync(long playlistId, string name, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        await using var connection = await library.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE playlists SET name=$name, updated_at=$now WHERE id=$id";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", playlistId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(long playlistId, CancellationToken cancellationToken = default)
    {
        await using var connection = await library.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM playlists WHERE id=$id";
        command.Parameters.AddWithValue("$id", playlistId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplaceTracksAsync(long playlistId, IReadOnlyList<long> trackIds, CancellationToken cancellationToken = default)
    {
        await using var connection = await library.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureManualAsync(connection, (SqliteTransaction)transaction, playlistId, cancellationToken);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM playlist_tracks WHERE playlist_id=$id";
            delete.Parameters.AddWithValue("$id", playlistId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertTracksAsync(connection, (SqliteTransaction)transaction, playlistId, trackIds, 0, cancellationToken);
        await TouchAsync(connection, (SqliteTransaction)transaction, playlistId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AddTracksAsync(long playlistId, IReadOnlyList<long> trackIds, CancellationToken cancellationToken = default)
    {
        await using var connection = await library.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureManualAsync(connection, (SqliteTransaction)transaction, playlistId, cancellationToken);
        await using var position = connection.CreateCommand();
        position.Transaction = (SqliteTransaction)transaction;
        position.CommandText = "SELECT COALESCE(MAX(position) + 1, 0) FROM playlist_tracks WHERE playlist_id=$id";
        position.Parameters.AddWithValue("$id", playlistId);
        var start = Convert.ToInt32(await position.ExecuteScalarAsync(cancellationToken));
        await InsertTracksAsync(connection, (SqliteTransaction)transaction, playlistId, trackIds, start, cancellationToken);
        await TouchAsync(connection, (SqliteTransaction)transaction, playlistId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Track>> GetTracksAsync(long playlistId, CancellationToken cancellationToken = default)
    {
        var playlist = await GetAsync(playlistId, cancellationToken) ?? throw new KeyNotFoundException("Playlist was not found.");
        await using var connection = await library.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (playlist.Kind == PlaylistKind.Manual)
        {
            command.CommandText = "SELECT t.* FROM playlist_tracks p JOIN tracks t ON t.id=p.track_id WHERE p.playlist_id=$id ORDER BY p.position";
            command.Parameters.AddWithValue("$id", playlistId);
        }
        else
        {
            var compiled = SmartPlaylistSqlCompiler.Compile(playlist.Rules ?? new SmartPlaylistDefinition(), DateTimeOffset.UtcNow);
            command.CommandText = $"SELECT * FROM tracks WHERE {compiled.Where} ORDER BY {compiled.OrderBy}" + (compiled.Limit is null ? "" : " LIMIT $limit");
            foreach (var (name, value) in compiled.Parameters) command.Parameters.AddWithValue(name, value);
            if (compiled.Limit is not null) command.Parameters.AddWithValue("$limit", compiled.Limit.Value);
        }
        return await SqliteLibraryRepository.ReadManyAsync(command, cancellationToken);
    }

    private async Task<PlaylistSummary> GetSmartSummaryAsync(Playlist playlist, CancellationToken cancellationToken)
    {
        var compiled = SmartPlaylistSqlCompiler.Compile(playlist.Rules ?? new SmartPlaylistDefinition(), DateTimeOffset.UtcNow);
        await using var connection = await library.OpenAsync(cancellationToken);

        await using var count = connection.CreateCommand();
        count.CommandText =
            $"SELECT COUNT(*) FROM (SELECT id FROM tracks WHERE {compiled.Where}" +
            (compiled.Limit is null ? ")" : " LIMIT $limit)");
        AddSmartParameters(count, compiled, includeLimit: true);
        var trackCount = checked((int)Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken)));

        await using var representative = connection.CreateCommand();
        representative.CommandText = $"SELECT * FROM tracks WHERE {compiled.Where} ORDER BY {compiled.OrderBy} LIMIT 1";
        AddSmartParameters(representative, compiled, includeLimit: false);
        await using var reader = await representative.ExecuteReaderAsync(cancellationToken);
        var track = await reader.ReadAsync(cancellationToken) ? SqliteLibraryRepository.ReadTrack(reader) : null;
        return new PlaylistSummary(playlist, trackCount, track);
    }

    private static void AddSmartParameters(SqliteCommand command, SmartPlaylistSqlCompiler.Result compiled, bool includeLimit)
    {
        foreach (var (name, value) in compiled.Parameters)
            command.Parameters.AddWithValue(name, value);
        if (includeLimit && compiled.Limit is not null)
            command.Parameters.AddWithValue("$limit", compiled.Limit.Value);
    }

    private async Task<long> CreateAsync(string name, PlaylistKind kind, string? rulesJson, CancellationToken cancellationToken)
    {
        name = ValidateName(name);
        await using var connection = await library.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO playlists(name,kind,rules_json,created_at,updated_at) VALUES($name,$kind,$rules,$now,$now); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$kind", kind == PlaylistKind.Manual ? "manual" : "smart");
        command.Parameters.AddWithValue("$rules", (object?)rulesJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task InsertTracksAsync(SqliteConnection connection, SqliteTransaction transaction, long playlistId, IReadOnlyList<long> trackIds, int start, CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO playlist_tracks(playlist_id,track_id,position) VALUES($playlist,$track,$position)";
        var playlistParameter = insert.Parameters.Add("$playlist", SqliteType.Integer);
        var trackParameter = insert.Parameters.Add("$track", SqliteType.Integer);
        var positionParameter = insert.Parameters.Add("$position", SqliteType.Integer);
        playlistParameter.Value = playlistId;
        var position = start;
        foreach (var trackId in trackIds.Distinct())
        {
            trackParameter.Value = trackId;
            positionParameter.Value = position++;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureManualAsync(SqliteConnection connection, SqliteTransaction transaction, long playlistId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT kind FROM playlists WHERE id=$id";
        command.Parameters.AddWithValue("$id", playlistId);
        var kind = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (kind is null) throw new KeyNotFoundException("Playlist was not found.");
        if (!kind.Equals("manual", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Tracks cannot be directly edited in a smart playlist.");
    }

    private static async Task TouchAsync(SqliteConnection connection, SqliteTransaction transaction, long playlistId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE playlists SET updated_at=$now WHERE id=$id";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", playlistId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Playlist ReadPlaylist(SqliteDataReader reader)
    {
        var rulesOrdinal = reader.GetOrdinal("rules_json");
        var rules = reader.IsDBNull(rulesOrdinal) ? null : JsonSerializer.Deserialize<SmartPlaylistDefinition>(reader.GetString(rulesOrdinal), JsonOptions);
        return new Playlist
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            Kind = reader.GetString(reader.GetOrdinal("kind")).Equals("smart", StringComparison.OrdinalIgnoreCase) ? PlaylistKind.Smart : PlaylistKind.Manual,
            Rules = rules,
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("created_at"))),
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("updated_at")))
        };
    }

    private static string ValidateName(string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 200) throw new ArgumentException("Playlist names must contain 1 to 200 characters.", nameof(name));
        return name;
    }
}
