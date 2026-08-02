using System.Text;
using System.Globalization;
using System.IO;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Lyrics;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.App.Lyrics;

public enum LyricsSourceKind
{
    LocalFile,
    Embedded,
    OnlineCache
}

public sealed record LyricsDocument(
    string Content,
    string DisplayName,
    LyricsSourceKind Kind,
    string? FilePath = null,
    string? Attribution = null)
{
    public bool CanEdit => Kind == LyricsSourceKind.LocalFile && FilePath is not null;
}

public sealed class LyricsDocumentService(ISettingsService settings)
{
    public async Task<IReadOnlyList<LyricsDocument>> DiscoverAsync(Track track, CancellationToken cancellationToken = default)
    {
        var documents = new List<LyricsDocument>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (settings.Current.SelectedLyricFiles.TryGetValue(track.Path, out var selected))
            await AddFileAsync(selected, documents, seen, cancellationToken);

        var directory = Path.GetDirectoryName(track.EffectiveMediaPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            var stem = Path.GetFileNameWithoutExtension(track.EffectiveMediaPath);
            foreach (var extension in new[] { ".lrc", ".txt" })
                await AddFileAsync(Path.Combine(directory, stem + extension), documents, seen, cancellationToken);
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, stem + ".*.lrc").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(32))
                    await AddFileAsync(path, documents, seen, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }

            foreach (var candidateStem in CandidateNames(track))
                foreach (var extension in new[] { ".lrc", ".txt" })
                    await AddFileAsync(Path.Combine(directory, candidateStem + extension), documents, seen, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(track.Lyrics)
            && documents.All(document => !document.Content.Equals(track.Lyrics, StringComparison.Ordinal)))
            documents.Add(new LyricsDocument(track.Lyrics, "Embedded lyrics", LyricsSourceKind.Embedded));
        return documents;
    }

    public async Task<LyricsDocument?> LoadPreferredAsync(Track track, CancellationToken cancellationToken = default) =>
        (await DiscoverAsync(track, cancellationToken)).FirstOrDefault();

    public async Task<LyricsDocument> ChooseAsync(Track track, string path, CancellationToken cancellationToken = default)
    {
        var document = await ReadFileAsync(path, cancellationToken);
        await settings.UpdateAsync(value => value.SelectedLyricFiles[track.Path] = document.FilePath!, cancellationToken);
        return document;
    }

    public async Task<LyricsDocument> SaveAsync(Track track, string content, LyricsDocument? current, CancellationToken cancellationToken = default)
    {
        if (content.Length > LrcParser.MaximumContentLength) throw new InvalidDataException("Lyrics exceed the 2 MB safety limit.");
        var path = current is { CanEdit: true, FilePath: { } existing }
            ? existing
            : Path.ChangeExtension(track.EffectiveMediaPath, ".lrc");
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
        await settings.UpdateAsync(value => value.SelectedLyricFiles[track.Path] = fullPath, cancellationToken);
        return new LyricsDocument(content, Path.GetFileName(fullPath), LyricsSourceKind.LocalFile, fullPath);
    }

    public async Task RemoveAsync(Track track, LyricsDocument document, CancellationToken cancellationToken = default)
    {
        if (!document.CanEdit || document.FilePath is not { } filePath) return;
        var extension = Path.GetExtension(filePath);
        if (!extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only local lyric sidecars can be removed.");
        File.Delete(filePath);
        await settings.UpdateAsync(value => value.SelectedLyricFiles.Remove(track.Path), cancellationToken);
    }

    private static async Task AddFileAsync(
        string path,
        ICollection<LyricsDocument> target,
        ISet<string> seen,
        CancellationToken cancellationToken)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        if (!seen.Add(fullPath) || !File.Exists(fullPath)) return;
        try { target.Add(await ReadFileAsync(fullPath, cancellationToken)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private static async Task<LyricsDocument> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Lyrics file was not found.", fullPath);
        if (info.Length > LrcParser.MaximumContentLength) throw new InvalidDataException("Lyrics file exceeds the 2 MB safety limit.");
        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        return new LyricsDocument(LyricTextDecoder.Decode(bytes), Path.GetFileName(fullPath), LyricsSourceKind.LocalFile, fullPath);
    }

    private static IEnumerable<string> CandidateNames(Track track)
    {
        static string Clean(string value)
        {
            var invalid = Path.GetInvalidFileNameChars().ToHashSet();
            return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character)).Trim();
        }
        var title = Clean(track.Title);
        if (title.Length > 0) yield return title;
        var artistTitle = Clean(track.DisplayArtist + " - " + track.Title);
        if (artistTitle.Length > 0 && !artistTitle.Equals(title, StringComparison.OrdinalIgnoreCase)) yield return artistTitle;
    }
}

internal static class LyricTextDecoder
{
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (HasPrefix(bytes, 0xEF, 0xBB, 0xBF)) return Encoding.UTF8.GetString(bytes[3..]);
        if (HasPrefix(bytes, 0xFF, 0xFE, 0x00, 0x00)) return Encoding.UTF32.GetString(bytes[4..]);
        if (HasPrefix(bytes, 0x00, 0x00, 0xFE, 0xFF)) return new UTF32Encoding(true, true).GetString(bytes[4..]);
        if (HasPrefix(bytes, 0xFF, 0xFE)) return Encoding.Unicode.GetString(bytes[2..]);
        if (HasPrefix(bytes, 0xFE, 0xFF)) return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        if (LooksLikeUtf16(bytes, oddZeros: true)) return Encoding.Unicode.GetString(bytes);
        if (LooksLikeUtf16(bytes, oddZeros: false)) return Encoding.BigEndianUnicode.GetString(bytes);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes); }
            catch { return Encoding.Latin1.GetString(bytes); }
        }
    }

    private static bool HasPrefix(ReadOnlySpan<byte> bytes, params byte[] prefix) =>
        bytes.Length >= prefix.Length && bytes[..prefix.Length].SequenceEqual(prefix);

    private static bool LooksLikeUtf16(ReadOnlySpan<byte> bytes, bool oddZeros)
    {
        if (bytes.Length < 4 || bytes.Length % 2 != 0) return false;
        var zeros = 0;
        var samples = Math.Min(bytes.Length / 2, 256);
        for (var index = 0; index < samples; index++)
            if (bytes[(index * 2) + (oddZeros ? 1 : 0)] == 0) zeros++;
        return zeros >= samples * 3 / 4;
    }
}
