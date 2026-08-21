using Dextromethorphan.Core.Models;
using System.Buffers.Binary;
using NAudio.Flac;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Vorbis;

namespace Dextromethorphan.Infrastructure.Audio;

internal static class AudioDecoderFactory
{
    public static DecodedAudio Open(Track track)
    {
        var path = track.EffectiveMediaPath;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        WaveStream reader = extension switch
        {
            ".wav" or ".wave" => new WaveFileReader(path),
            ".aif" or ".aiff" => new AiffFileReader(path),
            ".mp3" => new Mp3FileReader(path),
            ".flac" => OpenFlac(path),
            ".ogg" => new VorbisWaveReader(path),
            ".opus" => new OpusWaveStream(path),
            ".dsf" => new DsfDopWaveStream(path),
            ".dff" => new DffDopWaveStream(path),
            _ => new MediaFoundationReader(path)
        };
        var decoder = reader switch
        {
            DffDopWaveStream { IsDstCompressed: true } =>
                "Apache DST decoder → native DSD → DoP 1.1",
            DffDopWaveStream => "Native DSDIFF → DoP 1.1",
            DsfDopWaveStream => "Native DSF → DoP 1.1",
            _ => extension switch
            {
                ".flac" => "Managed FLAC decoder",
                ".ogg" => "NVorbis managed decoder",
                ".opus" => "Managed Concentus Opus decoder",
                ".wav" or ".wave" or ".aif" or ".aiff" or ".mp3" =>
                    "NAudio native",
                _ => "Windows Media Foundation"
            }
        };
        if (track.IsCueTrack)
        {
            reader = new SegmentWaveStream(
                reader,
                track.SegmentStart,
                track.SegmentEnd);
            decoder += " · CUE segment";
        }
        var waveSubFormat = extension is ".wav" or ".wave"
            ? TryReadWaveSubFormat(path)
            : null;
        return new DecodedAudio(
            track,
            reader,
            decoder,
            waveSubFormat);
    }

    private static WaveStream OpenFlac(string path)
    {
        var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        Stream? logical = null;
        try
        {
            var offset = FindFlacOffset(file);
            logical = offset == 0
                ? file
                : new OffsetReadStream(file, offset, file.Length - offset);
            var reader = new FlacReader(logical);
            // FlacReader owns the stream after construction. Do not dispose it
            // here; OffsetReadStream will close the underlying file with it.
            return reader;
        }
        catch
        {
            logical?.Dispose();
            if (!ReferenceEquals(logical, file)) file.Dispose();
            throw;
        }
    }

    private static long FindFlacOffset(Stream stream)
    {
        stream.Position = 0;
        Span<byte> marker = stackalloc byte[4];
        if (stream.Read(marker) == marker.Length && IsFlacMarker(marker))
        {
            stream.Position = 0;
            return 0;
        }

        stream.Position = 0;
        Span<byte> id3 = stackalloc byte[10];
        if (stream.Read(id3) == id3.Length
            && id3[0] == (byte)'I'
            && id3[1] == (byte)'D'
            && id3[2] == (byte)'3')
        {
            var tagSize = ReadSyncSafeInt(id3[6..10]);
            var footerSize = (id3[5] & 0x10) != 0 ? 10 : 0;
            var offset = 10L + tagSize + footerSize;
            if (offset <= stream.Length - marker.Length)
            {
                stream.Position = offset;
                if (stream.Read(marker) == marker.Length && IsFlacMarker(marker))
                    return offset;
            }
        }

        throw new InvalidDataException(
            "The file has a .flac extension but no FLAC stream marker was found.");
    }

    private static int ReadSyncSafeInt(ReadOnlySpan<byte> value) =>
        (value[0] << 21)
        | (value[1] << 14)
        | (value[2] << 7)
        | value[3];

    private static bool IsFlacMarker(ReadOnlySpan<byte> value) =>
        value.Length >= 4
        && value[0] == (byte)'f'
        && value[1] == (byte)'L'
        && value[2] == (byte)'a'
        && value[3] == (byte)'C';

    public static ISampleProvider Normalize(DecodedAudio decoded, WaveFormat target)
    {
        ISampleProvider provider = new PcmWaveToSampleProvider(
            decoded.Reader,
            decoded.WaveSubFormat);
        if (provider.WaveFormat.Channels != target.Channels)
        {
            provider = (provider.WaveFormat.Channels, target.Channels) switch
            {
                (1, 2) => new MonoToStereoSampleProvider(provider),
                (2, 1) => new StereoToMonoSampleProvider(provider),
                (> 2, 2) => new MultichannelToStereoSampleProvider(provider),
                _ => throw new NotSupportedException($"Channel conversion {provider.WaveFormat.Channels} → {target.Channels} is not supported.")
            };
        }
        if (provider.WaveFormat.SampleRate != target.SampleRate) provider = new WdlResamplingSampleProvider(provider, target.SampleRate);
        return provider;
    }

