using System.IO;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.App.ViewModels;

/// <summary>
/// Maintains stable, lightweight library groups. Track slots never move during
/// watcher updates, so existing cards keep valid indexes and only groups touched
/// by the changed tracks are rebuilt.
/// </summary>
internal sealed class LibraryGroupingIndex
{
    private readonly List<Track> _tracks = [];
    private readonly Dictionary<long, int> _indexById = [];
    private readonly Dictionary<string, int> _indexByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<GroupIdentity, HashSet<int>> _memberIndexes =
        new(GroupIdentityComparer.Instance);
    private readonly Dictionary<GroupIdentity, LibraryCardViewModel> _cards =
        new(GroupIdentityComparer.Instance);

    public IReadOnlyList<Track> Tracks => _tracks;

    public LibraryGroupSnapshot Reset(IReadOnlyList<Track> tracks)
    {
        _tracks.Clear();
        _tracks.AddRange(tracks);
        RebuildTrackLookups();
        _cards.Clear();
        _memberIndexes.Clear();
        for (var index = 0; index < _tracks.Count; index++)
        {
            foreach (var identity in Memberships(_tracks[index]))
                AddMembership(identity, index);
        }

        foreach (var identity in _memberIndexes.Keys.ToArray())
        {
            var card = BuildCard(identity);
            if (card is not null) _cards[identity] = card;
        }

        return Snapshot();
    }

