namespace Dextromethorphan.App.UI;

internal sealed class NavigationViewStateStore
{
    private readonly Dictionary<string, NavigationViewState> _states = new(StringComparer.Ordinal);

    public void Capture(string key, double verticalOffset, int materializedItemCount = 0)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        _states[key] = new NavigationViewState(
            Math.Max(0, verticalOffset),
            Math.Max(0, materializedItemCount));
    }

    public NavigationViewState Get(string key) =>
        !string.IsNullOrWhiteSpace(key) && _states.TryGetValue(key, out var state)
            ? state
            : NavigationViewState.Empty;
}

internal readonly record struct NavigationViewState(double VerticalOffset, int MaterializedItemCount)
{
    public static NavigationViewState Empty { get; } = new(0, 0);
}
