using System.IO;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.App.ViewModels;

public sealed class FolderTreeNodeViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;

    internal FolderTreeNodeViewModel(string name, string fullPath, bool isSource)
    {
        Name = name;
        FullPath = fullPath;
        IsSource = isSource;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsSource { get; }
    public IReadOnlyList<FolderTreeNodeViewModel> Children { get; internal set; } = [];
    public IReadOnlyList<int> TrackIndexes { get; internal set; } = [];
    public int TrackCount => TrackIndexes.Count;
    public string CountText => TrackCount == 1 ? "1 track" : $"{TrackCount:N0} tracks";
    public string? ArtworkPath { get; internal set; }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

internal static class FolderTreeBuilder
{
    public static IReadOnlyList<FolderTreeNodeViewModel> Build(
        IReadOnlyList<Track> tracks,
        IEnumerable<LibrarySourceSettings> configuredSources)
    {
        var roots = configuredSources
            .Where(source => source.Enabled && !string.IsNullOrWhiteSpace(source.Path))
            .Select(source => new MutableNode(DisplayName(source.Path), Path.GetFullPath(source.Path), true))
            .DistinctBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            if (track.IsMissing) continue;
            var directory = Path.GetDirectoryName(track.Path);
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var root = roots
                .Where(candidate => IsWithin(directory, candidate.FullPath))
                .OrderByDescending(candidate => candidate.FullPath.Length)
                .FirstOrDefault();
            if (root is null) continue;

            var current = root;
            current.AddTrack(index, track.ArtworkPath);
            var relative = Path.GetRelativePath(root.FullPath, directory);
            if (relative is "." or "") continue;
            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = current.GetOrAdd(segment);
                current.AddTrack(index, track.ArtworkPath);
            }
        }

        return roots.Select(root => root.Freeze(tracks)).ToArray();
    }

    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string DisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private sealed class MutableNode(string name, string fullPath, bool isSource)
    {
        private readonly Dictionary<string, MutableNode> _children = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<int> _trackIndexes = [];
        private string? _artworkPath;

        public string Name { get; } = name;
        public string FullPath { get; } = fullPath;
        public bool IsSource { get; } = isSource;

        public MutableNode GetOrAdd(string segment)
        {
            if (_children.TryGetValue(segment, out var child)) return child;
            child = new MutableNode(segment, Path.Combine(FullPath, segment), false);
            _children.Add(segment, child);
            return child;
        }

        public void AddTrack(int index, string? artworkPath)
        {
            _trackIndexes.Add(index);
            if (_artworkPath is null && !string.IsNullOrWhiteSpace(artworkPath)) _artworkPath = artworkPath;
        }

        public FolderTreeNodeViewModel Freeze(IReadOnlyList<Track> tracks) => new(Name, FullPath, IsSource)
        {
            TrackIndexes = _trackIndexes
                .Distinct()
                .OrderBy(index => tracks[index].Path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ArtworkPath = _artworkPath,
            Children = _children.Values
                .OrderBy(child => child.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(child => child.Freeze(tracks))
                .ToArray()
        };
    }
}
