using Dextromethorphan.Core.Lyrics;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Tests;

public sealed class LrcParserTests
{
    [Fact]
    public void ParsesMultipleTimestampsOffsetAndEnhancedWords()
    {
        const string source = """
            [ar:Miles Davis]
            [offset:50]
            [00:01.20][00:04.00]<00:01.20>Blue <00:01.80>in <00:02.10>Green
            [00:07.500]Second line
            """;

        var lyrics = LrcParser.Parse(source);

        Assert.Equal("Miles Davis", lyrics.Metadata["ar"]);
        Assert.Equal(3, lyrics.Lines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), lyrics.Lines[0].Start);
        Assert.Equal(3, lyrics.Lines[0].Words.Count);
        Assert.Equal("Blue in Green", lyrics.Lines[0].Text);
        Assert.Equal("Second line", lyrics.At(TimeSpan.FromSeconds(8))?.Text);
    }

    [Fact]
    public void ActivatesAStandardLrcLineOnlyInsideItsTimestampWindow()
    {
        const string source = """
            [00:15.63] First line
            [00:21.61] Second line
            [00:26.88] Third line
            """;

        var lyrics = LrcParser.Parse(source);

        Assert.Null(lyrics.At(TimeSpan.FromSeconds(6)));
        Assert.True(lyrics.Lines[0].IsActive(TimeSpan.FromSeconds(16)));
        Assert.False(lyrics.Lines[0].IsActive(TimeSpan.FromSeconds(22)));
        Assert.Equal("Second line", lyrics.At(TimeSpan.FromSeconds(22))?.Text);
    }

    [Fact]
    public void EqualTimestampLinesShareTheNextDistinctEndAndRemainActiveTogether()
    {
        const string source = """
            [00:10.00]世界
            [00:10.00][rom]sekai
            [00:10.00][tr]world
            [00:14.00]next
            """;

        var lyrics = LrcParser.Parse(source);
        var active = lyrics.AtAll(TimeSpan.FromSeconds(12));

        Assert.Equal(3, active.Count);
        Assert.All(active, line => Assert.Equal(TimeSpan.FromSeconds(14), line.End));
        Assert.Equal(LyricLineRole.Primary, active[0].Role);
        Assert.Equal(LyricLineRole.Romanization, active[1].Role);
        Assert.Equal(LyricLineRole.Translation, active[2].Role);
    }

    [Fact]
    public void EmptyTimedLineBecomesAnInstrumentalMarker()
    {
        var lyrics = LrcParser.Parse("[00:03.00]\n[00:08.00]Back in");
        var line = lyrics.At(TimeSpan.FromSeconds(5));
        Assert.Equal("♪", line?.Text);
        Assert.Equal(LyricLineRole.Instrumental, line?.Role);
    }

    [Fact]
    public void AcceptsBomHoursAndMixedFractionsWhileSkippingMalformedTimes()
    {
        var lyrics = LrcParser.Parse("\uFEFF[00:01.2]tenths\n[00:02:03.045]hours\n[00:99.00]bad\n[nope]");
        Assert.Equal(2, lyrics.Lines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), lyrics.Lines[0].Start);
        Assert.Equal(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3) + TimeSpan.FromMilliseconds(45), lyrics.Lines[1].Start);
    }

    [Fact]
    public void BoundsHostileInputWithoutThrowing()
    {
        var oversized = new string('x', LrcParser.MaximumContentLength + 100);
        var exception = Record.Exception(() => LrcParser.Parse(oversized));
        Assert.Null(exception);
    }
}
