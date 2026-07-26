using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Settings;
using Dextromethorphan.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Dextromethorphan.PerformanceFixtures;

public sealed record PerformanceFixtureOptions(
    int TrackCount,
    string OutputRoot,
    int Seed = PerformanceFixtureOptions.DefaultSeed,
    bool Force = false,
    int TracksPerAlbum = 20,
    int AlbumsPerArtist = 5)
{
    public const int DefaultSeed = 20_260_725;
}

public sealed record FixtureProgress(string Stage, int Completed, int Total);

public sealed record PerformanceFixtureManifest
{
    public int SchemaVersion { get; init; } = 1;
    public required string FixtureKind { get; init; }
    public required int Seed { get; init; }
    public required int TrackCount { get; init; }
    public required int AlbumCount { get; init; }
    public required int ArtistCount { get; init; }
    public required int GenreCount { get; init; }
    public required int ArtworkCount { get; init; }
    public required int PlaylistCount { get; init; }
    public required int TracksPerAlbum { get; init; }
    public required int AlbumsPerArtist { get; init; }
    public required string ContentSha256 { get; init; }
    public string DatabaseFile { get; init; } = "library.db";
    public string ArtworkDirectory { get; init; } = "artwork";
    public string SyntheticMediaDirectory { get; init; } = "synthetic-media";
    public string Note { get; init; } = "Metadata/UI benchmark fixture. Synthetic media paths intentionally do not contain playable audio.";
}

public sealed class PerformanceFixtureGenerator
{
    public const string ManifestFileName = "fixture.json";
    public const string MarkerFileName = ".dextromethorphan-performance-fixture";
    private const int ArtworkSize = 256;
    private const int DatabaseBatchSize = 500;
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Genres =
    [
        "Alternative", "Ambient", "Classical", "Electronic", "Film Soundtrack", "Folk",
        "Funk", "Hip-Hop", "Indie", "Jazz", "J-Pop", "Metal",
        "Orchestral", "Pop", "Post-Rock", "Progressive", "Punk", "R&B",
        "Rock", "Synthwave", "Trip-Hop", "Vocal", "World", "Uncategorized"
    ];
    private static readonly string[] TitleWords =
    [
        "Afterglow", "Arc", "Blue", "Cascade", "Clockwork", "Distant", "Echo", "Falling",
        "Glass", "Harbor", "Ivory", "Juniper", "Kite", "Liminal", "Midnight", "Neon",
        "Orbit", "Paper", "Quiet", "Rain", "Signal", "Tide", "Umbra", "Velvet",
        "Waking", "Xeno", "Yearning", "Zenith"
    ];
    private static readonly string[] Codecs = ["FLAC", "MP3", "M4A", "OGG", "OPUS", "WAV"];
    private static readonly int[] SampleRates = [44_100, 48_000, 88_200, 96_000, 192_000];

    public async Task<PerformanceFixtureManifest> GenerateAsync(
        PerformanceFixtureOptions options,
        IProgress<FixtureProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(options);
        var root = PrepareOutput(options);
        var paths = new AppPaths(root);
        paths.EnsureCreated();

        var settings = new JsonSettingsService(paths);
        await settings.InitializeAsync(cancellationToken);
        await settings.UpdateAsync(value =>
        {
            value.AnimationsEnabled = true;
            value.QueuePanelVisible = true;
            value.LibraryFolders.Clear();
            value.ExcludedFolders.Clear();
            value.PlaybackSession = new PlaybackSessionSettings { LastView = "Albums" };
        }, cancellationToken);

        var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(cancellationToken);

        var albumCount = DivideRoundUp(options.TrackCount, options.TracksPerAlbum);
        var artworkPaths = await GenerateArtworkAsync(paths.ArtworkCache, albumCount, options.Seed, progress, cancellationToken);
        var artists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var genres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var artworkPath in artworkPaths)
        {
            var relative = Path.GetRelativePath(root, artworkPath).Replace('\\', '/');
            AppendHash(contentHash, $"art|{relative}|");
            contentHash.AppendData(await File.ReadAllBytesAsync(artworkPath, cancellationToken));
        }

