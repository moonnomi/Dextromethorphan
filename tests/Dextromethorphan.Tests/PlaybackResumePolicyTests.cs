using Dextromethorphan.App.ViewModels;

namespace Dextromethorphan.Tests;

public sealed class PlaybackResumePolicyTests
{
    [Fact]
    public void ExplicitLibrarySelectionCanResumeSavedPosition()
    {
        Assert.True(MainViewModel.ShouldResumeTrackBookmark(
            resumeEnabled: true,
            PlaybackStartReason.ExplicitSelection));
    }

    [Fact]
    public void QueueNavigationAlwaysStartsTrackFresh()
    {
        Assert.False(MainViewModel.ShouldResumeTrackBookmark(
            resumeEnabled: true,
            PlaybackStartReason.QueueNavigation));
    }

    [Fact]
    public void DisabledResumeNeverAppliesSavedPosition()
    {
        Assert.False(MainViewModel.ShouldResumeTrackBookmark(
            resumeEnabled: false,
            PlaybackStartReason.ExplicitSelection));
    }
}
