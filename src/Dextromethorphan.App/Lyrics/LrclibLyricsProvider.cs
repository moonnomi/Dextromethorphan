using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.App.Lyrics;

public sealed class LrclibLyricsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;
    private readonly AppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly TimeSpan _minimumRequestInterval;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public LrclibLyricsProvider(
        HttpClient httpClient,
        AppPaths paths,
        ISettingsService settings,
        TimeSpan? minimumRequestInterval = null)
    {
        _httpClient = httpClient;
        _paths = paths;
        _settings = settings;
        _minimumRequestInterval = minimumRequestInterval ?? TimeSpan.FromMilliseconds(400);
    }

    public async Task<IReadOnlyList<LyricsDocument>> SearchAsync(
        Track track,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Current.OnlineLyricsEnabled)
            throw new InvalidOperationException("Online lyric lookup is disabled. Enable it in Lyrics settings first.");

        var cached = await ReadSearchCacheAsync(track, cancellationToken);
        if (cached is not null) return cached;

        var records = new List<LrclibRecord>();
        if (track.Duration > TimeSpan.Zero)
        {
            var exact = await GetExactAsync(track, cancellationToken);
            if (exact is not null) records.Add(exact);
        }
        if (records.Count == 0)
            records.AddRange(await SearchRecordsAsync(track, cancellationToken));

        records = records
            .Where(HasUsableLyrics)
            .GroupBy(record => record.Id)
            .Select(group => group.First())
            .Take(20)
            .ToList();
        foreach (var record in records)
            await WriteRecordAsync(record, cancellationToken);
        await WriteSearchCacheAsync(track, records, cancellationToken);
        return records.Select(ToDocument).ToArray();
    }

    public async Task<LyricsDocument?> LoadSelectedAsync(
        Track track,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Current.OnlineLyricsEnabled
            || !_settings.Current.SelectedOnlineLyrics.TryGetValue(track.Path, out var id)
            || id <= 0)
            return null;
        var record = await ReadRecordAsync(id, cancellationToken);
        return record is null || !HasUsableLyrics(record) ? null : ToDocument(record);
    }

    public async Task RememberSelectionAsync(
        Track track,
        LyricsDocument document,
        CancellationToken cancellationToken = default)
    {
        if (document.Kind != LyricsSourceKind.OnlineCache || document.SourceId is not > 0)
            throw new InvalidOperationException("Only a confirmed online lyric result can be remembered.");
        await _settings.UpdateAsync(
            settings => settings.SelectedOnlineLyrics[track.Path] = document.SourceId.Value,
            cancellationToken);
    }

    private async Task<LrclibRecord?> GetExactAsync(Track track, CancellationToken cancellationToken)
    {
        var uri = BuildUri("api/get", new Dictionary<string, string>
        {
            ["track_name"] = track.Title,
            ["artist_name"] = track.DisplayArtist,
            ["album_name"] = track.DisplayAlbum,
            ["duration"] = Math.Round(track.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture)
        });
        using var response = await SendAsync(uri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<LrclibRecord>(stream, JsonOptions, cancellationToken);
    }

    private async Task<IReadOnlyList<LrclibRecord>> SearchRecordsAsync(Track track, CancellationToken cancellationToken)
    {
        var uri = BuildUri("api/search", new Dictionary<string, string>
        {
            ["track_name"] = track.Title,
            ["artist_name"] = track.DisplayArtist,
            ["album_name"] = track.DisplayAlbum
        });
        using var response = await SendAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<List<LrclibRecord>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var remaining = _minimumRequestInterval - (DateTimeOffset.UtcNow - _lastRequestAt);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    "Dextromethorphan/1.0 (https://github.com/moonnomi/Dextromethorphan)");
                _lastRequestAt = DateTimeOffset.UtcNow;
                var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt > 0)
                    return response;
                var retry = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                    ?? TimeSpan.FromSeconds(1);
                response.Dispose();
                await Task.Delay(Clamp(retry, TimeSpan.Zero, TimeSpan.FromSeconds(30)), cancellationToken);
            }
            throw new HttpRequestException("LRCLIB did not accept the lyric request.");
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<IReadOnlyList<LyricsDocument>?> ReadSearchCacheAsync(Track track, CancellationToken cancellationToken)
    {
        var path = SearchCachePath(track);
        try
        {
            if (!File.Exists(path)) return null;
            await using var stream = File.OpenRead(path);
            var cache = await JsonSerializer.DeserializeAsync<LrclibSearchCache>(stream, JsonOptions, cancellationToken);
            if (cache is null || DateTimeOffset.UtcNow - cache.CachedAt > TimeSpan.FromDays(30)) return null;
            return cache.Results.Where(HasUsableLyrics).Select(ToDocument).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task WriteSearchCacheAsync(Track track, IReadOnlyList<LrclibRecord> records, CancellationToken cancellationToken) =>
        await WriteJsonAtomicallyAsync(
            SearchCachePath(track),
            new LrclibSearchCache(DateTimeOffset.UtcNow, records.ToList()),
            cancellationToken);

    private async Task WriteRecordAsync(LrclibRecord record, CancellationToken cancellationToken) =>
        await WriteJsonAtomicallyAsync(RecordPath(record.Id), record, cancellationToken);

    private async Task<LrclibRecord?> ReadRecordAsync(long id, CancellationToken cancellationToken)
    {
        try
        {
            var path = RecordPath(id);
            if (!File.Exists(path)) return null;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<LrclibRecord>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task WriteJsonAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private string SearchCachePath(Track track)
    {
        var signature = string.Join('\n', track.Title, track.DisplayArtist, track.DisplayAlbum, Math.Round(track.Duration.TotalSeconds));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
        return Path.Combine(_paths.LyricsCache, "lrclib", "queries", key + ".json");
    }

    private string RecordPath(long id) => Path.Combine(_paths.LyricsCache, "lrclib", "records", id + ".json");

    private static Uri BuildUri(string path, IReadOnlyDictionary<string, string> values)
    {
        var query = string.Join("&", values
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri($"https://lrclib.net/{path}?{query}");
    }

    private static bool HasUsableLyrics(LrclibRecord record) =>
        record.Instrumental
        || !string.IsNullOrWhiteSpace(record.SyncedLyrics)
        || !string.IsNullOrWhiteSpace(record.PlainLyrics);

    private static LyricsDocument ToDocument(LrclibRecord record)
    {
        var synced = !string.IsNullOrWhiteSpace(record.SyncedLyrics);
        var content = synced
            ? record.SyncedLyrics!
            : !string.IsNullOrWhiteSpace(record.PlainLyrics)
                ? record.PlainLyrics!
                : "[00:00][instrumental]";
        var title = string.IsNullOrWhiteSpace(record.TrackName) ? "Untitled" : record.TrackName.Trim();
        var artist = string.IsNullOrWhiteSpace(record.ArtistName) ? "Unknown artist" : record.ArtistName.Trim();
        return new LyricsDocument(
            content,
            $"{title} — {artist} ({(synced ? "synced" : record.Instrumental ? "instrumental" : "plain")})",
            LyricsSourceKind.OnlineCache,
            Attribution: "LRCLIB · lrclib.net",
            SourceId: record.Id);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    private sealed record LrclibSearchCache(DateTimeOffset CachedAt, List<LrclibRecord> Results);

    private sealed class LrclibRecord
    {
        [JsonPropertyName("id")] public long Id { get; init; }
        [JsonPropertyName("trackName")] public string TrackName { get; init; } = string.Empty;
        [JsonPropertyName("artistName")] public string ArtistName { get; init; } = string.Empty;
        [JsonPropertyName("albumName")] public string AlbumName { get; init; } = string.Empty;
        [JsonPropertyName("duration")] public double Duration { get; init; }
        [JsonPropertyName("instrumental")] public bool Instrumental { get; init; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; init; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; init; }
    }
}
