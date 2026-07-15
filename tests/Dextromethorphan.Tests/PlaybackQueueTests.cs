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

    private static Track NewTrack(int id) => new() { Id = id, Path = $"C:\\music\\{id}.flac", Title = $"Track {id}" };
}
