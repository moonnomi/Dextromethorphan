using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Dextromethorphan.Core.Models;
using TagLib.Id3v2;
using TagLib.Ogg;

namespace Dextromethorphan.Infrastructure.Library;

internal static class ChapterMetadataReader
{
    public static IReadOnlyList<AudioChapter> Read(
        TagLib.File file,
        string path,
        TimeSpan duration)
    {
        var raw = new List<RawChapter>();
        ReadId3(file, raw);
        ReadXiph(file, raw);
        if (Path.GetExtension(path).ToLowerInvariant()
            is ".m4a" or ".mp4" or ".m4b")
            ReadMp4(path, raw);
        return Normalize(raw, duration);
    }

    private static void ReadId3(
        TagLib.File file,
        ICollection<RawChapter> chapters)
    {
        if (file.GetTag(TagLib.TagTypes.Id3v2, false)
            is not TagLib.Id3v2.Tag tag)
            return;
        foreach (var frame in tag.GetFrames<ChapterFrame>())
        {
            var title = frame.SubFrames
                .OfType<TextInformationFrame>()
                .SelectMany(value => value.Text)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            TimeSpan? end = frame.EndMilliseconds == uint.MaxValue
                ? null
                : TimeSpan.FromMilliseconds(frame.EndMilliseconds);
            chapters.Add(new RawChapter(
                title,
                TimeSpan.FromMilliseconds(frame.StartMilliseconds),
                end));
        }
    }

    private static void ReadXiph(
        TagLib.File file,
        ICollection<RawChapter> chapters)
    {
        if (file.GetTag(TagLib.TagTypes.Xiph, false)
            is not XiphComment tag)
            return;
        for (var index = 1; index <= 999; index++)
        {
            var key = $"CHAPTER{index:D3}";
            var value = tag.GetFirstField(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (index == 1) continue;
                break;
            }
            if (!TryParseTimestamp(value, out var start)) continue;
            chapters.Add(new RawChapter(
                tag.GetFirstField(key + "NAME"),
                start,
                null));
        }
    }

    private static void ReadMp4(
        string path,
        ICollection<RawChapter> chapters)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                32 * 1024,
                FileOptions.SequentialScan);
            FindMp4Chapters(stream, 0, stream.Length, chapters, 0);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or EndOfStreamException
                or InvalidDataException
                or OverflowException)
        {
            // Optional chapter metadata must never block importing a track.
        }
    }

    private static bool FindMp4Chapters(
        Stream stream,
        long start,
        long end,
        ICollection<RawChapter> chapters,
        int depth)
    {
        if (depth > 8) return false;
        var header = new byte[16];
        var position = start;
        while (position + 8 <= end)
        {
            stream.Position = position;
            stream.ReadExactly(header.AsSpan(0, 8));
            var size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            var headerSize = 8L;
            long boxSize = size;
            if (size == 1)
            {
                stream.ReadExactly(header.AsSpan(8, 8));
                boxSize = checked((long)BinaryPrimitives
                    .ReadUInt64BigEndian(header.AsSpan(8, 8)));
                headerSize = 16;
            }
            else if (size == 0)
            {
                boxSize = end - position;
            }
            if (boxSize < headerSize || position + boxSize > end)
                throw new InvalidDataException("Invalid MP4 atom length.");
            var contentStart = position + headerSize;
            var contentEnd = position + boxSize;
            if (type == "chpl")
            {
                ParseChpl(stream, contentStart, contentEnd, chapters);
                return true;
            }
            if (type is "moov" or "udta")
            {
                if (FindMp4Chapters(
                        stream,
                        contentStart,
                        contentEnd,
                        chapters,
                        depth + 1))
                    return true;
            }
            else if (type == "meta" && contentStart + 4 <= contentEnd)
            {
                if (FindMp4Chapters(
                        stream,
                        contentStart + 4,
                        contentEnd,
                        chapters,
                        depth + 1))
                    return true;
            }
            position = contentEnd;
        }
        return false;
    }

    private static void ParseChpl(
        Stream stream,
        long start,
        long end,
        ICollection<RawChapter> chapters)
    {
        if (end - start < 9) return;
        stream.Position = start;
        Span<byte> header = stackalloc byte[9];
        stream.ReadExactly(header);
        var count = header[8];
        Span<byte> time = stackalloc byte[8];
        for (var index = 0; index < count; index++)
        {
            if (stream.Position + 9 > end) break;
            stream.ReadExactly(time);
            var hundredNanoseconds =
                BinaryPrimitives.ReadUInt64BigEndian(time);
            var titleLength = stream.ReadByte();
            if (titleLength < 0 || stream.Position + titleLength > end)
                break;
            var titleBytes = new byte[titleLength];
            stream.ReadExactly(titleBytes);
            chapters.Add(new RawChapter(
                Encoding.UTF8.GetString(titleBytes),
                TimeSpan.FromTicks(checked((long)hundredNanoseconds)),
                null));
        }
    }

    private static IReadOnlyList<AudioChapter> Normalize(
        IEnumerable<RawChapter> source,
        TimeSpan duration)
    {
        var ordered = source
            .Where(chapter => chapter.Start >= TimeSpan.Zero
                              && chapter.Start < duration)
            .OrderBy(chapter => chapter.Start)
            .GroupBy(chapter => chapter.Start)
            .Select(group => group.First())
            .ToArray();
        var result = new List<AudioChapter>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var chapter = ordered[index];
            var inferredEnd = index + 1 < ordered.Length
                ? ordered[index + 1].Start
                : duration;
            var end = chapter.End is { } explicitEnd
                      && explicitEnd > chapter.Start
                ? TimeSpan.FromTicks(Math.Min(
                    explicitEnd.Ticks,
                    duration.Ticks))
                : inferredEnd;
            if (end <= chapter.Start) continue;
            result.Add(new AudioChapter(
                string.IsNullOrWhiteSpace(chapter.Title)
                    ? $"Chapter {result.Count + 1}"
                    : chapter.Title.Trim(),
                chapter.Start,
                end));
        }
        return result;
    }

    private static bool TryParseTimestamp(
        string value,
        out TimeSpan timestamp) =>
        TimeSpan.TryParseExact(
            value.Trim(),
            [@"h\:mm\:ss\.fff", @"hh\:mm\:ss\.fff", @"m\:ss\.fff", @"mm\:ss\.fff"],
            CultureInfo.InvariantCulture,
            out timestamp)
        || TimeSpan.TryParse(
            value,
            CultureInfo.InvariantCulture,
            out timestamp);

    private sealed record RawChapter(
        string? Title,
        TimeSpan Start,
        TimeSpan? End);
}
