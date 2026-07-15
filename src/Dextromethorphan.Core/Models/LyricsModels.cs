namespace Dextromethorphan.Core.Models;

public sealed record LyricWord(TimeSpan Start, TimeSpan? End, string Text);
public sealed record LyricLine(TimeSpan Start, TimeSpan? End, string Text, IReadOnlyList<LyricWord> Words)
{
    public bool IsActive(TimeSpan position) => position >= Start && (End is null || position < End);
}

public sealed record SyncedLyrics(IReadOnlyList<LyricLine> Lines, IReadOnlyDictionary<string, string> Metadata)
{
    public LyricLine? At(TimeSpan position)
    {
        var low = 0;
        var high = Lines.Count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (Lines[mid].Start <= position) low = mid + 1;
            else high = mid - 1;
        }
        return high >= 0 ? Lines[high] : null;
    }
}
