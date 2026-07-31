using System.Security.Cryptography;
using System.Text.Json;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Audio;
using Dextromethorphan.Infrastructure.Library;

namespace Dextromethorphan.Tests;

public sealed class AudioFormatCorpusTests
{
    private static readonly string Corpus = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "AudioFormats");

    [Fact]
    public async Task InstalledDecoderCapabilityProbeUsesEmbeddedSyntheticAudio()
    {
        var capabilities = await new AudioDecoderCapabilityService()
            .InspectAsync(
                cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(11, capabilities.Count);
        Assert.Contains(
            capabilities,
            capability => capability.Format == "Opus"
                && capability.State == DecoderCapabilityState.Bundled);
        Assert.Contains(
            capabilities,
            capability => capability.Format == "DST-compressed DFF"
                && capability.State == DecoderCapabilityState.Bundled);
        Assert.DoesNotContain(
            capabilities,
            capability => capability.State
                == DecoderCapabilityState.Unavailable);
    }

    [Fact]
    public void ManifestHashesEveryGeneratedFixture()
    {
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(Corpus, "manifest.json")));
        Assert.Equal(
            "synthetic 997 Hz sine wave; no copyrighted audio",
            manifest.RootElement.GetProperty("source").GetString());
        foreach (var fixture in manifest.RootElement
                     .GetProperty("files")
                     .EnumerateArray())
        {
            var file = fixture.GetProperty("file").GetString()!;
            var expected = fixture.GetProperty("sha256").GetString();
            using var input = File.OpenRead(Path.Combine(Corpus, file));
            Assert.Equal(
                expected,
                Convert.ToHexString(SHA256.HashData(input))
                    .ToLowerInvariant());
        }
    }

    [Theory]
    [InlineData("reference.wav", "NAudio native")]
    [InlineData("reference.aiff", "NAudio native")]
    [InlineData("reference.mp3", "NAudio native")]
    [InlineData("reference.flac", "Managed FLAC decoder")]
    [InlineData("reference.ogg", "NVorbis managed decoder")]
    [InlineData("reference.opus", "Managed Concentus Opus decoder")]
    [InlineData("aac.aac", "Windows Media Foundation")]
    [InlineData("aac.m4a", "Windows Media Foundation")]
    [InlineData("alac.m4a", "Windows Media Foundation")]
    [InlineData("unicode-音楽-alac.m4a", "Windows Media Foundation")]
    [InlineData("reference.wma", "Windows Media Foundation")]
    public void PcmCorpusDecodesSeeksAndReachesStableEnd(
        string file,
        string expectedDecoder)
    {
        var path = Path.Combine(Corpus, file);
        using var decoded = AudioDecoderFactory.Open(
            new Track
            {
                Path = path,
                Title = file,
                Duration = TimeSpan.FromSeconds(2)
            });

        Assert.Equal(expectedDecoder, decoded.Decoder);
        Assert.True(decoded.Reader.WaveFormat.SampleRate > 0);
        Assert.True(decoded.Reader.WaveFormat.Channels > 0);
        var buffer = new byte[
            Math.Max(4096, decoded.Reader.WaveFormat.BlockAlign * 128)];
        Assert.True(decoded.Reader.Read(buffer, 0, buffer.Length) > 0);

        decoded.Reader.CurrentTime =
            TimeSpan.FromTicks(decoded.Reader.TotalTime.Ticks / 2);
        Assert.InRange(
            decoded.Reader.CurrentTime,
            TimeSpan.FromMilliseconds(700),
            TimeSpan.FromMilliseconds(1_300));
        Assert.True(decoded.Reader.Read(buffer, 0, buffer.Length) > 0);

        var reads = 0;
        while (decoded.Reader.Read(buffer, 0, buffer.Length) > 0)
        {
            reads++;
            Assert.True(reads < 10_000);
        }
        Assert.Equal(0, decoded.Reader.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void ManagedOpusRemovesEncoderDelayAndSeeksToTheDecodedFrame()
    {
        var path = Path.Combine(Corpus, "reference.opus");
        using var sequential = new OpusWaveStream(path);
        using var seeked = new OpusWaveStream(path);

        Assert.Equal(48_000, sequential.WaveFormat.SampleRate);
        Assert.Equal(2, sequential.WaveFormat.Channels);
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            sequential.TotalTime);

        var target = sequential.WaveFormat.AverageBytesPerSecond * 5L / 4L;
        target -= target % sequential.WaveFormat.BlockAlign;
        var discard = new byte[4096];
        while (sequential.Position < target)
        {
            var requested = (int)Math.Min(
                discard.Length,
                target - sequential.Position);
            Assert.True(sequential.Read(discard, 0, requested) > 0);
        }

        seeked.Position = target;
        var expected = new byte[4096];
        var actual = new byte[4096];
        sequential.ReadExactly(expected);
        seeked.ReadExactly(actual);

        Assert.Equal(target, seeked.Position - actual.Length);
        var maximumSampleDifference = 0;
        long totalSampleDifference = 0;
        for (var index = 0; index < expected.Length; index += sizeof(short))
        {
            var difference = Math.Abs(
                BitConverter.ToInt16(expected, index)
                - BitConverter.ToInt16(actual, index));
            maximumSampleDifference = Math.Max(
                maximumSampleDifference,
                difference);
            totalSampleDifference += difference;
        }
        Assert.InRange(maximumSampleDifference, 0, 8);
        Assert.InRange(
            totalSampleDifference / (expected.Length / (double)sizeof(short)),
            0,
            1);
    }

    [Fact]
    public async Task MetadataStressAndVbrFixturesRemainReadableAndUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var metadataPath = Path.Combine(Corpus, "metadata-heavy.flac");
        var vbrPath = Path.Combine(Corpus, "reference.mp3");
        var metadataHash = Hash(metadataPath);
        var vbrHash = Hash(vbrPath);
        var reader = new TagLibMetadataReader();

        var metadata = await reader.ReadAsync(
            metadataPath,
            cancellationToken);
        var vbr = await reader.ReadAsync(vbrPath, cancellationToken);

        Assert.Equal("\u97F3\u697D metadata fixture", metadata.Title);
        Assert.Contains("Alpha", metadata.Artist);
        Assert.Contains("Gamma", metadata.Artist);
        Assert.True(
            metadata.Comment.Length >= 12_000,
            $"Decoded comment length was {metadata.Comment.Length:N0} characters.");
        Assert.NotNull(metadata.Artwork);
        Assert.True(metadata.Artwork.Length > 1_000);
        Assert.InRange(
            metadata.Duration,
            TimeSpan.FromMilliseconds(1_950),
            TimeSpan.FromMilliseconds(2_050));
        Assert.InRange(
            vbr.Duration,
            TimeSpan.FromMilliseconds(1_950),
            TimeSpan.FromMilliseconds(2_100));
        Assert.Equal(metadataHash, Hash(metadataPath));
        Assert.Equal(vbrHash, Hash(vbrPath));
    }

    [Theory]
    [InlineData("reference.dsf", "Native DSF → DoP 1.1")]
    [InlineData("reference.dff", "Native DSDIFF → DoP 1.1")]
    public void DsdCorpusProducesSeekableDop(
        string file,
        string expectedDecoder)
    {
        using var decoded = AudioDecoderFactory.Open(
            new Track
            {
                Path = Path.Combine(Corpus, file),
                Title = file
            });
        var buffer = new byte[12];

        Assert.Equal(expectedDecoder, decoded.Decoder);
        Assert.Equal(12, decoded.Reader.Read(buffer, 0, buffer.Length));
        Assert.Contains(buffer[2], new byte[] { 0x05, 0xFA });
        decoded.Reader.Position = 0;
        Assert.Equal(12, decoded.Reader.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void MalformedAndTruncatedInputsFailWithoutEscapingNativeState()
    {
        Assert.ThrowsAny<Exception>(() =>
            AudioDecoderFactory.Open(
                new Track
                {
                    Path = Path.Combine(
                        Corpus,
                        "malformed-header.flac"),
                    Title = "Malformed"
                }));

        try
        {
            using var truncated = AudioDecoderFactory.Open(
                new Track
                {
                    Path = Path.Combine(Corpus, "truncated.mp3"),
                    Title = "Truncated"
                });
            var buffer = new byte[4096];
            var read = truncated.Reader.Read(buffer, 0, buffer.Length);
            Assert.InRange(read, 0, buffer.Length);
        }
        catch (Exception exception)
        {
            Assert.True(
                exception is InvalidDataException
                    or EndOfStreamException
                    or NotSupportedException,
                exception.ToString());
        }
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }
}
