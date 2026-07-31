using System.Collections.ObjectModel;

namespace Dextromethorphan.App.UI;

internal sealed class PresentationCollectionCache<T>
{
    private readonly Dictionary<string, PresentationCollection<T>> _entries = new(StringComparer.Ordinal);

    public PresentationCollection<T> GetOrCreate(
        string key,
        Func<IReadOnlyList<T>> sourceFactory,
        int initialCount,
        out bool cacheHit)
    {
        if (_entries.TryGetValue(key, out var cached))
        {
            cacheHit = true;
            return cached;
        }

        var source = sourceFactory();
        var count = Math.Clamp(initialCount, 0, source.Count);
        var items = count == source.Count
                    && source is ObservableCollection<T> liveSource
            ? liveSource
            : new ObservableCollection<T>(source.Take(count));
        var created = new PresentationCollection<T>(source, items);
        _entries.Add(key, created);
        cacheHit = false;
        return created;
    }

    public static bool EnsureMaterialized(PresentationCollection<T> entry, int count)
    {
        var target = Math.Clamp(count, 0, entry.Source.Count);
        if (target <= entry.Items.Count) return false;
        for (var index = entry.Items.Count; index < target; index++)
            entry.Items.Add(entry.Source[index]);
        return true;
    }

    public void Remove(string key) => _entries.Remove(key);

    public int RemoveWhere(Func<string, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var keys = _entries.Keys.Where(predicate).ToArray();
        foreach (var key in keys) _entries.Remove(key);
        return keys.Length;
    }

    public void Clear() => _entries.Clear();
}

internal sealed record PresentationCollection<T>(
    IReadOnlyList<T> Source,
    ObservableCollection<T> Items);
