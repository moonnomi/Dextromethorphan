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

    /// <summary>
    /// Determines whether a repack describes the exact same card references.
    /// Avoiding a redundant Reset notification is important: WPF can briefly
    /// discard every virtualized row when a hidden gallery is reset and shown
    /// again during the same layout pass.
    /// </summary>
    public static bool IsEquivalent(
        IReadOnlyList<GalleryRowViewModel> existing,
        IReadOnlyList<GalleryRowViewModel> replacement)
    {
        if (existing.Count != replacement.Count) return false;
        for (var rowIndex = 0; rowIndex < existing.Count; rowIndex++)
        {
            var left = existing[rowIndex];
            var right = replacement[rowIndex];
            if (left.StartIndex != right.StartIndex
                || left.Cards.Count != right.Cards.Count)
                return false;
            for (var cardIndex = 0; cardIndex < left.Cards.Count; cardIndex++)
                if (!ReferenceEquals(left.Cards[cardIndex], right.Cards[cardIndex]))
                    return false;
        }
        return true;
    }
}