        var batch = new List<Track>(DatabaseBatchSize);
        for (var index = 0; index < options.TrackCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var track = CreateTrack(root, artworkPaths, index, options);
            foreach (var artist in track.Artist.Split([';', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                artists.Add(artist);
            genres.Add(track.Genre);
            batch.Add(track);
            AppendTrackHash(contentHash, root, track);
            if (batch.Count < DatabaseBatchSize && index + 1 < options.TrackCount) continue;
            await repository.UpsertBatchAsync(batch, cancellationToken);
            batch.Clear();
            progress?.Report(new FixtureProgress("Metadata", index + 1, options.TrackCount));
        }

        var playlistCount = Math.Clamp(options.TrackCount / 500, 20, 100);
        await FinalizeDatabaseAsync(paths.DatabaseFile, options.TrackCount, playlistCount, options.Seed, contentHash, progress, cancellationToken);

        var manifest = new PerformanceFixtureManifest
        {
            FixtureKind = options.TrackCount == 10_000 ? "library-10k" : options.TrackCount == 50_000 ? "library-50k" : $"library-{options.TrackCount}",
            Seed = options.Seed,
            TrackCount = options.TrackCount,
            AlbumCount = albumCount,
            ArtistCount = artists.Count,
            GenreCount = genres.Count,
            ArtworkCount = artworkPaths.Count,
            PlaylistCount = playlistCount,
            TracksPerAlbum = options.TracksPerAlbum,
            AlbumsPerArtist = options.AlbumsPerArtist,
            ContentSha256 = Convert.ToHexString(contentHash.GetHashAndReset()).ToLowerInvariant()
        };
        await WriteManifestAsync(root, manifest, cancellationToken);
        return manifest;
    }

    private static void Validate(PerformanceFixtureOptions options)
    {
        if (options.TrackCount <= 0) throw new ArgumentOutOfRangeException(nameof(options), "Track count must be positive.");
        if (options.TracksPerAlbum <= 0) throw new ArgumentOutOfRangeException(nameof(options), "Tracks per album must be positive.");
        if (options.AlbumsPerArtist <= 0) throw new ArgumentOutOfRangeException(nameof(options), "Albums per artist must be positive.");
        if (string.IsNullOrWhiteSpace(options.OutputRoot)) throw new ArgumentException("An output directory is required.", nameof(options));
    }

    private static string PrepareOutput(PerformanceFixtureOptions options)
    {
        var root = Path.GetFullPath(options.OutputRoot);
        if (Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar).Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException("The fixture output cannot be a drive root.");

        if (Directory.Exists(root))
        {
            var hasEntries = Directory.EnumerateFileSystemEntries(root).Any();
            if (hasEntries && !options.Force)
                throw new InvalidOperationException($"The fixture directory is not empty: {root}. Use --force to replace a previous fixture.");
            if (hasEntries)
            {
                var marker = Path.Combine(root, MarkerFileName);
                if (!File.Exists(marker))
                    throw new InvalidOperationException($"Refusing to replace an unmarked directory: {root}");
                Directory.Delete(root, true);
            }
        }

        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, MarkerFileName), "Created by Dextromethorphan.PerformanceFixtures\n", Encoding.UTF8);
        return root;
    }

    private static async Task<IReadOnlyList<string>> GenerateArtworkAsync(
        string artworkRoot,
        int albumCount,
        int seed,
        IProgress<FixtureProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(artworkRoot);
        var paths = new string[albumCount];
        for (var album = 0; album < albumCount; album++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(artworkRoot, $"album-{album + 1:00000}.art");
            await File.WriteAllBytesAsync(path, CreateArtwork(album, seed), cancellationToken);
            paths[album] = path;
            if ((album + 1) % 25 == 0 || album + 1 == albumCount)
                progress?.Report(new FixtureProgress("Artwork", album + 1, albumCount));
        }
        return paths;
    }

