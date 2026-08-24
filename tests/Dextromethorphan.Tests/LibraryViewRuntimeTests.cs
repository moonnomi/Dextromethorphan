using System.Windows;
using Dextromethorphan.App.UI.Views;
using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Tests;

public sealed class LibraryViewRuntimeTests
{
    [Fact]
    public void TrackLayoutUsesTheCompleteSettingsColumnInventory()
    {
        Assert.Equal(
            SettingsOptionCoverage.TrackColumns,
            TrackListColumnLayout.ColumnNames);
    }

    [Fact]
    public void TrackLayoutAppliesOrderVisibilityAndConfiguredWidthsIndependently()
    {
        var layout = TrackListColumnLayout.Resolve(
            ["source", "Year", "Title", "Source", "unknown"],
            ["SOURCE", "Year"],
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Source"] = 333,
                ["Year"] = double.NaN
            });

        Assert.Equal(TrackListColumnLayout.ColumnNames.Count, layout.Count);
        Assert.Equal(
            ["Source", "Year", "Title"],
            layout.Take(3).Select(column => column.Name));
        Assert.Equal(
            layout.Count,
            layout.Select(column => column.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());

        var source = layout.Single(column => column.Name == "Source");
        Assert.True(source.IsVisible);
        Assert.Equal(GridUnitType.Pixel, source.Width.GridUnitType);
        Assert.Equal(333, source.Width.Value);

        var year = layout.Single(column => column.Name == "Year");
        Assert.True(year.IsVisible);
        Assert.Equal(64, year.Width.Value);

        var title = layout.Single(column => column.Name == "Title");
        Assert.False(title.IsVisible);
        Assert.Equal(0, title.Width.Value);
    }

    [Theory]
    [InlineData(LibraryDensity.Compact, 48, 58)]
    [InlineData(LibraryDensity.Comfortable, 58, 76)]
    [InlineData(LibraryDensity.Grid, 52, 48)]
    public void DensityHasConcreteTrackAndGalleryMetrics(
        LibraryDensity density,
        double expectedTrackHeight,
        double expectedGalleryMetadataHeight)
    {
        Assert.Equal(
            expectedTrackHeight,
            TrackListColumnLayout.TrackRowHeight(density));
        Assert.Equal(
            expectedGalleryMetadataHeight,
            TrackListColumnLayout.GalleryMetadataHeight(density));
    }

    [Fact]
    public void TrackPresentationFiltersBeforeApplyingConfiguredSort()
    {
        Track[] tracks =
        [
            NewTrack("FLAC late", "FLAC", 2024),
            NewTrack("MP3 middle", "MP3", 2018),
            NewTrack("ALAC early", "ALAC", 1999)
        ];

        var result = MainViewModel.ApplyTrackViewSettings(
            tracks,
            "lossless",
            "Year",
            sortDescending: false);

        Assert.Equal(
            ["ALAC early", "FLAC late"],
            result.Select(track => track.Title));
    }

    [Fact]
    public void RuntimeSortInventoryMatchesSettingsLabelsAndLegacyAliases()
    {
        Assert.Equal(
            [
                "Title", "Artist", "Album", "Year", "Duration", "Date added",
                "Last played", "Play count", "Rating", "Codec"
            ],
            MainViewModel.SupportedSortOptions);
        Assert.Equal("Date added", MainViewModel.NormalizeSortField("Added"));
        Assert.Equal("Last played", MainViewModel.NormalizeSortField("played"));
        Assert.Equal("Play count", MainViewModel.NormalizeSortField("PLAY COUNT"));
        Assert.Equal("Title", MainViewModel.NormalizeSortField("unsupported"));
    }

    [Fact]
    public void TrackPresentationSupportsPlayCountFromSettings()
    {
        Track[] tracks =
        [
            NewTrack("Often", "FLAC", 2020, playCount: 20),
            NewTrack("Sometimes", "FLAC", 2020, playCount: 3)
        ];

        var result = MainViewModel.ApplyTrackViewSettings(
            tracks,
            quickFilter: null,
            sortBy: "Play count",
            sortDescending: true);

        Assert.Equal(
            ["Often", "Sometimes"],
            result.Select(track => track.Title));
    }

    [Fact]
    public void GalleryPresentationFiltersUsingConstituentTracksThenSortsCards()
    {
        var flac = NewTrack("Lossless", "FLAC", 2020);
        var mp3 = NewTrack("Lossy", "MP3", 2021);
        LibraryCardViewModel[] cards =
        [
            Card("Zulu"),
            Card("Alpha"),
            Card("Loading")
        ];
        var tracksByCard = new Dictionary<string, IReadOnlyList<Track>?>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Zulu"] = [flac],
            ["Alpha"] = [mp3],
            ["Loading"] = null
        };

        var result = MainViewModel.ApplyCardViewSettings(
            cards,
            card => tracksByCard[card.Key],
            "codec:flac",
            "Title",
            sortDescending: false);

        Assert.Equal(
            ["Loading", "Zulu"],
            result.Select(card => card.Title));
    }

    private static Track NewTrack(
        string title,
        string codec,
        int year,
        long playCount = 0) => new()
    {
        Path = $@"C:\music\{title}.{codec.ToLowerInvariant()}",
        Title = title,
        Codec = codec,
        Year = year,
        PlayCount = playCount
    };

    private static LibraryCardViewModel Card(string title) => new()
    {
        Kind = "Album",
        Key = title,
        Title = title,
        TrackCount = 1
    };
}
