using Dextromethorphan.App.UI;
using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Tests;

public sealed class LibraryGroupingIndexTests
{
    [Fact]
    public void WatcherUpdateRebuildsOnlyAffectedGroups()
    {
        var index = new LibraryGroupingIndex();
        var first = Track(
            1,
            @"C:\Music\Alpha\one.flac",
            "One",
            "Alpha",
            "First");
        var second = Track(
            2,
            @"C:\Music\Beta\two.flac",
            "Two",
            "Beta",
            "Second");
        var before = index.Reset([first, second]);
        var untouchedAlbum = Assert.Single(
            before.Albums,
            card => card.Title == "Second");
        var untouchedArtist = Assert.Single(
            before.Artists,
            card => card.Title == "Beta");

        var updated = first with
        {
            Title = "One (remastered)",
            Genre = "Ambient"
        };
        var result = index.Apply(
        [
            new LibraryTrackUpdate(
                new LibraryFileChange(
                    LibraryFileChangeKind.AddedOrUpdated,
                    first.Path),
                updated)
        ]);
        var after = index.Snapshot();

        Assert.Same(
            untouchedAlbum,
            Assert.Single(after.Albums, card => card.Title == "Second"));
        Assert.Same(
            untouchedArtist,
            Assert.Single(after.Artists, card => card.Title == "Beta"));
        Assert.Contains(
            result.Mutations,
            mutation => mutation.Kind == "Genre"
                        && mutation.Key == "Ambient");
        Assert.DoesNotContain(
            result.Mutations,
            mutation => mutation.Kind == "Album"
                        && mutation.Key.EndsWith(
                            "\0Second",
                            StringComparison.Ordinal));
    }

    [Fact]
    public void MissingAndRelinkedTrackKeepsItsStableSlot()
    {
        var index = new LibraryGroupingIndex();
        var original = Track(
            7,
            @"C:\Music\Old\song.flac",
            "Song",
            "Artist",
            "Album");
        index.Reset([original]);

        var missing = original with { IsMissing = true };
        var missingResult = index.Apply(
        [
            new LibraryTrackUpdate(
                new LibraryFileChange(
                    LibraryFileChangeKind.Missing,
                    original.Path),
                missing)
        ]);

        Assert.Empty(index.Snapshot().Albums);
        Assert.True(missingResult.Tracks[0].IsMissing);

        var moved = original with
        {
            Path = @"D:\Recovered\song.flac",
            IsMissing = false
        };
        var relinked = index.Apply(
        [
            new LibraryTrackUpdate(
                new LibraryFileChange(
                    LibraryFileChangeKind.Relinked,
                    moved.Path,
                    original.Path),
                moved)
        ]);

        Assert.Equal(moved.Path, relinked.Tracks[0].Path);
        Assert.Single(index.Snapshot().Albums);
        Assert.Equal([0], index.Snapshot().Albums[0].TrackIndexes);
    }

    [Fact]
    public void ArtistProjectionKeepsSongsAfterIncrementalRefresh()
    {
        var index = new LibraryGroupingIndex();
        var first = Track(
            11,
            @"C:\Music\Artist\one.flac",
            "One",
            "Artist",
            "First");
        var second = Track(
            12,
            @"C:\Music\Artist\two.flac",
            "Two",
            "Artist",
            "Second");
        index.Reset([first]);

        var update = index.Apply(
        [
            new LibraryTrackUpdate(
                new LibraryFileChange(
                    LibraryFileChangeKind.AddedOrUpdated,
                    second.Path),
                second)
        ]);
        var artist = Assert.Single(index.Snapshot().Artists);
        var tracks = new IndexedReadOnlyList<Track>(
            update.Tracks,
            artist.TrackIndexes);

        Assert.Equal(2, tracks.Count);
        Assert.Equal(["One", "Two"], tracks.Select(track => track.Title));
    }

    [Theory]
    [InlineData("Artists", true, false, true)]
    [InlineData("Albums", true, false, true)]
    [InlineData("Genres", true, false, true)]
    [InlineData("Artists", true, true, false)]
    [InlineData("Songs", true, false, false)]
    [InlineData("Artists", false, false, false)]
    public void CollectionDetailRefreshPolicyPreservesOnlyOpenCollectionViews(
        string view,
        bool isOpen,
        bool resetSelection,
        bool expected)
    {
        var card = new LibraryCardViewModel
        {
            Kind = "Artist",
            Key = "Artist",
            Title = "Artist",
            TrackCount = 1,
            TrackIndexes = [0]
        };

        Assert.Equal(
            expected,
            MainViewModel.ShouldPreserveCollectionDetail(
                view,
                isOpen,
                resetSelection,
                card));
        Assert.False(MainViewModel.ShouldPreserveCollectionDetail(
            view,
            isOpen,
            resetSelection,
            selectedCard: null));
    }

    private static Track Track(
        long id,
        string path,
        string title,
        string artist,
        string album) => new()
        {
            Id = id,
            Path = path,
            Title = title,
            Artist = artist,
            AlbumArtist = artist,
            Album = album,
            Genre = "Rock",
            Year = 2026
        };
}
