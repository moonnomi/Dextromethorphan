namespace Dextromethorphan.Infrastructure.Library;

internal static class ExternalArtworkResolver
{
    private static readonly IReadOnlyDictionary<string, int> NamePriority =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["cover"] = 0,
            ["folder"] = 1,
            ["front"] = 2,
            ["album"] = 3,
            ["albumart"] = 4
        };

    private static readonly IReadOnlyDictionary<string, int> ExtensionPriority =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = 0,
            [".jpeg"] = 1,
            [".png"] = 2,
            [".webp"] = 3,
            [".tif"] = 4,
            [".tiff"] = 5,
            [".bmp"] = 6,
            [".gif"] = 7
        };

    public static string? FindPreferredForMedia(
        string mediaPath,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(mediaPath));
        return directory is null
            ? null
            : FindPreferredInDirectory(directory, cancellationToken);
    }

    public static string? FindPreferredInDirectory(
        string directory,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) return null;
        try
        {
            var candidates = Directory.EnumerateFiles(directory)
                .Select(path => new Candidate(
                    Path.GetFullPath(path),
                    NameRank(Path.GetFileNameWithoutExtension(path)),
                    ExtensionRank(Path.GetExtension(path))))
                .Where(candidate => candidate.NameRank >= 0 && candidate.ExtensionRank >= 0)
                .OrderBy(candidate => candidate.NameRank)
                .ThenBy(candidate => candidate.ExtensionRank)
                .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Path, StringComparer.Ordinal);

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ArtworkImageInspector.TryInspectFile(
                    candidate.Path,
                    out _,
                    out _,
                    cancellationToken))
                    return candidate.Path;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static int NameRank(string name) =>
        NamePriority.TryGetValue(name, out var priority) ? priority : -1;

    private static int ExtensionRank(string extension) =>
        ExtensionPriority.TryGetValue(extension, out var priority) ? priority : -1;

    private readonly record struct Candidate(
        string Path,
        int NameRank,
        int ExtensionRank);
}
