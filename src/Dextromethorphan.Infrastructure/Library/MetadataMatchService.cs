using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Infrastructure.Library;

/// <summary>Opt-in, read-only metadata lookup. Results are cached and never written without an explicit edit.</summary>
public sealed class MetadataMatchService(HttpClient client, AppPaths paths, ISettingsService settings) : IMetadataMatchService
{
    private static readonly SemaphoreSlim RateGate = new(1, 1);
    private static DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<IReadOnlyList<MetadataMatch>> SearchAsync(Track track, MetadataMatchProvider provider, CancellationToken cancellationToken = default)
    {
        if (!settings.Current.MetadataLookupEnabled || provider == MetadataMatchProvider.MusicBrainz && !settings.Current.MusicBrainzLookupEnabled || provider == MetadataMatchProvider.Discogs && !settings.Current.DiscogsLookupEnabled)
            return [];
        var query = provider == MetadataMatchProvider.MusicBrainz
            ? $"artist:\"{Escape(track.DisplayArtist)}\" AND release:\"{Escape(track.DisplayAlbum)}\""
            : string.Empty;
        var cacheKey = CacheKey($"{provider}|{track.DisplayArtist}|{track.DisplayAlbum}|{track.Year}");
        if (await ReadCacheAsync<List<MetadataMatch>>(cacheKey, cancellationToken) is { } cached) return cached;
        var matches = provider == MetadataMatchProvider.MusicBrainz ? await SearchMusicBrainzAsync(track, query, cancellationToken) : await SearchDiscogsAsync(track, cancellationToken);
        await WriteCacheAsync(cacheKey, matches, cancellationToken);
        return matches;
    }

