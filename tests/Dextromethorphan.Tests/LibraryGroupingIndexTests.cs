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
