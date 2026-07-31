using System.Buffers.Binary;
using System.Text;
using Dextromethorphan.Infrastructure.Audio;
using NAudio.Wave;

namespace Dextromethorphan.Tests;

public sealed class DopFramingQualificationTests
{
    [Theory]
    [InlineData(2_822_400, 176_400)]
    [InlineData(5_644_800, 352_800)]
    public void GeneratedDsfPreservesMarkersChannelsAndSeek(
        int dsdSampleRate,
        int dopSampleRate)
    {
        const int channels = 2;
        const int blockSize = 16;
        var planarDsd = Enumerable.Range(0, channels * blockSize)
            .Select(index => (byte)(index * 29 + 3))
            .ToArray();
        var path = WriteDsf(
            dsdSampleRate,
            channels,
            blockSize,
            planarDsd);
        try
        {
            using var stream = new DsfDopWaveStream(path);
            var expected = PlanarToDop(
                planarDsd,
                channels,
                blockSize,
                reverse: false);

            Assert.Equal(dopSampleRate, stream.WaveFormat.SampleRate);
            Assert.Equal(24, stream.WaveFormat.BitsPerSample);
            Assert.Equal(channels, stream.WaveFormat.Channels);
            Assert.Equal(expected, Drain(stream, 47));

            const int seekFrame = 5;
            AssertSeek(stream, expected, seekFrame, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GeneratedSixChannelDsd128DffPreservesInterleaveAndSeek()
    {
        const int channels = 6;
        const int bytesPerChannel = 12;
        var interleavedDsd = new byte[channels * bytesPerChannel];
        for (var byteIndex = 0; byteIndex < bytesPerChannel; byteIndex++)
        for (var channel = 0; channel < channels; channel++)
            interleavedDsd[byteIndex * channels + channel] =
                (byte)(byteIndex * 17 + channel * 31 + 1);
        var path = WriteDff(
            5_644_800,
            channels,
            interleavedDsd);
        try
        {
            using var stream = new DffDopWaveStream(path);
            var expected = InterleavedToDop(
                interleavedDsd,
                channels,
                reverse: true);

            Assert.False(stream.IsDstCompressed);
            Assert.Equal(352_800, stream.WaveFormat.SampleRate);
            Assert.Equal(channels, stream.WaveFormat.Channels);
            Assert.Equal(expected, Drain(
                stream,
                stream.WaveFormat.BlockAlign * 3 + 5));

            AssertSeek(stream, expected, seekFrame: 3, frames: 2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertSeek(
        WaveStream stream,
        byte[] expected,
        int seekFrame,
        int frames)
    {
        var position = seekFrame * stream.WaveFormat.BlockAlign;
        stream.Position = position;
        var actual = new byte[frames * stream.WaveFormat.BlockAlign];

        Assert.Equal(actual.Length, stream.Read(actual, 0, actual.Length));
        Assert.Equal(
            expected.AsSpan(position, actual.Length).ToArray(),
            actual);
        Assert.Equal(position + actual.Length, stream.Position);
    }

    private static byte[] Drain(WaveStream stream, int callbackBytes)
    {
        using var output = new MemoryStream();
        var buffer = new byte[callbackBytes];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            Assert.Equal(0, read % stream.WaveFormat.BlockAlign);
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static byte[] PlanarToDop(
        byte[] source,
        int channels,
        int blockSize,
        bool reverse)
    {
        var pairs = blockSize / 2;
        var output = new byte[pairs * channels * 3];
        var destination = 0;
        for (var pair = 0; pair < pairs; pair++)
        {
            var marker = pair % 2 == 0 ? (byte)0x05 : (byte)0xFA;
            for (var channel = 0; channel < channels; channel++)
            {
                var offset = channel * blockSize + pair * 2;
                output[destination++] = Convert(source[offset], reverse);
                output[destination++] = Convert(source[offset + 1], reverse);
                output[destination++] = marker;
            }
        }
        return output;
    }

    private static byte[] InterleavedToDop(
        byte[] source,
        int channels,
        bool reverse)
    {
        var pairs = source.Length / (channels * 2);
        var output = new byte[pairs * channels * 3];
        var destination = 0;
        for (var pair = 0; pair < pairs; pair++)
        {
            var marker = pair % 2 == 0 ? (byte)0x05 : (byte)0xFA;
            var first = pair * channels * 2;
            var second = first + channels;
            for (var channel = 0; channel < channels; channel++)
            {
                output[destination++] = Convert(
                    source[first + channel],
                    reverse);
                output[destination++] = Convert(
                    source[second + channel],
                    reverse);
                output[destination++] = marker;
            }
        }
        return output;
    }

    private static byte Convert(byte value, bool reverse) =>
        reverse ? Reverse(value) : value;

    private static byte Reverse(byte value)
    {
        value = (byte)(((value & 0xF0) >> 4)
                       | ((value & 0x0F) << 4));
        value = (byte)(((value & 0xCC) >> 2)
                       | ((value & 0x33) << 2));
        return (byte)(((value & 0xAA) >> 1)
                      | ((value & 0x55) << 1));
    }

    private static string WriteDsf(
        int dsdSampleRate,
        int channels,
        int blockSize,
        byte[] planarDsd)
    {
        Assert.Equal(channels * blockSize, planarDsd.Length);
        var path = Temporary(".dsf");
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("DSD "));
        writer.Write(28L);
        writer.Write(0L); // patched after writing
        writer.Write(0L);
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(52L);
        writer.Write(1);
        writer.Write(0);
        writer.Write(channels);
        writer.Write(channels);
        writer.Write(dsdSampleRate);
        writer.Write(1);
        writer.Write((long)blockSize * 8);
        writer.Write(blockSize);
        writer.Write(0);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(12L + planarDsd.Length);
        writer.Write(planarDsd);
        writer.Flush();
        var fileSize = file.Length;
        file.Position = 12;
        writer.Write(fileSize);
        return path;
    }

    private static string WriteDff(
        int dsdSampleRate,
        int channels,
        byte[] interleavedDsd)
    {
        using var properties = new MemoryStream();
        WriteId(properties, "SND ");
        WriteChunk(properties, "FS  ", stream =>
            WriteUInt32(stream, checked((uint)dsdSampleRate)));
        WriteChunk(properties, "CHNL", stream =>
        {
            WriteUInt16(stream, checked((ushort)channels));
            for (var channel = 0; channel < channels; channel++)
                WriteId(stream, $"C{channel:000}");
        });
        WriteChunk(properties, "CMPR", stream =>
        {
            WriteId(stream, "DSD ");
            stream.WriteByte(0);
        });

        using var form = new MemoryStream();
        WriteId(form, "DSD ");
        WriteChunk(form, "PROP", stream =>
        {
            properties.Position = 0;
            properties.CopyTo(stream);
        });
        WriteChunk(form, "DSD ", stream =>
            stream.Write(interleavedDsd));

        var path = Temporary(".dff");
        using var file = File.Create(path);
        WriteId(file, "FRM8");
        WriteUInt64(file, checked((ulong)form.Length));
        form.Position = 0;
        form.CopyTo(file);
        return path;
    }

    private static string Temporary(string extension) =>
        Path.Combine(Path.GetTempPath(), Guid.NewGuid() + extension);

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
        if ((data.Length & 1) != 0) destination.WriteByte(0);
    }

    private static void WriteId(Stream stream, string id) =>
        stream.Write(Encoding.ASCII.GetBytes(id));

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
