using Dextromethorphan.App.ViewModels;

namespace Dextromethorphan.Tests;

public sealed class PlaybackResumePolicyTests
{
    [Fact]
    public void SessionPositionIsSavedOnlyForTheActiveQueueTrack()
    {
        Assert.Equal(
            42.5,
            MainViewModel.CurrentSessionPositionSeconds(
                @"C:\music\current.flac",
                @"c:\MUSIC\CURRENT.flac",
                TimeSpan.FromSeconds(42.5)));
    }

    [Fact]
    public void PositionFromAnotherTrackIsNeverSavedIntoTheSession()
    {
        Assert.Equal(
            0,
            MainViewModel.CurrentSessionPositionSeconds(
                @"C:\music\selected.flac",
                @"C:\music\previous.flac",
                TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void SessionWithoutAnActivePlaybackTrackStartsAtTheBeginning()
    {
        Assert.Equal(
            0,
            MainViewModel.CurrentSessionPositionSeconds(
                @"C:\music\selected.flac",
                null,
                TimeSpan.FromMinutes(2)));
    }
}
