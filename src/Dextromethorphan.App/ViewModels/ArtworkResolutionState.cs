using System.Collections.Concurrent;
using System.IO;

namespace Dextromethorphan.App.ViewModels;

internal sealed class ArtworkResolutionState
{
    private readonly ConcurrentDictionary<string, string> _paths =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count => _paths.Count;
    public IEnumerable<string> Keys => _paths.Keys;

    public bool TryGet(string trackPath, out string? artworkPath)
    {
        if (_paths.TryGetValue(trackPath, out var cached)
            && File.Exists(cached))
        {
            artworkPath = cached;
            return true;
        }

        _paths.TryRemove(trackPath, out _);
        artworkPath = null;
        return false;
    }

    public void Remember(string trackPath, string? artworkPath)
    {
        if (string.IsNullOrWhiteSpace(artworkPath))
        {
            _paths.TryRemove(trackPath, out _);
            return;
        }

        _paths[trackPath] = artworkPath;
    }

    public void Remove(string trackPath) => _paths.TryRemove(trackPath, out _);
    public void Clear() => _paths.Clear();
}
