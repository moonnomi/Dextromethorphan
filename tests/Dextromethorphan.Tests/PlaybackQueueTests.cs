using Dextromethorphan.Core.Models;
using Dextromethorphan.Core.Playback;

namespace Dextromethorphan.Tests;

public sealed class PlaybackQueueTests
{
    [Fact]
    public void MutationsCanBeUndoneAndRedoneWithoutLosingCurrentTrack()
    {
        var tracks = Enumerable.Range(1, 3).Select(NewTrack).ToArray();
        var queue = new PlaybackQueue();
        queue.Replace(tracks, 1);
        var current = queue.Current;

        queue.Move(1, 2);
        Assert.Equal(current, queue.Current);
        Assert.True(queue.Undo());
        Assert.Equal(tracks, queue.Items.Select(x => x.Track));
        Assert.Equal(current, queue.Current);
        Assert.True(queue.Redo());
        Assert.Equal(current, queue.Current);
    }

    [Fact]
    public void RepeatAllWrapsAtEnd()
    {
        var queue = new PlaybackQueue { RepeatMode = RepeatMode.All };
        var tracks = new[] { NewTrack(1), NewTrack(2) };
        queue.Replace(tracks, 1);
        Assert.Equal(tracks[0], queue.Advance());
    }

    [Fact]
    public void RepeatOneKeepsTheCurrentTrack()
    {
        var queue = new PlaybackQueue { RepeatMode = RepeatMode.One };
        var tracks = new[] { NewTrack(1), NewTrack(2) };
        queue.Replace(tracks, 0);

        Assert.Equal(tracks[0], queue.Advance());
        Assert.Equal(0, queue.CurrentIndex);
    }

    [Fact]
    public void ShuffleNeverImmediatelyRepeatsWhenMoreThanOneTrackExists()
    {
        var queue = new PlaybackQueue { Shuffle = true };
        var tracks = Enumerable.Range(1, 4).Select(NewTrack).ToArray();
        queue.Replace(tracks, 0);

        var next = queue.Advance();

        Assert.NotNull(next);
        Assert.NotEqual(tracks[0], next);
        Assert.Contains(next, tracks);
    }

    [Fact]
    public void QueueEntryCanBeSelectedForImmediatePlayback()
    {
        var queue = new PlaybackQueue();
        var tracks = new[] { NewTrack(1), NewTrack(2), NewTrack(3) };
        queue.Replace(tracks);

        var selected = queue.Select(queue.Items[2].Id);

        Assert.Equal(tracks[2], selected);
        Assert.Equal(2, queue.CurrentIndex);
        Assert.True(queue.Items[2].IsPlaying);
    }

    private static Track NewTrack(int id) => new() { Id = id, Path = $"C:\\music\\{id}.flac", Title = $"Track {id}" };
}
