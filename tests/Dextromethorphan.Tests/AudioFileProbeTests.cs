using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Audio;
using NAudio.Wave;

namespace Dextromethorphan.Tests;

public sealed class AudioFileProbeTests
{
    [Fact]
    public async Task ProbeReadsHeadAndTailWithoutChangingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(path, new WaveFormat(8000, 16, 1)))
                writer.Write(new byte[48000], 0, 48000);
            var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            var result = await AudioFileProbe.RunAsync(new Track { Path = path, Title = "Probe" }, TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded, result.Error);
            Assert.True(result.HeadBytes > 0);
            Assert.True(result.TailBytes > 0);
            Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MissingFileProducesDiagnosticInsteadOfThrowing()
    {
        var result = await AudioFileProbe.RunAsync(new Track { Path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.wav"), Title = "Missing" }, TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Error!);
    }
}
