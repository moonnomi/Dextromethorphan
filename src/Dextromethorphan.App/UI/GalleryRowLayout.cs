using Dextromethorphan.App.ViewModels;

namespace Dextromethorphan.App.UI;

internal static class GalleryRowLayout
{
    public static IReadOnlyList<GalleryRowViewModel> Pack(
        IReadOnlyList<LibraryCardViewModel> cards,
        int columns)
    {
        ArgumentNullException.ThrowIfNull(cards);
        columns = Math.Max(1, columns);
        if (cards.Count == 0) return [];

        var rows = new List<GalleryRowViewModel>(
            (int)Math.Ceiling(cards.Count / (double)columns));
        for (var start = 0; start < cards.Count; start += columns)
        {
            var count = Math.Min(columns, cards.Count - start);
            var row = new LibraryCardViewModel[count];
            for (var offset = 0; offset < count; offset++)
                row[offset] = cards[start + offset];
            rows.Add(new GalleryRowViewModel(start, row));
        }
        return rows;
    }
}
