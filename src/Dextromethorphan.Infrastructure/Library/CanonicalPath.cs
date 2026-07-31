using System.Text;

namespace Dextromethorphan.Infrastructure.Library;

internal static class CanonicalPath
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Normalize(NormalizationForm.FormC);
        var root = Path.GetPathRoot(fullPath);
        if (root is not null
            && fullPath.Length > root.Length)
            fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar);
        return fullPath;
    }

    public static bool Equals(string left, string right) =>
        Normalize(left).Equals(
            Normalize(right),
            StringComparison.OrdinalIgnoreCase);
}
