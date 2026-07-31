using System.Buffers.Binary;
using NAudio.Wave;

namespace Dextromethorphan.Infrastructure.Audio;

/// <summary>
/// Converts integer PCM and IEEE-float wave streams to the floating-point DSP
/// pipeline. NAudio's generic ToSampleProvider helper rejects valid
/// WAVE_FORMAT_EXTENSIBLE PCM, which is commonly used for 24-bit WAV files.
/// </summary>
internal sealed class PcmWaveToSampleProvider : ISampleProvider
{
    private static readonly Guid PcmSubFormat =
        new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubFormat =
        new("00000003-0000-0010-8000-00aa00389b71");

    private readonly IWaveProvider _source;
    private readonly SampleEncoding _encoding;
    private readonly int _bytesPerSample;
    private byte[] _sourceBuffer = [];

    public PcmWaveToSampleProvider(
        IWaveProvider source,
        Guid? extensibleSubFormat = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        var format = source.WaveFormat;
        _encoding = ResolveEncoding(format, extensibleSubFormat);
        _bytesPerSample = format.BitsPerSample / 8;
        if (_bytesPerSample <= 0)
            throw new NotSupportedException(
                $"Invalid PCM bit depth: {format.BitsPerSample}.");
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            format.SampleRate,
            format.Channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        count -= count % WaveFormat.Channels;
        if (count == 0) return 0;

        var requestedBytes = checked(count * _bytesPerSample);
        if (_sourceBuffer.Length < requestedBytes)
            _sourceBuffer = new byte[requestedBytes];

        var bytesRead = 0;
        while (bytesRead < requestedBytes)
        {
            var current = _source.Read(
                _sourceBuffer,
                bytesRead,
                requestedBytes - bytesRead);
            if (current == 0) break;
            bytesRead += current;
        }

        var samplesRead = bytesRead / _bytesPerSample;
        samplesRead -= samplesRead % WaveFormat.Channels;
        for (var sample = 0; sample < samplesRead; sample++)
        {
            var input = sample * _bytesPerSample;
            buffer[offset + sample] = ReadSample(
                _sourceBuffer.AsSpan(input, _bytesPerSample));
        }
        return samplesRead;
    }

    private float ReadSample(ReadOnlySpan<byte> bytes) => _encoding switch
    {
        SampleEncoding.Pcm8 => (bytes[0] - 128) / 128f,
        SampleEncoding.Pcm16 =>
            BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
        SampleEncoding.Pcm24 => ReadInt24(bytes) / 8_388_608f,
        SampleEncoding.Pcm32 =>
            BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2_147_483_648f,
        SampleEncoding.Float32 => BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(bytes)),
        SampleEncoding.Float64 => (float)BitConverter.Int64BitsToDouble(
            BinaryPrimitives.ReadInt64LittleEndian(bytes)),
        _ => throw new InvalidOperationException()
    };

    private static int ReadInt24(ReadOnlySpan<byte> bytes)
    {
        var value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
        return (value & 0x0080_0000) == 0
            ? value
            : value | unchecked((int)0xff00_0000);
    }

    private static SampleEncoding ResolveEncoding(
        WaveFormat format,
        Guid? extensibleSubFormat)
    {
        var isPcm = format.Encoding == WaveFormatEncoding.Pcm;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        if (format is WaveFormatExtensible extensible)
        {
            isPcm = extensible.SubFormat == PcmSubFormat;
            isFloat = extensible.SubFormat == FloatSubFormat;
        }
        else if (format.Encoding == WaveFormatEncoding.Extensible)
        {
            isPcm = extensibleSubFormat == PcmSubFormat;
            isFloat = extensibleSubFormat == FloatSubFormat;
        }

        if (isPcm)
        {
            return format.BitsPerSample switch
            {
                8 => SampleEncoding.Pcm8,
                16 => SampleEncoding.Pcm16,
                24 => SampleEncoding.Pcm24,
                32 => SampleEncoding.Pcm32,
                _ => throw Unsupported(format)
            };
        }
        if (isFloat)
        {
            return format.BitsPerSample switch
            {
                32 => SampleEncoding.Float32,
                64 => SampleEncoding.Float64,
                _ => throw Unsupported(format)
            };
        }
        throw Unsupported(format);
    }

    private static NotSupportedException Unsupported(WaveFormat format) =>
        new(
            "Unsupported wave sample encoding: " +
            $"{format.Encoding}, {format.BitsPerSample}-bit" +
            (format is WaveFormatExtensible extensible
                ? $", subtype {extensible.SubFormat}"
                : string.Empty) +
            ".");

    private enum SampleEncoding
    {
        Pcm8,
        Pcm16,
        Pcm24,
        Pcm32,
        Float32,
        Float64
    }
}
