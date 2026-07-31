using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Audio;

namespace Dextromethorphan.Tests;

public sealed class DstDecoderQualificationTests
{
    [Fact]
    public void NativeDecoderRejectsMalformedFrameCleanly()
    {
        using var decoder = new DstNativeDecoder(2, 2_822_400);
        var output = new byte[decoder.FrameBytes];

        var exception = Assert.Throws<InvalidDataException>(() =>
            decoder.Decode([0x80, 0x00], 2, output));

        Assert.Contains("DST frame", exception.Message);
        Assert.Equal(9_408, decoder.FrameBytes);
    }

    [Fact]
    public void NativeDecoderContainsDeterministicMalformedFrameFuzz()
    {
        using var decoder = new DstNativeDecoder(2, 2_822_400);
        var output = new byte[decoder.FrameBytes];
        var random = new Random(0xD57);

        for (var index = 0; index < 64; index++)
        {
            var input = new byte[random.Next(1, 2_048)];
            random.NextBytes(input);
            var exception = Record.Exception(() =>
                decoder.Decode(input, input.Length, output));
            Assert.True(
                exception is null or InvalidDataException,
                $"Frame {index} escaped as {exception?.GetType().Name}: {exception?.Message}");
        }
    }

    [Fact]
    public void GeneratedDsd128EscapeFramePreservesDopAndSeeking()
    {
        const int channels = 2;
        const int dsdSampleRate = 5_644_800;
        using var decoder = new DstNativeDecoder(channels, dsdSampleRate);
        Assert.Equal(18_816, decoder.FrameBytes);
        var sourceDsd = Enumerable.Range(0, decoder.FrameBytes)
            .Select(index => (byte)(index * 37 + 11))
            .ToArray();
        var dstFrame = new byte[sourceDsd.Length + 1];
        // DST's lossless-coding escape hatch: coded=0, dummy/stuffing=0.
        sourceDsd.CopyTo(dstFrame, 1);
        var path = WriteDstDff(
            dstFrame,
            sampleRate: dsdSampleRate);
        try
        {
            using var stream = new DffDopWaveStream(path);
            var expected = ToDop(sourceDsd, channels);
            Assert.True(stream.IsDstCompressed);
            Assert.Equal(352_800, stream.WaveFormat.SampleRate);
            Assert.Equal(24, stream.WaveFormat.BitsPerSample);
            Assert.Equal(expected, Drain(stream));

            const int seekFrame = 3_777;
            var seekPosition = seekFrame * stream.WaveFormat.BlockAlign;
            stream.Position = seekPosition;
            var actual = new byte[stream.WaveFormat.BlockAlign * 31];
            Assert.Equal(
                actual.Length,
                stream.Read(actual, 0, actual.Length));
            Assert.Equal(
                expected.AsSpan(seekPosition, actual.Length).ToArray(),
                actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DstContainerRejectsDeclaredFrameCountMismatch()
    {
        var path = WriteDstDff([0x80, 0x00], declaredFrames: 2);
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() =>
                new DffDopWaveStream(path));
            Assert.Contains("frame count", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MalformedDstPayloadFailsOnReadWithFrameContext()
    {
        var path = WriteDstDff([0x80, 0x00]);
        try
        {
            using var stream = new DffDopWaveStream(path);
            var output = new byte[stream.WaveFormat.BlockAlign * 8];

            var exception = Assert.Throws<InvalidDataException>(() =>
                stream.Read(output, 0, output.Length));

            Assert.Contains("DST frame 1", exception.Message);
            Assert.Contains("could not be decoded", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "ExternalAudioFixture")]
    public void ExternalCompressedFrameIsBitExactAndSeekableThroughDop()
    {
        var root = Environment.GetEnvironmentVariable(
            "DEXTROMETHORPHAN_DST_FIXTURES");
        if (string.IsNullOrWhiteSpace(root)) return;
        var fixturePaths = Enumerable.Range(1, 3)
            .SelectMany(index => new[]
            {
                Path.Combine(root, $"frame_{index:000}.dst"),
                Path.Combine(root, $"frame_{index:000}.dsd")
            })
            .ToArray();
        Assert.All(fixturePaths, path => Assert.True(File.Exists(path), path));
        var fixtureState = fixturePaths.ToDictionary(
            path => path,
            path => (Hash(path), File.GetLastWriteTimeUtc(path)));
        var compressedFrames = Enumerable.Range(1, 3)
            .Select(index => File.ReadAllBytes(
                Path.Combine(root, $"frame_{index:000}.dst")))
            .ToArray();
        var referenceFrames = Enumerable.Range(1, 3)
            .Select(index => File.ReadAllBytes(
                Path.Combine(root, $"frame_{index:000}.dsd")))
            .ToArray();
        Assert.All(referenceFrames, frame => Assert.Equal(9_408, frame.Length));

        using (var decoder = new DstNativeDecoder(2, 2_822_400))
        {
            var actual = new byte[decoder.FrameBytes];
            for (var index = 0; index < compressedFrames.Length; index++)
            {
                var compressed = compressedFrames[index];
                Assert.Equal(
                    actual.Length,
                    decoder.Decode(compressed, compressed.Length, actual));
                Assert.Equal(referenceFrames[index], actual);
            }

            var stopwatch = Stopwatch.StartNew();
            for (var index = 0; index < 150; index++)
            {
                var compressed = compressedFrames[index % compressedFrames.Length];
                decoder.Decode(compressed, compressed.Length, actual);
            }
            stopwatch.Stop();
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"150 DST frames took {stopwatch.Elapsed.TotalMilliseconds:N0} ms and cannot sustain real-time playback.");
        }

        var dffPath = WriteDstDff(compressedFrames);
        try
        {
            using var decoded = AudioDecoderFactory.Open(new Track
            {
                Path = dffPath,
                Title = "Generated DST container"
            });
            var stream = Assert.IsType<DffDopWaveStream>(decoded.Reader);
            Assert.True(stream.IsDstCompressed);
            Assert.Equal(
                "Apache DST decoder → native DSD → DoP 1.1",
                decoded.Decoder);
            var expectedDop = ToDop(
                referenceFrames.SelectMany(frame => frame).ToArray(),
                2);
            Assert.Equal(expectedDop.Length, stream.Length);
            Assert.Equal(expectedDop, Drain(stream));

            var seekFrame = 2_352 + 1_337;
            var seekPosition = seekFrame * stream.WaveFormat.BlockAlign;
            stream.Position = seekPosition;
            var seekOutput = new byte[stream.WaveFormat.BlockAlign * 29];
            var read = stream.Read(seekOutput, 0, seekOutput.Length);
            Assert.Equal(seekOutput.Length, read);
            Assert.Equal(
                expectedDop.AsSpan(seekPosition, read).ToArray(),
                seekOutput);
        }
        finally
        {
            File.Delete(dffPath);
        }

        foreach (var path in fixturePaths)
        {
            Assert.Equal(fixtureState[path].Item1, Hash(path));
            Assert.Equal(
                fixtureState[path].Item2,
                File.GetLastWriteTimeUtc(path));
        }
    }

    private static string WriteDstDff(
        byte[] compressedFrame,
        uint declaredFrames = 1,
        int sampleRate = 2_822_400) =>
        WriteDstDff([compressedFrame], declaredFrames, sampleRate);

    private static string WriteDstDff(
        IReadOnlyList<byte[]> compressedFrames,
        uint? declaredFrames = null,
        int sampleRate = 2_822_400)
    {
        using var properties = new MemoryStream();
        WriteId(properties, "SND ");
        WriteChunk(properties, "FS  ", stream =>
            WriteUInt32(stream, checked((uint)sampleRate)));
        WriteChunk(properties, "CHNL", stream =>
        {
            WriteUInt16(stream, 2);
            WriteId(stream, "SLFT");
            WriteId(stream, "SRGT");
        });
        WriteChunk(properties, "CMPR", stream =>
        {
            WriteId(stream, "DST ");
            stream.WriteByte(0);
        });

        using var soundData = new MemoryStream();
        WriteChunk(soundData, "FRTE", stream =>
        {
            WriteUInt32(
                stream,
                declaredFrames ?? checked((uint)compressedFrames.Count));
            WriteUInt16(stream, 75);
        });
        foreach (var compressedFrame in compressedFrames)
            WriteChunk(soundData, "DSTF", stream =>
                stream.Write(compressedFrame));

        using var form = new MemoryStream();
        WriteId(form, "DSD ");
        WriteChunk(form, "PROP", stream =>
        {
            properties.Position = 0;
            properties.CopyTo(stream);
        });
        WriteChunk(form, "DST ", stream =>
        {
            soundData.Position = 0;
            soundData.CopyTo(stream);
        });

        var path = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid() + ".dff");
        using var file = File.Create(path);
        WriteId(file, "FRM8");
        WriteUInt64(file, checked((ulong)form.Length));
        form.Position = 0;
        form.CopyTo(file);
        return path;
    }

    private static byte[] ToDop(byte[] dsd, int channels)
    {
        Assert.Equal(0, dsd.Length % (channels * 2));
        var frames = dsd.Length / (channels * 2);
        var output = new byte[frames * channels * 3];
        var destination = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            var marker = frame % 2 == 0 ? (byte)0x05 : (byte)0xFA;
            var first = frame * channels * 2;
            var second = first + channels;
            for (var channel = 0; channel < channels; channel++)
            {
                output[destination++] = Reverse(dsd[first + channel]);
                output[destination++] = Reverse(dsd[second + channel]);
                output[destination++] = marker;
            }
        }
        return output;
    }

    private static byte[] Drain(DffDopWaveStream stream)
    {
        using var output = new MemoryStream();
        var buffer = new byte[1_003];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            Assert.Equal(0, read % stream.WaveFormat.BlockAlign);
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static byte Reverse(byte value)
    {
        value = (byte)(((value & 0xF0) >> 4)
                       | ((value & 0x0F) << 4));
        value = (byte)(((value & 0xCC) >> 2)
                       | ((value & 0x33) << 2));
        return (byte)(((value & 0xAA) >> 1)
                      | ((value & 0x55) << 1));
    }

    private static void WriteChunk(
        Stream destination,
        string id,
        Action<Stream> write)
    {
        using var data = new MemoryStream();
        write(data);
        WriteId(destination, id);
        WriteUInt64(destination, checked((ulong)data.Length));
        data.Position = 0;
        data.CopyTo(destination);
        if ((data.Length & 1) != 0)
            destination.WriteByte(0);
    }

    private static void WriteId(Stream stream, string id) =>
        stream.Write(System.Text.Encoding.ASCII.GetBytes(id));

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
