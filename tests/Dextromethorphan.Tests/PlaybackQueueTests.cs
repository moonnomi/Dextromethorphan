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
    public void ShufflePlaysEveryRemainingTrackOnceBeforeStopping()
    {
        var queue = new PlaybackQueue { Shuffle = true };
        var tracks = Enumerable.Range(1, 8).Select(NewTrack).ToArray();
        queue.Replace(tracks, 0);

        var played = new List<Track> { tracks[0] };
        Track? next;
        while ((next = queue.Advance()) is not null) played.Add(next);

        Assert.Equal(tracks.Length, played.Count);
        Assert.Equal(tracks.Length, played.DistinctBy(track => track.Path).Count());
    }

    [Fact]
    public void ShuffleOrderCanBeRestoredAcrossSessions()
    {
        var tracks = Enumerable.Range(1, 7).Select(NewTrack).ToArray();
        var first = new PlaybackQueue { Shuffle = true };
        first.Replace(tracks, 2);
        var expected = first.ShuffleUpcomingPaths.ToArray();

        var restored = new PlaybackQueue { Shuffle = true };
        restored.Replace(tracks, 2);
        restored.RestoreShuffleUpcoming(expected);

        Assert.Equal(expected, restored.ShuffleUpcomingPaths);
        Assert.Equal(expected, Enumerable.Range(0, expected.Length).Select(_ => restored.Advance()!.Path));
    }

    [Fact]
    public void ShuffleDeckSurvivesAddRemoveAndPrevious()
    {
        var queue = new PlaybackQueue { Shuffle = true };
        var tracks = Enumerable.Range(1, 5).Select(NewTrack).ToArray();
        queue.Replace(tracks, 0);
        var added = NewTrack(9);
        queue.Add([added]);
        var removed = queue.Items.First(item => !item.IsPlaying && item.Track != added);
        queue.Remove(removed.Id);

        var firstAdvance = queue.Advance();
        Assert.NotNull(firstAdvance);
        Assert.Equal(tracks[0], queue.Previous());
        Assert.Equal(firstAdvance, queue.Advance());
        Assert.DoesNotContain(removed.Track.Path, queue.ShuffleUpcomingPaths);
    }

    [Fact]
    public void BatchQueueChangesAreAtomicAndUndoable()
    {
        var queue = new PlaybackQueue();
        var tracks = Enumerable.Range(1, 6).Select(NewTrack).ToArray();
        queue.Replace(tracks, 2);
        var selected = new[] { queue.Items[1].Id, queue.Items[3].Id };

        queue.MoveToBottom(selected);
        Assert.Equal(new[] { 1L, 3L, 5L, 6L, 2L, 4L }, queue.Items.Select(item => item.Track.Id));
        Assert.Equal(tracks[2], queue.Current);
        Assert.True(queue.Undo());
        Assert.Equal(tracks, queue.Items.Select(item => item.Track));

        Assert.Equal(2, queue.RemoveMany(selected));
        Assert.True(queue.Undo());
        Assert.Equal(tracks, queue.Items.Select(item => item.Track));
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

    [Fact]
    public void PlaybackOrderStartsAtCurrentAndOmitsAlreadyPlayedTracks()
    {
        var queue = new PlaybackQueue();
        var tracks = Enumerable.Range(1, 5).Select(NewTrack).ToArray();
        queue.Replace(tracks, 2);

        Assert.Equal(tracks.Skip(2), queue.PlaybackOrder.Select(item => item.Track));
        Assert.True(queue.PlaybackOrder[0].IsPlaying);
    }

    [Fact]
    public void TimelineOrderKeepsPlayedTracksBeforeTheCurrentTrack()
    {
        var queue = new PlaybackQueue();
        var tracks = Enumerable.Range(1, 5).Select(NewTrack).ToArray();
        queue.Replace(tracks, 2);

        Assert.Equal(tracks, queue.TimelineOrder.Select(item => item.Track));
        Assert.Equal(2, queue.TimelineOrder.ToList().FindIndex(item => item.IsPlaying));
    }

    [Fact]
    public void ShuffledTimelineCombinesHistoryCurrentAndTrueUpcomingOrder()
    {
        var queue = new PlaybackQueue { Shuffle = true };
        var tracks = Enumerable.Range(1, 6).Select(NewTrack).ToArray();
        queue.Replace(tracks, 0);
        var first = queue.Current;
        var second = queue.Advance();
        var third = queue.Advance();

        Assert.Equal(first, queue.TimelineOrder[0].Track);
        Assert.Equal(second, queue.TimelineOrder[1].Track);
        Assert.Equal(third, queue.TimelineOrder[2].Track);
        Assert.True(queue.TimelineOrder[2].IsPlaying);
        Assert.Equal(
            queue.ShuffleUpcomingPaths,
            queue.TimelineOrder.Skip(3).Select(item => item.Track.Path));
        Assert.Equal(queue.Items.Count, queue.TimelineOrder.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public void ShuffledPlaybackOrderMatchesTheActualShuffleDeck()
    {
        var queue = new PlaybackQueue { Shuffle = true };
        var tracks = Enumerable.Range(1, 6).Select(NewTrack).ToArray();
        queue.Replace(tracks, 2);

        Assert.Equal(
            new[] { tracks[2].Path }.Concat(queue.ShuffleUpcomingPaths),
            queue.PlaybackOrder.Select(item => item.Track.Path));
    }

    [Fact]
    public void MovingAShuffledEntryToNextChangesTheTrackThatActuallyPlaysNext()
    {
        var queue = new PlaybackQueue { Shuffle = true };
        queue.Replace(Enumerable.Range(1, 6).Select(NewTrack), 0);
        var promoted = queue.PlaybackOrder[^1];

        queue.MoveInPlaybackOrder([promoted.Id], 1);

        Assert.Equal(promoted.Id, queue.PlaybackOrder[1].Id);
        Assert.Equal(promoted.Track, queue.Advance());
    }

    [Fact]
    public void ReorderingPlaybackOrderKeepsPlayedTracksAndCurrentPositionStable()
    {
        var queue = new PlaybackQueue();
        var tracks = Enumerable.Range(1, 6).Select(NewTrack).ToArray();
        queue.Replace(tracks, 2);
        var promoted = queue.PlaybackOrder[^1];

        queue.MoveInPlaybackOrder([promoted.Id], 1);

        Assert.Equal(tracks[2], queue.Current);
        Assert.Equal(new[] { 1L, 2L, 3L, 6L, 4L, 5L }, queue.Items.Select(item => item.Track.Id));
        Assert.Equal(new[] { 3L, 6L, 4L, 5L }, queue.PlaybackOrder.Select(item => item.Track.Id));
    }

    [Fact]
    public void DroppingAnEntryOnEitherSideOfItselfDoesNotMoveIt()
    {
        var queue = new PlaybackQueue();
        var tracks = Enumerable.Range(1, 6).Select(NewTrack).ToArray();
        queue.Replace(tracks, 1);
        var entry = queue.PlaybackOrder[2];
        var expected = queue.Items.Select(item => item.Id).ToArray();

        queue.MoveInPlaybackOrder([entry.Id], 2);
        Assert.Equal(expected, queue.Items.Select(item => item.Id));

        queue.MoveInPlaybackOrder([entry.Id], 3);
        Assert.Equal(expected, queue.Items.Select(item => item.Id));
    }

    [Fact]
    public void AdvancingShiftsPlaybackOrderToTheNewCurrentTrack()
    {
        var queue = new PlaybackQueue { Shuffle = true };
        queue.Replace(Enumerable.Range(1, 5).Select(NewTrack), 0);
        var expected = queue.PlaybackOrder[1];

        queue.Advance();

        Assert.Equal(expected.Id, queue.PlaybackOrder[0].Id);
        Assert.True(queue.PlaybackOrder[0].IsPlaying);
    }

    private static Track NewTrack(int id) => new() { Id = id, Path = $"C:\\music\\{id}.flac", Title = $"Track {id}" };
}
