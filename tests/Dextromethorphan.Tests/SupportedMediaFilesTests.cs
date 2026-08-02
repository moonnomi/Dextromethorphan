using Dextromethorphan.Core.Library;

namespace Dextromethorphan.Tests;

public sealed class SupportedMediaFilesTests
{
    [Theory]
    [InlineData("track.FLAC")]
    [InlineData("track.opus")]
    [InlineData("disc.cue")]
    [InlineData("master.dsf")]
    public void AcceptsSupportedAudioAndCueFiles(string path) => Assert.True(SupportedMediaFiles.IsSupported(path));

    [Theory]
    [InlineData("cover.jpg")]
    [InlineData("notes.txt")]
    [InlineData("")]
    public void RejectsNonAudioFiles(string path) => Assert.False(SupportedMediaFiles.IsSupported(path));
}
