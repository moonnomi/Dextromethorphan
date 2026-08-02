namespace Dextromethorphan.Core.Models;

public sealed record LyricWord(TimeSpan Start, TimeSpan? End, string Text);

public enum LyricLineRole
{
    Primary,
    Translation,
    Romanization,
    Instrumental
}

public sealed record LyricLine(TimeSpan Start, TimeSpan? End, string Text, IReadOnlyList<LyricWord> Words)
{
    public LyricLineRole Role { get; init; } = LyricLineRole.Primary;
    public bool IsActive(TimeSpan position) => position >= Start && (End is null || position < End);
}

public sealed record SyncedLyrics(IReadOnlyList<LyricLine> Lines, IReadOnlyDictionary<string, string> Metadata)
{
    public LyricLine? At(TimeSpan position)
    {
        var active = AtAll(position);
        return active.FirstOrDefault(line => line.Role == LyricLineRole.Primary)
            ?? active.FirstOrDefault();
    }

    public IReadOnlyList<LyricLine> AtAll(TimeSpan position)
    {
        if (Lines.Count == 0) return [];
        var low = 0;
        var high = Lines.Count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (Lines[mid].Start <= position) low = mid + 1;
            else high = mid - 1;
        }
        if (high < 0) return [];
        var start = Lines[high].Start;
        while (high > 0 && Lines[high - 1].Start == start) high--;
        var result = new List<LyricLine>();
        for (var index = high; index < Lines.Count && Lines[index].Start == start; index++)
            if (Lines[index].IsActive(position)) result.Add(Lines[index]);
        return result;
    }
}