    public static long TotalSamples(DecodedAudio decoded, WaveFormat format) =>
        (long)Math.Ceiling(decoded.Reader.TotalTime.TotalSeconds * format.SampleRate * format.Channels);

    private static Guid? TryReadWaveSubFormat(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Span<byte> riffHeader = stackalloc byte[12];
            if (stream.Read(riffHeader) != riffHeader.Length
                || BinaryPrimitives.ReadUInt32LittleEndian(
                    riffHeader[8..]) != 0x4556_4157) // WAVE
                return null;

            Span<byte> chunkHeader = stackalloc byte[8];
            while (stream.Position + chunkHeader.Length <= stream.Length)
            {
                if (stream.Read(chunkHeader) != chunkHeader.Length)
                    return null;
                var chunkId = BinaryPrimitives.ReadUInt32LittleEndian(
                    chunkHeader);
                var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(
                    chunkHeader[4..]);
                if (chunkId == 0x2074_6d66) // fmt chunk
                {
                    if (chunkSize < 40) return null;
                    Span<byte> format = stackalloc byte[40];
                    if (stream.Read(format) != format.Length)
                        return null;
                    var tag = BinaryPrimitives.ReadUInt16LittleEndian(
                        format);
                    return tag == (ushort)WaveFormatEncoding.Extensible
                        ? new Guid(format[24..40])
                        : null;
                }

                var paddedSize = (long)chunkSize + (chunkSize & 1);
                if (paddedSize > stream.Length - stream.Position)
                    return null;
                stream.Position += paddedSize;
            }
        }
        catch (IOException)
        {
            // The decoder remains authoritative if a concurrently changing
            // file cannot be inspected for its optional extensible subtype.
        }
        catch (UnauthorizedAccessException)
        {
        }
        return null;
    }
}

/// <summary>
/// Presents a seekable segment of a file as a zero-based stream. This keeps
/// leading ID3 metadata out of NAudio.Flac without touching the user's file.
/// </summary>
internal sealed class OffsetReadStream(
    Stream inner,
    long origin,
    long length) : Stream
{
    private long _position;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
            throw new ArgumentException("The buffer range is invalid.", nameof(offset));
        if (_position >= length || count == 0) return 0;
        inner.Position = origin + _position;
        var requested = (int)Math.Min(count, length - _position);
        var read = inner.Read(buffer, offset, requested);
        _position += read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        if (_position >= length || buffer.Length == 0) return 0;
        inner.Position = origin + _position;
        var requested = buffer[..(int)Math.Min(buffer.Length, length - _position)];
        var read = inner.Read(requested);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin originKind)
    {
        var target = originKind switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(originKind))
        };
        if (target < 0) throw new IOException("Cannot seek before the start of the FLAC stream.");
        _position = target;
        return _position;
    }

    public override void Flush() => inner.Flush();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class MultichannelToStereoSampleProvider(
    ISampleProvider source) : ISampleProvider
{
    private readonly int _sourceChannels = source.WaveFormat.Channels;
    private float[] _sourceBuffer = [];

    public WaveFormat WaveFormat { get; } =
        WaveFormat.CreateIeeeFloatWaveFormat(
            source.WaveFormat.SampleRate,
            2);

    public int Read(float[] buffer, int offset, int count)
    {
        var framesRequested = count / 2;
        var sourceSamples = framesRequested * _sourceChannels;
        if (_sourceBuffer.Length < sourceSamples)
            _sourceBuffer = new float[sourceSamples];
        var sourceRead = source.Read(
            _sourceBuffer,
            0,
            sourceSamples);
        var framesRead = sourceRead / _sourceChannels;
        for (var frame = 0; frame < framesRead; frame++)
        {
            var input = frame * _sourceChannels;
            var left = _sourceBuffer[input];
            var right = _sourceBuffer[input + 1];
            var weight = 1d;
            if (_sourceChannels > 2)
            {
                var center = _sourceBuffer[input + 2] * 0.70710678f;
                left += center;
                right += center;
                weight += 0.70710678;
            }
            if (_sourceChannels > 3)
            {
                var lfe = _sourceBuffer[input + 3] * 0.5f;
                left += lfe;
                right += lfe;
                weight += 0.5;
            }
            for (var channel = 4; channel < _sourceChannels; channel++)
            {
                var sample = _sourceBuffer[input + channel] * 0.70710678f;
                if ((channel & 1) == 0) left += sample;
                else right += sample;
                weight += 0.35355339;
            }
            buffer[offset + frame * 2] = (float)(left / weight);
            buffer[offset + frame * 2 + 1] = (float)(right / weight);
        }
        return framesRead * 2;
    }
}

internal sealed class DecodedAudio(
    Track track,
    WaveStream reader,
    string decoder,
    Guid? waveSubFormat = null) : IDisposable
{
    public Track Track { get; } = track;
    public WaveStream Reader { get; } = reader;
    public string Decoder { get; } = decoder;
    public Guid? WaveSubFormat { get; } = waveSubFormat;
    public void Dispose() => Reader.Dispose();
}
