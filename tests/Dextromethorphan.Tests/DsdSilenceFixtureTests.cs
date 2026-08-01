using Dextromethorphan.DopQualification;
using Dextromethorphan.Infrastructure.Audio;

namespace Dextromethorphan.Tests;

public sealed class DsdSilenceFixtureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dextromethorphan-dsd-silence-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(2_822_400, 176_400)]
    [InlineData(5_644_800, 352_800)]
    public void GeneratedFixtureIsFiniteStereoSilenceWithValidDopMarkers(
        int dsdRate,
        int carrierRate)
    {
        var fixture = DsdSilenceFixture.Write(_root, dsdRate, 2);
        var hashBefore = File.ReadAllBytes(fixture.Path);

        using var stream = new DsfDopWaveStream(fixture.Path);
        var buffer = new byte[stream.WaveFormat.BlockAlign * 4];
        Assert.Equal(buffer.Length, stream.Read(buffer, 0, buffer.Length));

        Assert.Equal(carrierRate, stream.WaveFormat.SampleRate);
        Assert.Equal(24, stream.WaveFormat.BitsPerSample);
        Assert.Equal(2, stream.WaveFormat.Channels);
        Assert.Equal(TimeSpan.FromSeconds(2), stream.TotalTime);
        for (var frame = 0; frame < 4; frame++)
        {
            var marker = frame % 2 == 0 ? (byte)0x05 : (byte)0xFA;
            for (var channel = 0; channel < 2; channel++)
            {
                var offset = frame * stream.WaveFormat.BlockAlign + channel * 3;
                Assert.Equal(DsdSilenceFixture.SilenceByte, buffer[offset]);
                Assert.Equal(DsdSilenceFixture.SilenceByte, buffer[offset + 1]);
                Assert.Equal(marker, buffer[offset + 2]);
            }
        }
        Assert.Equal(hashBefore, File.ReadAllBytes(fixture.Path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
