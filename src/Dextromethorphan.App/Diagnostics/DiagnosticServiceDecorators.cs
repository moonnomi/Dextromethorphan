using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using System.IO;

namespace Dextromethorphan.App.Diagnostics;

internal sealed class DiagnosticLibraryRepository(ILibraryRepository inner, DeveloperDiagnostics diagnostics) : ILibraryRepository
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "library.initialize", () => inner.InitializeAsync(cancellationToken));

    public Task<Track?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "library.get-by-path", () => inner.GetByPathAsync(path, cancellationToken));

    public Task<IReadOnlyDictionary<string, LibraryFileStamp>> GetFileIndexAsync(CancellationToken cancellationToken = default) =>
        MeasureCountAsync("library.file-index", () => inner.GetFileIndexAsync(cancellationToken));

    public Task UpsertAsync(Track track, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "library.upsert-one", () => inner.UpsertAsync(track, cancellationToken));

    public Task UpsertBatchAsync(IReadOnlyCollection<Track> tracks, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "library.upsert-batch", () => inner.UpsertBatchAsync(tracks, cancellationToken),
            new Dictionary<string, object?> { ["count"] = tracks.Count });

    public Task RemoveMissingAsync(IReadOnlyCollection<string> roots, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "library.remove-missing", () => inner.RemoveMissingAsync(roots, cancellationToken),
            new Dictionary<string, object?> { ["roots"] = roots.Count });

    public Task<IReadOnlyList<Track>> GetAllAsync(CancellationToken cancellationToken = default) =>
        MeasureCountAsync("library.get-all", () => inner.GetAllAsync(cancellationToken));

    public Task<IReadOnlyList<Track>> SearchAsync(string query, int limit = 250, CancellationToken cancellationToken = default) =>
        MeasureCountAsync("library.search", () => inner.SearchAsync(query, limit, cancellationToken),
            new Dictionary<string, object?> { ["queryLength"] = query.Length, ["limit"] = limit });

    public Task<IReadOnlyList<Track>> GetRecentlyAddedAsync(int limit = 100, CancellationToken cancellationToken = default) =>
        MeasureCountAsync("library.recent", () => inner.GetRecentlyAddedAsync(limit, cancellationToken),
            new Dictionary<string, object?> { ["limit"] = limit });

    public Task<LibraryStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "library.stats", () => inner.GetStatsAsync(cancellationToken));

    public Task SetRatingAsync(long trackId, int rating, bool loved, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "library.set-rating", () => inner.SetRatingAsync(trackId, rating, loved, cancellationToken));

    public Task RecordPlayAsync(long trackId, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "library.record-play", () => inner.RecordPlayAsync(trackId, cancellationToken));

    public Task SaveBookmarkAsync(long trackId, TimeSpan position, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "library.save-bookmark", () => inner.SaveBookmarkAsync(trackId, position, cancellationToken));

    public Task<TimeSpan?> GetBookmarkAsync(long trackId, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "library.get-bookmark", () => inner.GetBookmarkAsync(trackId, cancellationToken));

    private async Task<T> MeasureCountAsync<T>(string operation, Func<Task<T>> action, Dictionary<string, object?>? data = null)
    {
        if (!diagnostics.Enabled) return await action();
        data ??= [];
        using var scope = diagnostics.Measure("repository", operation, data);
        try
        {
            var result = await action();
            data["count"] = result switch
            {
                System.Collections.ICollection collection => collection.Count,
                IReadOnlyDictionary<string, LibraryFileStamp> dictionary => dictionary.Count,
                _ => null
            };
            return result;
        }
        catch (Exception exception)
        {
            scope.Fail(exception);
            throw;
        }
    }
}

internal sealed class DiagnosticPlaylistRepository(IPlaylistRepository inner, DeveloperDiagnostics diagnostics) : IPlaylistRepository
{
    public Task<IReadOnlyList<Playlist>> GetAllAsync(CancellationToken cancellationToken = default) =>
        MeasureCountAsync("playlist.get-all", () => inner.GetAllAsync(cancellationToken));

    public Task<Playlist?> GetAsync(long playlistId, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "playlist.get", () => inner.GetAsync(playlistId, cancellationToken));

    public Task<long> CreateManualAsync(string name, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "playlist.create-manual", () => inner.CreateManualAsync(name, cancellationToken));

    public Task<long> CreateSmartAsync(string name, SmartPlaylistDefinition rules, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "playlist.create-smart", () => inner.CreateSmartAsync(name, rules, cancellationToken));

    public Task UpdateSmartRulesAsync(long playlistId, SmartPlaylistDefinition rules, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "playlist.update-rules", () => inner.UpdateSmartRulesAsync(playlistId, rules, cancellationToken));

    public Task RenameAsync(long playlistId, string name, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "playlist.rename", () => inner.RenameAsync(playlistId, name, cancellationToken));

    public Task DeleteAsync(long playlistId, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "playlist.delete", () => inner.DeleteAsync(playlistId, cancellationToken));

    public Task ReplaceTracksAsync(long playlistId, IReadOnlyList<long> trackIds, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "playlist.replace-tracks", () => inner.ReplaceTracksAsync(playlistId, trackIds, cancellationToken),
            new Dictionary<string, object?> { ["count"] = trackIds.Count });

    public Task AddTracksAsync(long playlistId, IReadOnlyList<long> trackIds, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("repository", "playlist.add-tracks", () => inner.AddTracksAsync(playlistId, trackIds, cancellationToken),
            new Dictionary<string, object?> { ["count"] = trackIds.Count });

    public Task<IReadOnlyList<Track>> GetTracksAsync(long playlistId, CancellationToken cancellationToken = default) =>
        MeasureCountAsync("playlist.get-tracks", () => inner.GetTracksAsync(playlistId, cancellationToken));

    private async Task<IReadOnlyList<T>> MeasureCountAsync<T>(string operation, Func<Task<IReadOnlyList<T>>> action)
    {
        if (!diagnostics.Enabled) return await action();
        var data = new Dictionary<string, object?>();
        using var scope = diagnostics.Measure("repository", operation, data);
        try
        {
            var result = await action();
            data["count"] = result.Count;
            return result;
        }
        catch (Exception exception)
        {
            scope.Fail(exception);
            throw;
        }
    }
}

internal sealed class DiagnosticArtworkCache(IArtworkCache inner, DeveloperDiagnostics diagnostics) : IArtworkCache
{
    public Task<string?> StoreAsync(string mediaPath, DateTimeOffset modifiedAt, ReadOnlyMemory<byte> artwork, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("artwork", "cache.store", () => inner.StoreAsync(mediaPath, modifiedAt, artwork, cancellationToken),
            new Dictionary<string, object?> { ["bytes"] = artwork.Length });

    public Task<string?> GetOrCreateAsync(string mediaPath, CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("artwork", "cache.lookup", () => inner.GetOrCreateAsync(mediaPath, cancellationToken),
            new Dictionary<string, object?> { ["extension"] = Path.GetExtension(mediaPath) });

    public Task<ArtworkCacheStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("artwork", "cache.stats", () => inner.GetStatsAsync(cancellationToken));

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("artwork", "cache.clear", () => inner.ClearAsync(cancellationToken));

    public Task PruneAsync(CancellationToken cancellationToken = default) =>
        diagnostics.MeasureAsync("artwork", "cache.prune", () => inner.PruneAsync(cancellationToken));
}
