using System.Globalization;
using System.Text;

namespace Dextromethorphan.Infrastructure.Library;

public sealed record CueSheet(
    string Path,
    string Title,
    string Performer,
    string Genre,
    int Year,
    int DiscNumber,
    IReadOnlyList<CueTrack> Tracks);

public sealed record CueTrack(
    int Sequence,
    int Number,
    string Title,
    string Performer,
    string MediaPath,
    TimeSpan Start,
    TimeSpan? End)
{
    public string VirtualPath(string cueSheetPath) =>
        $"{cueSheetPath}.dextromethorphan-cue-{Sequence:D4}";
}

public static class CueSheetParser
{
    static CueSheetParser() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static CueSheet Parse(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var lines = ReadLines(fullPath);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidDataException(
                            "CUE sheet has no containing directory.");
        var title = "";
        var performer = "";
        var genre = "";
        var year = 0;
        var discNumber = 0;
        string? currentFile = null;
        var fileOrdinal = 0;
        MutableTrack? currentTrack = null;
        var tracks = new List<MutableTrack>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var (command, remainder) = SplitCommand(line);
            switch (command)
            {
                case "REM":
                {
                    var (key, value) = SplitCommand(remainder);
                    if (key == "GENRE") genre = ReadValue(value);
                    else if (key is "DATE" or "YEAR")
                        int.TryParse(
                            ReadValue(value),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out year);
                    else if (key is "DISCNUMBER" or "DISC")
                        int.TryParse(
                            ReadValue(value),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out discNumber);
                    break;
                }
                case "FILE":
                    currentFile = ResolveMediaPath(directory, remainder);
                    fileOrdinal++;
                    break;
                case "TRACK":
                {
                    if (currentFile is null)
                        throw new InvalidDataException(
                            "CUE TRACK appears before FILE.");
                    var (numberText, type) = SplitCommand(remainder);
                    if (!type.Equals(
                            "AUDIO",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        currentTrack = null;
                        break;
                    }
                    if (!int.TryParse(
                            numberText,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var number))
                        throw new InvalidDataException(
                            $"Invalid CUE track number '{numberText}'.");
                    currentTrack = new MutableTrack(
                        tracks.Count + 1,
                        fileOrdinal,
                        number,
                        currentFile);
                    tracks.Add(currentTrack);
                    break;
                }
                case "TITLE":
                    if (currentTrack is null) title = ReadValue(remainder);
                    else currentTrack.Title = ReadValue(remainder);
                    break;
                case "PERFORMER":
                    if (currentTrack is null)
                        performer = ReadValue(remainder);
                    else currentTrack.Performer = ReadValue(remainder);
                    break;
                case "INDEX" when currentTrack is not null:
                {
                    var (indexText, timestamp) = SplitCommand(remainder);
                    if (indexText == "01")
                        currentTrack.Start = ParseTimestamp(timestamp);
                    break;
                }
            }
        }

        var audible = tracks
            .Where(track => track.Start is not null)
            .ToArray();
        if (audible.Length == 0)
            throw new InvalidDataException(
                "CUE sheet contains no AUDIO tracks with INDEX 01.");
        var result = new List<CueTrack>(audible.Length);
        for (var index = 0; index < audible.Length; index++)
        {
            var item = audible[index];
            TimeSpan? end = null;
            if (index + 1 < audible.Length
                && audible[index + 1].FileOrdinal == item.FileOrdinal)
                end = audible[index + 1].Start;
            result.Add(new CueTrack(
                item.Sequence,
                item.Number,
                string.IsNullOrWhiteSpace(item.Title)
                    ? $"Track {item.Number:00}"
                    : item.Title,
                string.IsNullOrWhiteSpace(item.Performer)
                    ? performer
                    : item.Performer,
                item.MediaPath,
                item.Start!.Value,
                end));
        }
        return new CueSheet(
            fullPath,
            string.IsNullOrWhiteSpace(title)
                ? Path.GetFileNameWithoutExtension(fullPath)
                : title,
            performer,
            genre,
            year,
            discNumber,
            result);
    }

    public static IReadOnlySet<string> ReferencedMediaFiles(string path)
    {
        try
        {
            return Parse(path).Tracks
                .Select(track => track.MediaPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string[] ReadLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.GetEncoding(
                    CultureInfo.CurrentCulture.TextInfo.ANSICodePage)
                .GetString(bytes);
        }
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static string ResolveMediaPath(
        string directory,
        string remainder)
    {
        var trimmed = remainder.Trim();
        string file;
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end < 0)
                throw new InvalidDataException(
                    "Unterminated quoted CUE FILE value.");
            file = trimmed[1..end];
        }
        else
        {
            var separator = trimmed.LastIndexOfAny([' ', '\t']);
            file = separator > 0 ? trimmed[..separator] : trimmed;
        }
        return Path.GetFullPath(file, directory);
    }

    private static TimeSpan ParseTimestamp(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var minutes)
            || !int.TryParse(parts[1], out var seconds)
            || !int.TryParse(parts[2], out var frames)
            || minutes < 0
            || seconds is < 0 or >= 60
            || frames is < 0 or >= 75)
            throw new InvalidDataException(
                $"Invalid CUE timestamp '{value}'.");
        return TimeSpan.FromMinutes(minutes)
               + TimeSpan.FromSeconds(seconds)
               + TimeSpan.FromSeconds(frames / 75d);
    }

    private static (string Command, string Remainder) SplitCommand(
        string value)
    {
        var separator = value.IndexOfAny([' ', '\t']);
        return separator < 0
            ? (value.ToUpperInvariant(), "")
            : (value[..separator].ToUpperInvariant(),
                value[(separator + 1)..].TrimStart());
    }

    private static string ReadValue(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('"')) return trimmed;
        var end = trimmed.IndexOf('"', 1);
        if (end < 0)
            throw new InvalidDataException(
                "Unterminated quoted CUE value.");
        return trimmed[1..end];
    }

    private sealed class MutableTrack(
        int sequence,
        int fileOrdinal,
        int number,
        string mediaPath)
    {
        public int Sequence { get; } = sequence;
        public int FileOrdinal { get; } = fileOrdinal;
        public int Number { get; } = number;
        public string MediaPath { get; } = mediaPath;
        public string Title { get; set; } = "";
        public string Performer { get; set; } = "";
        public TimeSpan? Start { get; set; }
    }
}
