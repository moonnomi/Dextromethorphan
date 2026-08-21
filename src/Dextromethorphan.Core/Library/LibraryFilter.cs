using System.Globalization;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Core.Library;

/// <summary>Small, deterministic quick-filter parser shared by the browse and search surfaces.</summary>
public static class LibraryFilter
{
    public static IReadOnlyList<Track> Apply(IEnumerable<Track> tracks, string? expression)
    {
        var filters = Parse(expression);
        if (filters.Count == 0) return tracks.ToArray();
        return tracks.Where(track => filters.All(filter => filter(track))).ToArray();
    }

    public static IReadOnlyList<Func<Track, bool>> Parse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return [];
        var result = new List<Func<Track, bool>>();
        foreach (var token in Tokenize(expression))
        {
            var value = token.Trim();
            if (value.Equals("lossless", StringComparison.OrdinalIgnoreCase))
            { result.Add(static track => track.Codec.Equals("FLAC", StringComparison.OrdinalIgnoreCase) || track.Codec.Equals("ALAC", StringComparison.OrdinalIgnoreCase) || track.Codec.Equals("WAV", StringComparison.OrdinalIgnoreCase) || track.Codec.Equals("AIFF", StringComparison.OrdinalIgnoreCase)); continue; }
            if (value.Equals("loved", StringComparison.OrdinalIgnoreCase)) { result.Add(static track => track.IsLoved); continue; }
            if (value.Equals("unloved", StringComparison.OrdinalIgnoreCase)) { result.Add(static track => !track.IsLoved); continue; }
            if (value.Equals("compilation", StringComparison.OrdinalIgnoreCase)) { result.Add(static track => track.IsCompilation || track.AlbumArtist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)); continue; }
            if (value.StartsWith("codec:", StringComparison.OrdinalIgnoreCase)) { var codec = value[6..].Trim(); if (codec.Length > 0) result.Add(track => track.Codec.Equals(codec, StringComparison.OrdinalIgnoreCase)); continue; }
            if (value.StartsWith("source:", StringComparison.OrdinalIgnoreCase)) { var source = Unquote(value[7..].Trim()); if (source.Length > 0) result.Add(track => track.Path.StartsWith(source, StringComparison.OrdinalIgnoreCase)); continue; }
            if (TryNumber(value, "year", out var year, out var yearCompare)) { result.Add(track => yearCompare(track.Year, year)); continue; }
            if (TryNumber(value, "rating", out var rating, out var ratingCompare)) { result.Add(track => ratingCompare(track.Rating, rating)); continue; }
        }
        return result;
    }

    private static bool TryNumber(string token, string field, out int value, out Func<int, int, bool> compare)
    {
        value = 0; compare = static (_, _) => true;
        if (!token.StartsWith(field, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = token[field.Length..].TrimStart();
        if (rest.Length == 0) return false;
        var op = rest.StartsWith(">=") || rest.StartsWith("<=") ? rest[..2] : rest.Length > 0 && ">=<".Contains(rest[0]) ? rest[..1] : rest.StartsWith("=") ? "=" : ":";
        if (op == ":") rest = rest[1..]; else rest = rest[op.Length..];
        if (!int.TryParse(rest.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return false;
        compare = op switch { ">" => (left, right) => left > right, ">=" => (left, right) => left >= right, "<" => (left, right) => left < right, "<=" => (left, right) => left <= right, _ => (left, right) => left == right };
        return true;
    }

    private static IEnumerable<string> Tokenize(string expression)
    {
        var current = new System.Text.StringBuilder(); var quoted = false;
        foreach (var character in expression)
        {
            if (character == '"') { quoted = !quoted; current.Append(character); continue; }
            if (!quoted && (char.IsWhiteSpace(character) || character == ',')) { if (current.Length > 0) { yield return current.ToString(); current.Clear(); } continue; }
            current.Append(character);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
