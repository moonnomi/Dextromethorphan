using Dextromethorphan.Core.Lyrics;

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
}
