using System.Globalization;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Infrastructure.Storage;

internal static class SmartPlaylistSqlCompiler
{
    internal sealed record Result(string Where, string OrderBy, int? Limit, IReadOnlyList<(string Name, object Value)> Parameters);

    public static Result Compile(SmartPlaylistDefinition definition, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var parameters = new List<(string Name, object Value)>();
        var where = CompileGroup(definition.Root, now, parameters, 0);
        var direction = definition.SortDescending ? "DESC" : "ASC";
        var order = $"{Column(definition.SortBy)} {direction}, title COLLATE NOCASE ASC";
        return new Result(where, order, definition.Limit is null ? null : Math.Clamp(definition.Limit.Value, 1, 5000), parameters);
    }

    private static string CompileGroup(SmartRuleGroup group, DateTimeOffset now, List<(string Name, object Value)> parameters, int depth)
    {
        if (depth > 8) throw new ArgumentException("Smart playlist rules cannot be nested more than eight levels.");
        var parts = group.Conditions.Select(x => CompileCondition(x, now, parameters))
            .Concat(group.Groups.Select(x => CompileGroup(x, now, parameters, depth + 1)))
            .ToArray();
        if (parts.Length == 0) return "1=1";
        return $"({string.Join(group.Match == SmartRuleMatch.All ? " AND " : " OR ", parts)})";
    }

    private static string CompileCondition(SmartRuleCondition rule, DateTimeOffset now, List<(string Name, object Value)> parameters)
    {
        var column = Column(rule.Field);
        if (rule.Operator is SmartOperator.IsTrue or SmartOperator.IsFalse)
        {
            if (rule.Field != SmartField.Loved) throw new ArgumentException($"{rule.Operator} is only valid for Loved.");
            return $"{column}={(rule.Operator == SmartOperator.IsTrue ? 1 : 0)}";
        }

        if (rule.Operator is SmartOperator.InLastDays or SmartOperator.NotInLastDays)
        {
            if (rule.Field is not (SmartField.LastPlayed or SmartField.DateAdded))
                throw new ArgumentException($"{rule.Operator} is only valid for date fields.");
            var days = ParseDouble(rule.Value, rule.Field);
            if (days < 0) throw new ArgumentException("Day count cannot be negative.");
            var parameter = Add(parameters, now.Subtract(TimeSpan.FromDays(days)).ToUnixTimeMilliseconds());
            return rule.Operator == SmartOperator.InLastDays
                ? $"{column} >= {parameter}"
                : $"({column} IS NULL OR {column} < {parameter})";
        }

        object value = FieldType(rule.Field) switch
        {
            SmartValueType.Text => rule.Value?.Trim() ?? "",
            SmartValueType.Number => ParseDouble(rule.Value, rule.Field),
            SmartValueType.Boolean => ParseBoolean(rule.Value) ? 1 : 0,
            SmartValueType.Date => ParseDate(rule.Value, rule.Field).ToUnixTimeMilliseconds(),
            _ => throw new ArgumentOutOfRangeException()
        };
        if (value is string text && rule.Operator is SmartOperator.Contains or SmartOperator.NotContains)
            value = text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var name = Add(parameters, value);
        return rule.Operator switch
        {
            SmartOperator.Contains when FieldType(rule.Field) == SmartValueType.Text => $"{column} LIKE '%' || {name} || '%' ESCAPE '\\' COLLATE NOCASE",
            SmartOperator.NotContains when FieldType(rule.Field) == SmartValueType.Text => $"{column} NOT LIKE '%' || {name} || '%' ESCAPE '\\' COLLATE NOCASE",
            SmartOperator.Equals => $"{column} = {name}" + (FieldType(rule.Field) == SmartValueType.Text ? " COLLATE NOCASE" : ""),
            SmartOperator.NotEquals => $"{column} <> {name}" + (FieldType(rule.Field) == SmartValueType.Text ? " COLLATE NOCASE" : ""),
            SmartOperator.GreaterThan when FieldType(rule.Field) == SmartValueType.Number => $"{column} > {name}",
            SmartOperator.GreaterOrEqual when FieldType(rule.Field) == SmartValueType.Number => $"{column} >= {name}",
            SmartOperator.LessThan when FieldType(rule.Field) == SmartValueType.Number => $"{column} < {name}",
            SmartOperator.LessOrEqual when FieldType(rule.Field) == SmartValueType.Number => $"{column} <= {name}",
            SmartOperator.Before when FieldType(rule.Field) == SmartValueType.Date => $"({column} IS NULL OR {column} < {name})",
            SmartOperator.After when FieldType(rule.Field) == SmartValueType.Date => $"{column} > {name}",
            _ => throw new ArgumentException($"Operator {rule.Operator} is not valid for {rule.Field}.")
        };
    }

    private static string Add(List<(string Name, object Value)> parameters, object value)
    {
        var name = $"$p{parameters.Count}";
        parameters.Add((name, value));
        return name;
    }

    private static double ParseDouble(string? value, SmartField field) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new ArgumentException($"{field} requires a number.");

    private static bool ParseBoolean(string? value) =>
        bool.TryParse(value, out var result) ? result : value switch
        {
            "1" => true,
            "0" => false,
            _ => throw new ArgumentException("Loved requires true or false.")
        };

    private static DateTimeOffset ParseDate(string? value, SmartField field) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result)
            ? result
            : throw new ArgumentException($"{field} requires an ISO-8601 date.");

    private static SmartValueType FieldType(SmartField field) => field switch
    {
        SmartField.Title or SmartField.Artist or SmartField.AlbumArtist or SmartField.Album or SmartField.Genre or SmartField.Comment or SmartField.Codec or SmartField.Path => SmartValueType.Text,
        SmartField.Loved => SmartValueType.Boolean,
        SmartField.LastPlayed or SmartField.DateAdded => SmartValueType.Date,
        _ => SmartValueType.Number
    };

    internal static string Column(SmartField field) => field switch
    {
        SmartField.Title => "title COLLATE NOCASE",
        SmartField.Artist => "artist COLLATE NOCASE",
        SmartField.AlbumArtist => "album_artist COLLATE NOCASE",
        SmartField.Album => "album COLLATE NOCASE",
        SmartField.Genre => "genre COLLATE NOCASE",
        SmartField.Comment => "comment COLLATE NOCASE",
        SmartField.Year => "year",
        SmartField.Rating => "rating",
        SmartField.Loved => "loved",
        SmartField.PlayCount => "play_count",
        SmartField.LastPlayed => "last_played_at",
        SmartField.DateAdded => "added_at",
        SmartField.Duration => "duration_ms / 1000.0",
        SmartField.Codec => "codec COLLATE NOCASE",
        SmartField.Bitrate => "bitrate",
        SmartField.SampleRate => "sample_rate",
        SmartField.Path => "path COLLATE NOCASE",
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private enum SmartValueType { Text, Number, Boolean, Date }
}
