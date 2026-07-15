using System.Globalization;
using System.Text.RegularExpressions;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Core.Lyrics;

public static partial class LrcParser
{
    [GeneratedRegex(@"\[(?<minutes>\d{1,3}):(?<seconds>\d{1,2})(?:[\.:](?<fraction>\d{1,3}))?\]", RegexOptions.Compiled)]
    private static partial Regex TimeTagRegex();
    [GeneratedRegex(@"<(?<minutes>\d{1,3}):(?<seconds>\d{1,2})(?:[\.:](?<fraction>\d{1,3}))?>", RegexOptions.Compiled)]
    private static partial Regex WordTagRegex();
    [GeneratedRegex(@"^\[(?<key>[a-zA-Z]+):(?<value>.*)\]$", RegexOptions.Compiled)]
    private static partial Regex MetadataRegex();

    public static SyncedLyrics Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<LyricLine>();
        var offset = TimeSpan.Zero;

        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var metadataMatch = MetadataRegex().Match(line);
            if (metadataMatch.Success)
            {
                var key = metadataMatch.Groups["key"].Value;
                var value = metadataMatch.Groups["value"].Value.Trim();
                metadata[key] = value;
                if (key.Equals("offset", StringComparison.OrdinalIgnoreCase) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                    offset = TimeSpan.FromMilliseconds(ms);
                continue;
            }

            var tags = TimeTagRegex().Matches(line);
            if (tags.Count == 0) continue;
            var textStart = tags[^1].Index + tags[^1].Length;
            var text = line[textStart..].Trim();
            var words = ParseWords(text, offset);
            var cleanText = WordTagRegex().Replace(text, "").Trim();
            foreach (Match tag in tags)
                lines.Add(new LyricLine(ParseTime(tag) + offset, null, cleanText, words));
        }

        lines.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 0; i < lines.Count; i++)
        {
            TimeSpan? end = i + 1 < lines.Count ? lines[i + 1].Start : null;
            var words = lines[i].Words.Select((word, index) => word with
            {
                End = index + 1 < lines[i].Words.Count ? lines[i].Words[index + 1].Start : end
            }).ToArray();
            lines[i] = lines[i] with { End = end, Words = words };
        }
        return new SyncedLyrics(lines, metadata);
    }

    private static IReadOnlyList<LyricWord> ParseWords(string text, TimeSpan offset)
    {
        var matches = WordTagRegex().Matches(text);
        if (matches.Count == 0) return [];
        var words = new List<LyricWord>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            words.Add(new LyricWord(ParseTime(matches[i]) + offset, null, text[start..end]));
        }
        return words;
    }

    private static TimeSpan ParseTime(Match match)
    {
        var minutes = int.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups["seconds"].Value, CultureInfo.InvariantCulture);
        var fractionText = match.Groups["fraction"].Value;
        var milliseconds = fractionText.Length switch { 1 => int.Parse(fractionText) * 100, 2 => int.Parse(fractionText) * 10, 3 => int.Parse(fractionText), _ => 0 };
        return TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(milliseconds);
    }
}