    public async Task<ArtistProfile?> GetArtistProfileAsync(string artist, MetadataMatchProvider provider, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artist)) return null;
        var cacheKey = CacheKey($"artist|{provider}|{artist}");
        if (await ReadCacheAsync<ArtistProfile>(cacheKey, cancellationToken) is { } cached) return cached;
        ArtistProfile? result = provider == MetadataMatchProvider.MusicBrainz ? await GetMusicBrainzArtistAsync(artist, cancellationToken) : null;
        if (result is not null) await WriteCacheAsync(cacheKey, result, cancellationToken);
        return result;
    }

    private async Task<IReadOnlyList<MetadataMatch>> SearchMusicBrainzAsync(Track track, string query, CancellationToken cancellationToken)
    {
        var uri = "https://musicbrainz.org/ws/2/release/?query=" + Uri.EscapeDataString(query) + "&fmt=json&limit=8";
        using var response = await GetAsync(uri, "Dextromethorphan/0.1 (metadata lookup)", cancellationToken);
        if (!response.IsSuccessStatusCode) return [];
        var payload = await response.Content.ReadFromJsonAsync<MusicBrainzReleaseResponse>(JsonOptions, cancellationToken) ?? new();
        return payload.Releases?.Select(release => new MetadataMatch(MetadataMatchProvider.MusicBrainz, release.Id ?? string.Empty, release.Title ?? track.DisplayAlbum, release.ArtistCredit?.FirstOrDefault()?.Name ?? track.DisplayArtist, release.Title ?? track.DisplayAlbum, ParseYear(release.Date), release.Id is null ? null : $"https://coverartarchive.org/release/{release.Id}/front-250", release.Id is null ? null : $"https://musicbrainz.org/release/{release.Id}", "MusicBrainz / Cover Art Archive")).Where(match => match.ExternalId.Length > 0).ToArray() ?? [];
    }

    private async Task<IReadOnlyList<MetadataMatch>> SearchDiscogsAsync(Track track, CancellationToken cancellationToken)
    {
        var query = $"https://api.discogs.com/database/search?type=release&per_page=8&artist={Uri.EscapeDataString(track.DisplayArtist)}&release_title={Uri.EscapeDataString(track.DisplayAlbum)}";
        if (!string.IsNullOrWhiteSpace(settings.Current.DiscogsUserToken)) query += "&token=" + Uri.EscapeDataString(settings.Current.DiscogsUserToken);
        using var response = await GetAsync(query, "Dextromethorphan/0.1 (metadata lookup)", cancellationToken);
        if (!response.IsSuccessStatusCode) return [];
        var payload = await response.Content.ReadFromJsonAsync<DiscogsResponse>(JsonOptions, cancellationToken) ?? new();
        return payload.Results?.Select(result => new MetadataMatch(MetadataMatchProvider.Discogs, result.Id?.ToString() ?? string.Empty, result.Title ?? track.DisplayAlbum, track.DisplayArtist, result.Title ?? track.DisplayAlbum, result.Year, result.CoverImage, result.ResourceUrl, "Discogs")).Where(match => match.ExternalId.Length > 0).ToArray() ?? [];
    }

    private async Task<ArtistProfile?> GetMusicBrainzArtistAsync(string artist, CancellationToken cancellationToken)
    {
        var search = "https://musicbrainz.org/ws/2/artist/?query=" + Uri.EscapeDataString($"artist:\"{Escape(artist)}\"") + "&fmt=json&limit=1";
        using var response = await GetAsync(search, "Dextromethorphan/0.1 (artist profile)", cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        var payload = await response.Content.ReadFromJsonAsync<MusicBrainzArtistResponse>(JsonOptions, cancellationToken) ?? new();
        var result = payload.Artists?.FirstOrDefault();
        return result?.Id is null ? null : new ArtistProfile(result.Id, result.Name ?? artist, null, null, "MusicBrainz", 0, 0);
    }

    private async Task<HttpResponseMessage> GetAsync(string uri, string userAgent, CancellationToken cancellationToken)
    {
        await RateGate.WaitAsync(cancellationToken);
        try
        {
            var delay = TimeSpan.FromMilliseconds(1_100) - (DateTimeOffset.UtcNow - _lastRequest);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(ProductInfoHeaderValue.Parse(userAgent));
            _lastRequest = DateTimeOffset.UtcNow;
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        finally { RateGate.Release(); }
    }

    private async Task<T?> ReadCacheAsync<T>(string key, CancellationToken cancellationToken)
    {
        var path = CachePath(key); if (!File.Exists(path)) return default;
        try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(settings.Current.MetadataCacheDays)) return default; await using var stream = File.OpenRead(path); return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken); }
        catch (JsonException) { return default; }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
    }

    private async Task WriteCacheAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        try { Directory.CreateDirectory(Path.Combine(paths.Root, "metadata-cache")); await using var stream = File.Create(CachePath(key)); await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string CachePath(string key) => Path.Combine(paths.Root, "metadata-cache", CacheKey(key) + ".json");
    private static string CacheKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    private static int? ParseYear(string? value) => int.TryParse(value?.Split('-', 2)[0], out var year) ? year : null;

    private sealed class MusicBrainzReleaseResponse { public List<MusicBrainzRelease>? Releases { get; set; } }
    private sealed class MusicBrainzRelease { public string? Id { get; set; } public string? Title { get; set; } public string? Date { get; set; } [JsonPropertyName("artist-credit")] public List<MusicBrainzCredit>? ArtistCredit { get; set; } }
    private sealed class MusicBrainzCredit { public string? Name { get; set; } }
    private sealed class MusicBrainzArtistResponse { public List<MusicBrainzArtist>? Artists { get; set; } }
    private sealed class MusicBrainzArtist { public string? Id { get; set; } public string? Name { get; set; } }
    private sealed class DiscogsResponse { public List<DiscogsResult>? Results { get; set; } }
    private sealed class DiscogsResult { public int? Id { get; set; } public string? Title { get; set; } public int? Year { get; set; } [JsonPropertyName("cover_image")] public string? CoverImage { get; set; } [JsonPropertyName("resource_url")] public string? ResourceUrl { get; set; } }
}
