namespace Dextromethorphan.App.UI;

internal sealed class NavigationViewStateStore
{
    private readonly Dictionary<string, StoredNavigationViewState> _states =
        new(StringComparer.Ordinal);

    public void Capture(
        string key,
        double verticalOffset,
        int materializedItemCount = 0,
        int galleryAnchorIndex = -1,
        double galleryAnchorOffset = 0)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        _states[key] = new StoredNavigationViewState(
            new NavigationViewState(
                Math.Max(0, verticalOffset),
                Math.Max(0, materializedItemCount),
                Math.Max(-1, galleryAnchorIndex),
                Math.Max(0, galleryAnchorOffset)),
            DateTimeOffset.UtcNow);
    }

    public NavigationViewState Get(string key) =>
        !string.IsNullOrWhiteSpace(key)
        && _states.TryGetValue(key, out var stored)
            ? stored.State
            : NavigationViewState.Empty;

    public int Trim(
        IReadOnlySet<string> protectedKeys,
        DateTimeOffset olderThan,
        int maximumEntries)
    {
        ArgumentNullException.ThrowIfNull(protectedKeys);
        var removable = _states
            .Where(pair => !protectedKeys.Contains(pair.Key))
            .OrderBy(pair => pair.Value.LastUsed)
            .ToArray();
        var excess = Math.Max(
            0,
            _states.Count - Math.Max(1, maximumEntries));
        var removed = 0;
        foreach (var pair in removable)
        {
            if (pair.Value.LastUsed >= olderThan && removed >= excess)
                break;
            if (_states.Remove(pair.Key)) removed++;
        }
        return removed;
    }

    private sealed record StoredNavigationViewState(
        NavigationViewState State,
        DateTimeOffset LastUsed);
}

internal readonly record struct NavigationViewState(
    double VerticalOffset,
    int MaterializedItemCount,
    int GalleryAnchorIndex = -1,
    double GalleryAnchorOffset = 0)
{
    public static NavigationViewState Empty { get; } = new(0, 0, -1, 0);
}