    public LibraryGroupingUpdate Apply(
        IReadOnlyList<LibraryTrackUpdate> updates)
    {
        var affected = new HashSet<GroupIdentity>(
            GroupIdentityComparer.Instance);

        foreach (var update in updates)
        {
            var index = FindTrackIndex(update);
            if (update.Track is null) continue;
            if (index >= 0)
            {
                var oldTrack = _tracks[index];
                foreach (var identity in Memberships(oldTrack))
                {
                    affected.Add(identity);
                    RemoveMembership(identity, index);
                }
                if (oldTrack.Id > 0) _indexById.Remove(oldTrack.Id);
                _indexByPath.Remove(oldTrack.Path);
                _tracks[index] = update.Track;
            }
            else
            {
                index = _tracks.Count;
                _tracks.Add(update.Track);
            }

            foreach (var identity in Memberships(update.Track))
            {
                affected.Add(identity);
                AddMembership(identity, index);
            }
            if (update.Track.Id > 0)
                _indexById[update.Track.Id] = index;
            _indexByPath[update.Track.Path] = index;
        }

        var mutations = new List<LibraryGroupMutation>(affected.Count);
        foreach (var identity in affected)
        {
            _cards.TryGetValue(identity, out var previous);
            var replacement = BuildCard(identity);
            if (replacement is null) _cards.Remove(identity);
            else _cards[identity] = replacement;
            mutations.Add(new LibraryGroupMutation(
                identity.Kind,
                identity.Key,
                previous,
                replacement));
        }

        return new LibraryGroupingUpdate(
            _tracks.ToArray(),
            mutations,
            affected.Select(identity => identity.Kind)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public LibraryGroupSnapshot Snapshot() => new(
        Cards("Album"),
        Cards("Artist"),
        Cards("Genre"),
        Cards("Folder"));

    private IReadOnlyList<LibraryCardViewModel> Cards(string kind) =>
        _cards
            .Where(pair => pair.Key.Kind.Equals(
                kind,
                StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .OrderBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(card => card.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private int FindTrackIndex(LibraryTrackUpdate update)
    {
        if (update.Track is { Id: > 0 } track
            && _indexById.TryGetValue(track.Id, out var byId))
            return byId;
        if (!string.IsNullOrWhiteSpace(update.Change.PreviousPath)
            && _indexByPath.TryGetValue(
                update.Change.PreviousPath,
                out var byPreviousPath))
            return byPreviousPath;
        return _indexByPath.TryGetValue(update.Change.Path, out var byPath)
            ? byPath
            : -1;
    }

    private void RebuildTrackLookups()
    {
        _indexById.Clear();
        _indexByPath.Clear();
        for (var index = 0; index < _tracks.Count; index++)
        {
            var track = _tracks[index];
            if (track.Id > 0) _indexById[track.Id] = index;
            _indexByPath[track.Path] = index;
        }
    }

    private void AddMembership(GroupIdentity identity, int index)
    {
        if (!_memberIndexes.TryGetValue(identity, out var indexes))
        {
            indexes = [];
            _memberIndexes.Add(identity, indexes);
        }
        indexes.Add(index);
    }

    private void RemoveMembership(GroupIdentity identity, int index)
    {
        if (!_memberIndexes.TryGetValue(identity, out var indexes)) return;
        indexes.Remove(index);
        if (indexes.Count == 0) _memberIndexes.Remove(identity);
    }

    private LibraryCardViewModel? BuildCard(GroupIdentity identity)
    {
        if (!_memberIndexes.TryGetValue(identity, out var indexes)
            || indexes.Count == 0)
            return null;
        var members = indexes
            .Select(index => new IndexedTrack(index, _tracks[index]))
            .ToArray();

        return identity.Kind switch
        {
            "Album" => BuildAlbum(identity, members),
            "Artist" => BuildArtist(identity, members),
            "Genre" => BuildGenre(identity, members),
            "Folder" => BuildFolder(identity, members),
            _ => null
        };
    }

    private LibraryCardViewModel BuildAlbum(
        GroupIdentity identity,
        IReadOnlyList<IndexedTrack> members)
    {
        var first = members[0].Track;
        var artist = string.IsNullOrWhiteSpace(first.AlbumArtist)
            ? first.DisplayArtist
            : first.AlbumArtist;
        var indexes = members
            .OrderBy(item => item.Track.DiscNumber)
            .ThenBy(item => item.Track.TrackNumber)
            .ThenBy(item => item.Track.Title)
            .Select(item => item.Index)
            .ToArray();
        var year = members.Max(item => item.Track.Year);
        return Card(
            identity,
            first.DisplayAlbum,
            year > 0 ? $"{artist} · {year}" : artist,
            indexes);
    }

    private LibraryCardViewModel BuildArtist(
        GroupIdentity identity,
        IReadOnlyList<IndexedTrack> members)
    {
        var albumCount = members
            .Select(item => item.Track.DisplayAlbum)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Count();
        return Card(
            identity,
            identity.Key,
            albumCount == 1 ? "1 album" : $"{albumCount:N0} albums",
            members
                .OrderBy(item => item.Track.Year)
                .ThenBy(item => item.Track.Album)
                .ThenBy(item => item.Track.TrackNumber)
                .Select(item => item.Index)
                .ToArray());
    }

    private LibraryCardViewModel BuildGenre(
        GroupIdentity identity,
        IReadOnlyList<IndexedTrack> members) =>
        Card(
            identity,
            identity.Key,
            "Genre",
            members
                .OrderBy(item => item.Track.Artist)
                .ThenBy(item => item.Track.Album)
                .ThenBy(item => item.Track.TrackNumber)
                .Select(item => item.Index)
                .ToArray());

    private LibraryCardViewModel BuildFolder(
        GroupIdentity identity,
        IReadOnlyList<IndexedTrack> members)
    {
        var name = Path.GetFileName(
            identity.Key.TrimEnd(Path.DirectorySeparatorChar));
        return Card(
            identity,
            string.IsNullOrWhiteSpace(name) ? identity.Key : name,
            identity.Key,
            members
                .OrderBy(item => item.Track.Path, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Index)
                .ToArray(),
            identity.Key);
    }

    private LibraryCardViewModel Card(
        GroupIdentity identity,
        string title,
        string subtitle,
        IReadOnlyList<int> indexes,
        string detail = "")
    {
        var representative = indexes.Count > 0
            ? _tracks[indexes[0]]
            : null;
        return new LibraryCardViewModel
        {
            Kind = identity.Kind,
            Key = identity.Key,
            Title = title,
            Subtitle = subtitle,
            Detail = detail,
            TrackIndexes = indexes,
            TrackCount = indexes.Count,
            RepresentativeTrack = representative,
            ArtworkPath = string.IsNullOrWhiteSpace(
                representative?.ArtworkPath)
                ? null
                : representative.ArtworkPath
        };
    }

    private static IEnumerable<GroupIdentity> Memberships(Track track)
    {
        if (track.IsMissing) yield break;

        var albumArtist = string.IsNullOrWhiteSpace(track.AlbumArtist)
            ? track.DisplayArtist
            : track.AlbumArtist;
        yield return new GroupIdentity(
            "Album",
            $"{albumArtist}\0{track.DisplayAlbum}");

        foreach (var artist in SplitValues(track.DisplayArtist))
            yield return new GroupIdentity("Artist", artist);
        foreach (var genre in SplitValues(track.Genre, "Uncategorized"))
            yield return new GroupIdentity("Genre", genre);
        yield return new GroupIdentity(
            "Folder",
            Path.GetDirectoryName(track.Path) ?? track.Path);
    }

    private static IEnumerable<string> SplitValues(
        string? value,
        string fallback = "Unknown artist")
    {
        if (string.IsNullOrWhiteSpace(value)) return [fallback];
        var values = value
            .Split(
                [';', '/'],
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return values.Length == 0 ? [fallback] : values;
    }

    private sealed record GroupIdentity(string Kind, string Key);
    private sealed record IndexedTrack(int Index, Track Track);

    private sealed class GroupIdentityComparer :
        IEqualityComparer<GroupIdentity>
    {
        public static GroupIdentityComparer Instance { get; } = new();

        public bool Equals(GroupIdentity? x, GroupIdentity? y) =>
            ReferenceEquals(x, y)
            || (x is not null
                && y is not null
                && x.Kind.Equals(y.Kind, StringComparison.OrdinalIgnoreCase)
                && x.Key.Equals(y.Key, StringComparison.OrdinalIgnoreCase));

        public int GetHashCode(GroupIdentity obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Kind),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key));
    }
}

internal sealed record LibraryTrackUpdate(
    LibraryFileChange Change,
    Track? Track);

internal sealed record LibraryGroupMutation(
    string Kind,
    string Key,
    LibraryCardViewModel? Previous,
    LibraryCardViewModel? Replacement);

internal sealed record LibraryGroupingUpdate(
    IReadOnlyList<Track> Tracks,
    IReadOnlyList<LibraryGroupMutation> Mutations,
    IReadOnlyList<string> AffectedKinds);

internal sealed record LibraryGroupSnapshot(
    IReadOnlyList<LibraryCardViewModel> Albums,
    IReadOnlyList<LibraryCardViewModel> Artists,
    IReadOnlyList<LibraryCardViewModel> Genres,
    IReadOnlyList<LibraryCardViewModel> Folders);
