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

    [Fact]
    public void MetadataPresentationUsesReadableFallbacksAndChannelNames()
    {
        var track = new Track
        {
            Path = @"C:\Music\Album\Sample.flac",
            Title = "Sample",
            Artist = "Artist",
            Album = "Album",
            Codec = "flac",
            SampleRate = 96_000,
            BitsPerSample = 24,
            Channels = 2,
            Bitrate = 2_841,
            FileSize = 31_457_280,
            Duration = TimeSpan.FromMinutes(4.5)
        };

        Assert.Equal("2,841 kbps", track.BitrateText);
        Assert.Equal("Stereo", track.ChannelText);
        Assert.Equal("30 MB", track.FileSizeText);
        Assert.Contains("Sample by Artist", track.MetadataAutomationText);
        Assert.Contains("FLAC · 96 kHz · 24-bit", track.MetadataAutomationText);
        Assert.Contains(track.Path, track.MetadataAutomationText);
    }

    [Fact]
    public void MetadataPresentationDoesNotShowMisleadingZeroValues()
    {
        var track = new Track
        {
            Path = "unknown.audio",
            Title = "Unknown"
        };

        Assert.Equal("Unknown", track.BitrateText);
        Assert.Equal("Unknown", track.ChannelText);
        Assert.Equal("Unknown", track.FileSizeText);
        Assert.Equal("Unknown", track.YearText);
        Assert.Equal("Unknown", track.GenreText);
        Assert.Equal("Not tagged", track.ReplayGainDisplayText);
        Assert.Equal("0", track.PlayCountText);
    }
}
