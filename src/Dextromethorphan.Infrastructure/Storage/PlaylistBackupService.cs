using System.Text.Json;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Infrastructure.Storage;

public sealed class PlaylistBackupService(IPlaylistRepository playlists, AppPaths paths) : IPlaylistBackupService
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string BackupPath => Path.Combine(paths.DatabaseBackups, "playlists.json");

    public async Task BackupAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var payload = new List<BackupPlaylist>();
        foreach (var playlist in await playlists.GetAllAsync(cancellationToken))
        {
            var tracks = await playlists.GetTracksAsync(playlist.Id, cancellationToken);
            payload.Add(new BackupPlaylist(playlist.Name, playlist.Kind, playlist.Description, playlist.CoverPath is null ? null : ToPortablePath(playlist.CoverPath), playlist.Rules, tracks.Select(track => ToPortablePath(track.Path)).ToArray()));
        }
        var temporary = BackupPath + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, new BackupDocument(DateTimeOffset.UtcNow, paths.IsPortable, payload), Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        try
        {
            if (File.Exists(BackupPath)) File.Replace(temporary, BackupPath, null, true);
            else File.Move(temporary, BackupPath);
        }
        catch (PlatformNotSupportedException) { File.Move(temporary, BackupPath, true); }
        catch (IOException) { File.Move(temporary, BackupPath, true); }
    }

    private string ToPortablePath(string path) => paths.IsPortable ? Path.GetRelativePath(paths.Root, path) : path;

    private sealed record BackupDocument(DateTimeOffset CreatedAt, bool Portable, IReadOnlyList<BackupPlaylist> Playlists);
    private sealed record BackupPlaylist(string Name, PlaylistKind Kind, string Description, string? CoverPath, SmartPlaylistDefinition? Rules, IReadOnlyList<string> Paths);
}
