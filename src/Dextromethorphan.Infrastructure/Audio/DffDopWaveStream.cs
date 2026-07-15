using System.Buffers.Binary;
using System.Text;
using NAudio.Wave;

namespace Dextromethorphan.Infrastructure.Audio;

/// <summary>Streams uncompressed DSDIFF/DFF as DoP 1.1 without PCM conversion.</summary>
public sealed class DffDopWaveStream : WaveStream
{
    private readonly FileStream _stream;
    private readonly long _dataOffset;
    private readonly long _dataLength;
    private readonly int _channels;
    private byte[] _input = new byte[32 * 1024];
    private byte[] _output = new byte[48 * 1024];
    private int _outputOffset;
    private int _outputCount;
    private long _position;
    private long _nextFrame;

    public DffDopWaveStream(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        Require(_stream, "FRM8");
        _ = ReadInt64(_stream);
        Require(_stream, "DSD ");
        var sampleRate = 0;
        var channels = 0;
        var compression = "";
        long dataOffset = 0;
        long dataLength = 0;
        while (_stream.Position + 12 <= _stream.Length)
        {
            var id = ReadId(_stream);
            var size = ReadInt64(_stream);
            if (size < 0 || size > _stream.Length - _stream.Position) throw Invalid("Invalid DSDIFF chunk size.");
            var end = _stream.Position + size;
            if (id == "PROP")
            {
                Require(_stream, "SND ");
                while (_stream.Position + 12 <= end)
                {
                    var property = ReadId(_stream);
                    var propertySize = ReadInt64(_stream);
                    var propertyEnd = _stream.Position + propertySize;
                    if (propertyEnd > end) throw Invalid("Invalid DSDIFF sound property.");
                    switch (property)
                    {
                        case "FS  ": sampleRate = checked((int)ReadUInt32(_stream)); break;
                        case "CHNL": channels = ReadUInt16(_stream); break;
                        case "CMPR": compression = ReadId(_stream); break;
                    }
                    _stream.Position = propertyEnd + (propertySize & 1);
                }
            }
            else if (id == "DSD ")
            {
                dataOffset = _stream.Position;
                dataLength = size;
            }
            else if (id == "DST ") compression = "DST ";
            _stream.Position = end + (size & 1);
        }
        if (sampleRate <= 0 || sampleRate % 16 != 0 || channels is < 1 or > 8 || dataOffset == 0 || dataLength <= 0)
            throw Invalid("The DSDIFF stream is missing valid sample-rate, channel, or audio data chunks.");
        if (compression == "DST ") throw Invalid("DST-compressed DSDIFF is not supported for DoP streaming.");
        if (!string.IsNullOrEmpty(compression) && compression != "DSD ") throw Invalid($"Unsupported DSDIFF compression '{compression}'.");
        _channels = channels;
        _dataOffset = dataOffset;
        _dataLength = dataLength;
        WaveFormat = new WaveFormat(sampleRate / 16, 24, channels);
        var frames = dataLength / (2 * channels);
        Length = frames * WaveFormat.BlockAlign;
    }

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
        var written = 0;
        while (written < wanted)
        {
            if (_outputOffset >= _outputCount && !LoadFrames()) break;
            var copy = Math.Min(wanted - written, _outputCount - _outputOffset);
            Array.Copy(_output, _outputOffset, buffer, offset + written, copy);
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
        var inputBytes = frames * _channels * 2;
        var outputBytes = frames * WaveFormat.BlockAlign;
        if (_input.Length < inputBytes) _input = new byte[inputBytes];
        if (_output.Length < outputBytes) _output = new byte[outputBytes];
        _stream.Position = _dataOffset + (_nextFrame * _channels * 2);
        var read = 0;
        while (read < inputBytes)
        {
            var part = _stream.Read(_input, read, inputBytes - read);
            if (part == 0) break;
            read += part;
        }
        var completeFrames = read / (_channels * 2);
        var destination = 0;
        for (var frame = 0; frame < completeFrames; frame++)
        {
            var marker = (_nextFrame + frame) % 2 == 0 ? (byte)0x05 : (byte)0xFA;
            var firstRound = frame * _channels * 2;
            var secondRound = firstRound + _channels;
            for (var channel = 0; channel < _channels; channel++)
            {
                _output[destination++] = Reverse(_input[firstRound + channel]);
                _output[destination++] = Reverse(_input[secondRound + channel]);
                _output[destination++] = marker;
            }
        }
        _nextFrame += completeFrames;
        _outputOffset = 0;
        _outputCount = destination;
        return destination > 0;
    }

    private static ushort ReadUInt16(Stream stream) { Span<byte> b = stackalloc byte[2]; stream.ReadExactly(b); return BinaryPrimitives.ReadUInt16BigEndian(b); }
    private static uint ReadUInt32(Stream stream) { Span<byte> b = stackalloc byte[4]; stream.ReadExactly(b); return BinaryPrimitives.ReadUInt32BigEndian(b); }
    private static long ReadInt64(Stream stream) { Span<byte> b = stackalloc byte[8]; stream.ReadExactly(b); return checked((long)BinaryPrimitives.ReadUInt64BigEndian(b)); }
    private static string ReadId(Stream stream) { Span<byte> b = stackalloc byte[4]; stream.ReadExactly(b); return Encoding.ASCII.GetString(b); }
    private static void Require(Stream stream, string expected) { var actual = ReadId(stream); if (actual != expected) throw Invalid($"Expected DSDIFF chunk '{expected}', found '{actual}'."); }
    private static byte Reverse(byte value) { value = (byte)(((value & 0xF0) >> 4) | ((value & 0x0F) << 4)); value = (byte)(((value & 0xCC) >> 2) | ((value & 0x33) << 2)); return (byte)(((value & 0xAA) >> 1) | ((value & 0x55) << 1)); }
    private static InvalidDataException Invalid(string message) => new(message);
    protected override void Dispose(bool disposing) { if (disposing) _stream.Dispose(); base.Dispose(disposing); }
}
