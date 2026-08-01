using Dextromethorphan.App.UI;
using Dextromethorphan.App.ViewModels;

namespace Dextromethorphan.Tests;

public sealed class GalleryRowLayoutTests
{
    [Fact]
    public void PackPreservesEveryCardExactlyOnceAcrossPartialFinalRow()
    {
        var cards = Enumerable.Range(0, 302).Select(Card).ToArray();

        var rows = GalleryRowLayout.Pack(cards, 9);

        Assert.Equal(34, rows.Count);
        Assert.Equal(9, rows[0].Cards.Count);
        Assert.Equal(5, rows[^1].Cards.Count);
        Assert.Equal(cards, rows.SelectMany(row => row.Cards));
        Assert.Equal(
            Enumerable.Range(0, rows.Count).Select(index => index * 9),
            rows.Select(row => row.StartIndex));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(17)]
    public void RepackingNeverDropsOrDuplicatesCards(int columns)
    {
        var cards = Enumerable.Range(0, 2_500).Select(Card).ToArray();

        var packed = GalleryRowLayout.Pack(cards, columns)
            .SelectMany(row => row.Cards)
            .ToArray();

        Assert.Equal(cards, packed);
        Assert.Equal(cards.Length, packed.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    [Fact]
    public void PackTreatsNonPositiveColumnCountAsOne()
    {
        var cards = Enumerable.Range(0, 3).Select(Card).ToArray();

        var rows = GalleryRowLayout.Pack(cards, 0);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Single(row.Cards));
    }

    private static LibraryCardViewModel Card(int index) => new()
    {
        Kind = "Album",
        Key = index.ToString(),
        Title = $"Album {index}",
        TrackCount = 1
    };
}
