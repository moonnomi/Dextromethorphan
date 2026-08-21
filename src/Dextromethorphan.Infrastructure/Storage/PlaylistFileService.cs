using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Infrastructure.Storage;

public sealed class PlaylistFileService(
    IPlaylistRepository playlists,
    ILibraryRepository library,
    IPlaylistInterchangeService interchange) : IPlaylistFileService
{
    public PlaylistImportReport? LastImportReport { get; private set; }

    public async Task<long> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        var imported = await interchange.ImportAsync(path, cancellationToken);
        var index = await library.GetFileIndexAsync(cancellationToken);
        var resolved = imported.Locations.Where(location => index.ContainsKey(location)).ToArray();
        var missing = imported.Locations.Where(location => !index.ContainsKey(location)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var trackIds = resolved.Select(location => index[location].Id).ToArray();
        LastImportReport = new PlaylistImportReport(imported.Locations.Count, trackIds.Length, missing.Length, missing);
        var playlistId = await playlists.CreateManualAsync(imported.Name, cancellationToken);
        await playlists.ReplaceTracksAsync(playlistId, trackIds, cancellationToken);
        return playlistId;
    }

    public async Task ExportAsync(long playlistId, string path, PlaylistFormat format, CancellationToken cancellationToken = default)
    {
        var playlist = await playlists.GetAsync(playlistId, cancellationToken) ?? throw new KeyNotFoundException("Playlist was not found.");
        var tracks = await playlists.GetTracksAsync(playlistId, cancellationToken);
        await interchange.ExportAsync(path, playlist.Name, tracks, format, cancellationToken);
    }
}
