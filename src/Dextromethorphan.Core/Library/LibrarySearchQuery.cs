using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Core.Library;

public sealed record LibrarySearchQuery(
    IReadOnlyList<string> FreeTerms,
    IReadOnlyList<SearchClause> Clauses)
{
    public bool IsStructured => Clauses.Count > 0;
    public string RepositoryText => string.Join(' ', FreeTerms);

    public bool Matches(Track track)
    {
        if (FreeTerms.Count > 0 && !FreeTerms.All(term => ContainsAny(track, term))) return false;
        return Clauses.All(clause => clause.Matches(track));
    }

    public static LibrarySearchQuery Parse(string? input)
    {
        var free = new List<string>(); var clauses = new List<SearchClause>();
        foreach (var token in Tokenize(input ?? string.Empty))
        {
            var excluded = token.StartsWith('-') && token.Length > 1;
            var normalized = excluded ? token[1..] : token;
            var separator = normalized.IndexOf(':');
            if (separator > 0 && SearchClause.TryParse(normalized[..separator], normalized[(separator + 1)..], excluded, out var clause)) clauses.Add(clause);
            else if (!excluded) free.Add(Unquote(normalized));
            else clauses.Add(new SearchClause("text", Unquote(normalized), false, null, true));
        }
        return new LibrarySearchQuery(free.Where(value => value.Length > 0).ToArray(), clauses);
    }

    private static bool ContainsAny(Track track, string term)
    {
        var normalized = Normalize(term);
        return new[] { track.Title, track.Artist, track.AlbumArtist, track.Album, track.Genre, track.Comment, track.Path, track.Codec }.Any(value => Normalize(value).Contains(normalized, StringComparison.Ordinal));
    }

    internal static string Normalize(string? value) => string.Concat((value ?? string.Empty).Normalize(NormalizationForm.FormD).Where(character => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)).Normalize(NormalizationForm.FormC).ToUpperInvariant();

    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    private static IEnumerable<string> Tokenize(string input)
    {
        var current = new StringBuilder(); var quoted = false;
        foreach (var character in input)
        {
            if (character == '"') { quoted = !quoted; current.Append(character); continue; }
            if (!quoted && char.IsWhiteSpace(character)) { if (current.Length > 0) { yield return current.ToString(); current.Clear(); } continue; }
            current.Append(character);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}

public sealed record SearchClause(string Field, string Value, bool IsNegative, int? Number, bool TextFallback)
{
    private static readonly Regex NumberPattern = new("^(?<op>>=|<=|=|>|<)?(?<value>\\d+)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public bool Matches(Track track)
    {
        var result = Field switch
        {
            "title" => Contains(track.Title), "artist" => Contains(track.Artist) || Contains(track.AlbumArtist), "album" => Contains(track.Album),
            "genre" => Contains(track.Genre), "filename" => Contains(Path.GetFileName(track.Path)), "comment" => Contains(track.Comment),
            "source" => track.Path.StartsWith(Value, StringComparison.OrdinalIgnoreCase), "codec" => track.Codec.Equals(Value, StringComparison.OrdinalIgnoreCase),
            "text" => new[] { track.Title, track.Artist, track.AlbumArtist, track.Album, track.Genre, track.Comment, track.Path }.Any(Contains),
            "year" => Compare(track.Year), "rating" => Compare(track.Rating), "loved" => track.IsLoved == (Value is "true" or "1" or "yes"), "compilation" => track.IsCompilation || track.AlbumArtist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
        return IsNegative ? !result : result;
    }

    public static bool TryParse(string field, string value, bool negative, out SearchClause clause)
    {
        field = field.Trim().ToLowerInvariant(); value = Unquote(value.Trim());
        var allowed = new[] { "title", "artist", "album", "genre", "filename", "comment", "playlist", "source", "codec", "year", "rating", "loved", "compilation" };
        if (!allowed.Contains(field, StringComparer.OrdinalIgnoreCase) || value.Length == 0) { clause = default!; return false; }
        int? number = null; var textFallback = false;
        if (field is "year" or "rating")
        {
            var match = NumberPattern.Match(value);
            if (!match.Success || !int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) { clause = default!; return false; }
            number = parsed;
        }
        else if (field is "loved" or "compilation") value = value.ToLowerInvariant();
        clause = new SearchClause(field, value, negative, number, textFallback); return true;
    }

    private bool Contains(string? value) => LibrarySearchQuery.Normalize(value).Contains(LibrarySearchQuery.Normalize(Value), StringComparison.Ordinal);
    private bool Compare(int value)
    {
        var match = NumberPattern.Match(Value); if (!match.Success || Number is null) return false;
        return match.Groups["op"].Value switch { ">" => value > Number, ">=" => value >= Number, "<" => value < Number, "<=" => value <= Number, _ => value == Number };
    }
    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
