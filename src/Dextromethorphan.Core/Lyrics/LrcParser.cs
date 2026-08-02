using System.Globalization;
using System.Text.RegularExpressions;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Core.Lyrics;

public static partial class LrcParser
{
    public const int MaximumContentLength = 2 * 1024 * 1024;
    public const int MaximumLineLength = 16 * 1024;
    public const int MaximumLines = 20_000;

    [GeneratedRegex(@"\[(?:(?<hours>\d{1,2}):)?(?<minutes>\d{1,3}):(?<seconds>\d{1,2})(?:[\.:](?<fraction>\d{1,3}))?\]", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex TimeTagRegex();
    [GeneratedRegex(@"<(?:(?<hours>\d{1,2}):)?(?<minutes>\d{1,3}):(?<seconds>\d{1,2})(?:[\.:](?<fraction>\d{1,3}))?>", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex WordTagRegex();
    [GeneratedRegex(@"^\[(?<key>[a-zA-Z][a-zA-Z0-9_-]*):(?<value>.*)\]$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MetadataRegex();
    [GeneratedRegex(@"^\s*\[(?<role>tr|translation|rom|romanization|instrumental|inst)\]\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex RolePrefixRegex();

    public static SyncedLyrics Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > MaximumContentLength) content = content[..MaximumContentLength];
        content = content.TrimStart('\uFEFF');
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<LyricLine>();
        var offset = TimeSpan.Zero;
        var rawLines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        foreach (var sourceLine in rawLines.Take(MaximumLines))
        {
            var raw = sourceLine.Length > MaximumLineLength ? sourceLine[..MaximumLineLength] : sourceLine;
            var line = raw.TrimEnd().TrimStart('\uFEFF');
            var metadataMatch = MetadataRegex().Match(line);
            if (metadataMatch.Success && TimeTagRegex().Matches(line).Count == 0)
            {
                var key = metadataMatch.Groups["key"].Value;
                var value = metadataMatch.Groups["value"].Value.Trim();
                metadata[key] = value;
                if (key.Equals("offset", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds)
                    && double.IsFinite(milliseconds))
                    offset = TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, -3_600_000, 3_600_000));
                continue;
            }

            var tags = TimeTagRegex().Matches(line);
            if (tags.Count == 0) continue;
            var textStart = tags[^1].Index + tags[^1].Length;
            var taggedText = line[textStart..].Trim();
            var role = ParseRole(ref taggedText);
            var words = ParseWords(taggedText, offset);
            var cleanText = WordTagRegex().Replace(taggedText, string.Empty).Trim();
            if (cleanText.Length == 0)
            {
                cleanText = "♪";
                role = LyricLineRole.Instrumental;
            }
            foreach (Match tag in tags)
                if (TryParseTime(tag, out var timestamp))
                    lines.Add(new LyricLine(ClampTime(timestamp + offset), null, cleanText, words) { Role = role });
        }

        lines = lines
            .OrderBy(line => line.Start)
            .ThenBy(line => RoleOrder(line.Role))
            .ToList();
        var starts = lines.Select(line => line.Start).Distinct().ToArray();
        for (var index = 0; index < lines.Count; index++)
        {
            var startIndex = Array.BinarySearch(starts, lines[index].Start);
            TimeSpan? end = startIndex >= 0 && startIndex + 1 < starts.Length ? starts[startIndex + 1] : null;
            var words = lines[index].Words.Select((word, wordIndex) => word with
            {
                End = wordIndex + 1 < lines[index].Words.Count ? lines[index].Words[wordIndex + 1].Start : end
            }).ToArray();
            lines[index] = lines[index] with { End = end, Words = words };
        }
        return new SyncedLyrics(lines, metadata);
    }

    private static LyricLineRole ParseRole(ref string text)
    {
        var match = RolePrefixRegex().Match(text);
        if (!match.Success) return LyricLineRole.Primary;
        text = text[match.Length..];
        return match.Groups["role"].Value.ToLowerInvariant() switch
        {
            "tr" or "translation" => LyricLineRole.Translation,
            "rom" or "romanization" => LyricLineRole.Romanization,
            _ => LyricLineRole.Instrumental
        };
    }

    private static int RoleOrder(LyricLineRole role) => role switch
    {
        LyricLineRole.Primary => 0,
        LyricLineRole.Romanization => 1,
        LyricLineRole.Translation => 2,
        _ => 3
    };

    private static IReadOnlyList<LyricWord> ParseWords(string text, TimeSpan offset)
    {
        var matches = WordTagRegex().Matches(text);
        if (matches.Count == 0) return [];
        var words = new List<LyricWord>(matches.Count);
        for (var index = 0; index < matches.Count; index++)
        {
            if (!TryParseTime(matches[index], out var timestamp)) continue;
            var start = matches[index].Index + matches[index].Length;
            var end = index + 1 < matches.Count ? matches[index + 1].Index : text.Length;
            words.Add(new LyricWord(ClampTime(timestamp + offset), null, text[start..end]));
        }
        return words;
    }

    private static bool TryParseTime(Match match, out TimeSpan timestamp)
    {
        timestamp = TimeSpan.Zero;
        if (!int.TryParse(match.Groups["hours"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hours)) hours = 0;
        if (!int.TryParse(match.Groups["minutes"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(match.Groups["seconds"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds is < 0 or >= 60)
            return false;
        var fractionText = match.Groups["fraction"].Value;
        if (!int.TryParse(fractionText, NumberStyles.None, CultureInfo.InvariantCulture, out var fraction)) fraction = 0;
        var milliseconds = fractionText.Length switch { 1 => fraction * 100, 2 => fraction * 10, 3 => fraction, _ => 0 };
        try
        {
            timestamp = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(milliseconds);
            return true;
        }
        catch (OverflowException) { return false; }
    }

    private static TimeSpan ClampTime(TimeSpan time) => time < TimeSpan.Zero ? TimeSpan.Zero : time;
}
