namespace Dextromethorphan.Core.Library;

public static class SupportedMediaFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".m4a", ".mp4", ".alac", ".wav", ".wave",
        ".aif", ".aiff", ".dsf", ".dff", ".ogg", ".opus", ".aac",
        ".wma", ".cue"
    };

    public static bool IsSupported(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Extensions.Contains(Path.GetExtension(path));
}
