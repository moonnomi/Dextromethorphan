using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Tests;

public sealed class TrackModelTests
{
    [Fact]
    public void QualityTextDoesNotInventZeroBitDepth()
    {
        var track = new Track
        {
            Path = "sample.mp3",
            Title = "Sample",
            Codec = "mp3",
            SampleRate = 44_100
        };

        Assert.DoesNotContain("0-bit", track.QualityText, StringComparison.Ordinal);
        Assert.Contains("MP3", track.QualityText, StringComparison.Ordinal);
        Assert.Contains("44.1 kHz", track.QualityText, StringComparison.Ordinal);
    }
}
