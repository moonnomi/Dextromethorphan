using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Core.Playback;

public sealed class PlaybackQueue : IPlaybackQueue
{
    private readonly List<QueueEntry> _items = [];
    private readonly Stack<QueueState> _undo = [];
    private readonly Stack<QueueState> _redo = [];
    private readonly Random _random = new();
    private int _currentIndex = -1;

    public IReadOnlyList<QueueEntry> Items => _items;
    public int CurrentIndex => _currentIndex;
    public RepeatMode RepeatMode { get; set; }
    public bool Shuffle { get; set; }
    public Track? Current => _currentIndex >= 0 && _currentIndex < _items.Count ? _items[_currentIndex].Track : null;
    public event EventHandler? Changed;

    public Track? Select(Guid id)
    {
        var index = _items.FindIndex(x => x.Id == id);
        if (index < 0) return null;
        _currentIndex = index;
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
        NormalizePlayingFlag();
        OnChanged();
    }

    public void PlayNext(IEnumerable<Track> tracks)
    {
        var additions = tracks.Select(CreateEntry).ToList();
        if (additions.Count == 0) return;
        SaveUndo();
        _items.InsertRange(Math.Max(0, _currentIndex + 1), additions);
        if (_currentIndex < 0) _currentIndex = 0;
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

    public bool Remove(Guid id)
    {
        var index = _items.FindIndex(x => x.Id == id);
        if (index < 0) return false;
        SaveUndo();
        var currentId = CurrentId();
        _items.RemoveAt(index);
        RestoreCurrent(currentId);
        OnChanged();
        return true;
    }

    public Track? Advance()
    {
        if (_items.Count == 0) return null;
        if (RepeatMode == RepeatMode.One) return Current;
        if (Shuffle && _items.Count > 1)
        {
            var next = _currentIndex;
            while (next == _currentIndex) next = _random.Next(_items.Count);
            _currentIndex = next;
        }
        else if (_currentIndex + 1 < _items.Count) _currentIndex++;
        else if (RepeatMode == RepeatMode.All) _currentIndex = 0;
        else return null;
        NormalizePlayingFlag();
        OnChanged(false);
        return Current;
    }

    public Track? Previous()
    {
        if (_items.Count == 0) return null;
        if (_currentIndex > 0) _currentIndex--;
        else if (RepeatMode == RepeatMode.All) _currentIndex = _items.Count - 1;
        else return Current;
        NormalizePlayingFlag();
        OnChanged(false);
        return Current;
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

    private QueueState Capture() => new([.. _items], CurrentId());
    private void Restore(QueueState state)
    {
        _items.Clear();
        _items.AddRange(state.Items);
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

    private void OnChanged(bool mutation = true) => Changed?.Invoke(this, EventArgs.Empty);
    private sealed record QueueState(List<QueueEntry> Items, Guid? CurrentId);
}
