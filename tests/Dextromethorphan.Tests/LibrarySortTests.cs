using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Tests;

public sealed class LibrarySortTests
{
    [Fact]
    public void TrackSortHonorsTheSelectedFieldAndDirection()
    {
        Track[] tracks =
        [
            NewTrack("Later", year: 2024),
            NewTrack("Earlier", year: 1998),
            NewTrack("Middle", year: 2012)
        ];

        var ascending = MainViewModel.SortTracks(tracks, "Year", sortDescending: false);
        var descending = MainViewModel.SortTracks(tracks, "Year", sortDescending: true);

        Assert.Equal(["Earlier", "Middle", "Later"], ascending.Select(track => track.Title));
        Assert.Equal(["Later", "Middle", "Earlier"], descending.Select(track => track.Title));
    }

    [Fact]
    public void CardSortSupportsCodecInsteadOfFallingBackToTitle()
    {
        LibraryCardViewModel[] cards =
        [
            NewCard("Alpha", "FLAC"),
            NewCard("Zulu", "AAC")
        ];

        var sorted = MainViewModel.SortCards(cards, "Codec", sortDescending: false);

        Assert.Equal(["Zulu", "Alpha"], sorted.Select(card => card.Title));
    }

    private static Track NewTrack(string title, int year = 0, string codec = "FLAC") => new()
    {
        Path = $"C:\\music\\{title}.flac",
        Title = title,
        Year = year,
        Codec = codec
    };

    private static LibraryCardViewModel NewCard(string title, string codec) => new()
    {
        Kind = "Album",
        Key = title,
        Title = title,
        TrackCount = 1,
        RepresentativeTrack = NewTrack(title, codec: codec)
    };
}
