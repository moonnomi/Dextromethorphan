using System.Buffers.Binary;
using System.Text;
using NAudio.Wave;

namespace Dextromethorphan.Infrastructure.Audio;

/// <summary>
/// Streams uncompressed or DST-compressed DSDIFF/DFF as DoP 1.1 without
/// converting the DSD payload to PCM.
/// </summary>
public sealed class DffDopWaveStream : WaveStream
{
    private readonly FileStream _stream;
    private readonly long _dataOffset;
    private readonly int _channels;
    private readonly DstFrame[] _dstFrames = [];
    private readonly DstNativeDecoder? _dstDecoder;
    private readonly int _dopFramesPerDstFrame;
    private byte[] _input = new byte[32 * 1024];
    private byte[] _decodedDsd = [];
    private byte[] _output = new byte[48 * 1024];
    private int _decodedDstFrame = -1;
    private int _outputOffset;
    private int _outputCount;
    private long _position;
    private long _nextFrame;

    public DffDopWaveStream(string path)
    {
        _stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.RandomAccess);
        try
        {
            Require(_stream, "FRM8");
            var formSize = ReadInt64(_stream);
            if (formSize < 4 || formSize > _stream.Length - 12)
                throw Invalid("Invalid DSDIFF form size.");
            var formEnd = 12 + formSize;
            Require(_stream, "DSD ");

            var sampleRate = 0;
            var channels = 0;
            var compression = "";
            long dataOffset = 0;
            long dataLength = 0;
            uint declaredDstFrames = 0;
            ushort dstFrameRate = 0;
            var dstFrames = new List<DstFrame>();
            var sawDsdData = false;
            var sawDstData = false;

            while (_stream.Position + 12 <= formEnd)
            {
                var id = ReadId(_stream);
                var size = ReadInt64(_stream);
                var end = CheckedChunkEnd(_stream, size, formEnd);
                if (id == "PROP")
                {
                    if (size < 4)
                        throw Invalid("Invalid DSDIFF sound property chunk.");
                    Require(_stream, "SND ");
                    while (_stream.Position + 12 <= end)
                    {
                        var property = ReadId(_stream);
                        var propertySize = ReadInt64(_stream);
                        var propertyEnd = CheckedChunkEnd(
                            _stream,
                            propertySize,
                            end);
                        switch (property)
                        {
                            case "FS  ":
                                if (propertySize < 4)
                                    throw Invalid("Invalid DSDIFF sample-rate property.");
                                sampleRate = checked((int)ReadUInt32(_stream));
                                break;
                            case "CHNL":
                                if (propertySize < 2)
                                    throw Invalid("Invalid DSDIFF channel property.");
                                channels = ReadUInt16(_stream);
                                break;
                            case "CMPR":
                                if (propertySize < 4)
                                    throw Invalid("Invalid DSDIFF compression property.");
                                compression = ReadId(_stream);
                                break;
                        }
                        _stream.Position = PaddedEnd(
                            propertyEnd,
                            propertySize,
                            end);
                    }
                }
                else if (id == "DSD ")
                {
                    if (sawDsdData || sawDstData)
                        throw Invalid("DSDIFF contains duplicate or conflicting sound data chunks.");
                    sawDsdData = true;
                    dataOffset = _stream.Position;
                    dataLength = size;
                }
                else if (id == "DST ")
                {
                    if (sawDsdData || sawDstData)
                        throw Invalid("DSDIFF contains duplicate or conflicting sound data chunks.");
                    sawDstData = true;
                    compression = "DST ";
                    ReadDstSoundData(
                        _stream,
                        end,
                        dstFrames,
                        ref declaredDstFrames,
                        ref dstFrameRate);
                }
                _stream.Position = PaddedEnd(end, size, formEnd);
            }

            if (sampleRate <= 0
                || sampleRate % 16 != 0
                || channels is < 1 or > 8)
                throw Invalid(
                    "The DSDIFF stream is missing valid sample-rate or channel properties.");
            if (!string.IsNullOrEmpty(compression)
                && compression is not ("DSD " or "DST "))
                throw Invalid(
                    $"Unsupported DSDIFF compression '{compression}'.");

            _channels = channels;
            WaveFormat = new WaveFormat(sampleRate / 16, 24, channels);
            if (compression == "DST ")
            {
                if (channels > 6)
                    throw Invalid(
                        "DST-compressed DSDIFF supports at most six channels.");
                if (dstFrameRate != 75)
                    throw Invalid(
                        $"Unsupported DST frame rate {dstFrameRate}; expected 75 frames per second.");
                if (declaredDstFrames == 0
                    || declaredDstFrames != dstFrames.Count)
                    throw Invalid(
                        "The DST frame count does not match its frame information chunk.");

                _dstDecoder = new DstNativeDecoder(channels, sampleRate);
                _decodedDsd = new byte[_dstDecoder.FrameBytes];
                _dstFrames = dstFrames.ToArray();
                if (_decodedDsd.Length % (channels * 2) != 0)
                    throw Invalid("The decoded DST frame is not channel aligned.");
                _dopFramesPerDstFrame = _decodedDsd.Length / (channels * 2);
                Length = checked(
                    (long)_dstFrames.Length
                    * _dopFramesPerDstFrame
                    * WaveFormat.BlockAlign);
                IsDstCompressed = true;
            }
            else
            {
                if (dataOffset == 0 || dataLength <= 0)
                    throw Invalid(
                        "The DSDIFF stream is missing its uncompressed audio data chunk.");
                _dataOffset = dataOffset;
                var frames = dataLength / (2 * channels);
                Length = checked(frames * WaveFormat.BlockAlign);
            }
        }
        catch
        {
            _dstDecoder?.Dispose();
            _stream.Dispose();
            throw;
        }
    }

    public bool IsDstCompressed { get; }
    public override WaveFormat WaveFormat { get; }
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set
        {
            var aligned = Math.Clamp(value, 0, Length);
            aligned -= aligned % WaveFormat.BlockAlign;
            _nextFrame = aligned / WaveFormat.BlockAlign;
            _position = aligned;
            _outputOffset = _outputCount = 0;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var wanted = (int)Math.Min(count, Length - _position);
        wanted -= wanted % WaveFormat.BlockAlign;
        var written = 0;
        while (written < wanted)
        {
            if (_outputOffset >= _outputCount && !LoadFrames()) break;
            var copy = Math.Min(
                wanted - written,
                _outputCount - _outputOffset);
            copy -= copy % WaveFormat.BlockAlign;
            if (copy <= 0) break;
            Array.Copy(
                _output,
                _outputOffset,
                buffer,
                offset + written,
                copy);
            _outputOffset += copy;
            written += copy;
            _position += copy;
        }
        return written;
    }

    private bool LoadFrames()
    {
        var totalFrames = Length / WaveFormat.BlockAlign;
        if (_nextFrame >= totalFrames) return false;
        var frames = (int)Math.Min(4096, totalFrames - _nextFrame);
        byte[] source;
        var sourceOffset = 0;

        if (IsDstCompressed)
        {
            var dstFrameIndex = checked((int)(
                _nextFrame / _dopFramesPerDstFrame));
            var frameOffset = checked((int)(
                _nextFrame % _dopFramesPerDstFrame));
            DecodeDstFrame(dstFrameIndex);
            frames = Math.Min(
                frames,
                _dopFramesPerDstFrame - frameOffset);
            source = _decodedDsd;
            sourceOffset = frameOffset * _channels * 2;
        }
        else
        {
            var inputBytes = frames * _channels * 2;
            if (_input.Length < inputBytes)
                _input = new byte[inputBytes];
            _stream.Position = _dataOffset
                               + (_nextFrame * _channels * 2);
            var read = 0;
            while (read < inputBytes)
            {
                var part = _stream.Read(
                    _input,
                    read,
                    inputBytes - read);
                if (part == 0) break;
                read += part;
            }
            frames = read / (_channels * 2);
            source = _input;
        }

        var outputBytes = frames * WaveFormat.BlockAlign;
        if (_output.Length < outputBytes)
            _output = new byte[outputBytes];
        var destination = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            var marker = (_nextFrame + frame) % 2 == 0
                ? (byte)0x05
                : (byte)0xFA;
            var firstRound = sourceOffset
                             + frame * _channels * 2;
            var secondRound = firstRound + _channels;
            for (var channel = 0; channel < _channels; channel++)
            {
                _output[destination++] = Reverse(
                    source[firstRound + channel]);
                _output[destination++] = Reverse(
                    source[secondRound + channel]);
                _output[destination++] = marker;
            }
        }
        _nextFrame += frames;
        _outputOffset = 0;
        _outputCount = destination;
        return destination > 0;
    }

    private void DecodeDstFrame(int index)
    {
        if (_decodedDstFrame == index) return;
        var frame = _dstFrames[index];
        if (_input.Length < frame.Length)
            _input = new byte[frame.Length];
        _stream.Position = frame.Offset;
        _stream.ReadExactly(_input.AsSpan(0, frame.Length));
        try
        {
            var written = _dstDecoder!.Decode(
                _input,
                frame.Length,
                _decodedDsd);
            if (written != _decodedDsd.Length)
                throw Invalid(
                    $"DST frame {index + 1} decoded {written:N0} of {_decodedDsd.Length:N0} expected bytes.");
            _decodedDstFrame = index;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or ArgumentException)
        {
            throw Invalid(
                $"DST frame {index + 1} could not be decoded: {exception.Message}");
        }
    }

    private static void ReadDstSoundData(
        Stream stream,
        long end,
        ICollection<DstFrame> frames,
        ref uint declaredFrames,
        ref ushort frameRate)
    {
        while (stream.Position + 12 <= end)
        {
            var id = ReadId(stream);
            var size = ReadInt64(stream);
            var chunkEnd = CheckedChunkEnd(stream, size, end);
            switch (id)
            {
                case "FRTE":
                    if (size != 6 || declaredFrames != 0)
                        throw Invalid("Invalid or duplicate DST frame information chunk.");
                    declaredFrames = ReadUInt32(stream);
                    frameRate = ReadUInt16(stream);
                    break;
                case "DSTF":
                    if (size <= 0 || size > 16 * 1024 * 1024)
                        throw Invalid("Invalid DST frame data size.");
                    frames.Add(new DstFrame(
                        stream.Position,
                        checked((int)size)));
                    break;
                case "DSTC":
                    if (size != 4)
                        throw Invalid("Invalid DST frame CRC chunk.");
                    break;
            }
            stream.Position = PaddedEnd(chunkEnd, size, end);
        }
    }

    private static long CheckedChunkEnd(
        Stream stream,
        long size,
        long containerEnd)
    {
        if (size < 0 || size > containerEnd - stream.Position)
            throw Invalid("Invalid DSDIFF chunk size.");
        return stream.Position + size;
    }

    private static long PaddedEnd(
        long end,
        long size,
        long containerEnd)
    {
        var padded = checked(end + (size & 1));
        if (padded > containerEnd)
        {
            if (end == containerEnd) return end;
            throw Invalid("Invalid DSDIFF chunk padding.");
        }
        return padded;
    }

    private static ushort ReadUInt16(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[2];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    private static uint ReadUInt32(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static long ReadInt64(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[8];
        stream.ReadExactly(bytes);
        return checked((long)BinaryPrimitives.ReadUInt64BigEndian(bytes));
    }

    private static string ReadId(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactly(bytes);
        return Encoding.ASCII.GetString(bytes);
    }

    private static void Require(Stream stream, string expected)
    {
        var actual = ReadId(stream);
        if (actual != expected)
            throw Invalid(
                $"Expected DSDIFF chunk '{expected}', found '{actual}'.");
    }

    private static byte Reverse(byte value)
    {
        value = (byte)(((value & 0xF0) >> 4)
                       | ((value & 0x0F) << 4));
        value = (byte)(((value & 0xCC) >> 2)
                       | ((value & 0x33) << 2));
        return (byte)(((value & 0xAA) >> 1)
                      | ((value & 0x55) << 1));
    }

    private static InvalidDataException Invalid(string message) => new(message);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dstDecoder?.Dispose();
            _stream.Dispose();
        }
        base.Dispose(disposing);
    }

    private readonly record struct DstFrame(long Offset, int Length);
}
