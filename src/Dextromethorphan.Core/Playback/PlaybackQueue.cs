using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Core.Playback;

public sealed class PlaybackQueue : IPlaybackQueue
{
    private readonly List<QueueEntry> _items = [];
    private readonly Stack<QueueState> _undo = [];
    private readonly Stack<QueueState> _redo = [];
    private readonly Random _random = new();
    private readonly List<Guid> _shuffleDeck = [];
    private readonly List<Guid> _playHistory = [];
    private int _currentIndex = -1;
    private bool _shuffle;

    public IReadOnlyList<QueueEntry> Items => _items;
    public IReadOnlyList<QueueEntry> PlaybackOrder
    {
        get
        {
            if (_items.Count == 0) return [];
            if (!Shuffle) return _currentIndex < 0 ? [.. _items] : [.. _items.Skip(_currentIndex)];

            var byId = _items.ToDictionary(item => item.Id);
            var ordered = new List<QueueEntry>(_shuffleDeck.Count + 1);
            if (CurrentId() is { } currentId && byId.TryGetValue(currentId, out var current)) ordered.Add(current);
            foreach (var id in _shuffleDeck)
                if (byId.TryGetValue(id, out var item)) ordered.Add(item);
            return ordered;
        }
    }
    public IReadOnlyList<QueueEntry> TimelineOrder
    {
        get
        {
            if (_items.Count == 0) return [];
            if (!Shuffle) return [.. _items];

            var byId = _items.ToDictionary(item => item.Id);
            var orderedIds = new List<Guid>(_items.Count);
            foreach (var id in _playHistory)
            {
                if (!byId.ContainsKey(id) || id == CurrentId()) continue;
                // A repeated visit should appear only once, at its most recent
                // position in the navigable history.
                orderedIds.Remove(id);
                orderedIds.Add(id);
            }
            if (CurrentId() is { } currentId) orderedIds.Add(currentId);
            foreach (var id in _shuffleDeck)
                if (byId.ContainsKey(id) && !orderedIds.Contains(id)) orderedIds.Add(id);
            foreach (var item in _items)
                if (!orderedIds.Contains(item.Id)) orderedIds.Add(item.Id);
            return orderedIds.Select(id => byId[id]).ToArray();
        }
    }
    public int CurrentIndex => _currentIndex;
    public RepeatMode RepeatMode { get; set; }
    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (_shuffle == value) return;
            _shuffle = value;
            if (_shuffle) RebuildShuffleDeck();
            else _shuffleDeck.Clear();
            OnChanged(false);
        }
    }
    public IReadOnlyList<string> ShuffleUpcomingPaths => _shuffleDeck
        .Select(id => _items.FirstOrDefault(item => item.Id == id)?.Track.Path)
        .Where(path => path is not null)
        .Cast<string>()
        .ToArray();
    public Track? Current => _currentIndex >= 0 && _currentIndex < _items.Count ? _items[_currentIndex].Track : null;
    public event EventHandler? Changed;

    public Track? Select(Guid id)
    {
        var index = _items.FindIndex(x => x.Id == id);
        if (index < 0) return null;
        var previousId = CurrentId();
        if (previousId is { } previous && previous != id) _playHistory.Add(previous);
        _currentIndex = index;
        _shuffleDeck.Remove(id);
        NormalizePlayingFlag();
        OnChanged(false);
        return Current;
    }

    public void Replace(IEnumerable<Track> tracks, int startIndex = 0)
    {
        SaveUndo();
        _items.Clear();
        _items.AddRange(tracks.Select(CreateEntry));
        _currentIndex = _items.Count == 0 ? -1 : Math.Clamp(startIndex, 0, _items.Count - 1);
        _playHistory.Clear();
        if (Shuffle) RebuildShuffleDeck();
        else _shuffleDeck.Clear();
        NormalizePlayingFlag();
        OnChanged();
    }

    public void Add(IEnumerable<Track> tracks)
    {
        var additions = tracks.Select(CreateEntry).ToList();
        if (additions.Count == 0) return;
        SaveUndo();
        _items.AddRange(additions);
        if (_currentIndex < 0) _currentIndex = 0;
        if (Shuffle) _shuffleDeck.AddRange(additions.Select(item => item.Id));
        NormalizePlayingFlag();
        OnChanged();
    }

    public void InsertAtPlaybackIndex(IEnumerable<Track> tracks, int playbackIndex)
    {
        var additions = tracks.Select(CreateEntry).ToList();
        if (additions.Count == 0) return;

        // A queue without a current item needs the first inserted track to
        // become the current item, just like Add does.
        if (_items.Count == 0)
        {
            SaveUndo();
            _items.AddRange(additions);
            _currentIndex = 0;
            if (Shuffle) _shuffleDeck.AddRange(additions.Skip(1).Select(item => item.Id));
            NormalizePlayingFlag();
            OnChanged();
            return;
        }

        SaveUndo();
        var currentId = CurrentId();
        if (_currentIndex < 0) _currentIndex = 0;

        if (Shuffle)
        {
            _items.AddRange(additions);
            var insertion = Math.Clamp(playbackIndex - 1, 0, _shuffleDeck.Count);
            _shuffleDeck.InsertRange(insertion, additions.Select(item => item.Id));
        }
        else
        {
            var prefixLength = Math.Clamp(_currentIndex + 1, 0, _items.Count);
            var futureCount = _items.Count - prefixLength;
            var insertion = Math.Clamp(playbackIndex - 1, 0, futureCount);
            _items.InsertRange(prefixLength + insertion, additions);
        }

        RestoreCurrent(currentId);
        OnChanged();
    }

    public void PlayNext(IEnumerable<Track> tracks)
    {
        var additions = tracks.Select(CreateEntry).ToList();
        if (additions.Count == 0) return;
        SaveUndo();
        _items.InsertRange(Math.Max(0, _currentIndex + 1), additions);
        if (_currentIndex < 0) _currentIndex = 0;
        if (Shuffle) _shuffleDeck.InsertRange(0, additions.Select(item => item.Id));
        NormalizePlayingFlag();
        OnChanged();
    }

    public void Move(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _items.Count || toIndex < 0 || toIndex >= _items.Count || fromIndex == toIndex) return;
        SaveUndo();
        var currentId = CurrentId();
        var item = _items[fromIndex];
        _items.RemoveAt(fromIndex);
        _items.Insert(toIndex, item);
        RestoreCurrent(currentId);
        OnChanged();
    }

    public void MoveMany(IReadOnlyCollection<Guid> ids, int toIndex)
    {
        if (ids.Count == 0 || _items.Count < 2) return;
        var selected = ids.ToHashSet();
        var moving = _items.Where(item => selected.Contains(item.Id)).ToList();
        if (moving.Count == 0) return;
        var targetId = toIndex >= 0 && toIndex < _items.Count ? _items[toIndex].Id : (Guid?)null;
        SaveUndo();
        var currentId = CurrentId();
        _items.RemoveAll(item => selected.Contains(item.Id));
        var insertion = targetId is { } target
            ? _items.FindIndex(item => item.Id == target)
            : _items.Count;
        if (insertion < 0) insertion = Math.Clamp(toIndex, 0, _items.Count);
        _items.InsertRange(insertion, moving);
        RestoreCurrent(currentId);
        OnChanged();
    }

    public void MoveInPlaybackOrder(IReadOnlyCollection<Guid> ids, int toIndex)
    {
        if (ids.Count == 0 || _items.Count < 2) return;
        var selected = ids.ToHashSet();
        var currentId = CurrentId();
        if (currentId is { } current) selected.Remove(current);

        var playbackOrder = PlaybackOrder;
        var moving = playbackOrder.Where(item => selected.Contains(item.Id)).ToList();
        if (moving.Count == 0) return;
        var future = playbackOrder.Skip(1).ToList();
        var requestedSlot = Math.Clamp(toIndex - 1, 0, future.Count);
        var selectedBeforeSlot = future
            .Take(requestedSlot)
            .Count(item => selected.Contains(item.Id));
        var insertion = requestedSlot - selectedBeforeSlot;

        SaveUndo();
        if (Shuffle)
        {
            _shuffleDeck.RemoveAll(selected.Contains);
            insertion = Math.Clamp(insertion, 0, _shuffleDeck.Count);
            _shuffleDeck.InsertRange(insertion, moving.Select(item => item.Id));
        }
        else
        {
            var remaining = future
                .Where(item => !selected.Contains(item.Id))
                .ToList();
            insertion = Math.Clamp(insertion, 0, remaining.Count);
            remaining.InsertRange(insertion, moving);

            var prefixLength = Math.Clamp(_currentIndex + 1, 0, _items.Count);
            var prefix = _items.Take(prefixLength).ToList();
            _items.Clear();
            _items.AddRange(prefix);
            _items.AddRange(remaining);
            RestoreCurrent(currentId);
        }
        OnChanged();
    }

    public bool Remove(Guid id)
    {
        var index = _items.FindIndex(x => x.Id == id);
        if (index < 0) return false;
        SaveUndo();
        var currentId = CurrentId();
        _items.RemoveAt(index);
        _shuffleDeck.Remove(id);
        _playHistory.RemoveAll(item => item == id);
        RestoreCurrent(currentId);
        OnChanged();
        return true;
    }

    public int RemoveMany(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return 0;
        var selected = ids.ToHashSet();
        var removed = _items.Count(item => selected.Contains(item.Id));
        if (removed == 0) return 0;
        SaveUndo();
        var currentId = CurrentId();
        _items.RemoveAll(item => selected.Contains(item.Id));
        _shuffleDeck.RemoveAll(item => selected.Contains(item));
        _playHistory.RemoveAll(item => selected.Contains(item));
        RestoreCurrent(currentId);
        OnChanged();
        return removed;
    }

    public void MoveToTop(IReadOnlyCollection<Guid> ids) => MoveMany(ids, 0);

    public void MoveToBottom(IReadOnlyCollection<Guid> ids) => MoveMany(ids, _items.Count);

    public void ReplaceTrack(string path, Track replacement)
    {
        var changed = false;
        for (var index = 0; index < _items.Count; index++)
        {
            if (!_items[index].Track.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
            _items[index] = _items[index] with { Track = replacement };
            changed = true;
        }
        if (changed) OnChanged(false);
    }

    public Track? Advance()
    {
        if (_items.Count == 0) return null;
        if (RepeatMode == RepeatMode.One) return Current;
        var previousId = CurrentId();
        if (Shuffle && _items.Count > 1)
        {
            if (_shuffleDeck.Count == 0)
            {
                if (RepeatMode != RepeatMode.All) return null;
                RebuildShuffleDeck();
            }
            if (_shuffleDeck.Count == 0) return Current;
            var nextId = _shuffleDeck[0];
            _shuffleDeck.RemoveAt(0);
            _currentIndex = _items.FindIndex(item => item.Id == nextId);
        }
        else if (_currentIndex + 1 < _items.Count) _currentIndex++;
        else if (RepeatMode == RepeatMode.All) _currentIndex = 0;
        else return null;
        if (previousId is { } previous && CurrentId() != previous) _playHistory.Add(previous);
        NormalizePlayingFlag();
        OnChanged(false);
        return Current;
    }

    public Track? Previous()
    {
        if (_items.Count == 0) return null;
        if (_playHistory.Count > 0)
        {
            var previousId = _playHistory[^1];
            _playHistory.RemoveAt(_playHistory.Count - 1);
            var previousIndex = _items.FindIndex(item => item.Id == previousId);
            if (previousIndex >= 0)
            {
                var currentId = CurrentId();
                if (Shuffle && currentId is { } current && !_shuffleDeck.Contains(current)) _shuffleDeck.Insert(0, current);
                _currentIndex = previousIndex;
                _shuffleDeck.Remove(previousId);
            }
        }
        else if (_currentIndex > 0) _currentIndex--;
        else if (RepeatMode == RepeatMode.All) _currentIndex = _items.Count - 1;
        else return Current;
        NormalizePlayingFlag();
        OnChanged(false);
        return Current;
    }

    public void RestoreShuffleUpcoming(IEnumerable<string> paths)
    {
        _shuffleDeck.Clear();
        if (!Shuffle) return;
        var remaining = _items
            .Where(item => item.Id != CurrentId())
            .ToList();
        foreach (var path in paths)
        {
            var index = remaining.FindIndex(item => item.Track.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (index < 0) continue;
            _shuffleDeck.Add(remaining[index].Id);
            remaining.RemoveAt(index);
        }
        _shuffleDeck.AddRange(remaining.Select(item => item.Id));
        OnChanged(false);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(Capture());
        Restore(_undo.Pop());
        OnChanged(false);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(Capture());
        Restore(_redo.Pop());
        OnChanged(false);
        return true;
    }

    private static QueueEntry CreateEntry(Track track) => new(Guid.NewGuid(), track, DateTimeOffset.UtcNow);
    private Guid? CurrentId() => _currentIndex >= 0 && _currentIndex < _items.Count ? _items[_currentIndex].Id : null;

    private void SaveUndo()
    {
        _undo.Push(Capture());
        if (_undo.Count > 50)
        {
            var recent = _undo.Take(50).Reverse().ToArray();
            _undo.Clear();
            foreach (var state in recent) _undo.Push(state);
        }
        _redo.Clear();
    }

    private QueueState Capture() => new([.. _items], CurrentId(), [.. _shuffleDeck], [.. _playHistory]);
    private void Restore(QueueState state)
    {
        _items.Clear();
        _items.AddRange(state.Items);
        _shuffleDeck.Clear();
        _shuffleDeck.AddRange(state.ShuffleDeck.Where(id => _items.Any(item => item.Id == id)));
        _playHistory.Clear();
        _playHistory.AddRange(state.PlayHistory.Where(id => _items.Any(item => item.Id == id)));
        RestoreCurrent(state.CurrentId);
    }

    private void RestoreCurrent(Guid? id)
    {
        _currentIndex = id is null ? (_items.Count == 0 ? -1 : 0) : _items.FindIndex(x => x.Id == id);
        if (_currentIndex < 0 && _items.Count > 0) _currentIndex = Math.Min(_items.Count - 1, Math.Max(0, _currentIndex));
        NormalizePlayingFlag();
    }

    private void NormalizePlayingFlag()
    {
        for (var i = 0; i < _items.Count; i++) _items[i] = _items[i] with { IsPlaying = i == _currentIndex };
    }

    private void RebuildShuffleDeck()
    {
        _shuffleDeck.Clear();
        _shuffleDeck.AddRange(_items.Where(item => item.Id != CurrentId()).Select(item => item.Id));
        for (var index = _shuffleDeck.Count - 1; index > 0; index--)
        {
            var swap = _random.Next(index + 1);
            (_shuffleDeck[index], _shuffleDeck[swap]) = (_shuffleDeck[swap], _shuffleDeck[index]);
        }
    }

    private void OnChanged(bool mutation = true) => Changed?.Invoke(this, EventArgs.Empty);
    private sealed record QueueState(List<QueueEntry> Items, Guid? CurrentId, List<Guid> ShuffleDeck, List<Guid> PlayHistory);
}
