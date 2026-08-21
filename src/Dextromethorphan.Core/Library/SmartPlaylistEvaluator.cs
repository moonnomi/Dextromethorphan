using System.Globalization;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Core.Library;

/// <summary>
/// In-memory counterpart to the SQLite smart-playlist compiler.  It is used
/// for instant preview counts and keeps the rule editor honest before a
/// playlist is saved.
/// </summary>
public static class SmartPlaylistEvaluator
{
    public static bool Matches(Track track, SmartPlaylistDefinition definition, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(definition);
        return MatchGroup(track, definition.Root, now ?? DateTimeOffset.UtcNow, 0);
    }

    private static bool MatchGroup(Track track, SmartRuleGroup group, DateTimeOffset now, int depth)
    {
        if (depth > 8) throw new ArgumentException("Smart playlist rules cannot be nested more than eight levels.");
        var values = group.Conditions.Select(condition => MatchCondition(track, condition, now))
            .Concat(group.Groups.Select(child => MatchGroup(track, child, now, depth + 1)))
            .ToArray();
        if (values.Length == 0) return true;
        return group.Match == SmartRuleMatch.All ? values.All(value => value) : values.Any(value => value);
    }

    private static bool MatchCondition(Track track, SmartRuleCondition condition, DateTimeOffset now)
    {
        if (condition.Operator is SmartOperator.IsTrue or SmartOperator.IsFalse)
        {
            if (condition.Field != SmartField.Loved) throw new ArgumentException("Boolean operators are only valid for Loved.");
            return condition.Operator == SmartOperator.IsTrue ? track.IsLoved : !track.IsLoved;
        }

        if (condition.Operator is SmartOperator.InLastDays or SmartOperator.NotInLastDays)
        {
            if (condition.Field is not (SmartField.LastPlayed or SmartField.DateAdded)) throw new ArgumentException("Day operators are only valid for date fields.");
            var days = ParseDouble(condition.Value, condition.Field);
            if (days < 0) throw new ArgumentException("Day count cannot be negative.");
            var value = DateValue(track, condition.Field);
            var recent = value is { } date && date >= now.Subtract(TimeSpan.FromDays(days));
            return condition.Operator == SmartOperator.InLastDays ? recent : !recent;
        }

        return TypeOf(condition.Field) switch
        {
            SmartValueType.Text => CompareText(TextValue(track, condition.Field), condition),
            SmartValueType.Number => CompareNumber(NumberValue(track, condition.Field), ParseDouble(condition.Value, condition.Field), condition.Operator),
            SmartValueType.Boolean => CompareNumber(track.IsLoved ? 1 : 0, ParseBoolean(condition.Value) ? 1 : 0, condition.Operator),
            SmartValueType.Date => CompareDate(DateValue(track, condition.Field), ParseDate(condition.Value, condition.Field), condition.Operator),
            _ => throw new ArgumentOutOfRangeException(nameof(condition.Field))
        };
    }

    private static bool CompareText(string actual, SmartRuleCondition condition)
    {
        var value = condition.Value?.Trim() ?? string.Empty;
        var contains = actual.Contains(value, StringComparison.CurrentCultureIgnoreCase);
        var equals = actual.Equals(value, StringComparison.CurrentCultureIgnoreCase);
        var result = condition.Operator switch
        {
            SmartOperator.Contains => contains,
            SmartOperator.NotContains => !contains,
            SmartOperator.Equals => equals,
            SmartOperator.NotEquals => !equals,
            _ => throw new ArgumentException($"Operator {condition.Operator} is not valid for text fields.")
        };
        return result;
    }

    private static bool CompareNumber(double actual, double expected, SmartOperator operation) => operation switch
    {
        SmartOperator.Equals => actual == expected,
        SmartOperator.NotEquals => actual != expected,
        SmartOperator.GreaterThan => actual > expected,
        SmartOperator.GreaterOrEqual => actual >= expected,
        SmartOperator.LessThan => actual < expected,
        SmartOperator.LessOrEqual => actual <= expected,
        _ => throw new ArgumentException($"Operator {operation} is not valid for numeric fields.")
    };

    private static bool CompareDate(DateTimeOffset? actual, DateTimeOffset expected, SmartOperator operation) => operation switch
    {
        SmartOperator.Equals => actual == expected,
        SmartOperator.NotEquals => actual is null || actual != expected,
        SmartOperator.Before => actual is null || actual < expected,
        SmartOperator.After => actual > expected,
        _ => throw new ArgumentException($"Operator {operation} is not valid for date fields.")
    };

    private static string TextValue(Track track, SmartField field) => field switch
    {
        SmartField.Title => track.Title,
        SmartField.Artist => track.Artist,
        SmartField.AlbumArtist => track.AlbumArtist,
        SmartField.Album => track.Album,
        SmartField.Genre => track.Genre,
        SmartField.Comment => track.Comment,
        SmartField.Codec => track.Codec,
        SmartField.Path => track.Path,
        _ => string.Empty
    };

    private static double NumberValue(Track track, SmartField field) => field switch
    {
        SmartField.Year => track.Year,
        SmartField.Rating => track.Rating,
        SmartField.PlayCount => track.PlayCount,
        SmartField.Duration => track.Duration.TotalSeconds,
        SmartField.Bitrate => track.Bitrate,
        SmartField.SampleRate => track.SampleRate,
        _ => 0
    };

    private static DateTimeOffset? DateValue(Track track, SmartField field) => field switch
    {
        SmartField.LastPlayed => track.LastPlayedAt,
        SmartField.DateAdded => track.AddedAt,
        _ => null
    };

    private static SmartValueType TypeOf(SmartField field) => field switch
    {
        SmartField.Title or SmartField.Artist or SmartField.AlbumArtist or SmartField.Album or SmartField.Genre or SmartField.Comment or SmartField.Codec or SmartField.Path => SmartValueType.Text,
        SmartField.Loved => SmartValueType.Boolean,
        SmartField.LastPlayed or SmartField.DateAdded => SmartValueType.Date,
        _ => SmartValueType.Number
    };

    private static double ParseDouble(string? value, SmartField field) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : throw new ArgumentException($"{field} requires a number.");
    private static bool ParseBoolean(string? value) => bool.TryParse(value, out var result) ? result : value switch { "1" => true, "0" => false, _ => throw new ArgumentException("Loved requires true or false.") };
    private static DateTimeOffset ParseDate(string? value, SmartField field) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result) ? result : throw new ArgumentException($"{field} requires an ISO-8601 date.");
    private enum SmartValueType { Text, Number, Boolean, Date }
}
