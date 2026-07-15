using System.Text;
using NAudio.Wave;

namespace Dextromethorphan.Infrastructure.Audio;

/// <summary>Streams uncompressed DSF as DoP 1.1 without converting the DSD payload to PCM.</summary>
public sealed class DsfDopWaveStream : WaveStream
{
    private readonly FileStream _stream;
    private readonly long _dataOffset;
    private readonly int _channels;
    private readonly int _blockSize;
    private readonly long _bytesPerChannel;
    private readonly bool _reverseBits;
    private readonly byte[] _dsdBlock;
    private byte[] _dopBlock;
    private int _dopOffset;
    private int _dopCount;
    private long _position;
    private long _nextBlock;
    private int _seekPairOffset;

    public DsfDopWaveStream(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        using var reader = new BinaryReader(_stream, Encoding.ASCII, true);
        Require(reader, "DSD ");
        var dsdChunkSize = reader.ReadInt64();
        if (dsdChunkSize < 28) throw Invalid("Invalid DSD header size.");
        _ = reader.ReadInt64(); // file size
        _ = reader.ReadInt64(); // metadata offset
        Require(reader, "fmt ");
        var formatChunkSize = reader.ReadInt64();
        if (formatChunkSize < 52) throw Invalid("Invalid DSF format chunk.");
        _ = reader.ReadInt32(); // format version
        var formatId = reader.ReadInt32();
        if (formatId != 0) throw Invalid("DST-compressed DSF is not supported for DoP streaming.");
        _ = reader.ReadInt32(); // channel type
        _channels = reader.ReadInt32();
        var dsdSampleRate = reader.ReadInt32();
        var bitsPerSample = reader.ReadInt32();
        var sampleCount = reader.ReadInt64();
        _blockSize = reader.ReadInt32();
        _ = reader.ReadInt32(); // reserved
        if (_channels is < 1 or > 8 || dsdSampleRate <= 0 || dsdSampleRate % 16 != 0 || _blockSize <= 0 || sampleCount <= 0)
            throw Invalid("The DSF stream has invalid channel, rate, sample-count, or block-size fields.");
        if (bitsPerSample is not (1 or 8)) throw Invalid("Unsupported DSF bit order.");
        _reverseBits = bitsPerSample == 8;
        if (formatChunkSize > 52) _stream.Position += formatChunkSize - 52;
        Require(reader, "data");
        var dataChunkSize = reader.ReadInt64();
        if (dataChunkSize < 12) throw Invalid("Invalid DSF data chunk.");
        _dataOffset = _stream.Position;
        _bytesPerChannel = (sampleCount + 7) / 8;
        WaveFormat = new WaveFormat(dsdSampleRate / 16, 24, _channels);
        Length = (_bytesPerChannel / 2) * WaveFormat.BlockAlign;
        _dsdBlock = new byte[checked(_blockSize * _channels)];
        _dopBlock = new byte[checked((_blockSize / 2) * WaveFormat.BlockAlign)];
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
            var frame = aligned / WaveFormat.BlockAlign;
            var bytePerChannel = frame * 2;
            _nextBlock = bytePerChannel / _blockSize;
            _seekPairOffset = (int)((bytePerChannel % _blockSize) / 2);
            _dopOffset = _dopCount = 0;
            _position = aligned;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = (int)Math.Min(count, Length - _position);
        var written = 0;
        while (written < remaining)
        {
            if (_dopOffset >= _dopCount && !LoadNextBlock()) break;
            var copy = Math.Min(remaining - written, _dopCount - _dopOffset);
            Array.Copy(_dopBlock, _dopOffset, buffer, offset + written, copy);
            _dopOffset += copy;
            written += copy;
            _position += copy;
        }
        return written;
    }

    private bool LoadNextBlock()
    {
        var channelByteOffset = _nextBlock * _blockSize;
        if (channelByteOffset >= _bytesPerChannel) return false;
        _stream.Position = _dataOffset + (_nextBlock * _blockSize * _channels);
        Array.Clear(_dsdBlock);
        var wanted = _dsdBlock.Length;
        var read = 0;
        while (read < wanted)
        {
            var part = _stream.Read(_dsdBlock, read, wanted - read);
            if (part == 0) break;
            read += part;
        }
        var validPerChannel = (int)Math.Min(_blockSize, _bytesPerChannel - channelByteOffset);
        var pairs = validPerChannel / 2;
        var startPair = Math.Min(_seekPairOffset, pairs);
        var frames = pairs - startPair;
        var needed = frames * WaveFormat.BlockAlign;
        if (_dopBlock.Length < needed) _dopBlock = new byte[needed];
        var destination = 0;
        for (var pair = startPair; pair < pairs; pair++)
        {
            var marker = (pair + (_nextBlock * (_blockSize / 2))) % 2 == 0 ? (byte)0x05 : (byte)0xFA;
            for (var channel = 0; channel < _channels; channel++)
            {
                var source = (channel * _blockSize) + (pair * 2);
                var first = _dsdBlock[source];
                var second = _dsdBlock[source + 1];
                _dopBlock[destination++] = _reverseBits ? Reverse(first) : first;
                _dopBlock[destination++] = _reverseBits ? Reverse(second) : second;
                _dopBlock[destination++] = marker;
            }
        }
        _nextBlock++;
        _seekPairOffset = 0;
        _dopOffset = 0;
        _dopCount = destination;
        return destination > 0;
    }

    private static byte Reverse(byte value)
    {
        value = (byte)(((value & 0xF0) >> 4) | ((value & 0x0F) << 4));
        value = (byte)(((value & 0xCC) >> 2) | ((value & 0x33) << 2));
        return (byte)(((value & 0xAA) >> 1) | ((value & 0x55) << 1));
    }

    private static void Require(BinaryReader reader, string expected)
    {
        var actual = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (actual != expected) throw Invalid($"Expected DSF chunk '{expected}', found '{actual}'.");
    }

    private static InvalidDataException Invalid(string message) => new(message);
    protected override void Dispose(bool disposing) { if (disposing) _stream.Dispose(); base.Dispose(disposing); }
}
