using Dextromethorphan.App.ViewModels;

namespace Dextromethorphan.Tests;

public sealed class TrackFailurePolicyTests
{
    [Theory]
    [MemberData(nameof(RecoverableFailures))]
    public void MediaAndFileReadFailuresAreSkippable(Exception exception)
    {
        Assert.True(TrackFailurePolicy.IsRecoverable(exception));
    }

    [Fact]
    public void UnexpectedEngineStateFailureIsNotSilentlySkipped()
    {
        Assert.False(
            TrackFailurePolicy.IsRecoverable(
                new InvalidOperationException("broken state")));
    }

    [Fact]
    public void MediaFoundationUnsupportedCodeHasFriendlyExplanation()
    {
        var exception = new Exception("raw HRESULT")
        {
            HResult = unchecked((int)0xC00D36C4)
        };

        Assert.Equal(
            "Windows could not decode this audio format",
            TrackFailurePolicy.FriendlyMessage(exception));
    }

    public static TheoryData<Exception> RecoverableFailures => new()
    {
        new IOException("offline"),
        new UnauthorizedAccessException("blocked"),
        new NotSupportedException("codec"),
        new InvalidDataException("corrupt")
    };
}