    private static byte[] CreateArtwork(int albumIndex, int seed)
    {
        var state = unchecked((uint)(seed * 397) ^ (uint)(albumIndex + 1) * 0x9E3779B9u);
        var redA = (byte)(42 + Next(ref state) % 150);
        var greenA = (byte)(38 + Next(ref state) % 150);
        var blueA = (byte)(48 + Next(ref state) % 150);
        var redB = (byte)(60 + Next(ref state) % 170);
        var greenB = (byte)(50 + Next(ref state) % 170);
        var blueB = (byte)(70 + Next(ref state) % 170);
        var pixels = new byte[ArtworkSize * ArtworkSize * 4];
        var stride = ArtworkSize * 4;

        for (var y = 0; y < ArtworkSize; y++)
        {
            for (var x = 0; x < ArtworkSize; x++)
            {
                var offset = (y * stride) + (x * 4);
                var mix = (x + y) / (double)((ArtworkSize - 1) * 2);
                var ring = ((x - 128) * (x - 128) + (y - 128) * (y - 128) + albumIndex * 97) % 5200 < 850;
                var stripe = ((x * 3 + y * 2 + albumIndex * 11) % 67) < 9;
                var accent = ring ^ stripe ? 0.23 : 0;
                pixels[offset] = Blend(blueA, blueB, Math.Min(1, mix + accent));
                pixels[offset + 1] = Blend(greenA, greenB, Math.Min(1, mix + accent));
                pixels[offset + 2] = Blend(redA, redB, Math.Min(1, mix + accent));
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(ArtworkSize, ArtworkSize, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Track CreateTrack(string root, IReadOnlyList<string> artworkPaths, int index, PerformanceFixtureOptions options)
    {
        var albumIndex = index / options.TracksPerAlbum;
        var artistIndex = albumIndex / options.AlbumsPerArtist;
        var withinAlbum = index % options.TracksPerAlbum;
        var disc = withinAlbum / 10 + 1;
        var trackNumber = withinAlbum % 10 + 1;
        var titleA = TitleWords[(index * 7 + options.Seed) % TitleWords.Length];
        var titleB = TitleWords[(index * 13 + albumIndex) % TitleWords.Length];
        var primaryArtist = $"Synthetic Artist {artistIndex + 1:0000}";
        var artist = index % 41 == 0 ? $"{primaryArtist}; Guest {(index % 31) + 1:00}" : primaryArtist;
        var album = $"Benchmark Album {albumIndex + 1:00000}";
        var relativeMediaPath = Path.Combine(
            "synthetic-media",
            $"Artist-{artistIndex + 1:0000}",
            $"Album-{albumIndex + 1:00000}",
            $"{disc:00}-{trackNumber:00}-Synthetic-Track-{index + 1:000000}.flac");
        var sampleRate = SampleRates[(index + albumIndex) % SampleRates.Length];
        var bits = index % 5 == 0 ? 24 : 16;
        var codec = Codecs[(index + artistIndex) % Codecs.Length];
        var durationSeconds = 90 + Math.Abs((index * 37 + options.Seed) % 330);
        var modified = Epoch.AddMinutes(-(index % 100_000));
        var hasLyrics = index % 25 == 0;

        return new Track
        {
            Path = Path.Combine(root, relativeMediaPath),
            Title = $"{titleA} {titleB} {index + 1:000000}",
            Artist = artist,
            AlbumArtist = primaryArtist,
            Album = album,
            Genre = Genres[(albumIndex + artistIndex) % Genres.Length],
            Comment = index % 19 == 0 ? $"Synthetic benchmark comment {index + 1:000000}" : "",
            Year = 1980 + albumIndex % 47,
            TrackNumber = trackNumber,
            DiscNumber = disc,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            Bitrate = codec == "FLAC" ? 850 + index % 550 : codec == "WAV" ? sampleRate * bits * 2 / 1000 : 128 + (index % 5) * 64,
            SampleRate = sampleRate,
            BitsPerSample = bits,
            Channels = 2,
            Codec = codec,
            ReplayGainTrackDb = -12 + (index % 90) / 10d,
            ReplayGainAlbumDb = -9 + (albumIndex % 45) / 10d,
            ReplayPeak = 0.72 + (index % 280) / 1000d,
            Rating = index % 17 == 0 ? index % 6 : 0,
            IsLoved = index % 29 == 0,
            PlayCount = index % 101,
            LastPlayedAt = index % 7 == 0 ? Epoch.AddDays(-(index % 365)) : null,
            AddedAt = Epoch.AddMinutes(-(index % 50_000)),
            FileModifiedAt = modified,
            FileSize = 2_000_000 + (index * 7919L) % 48_000_000,
            ArtworkPath = artworkPaths[albumIndex],
            Lyrics = hasLyrics ? CreateLyrics(index, durationSeconds) : ""
        };
    }

    private static string CreateLyrics(int index, int durationSeconds)
    {
        var builder = new StringBuilder("[ar:Synthetic Artist]\n[al:Benchmark Fixture]\n");
        var lines = Math.Min(12, Math.Max(4, durationSeconds / 25));
        for (var line = 0; line < lines; line++)
        {
            var second = 5 + line * 20;
            builder.Append(CultureInvariantTimestamp(second));
            builder.Append("Synthetic lyric line ");
            builder.Append(index + 1);
            builder.Append('.');
            builder.Append(line + 1);
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private static string CultureInvariantTimestamp(int seconds) =>
        FormattableString.Invariant($"[{seconds / 60:00}:{seconds % 60:00}.00]");

    private static async Task FinalizeDatabaseAsync(
        string databaseFile,
        int trackCount,
        int playlistCount,
        int seed,
        IncrementalHash contentHash,
        IProgress<FixtureProgress>? progress,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var dates = connection.CreateCommand())
        {
            dates.Transaction = (SqliteTransaction)transaction;
            dates.CommandText = "UPDATE tracks SET added_at=$epoch-(id%50000)*60000, updated_at=$epoch";
            dates.Parameters.AddWithValue("$epoch", Epoch.ToUnixTimeMilliseconds());
            await dates.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insertPlaylist = connection.CreateCommand();
        insertPlaylist.Transaction = (SqliteTransaction)transaction;
        insertPlaylist.CommandText = "INSERT INTO playlists(name,kind,rules_json,created_at,updated_at) VALUES($name,'manual',NULL,$time,$time); SELECT last_insert_rowid();";
        var playlistName = insertPlaylist.Parameters.Add("$name", SqliteType.Text);
        var playlistTime = insertPlaylist.Parameters.Add("$time", SqliteType.Integer);

        await using var insertTrack = connection.CreateCommand();
        insertTrack.Transaction = (SqliteTransaction)transaction;
        insertTrack.CommandText = "INSERT INTO playlist_tracks(playlist_id,track_id,position) VALUES($playlist,$track,$position)";
        var playlistId = insertTrack.Parameters.Add("$playlist", SqliteType.Integer);
        var trackId = insertTrack.Parameters.Add("$track", SqliteType.Integer);
        var position = insertTrack.Parameters.Add("$position", SqliteType.Integer);
        var tracksPerPlaylist = Math.Min(250, trackCount);

        for (var playlist = 0; playlist < playlistCount; playlist++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = $"Benchmark Playlist {playlist + 1:000}";
            playlistName.Value = name;
            playlistTime.Value = Epoch.AddDays(-playlist).ToUnixTimeMilliseconds();
            var id = Convert.ToInt64(await insertPlaylist.ExecuteScalarAsync(cancellationToken));
            AppendHash(contentHash, $"playlist|{name}|{tracksPerPlaylist}\n");

            for (var item = 0; item < tracksPerPlaylist; item++)
            {
                var selectedTrack = 1 + ((playlist * 499 + item * 197 + seed) % trackCount);
                playlistId.Value = id;
                trackId.Value = selectedTrack;
                position.Value = item;
                await insertTrack.ExecuteNonQueryAsync(cancellationToken);
                AppendHash(contentHash, $"{selectedTrack},");
            }
            AppendHash(contentHash, "\n");
            progress?.Report(new FixtureProgress("Playlists", playlist + 1, playlistCount));
        }

        await transaction.CommitAsync(cancellationToken);
        await using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await checkpoint.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AppendTrackHash(IncrementalHash hash, string root, Track track)
    {
        var relativePath = Path.GetRelativePath(root, track.Path).Replace('\\', '/');
        var relativeArtwork = Path.GetRelativePath(root, track.ArtworkPath!).Replace('\\', '/');
        AppendHash(hash, string.Join('|',
            "track", relativePath, track.Title, track.Artist, track.AlbumArtist, track.Album, track.Genre,
            track.Year, track.TrackNumber, track.DiscNumber, (long)track.Duration.TotalMilliseconds,
            track.Bitrate, track.SampleRate, track.BitsPerSample, track.Channels, track.Codec,
            track.Rating, track.IsLoved, track.PlayCount, relativeArtwork, track.Lyrics) + "\n");
    }

    private static void AppendHash(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static async Task WriteManifestAsync(string root, PerformanceFixtureManifest manifest, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(root, ManifestFileName), json + Environment.NewLine, Encoding.UTF8, cancellationToken);
    }

    private static int DivideRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;
    private static byte Blend(byte first, byte second, double amount) => (byte)Math.Clamp(first + (second - first) * amount, 0, 255);

    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }
}
