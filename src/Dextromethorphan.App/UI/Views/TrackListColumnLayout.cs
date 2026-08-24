using System.Windows;
using System.Windows.Controls;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.App.UI.Views;

internal sealed record TrackColumnLayoutEntry(
    string Name,
    int DisplayIndex,
    bool IsVisible,
    GridLength Width);

internal static class TrackListColumnLayout
{
    internal static IReadOnlyList<string> ColumnNames { get; } =
    [
        "Track", "Title", "Artist", "Album", "Quality", "Rating",
        "Duration", "Year", "Codec", "Source"
    ];

    private static IReadOnlyDictionary<string, GridLength> DefaultWidths { get; }
        = new Dictionary<string, GridLength>(StringComparer.OrdinalIgnoreCase)
        {
            ["Track"] = new(42),
            ["Title"] = new(1.8, GridUnitType.Star),
            ["Artist"] = new(1.15, GridUnitType.Star),
            ["Album"] = new(1.1, GridUnitType.Star),
            ["Quality"] = new(146),
            ["Rating"] = new(112),
            ["Duration"] = new(64),
            ["Year"] = new(64),
            ["Codec"] = new(82),
            ["Source"] = new(1.35, GridUnitType.Star)
        };

    internal static IReadOnlyList<TrackColumnLayoutEntry> Resolve(
        IEnumerable<string>? columnOrder,
        IEnumerable<string>? visibleColumns,
        IReadOnlyDictionary<string, double>? columnWidths)
    {
        var canonical = ColumnNames.ToDictionary(
            name => name,
            StringComparer.OrdinalIgnoreCase);
        var order = (columnOrder ?? [])
            .Where(canonical.ContainsKey)
            .Select(name => canonical[name])
            .Concat(ColumnNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var visible = (visibleColumns ?? [])
            .Where(canonical.ContainsKey)
            .Select(name => canonical[name])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return order.Select((name, index) =>
        {
            var isVisible = visible.Contains(name);
            var width = isVisible
                ? ResolveWidth(name, columnWidths)
                : new GridLength(0);
            return new TrackColumnLayoutEntry(name, index, isVisible, width);
        }).ToArray();
    }

    internal static double TrackRowHeight(LibraryDensity density) => density switch
    {
        LibraryDensity.Compact => 48,
        LibraryDensity.Grid => 52,
        _ => 58
    };

    internal static double GalleryMetadataHeight(LibraryDensity density) =>
        density switch
        {
            LibraryDensity.Compact => 58,
            LibraryDensity.Grid => 48,
            _ => 76
        };

    private static GridLength ResolveWidth(
        string name,
        IReadOnlyDictionary<string, double>? columnWidths)
    {
        if (columnWidths is not null
            && columnWidths.TryGetValue(name, out var configured)
            && double.IsFinite(configured))
            return new GridLength(Math.Clamp(configured, 40, 800));
        return DefaultWidths[name];
    }
}
