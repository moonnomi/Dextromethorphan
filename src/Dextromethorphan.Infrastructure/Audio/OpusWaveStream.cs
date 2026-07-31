using Concentus;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Wave;

namespace Dextromethorphan.Infrastructure.Audio;

/// <summary>
/// Streams Ogg Opus through the managed Concentus decoder. The stream removes
/// the Opus pre-skip and caps output at the final Ogg granule so encoder delay
/// and end padding never leak into gapless playback.
/// </summary>
public sealed class OpusWaveStream : WaveStream
{
    private const int OpusSampleRate = 48_000;
    private const int SeekPreRollFrames = OpusSampleRate * 120 / 1000;
    private const int MaximumHeaderSearchBytes = 1024 * 1024;
    private readonly string _path;
    private readonly int _channels;
    private readonly int _preSkipFrames;
    private FileStream? _input;
    private OpusOggReadStream? _ogg;
    private short[]? _pending;
    private int _pendingSampleOffset;
    private long _position;
    private bool _disposed;

    public OpusWaveStream(string path)
    {
        _path = Path.GetFullPath(path);
        var header = ReadHeader(_path);
        _channels = header.Channels;
        _preSkipFrames = header.PreSkipFrames;
        WaveFormat = new WaveFormat(OpusSampleRate, 16, _channels);
        RestartDecoder();
        var granules = Ogg.GranuleCount;
        if (granules <= 0)
            throw Invalid("The Ogg Opus stream has no audio granules.");
        var audibleFrames = Math.Max(0, granules - _preSkipFrames);
        Length = checked(audibleFrames * WaveFormat.BlockAlign);
        SeekToFrame(0);
    }

    public override WaveFormat WaveFormat { get; }
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set
        {
            ThrowIfDisposed();
            var aligned = Math.Clamp(value, 0, Length);
            aligned -= aligned % WaveFormat.BlockAlign;
            SeekToFrame(aligned / WaveFormat.BlockAlign);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
            throw new ArgumentException("The requested output range exceeds the buffer.");

        var requested = (int)Math.Min(count, Length - _position);
        requested -= requested % WaveFormat.BlockAlign;
        var written = 0;
        while (written < requested)
        {
            if (_pending is null || _pendingSampleOffset >= _pending.Length)
            {
                _pending = DecodeNextPacket();
                _pendingSampleOffset = 0;
                if (_pending is null) break;
            }

            var availableBytes = (_pending.Length - _pendingSampleOffset) * sizeof(short);
            var copy = Math.Min(requested - written, availableBytes);
            Buffer.BlockCopy(
                _pending,
                _pendingSampleOffset * sizeof(short),
                buffer,
                offset + written,
                copy);
            _pendingSampleOffset += copy / sizeof(short);
            written += copy;
            _position += copy;
        }
        return written;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            CloseDecoder();
        }
        base.Dispose(disposing);
    }

    private void SeekToFrame(long audibleFrame)
    {
        var audibleFrames = Length / WaveFormat.BlockAlign;
        audibleFrame = Math.Clamp(audibleFrame, 0, audibleFrames);
        _pending = null;
        _pendingSampleOffset = 0;
        _position = audibleFrame * WaveFormat.BlockAlign;
        if (audibleFrame == audibleFrames) return;

        var rawTarget = checked(audibleFrame + _preSkipFrames);
        RestartDecoder();
        if (rawTarget > _preSkipFrames && TrySeekByGranule(rawTarget)) return;
        RestartDecoder();
        SkipFromStart(rawTarget);
    }

    private bool TrySeekByGranule(long rawTarget)
    {
        var ogg = Ogg;
        try
        {
            var seekAnchor = Math.Max(
                _preSkipFrames,
                rawTarget - SeekPreRollFrames);
            ogg.SeekTo(TimeSpan.FromSeconds(
                seekAnchor / (double)OpusSampleRate));

            // OpusOggReadStream queues one packet before SeekTo. Decode and
            // discard that stale packet so the next queued packet belongs to
            // the newly selected page.
            _ = DecodeNextPacket();
            _pending = null;
            _pendingSampleOffset = 0;

            for (var pageAttempt = 0; pageAttempt < 8 && ogg.HasNextPacket; pageAttempt++)
            {
                var page = ogg.PagePosition;
                var pageEndGranule = ogg.PageGranulePosition;
                var samples = new List<short>();
                do
                {
                    var packet = DecodeNextPacket();
                    if (packet is null) break;
                    samples.AddRange(packet);
                }
                while (ogg.HasNextPacket && ogg.PagePosition == page);

                if (samples.Count == 0 || pageEndGranule < 0) continue;
                var pageFrames = samples.Count / _channels;
                var pageStartGranule = pageEndGranule - pageFrames;
                if (rawTarget < pageStartGranule) return false;
                if (rawTarget > pageEndGranule) continue;
                if (rawTarget == pageEndGranule) return true;

                var frameOffset = checked((int)(rawTarget - pageStartGranule));
                if (frameOffset < 0 || frameOffset > pageFrames) return false;
                _pending = samples.ToArray();
                _pendingSampleOffset = frameOffset * _channels;
                return true;
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or ArgumentOutOfRangeException
                or InvalidDataException)
        {
            // A legal but unusually paged stream can defeat the package's
            // approximate seek index. The exact sequential fallback below is
            // slower, but remains bounded-memory and preserves correctness.
        }
        return false;
    }

    private void SkipFromStart(long rawFrames)
    {
        var remaining = rawFrames;
        while (remaining > 0)
        {
            var packet = DecodeNextPacket()
                ?? throw Invalid("The Opus stream ended before its declared granule position.");
            var frames = packet.Length / _channels;
            if (remaining >= frames)
            {
                remaining -= frames;
                continue;
            }
            _pending = packet;
            _pendingSampleOffset = checked((int)remaining * _channels);
            remaining = 0;
        }
    }

    private short[]? DecodeNextPacket()
    {
        var packet = Ogg.DecodeNextPacket();
        if (packet is null && !string.IsNullOrWhiteSpace(Ogg.LastError))
            throw Invalid(Ogg.LastError);
        return packet;
    }

    private void RestartDecoder()
    {
        CloseDecoder();
        _input = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        var decoder = OpusCodecFactory.CreateDecoder(
            OpusSampleRate,
            _channels);
        _ogg = new OpusOggReadStream(decoder, _input);
        if (!_ogg.HasNextPacket)
            throw Invalid(
                string.IsNullOrWhiteSpace(_ogg.LastError)
                    ? "The Ogg stream contains no decodable Opus packets."
                    : _ogg.LastError);
    }

    private void CloseDecoder()
    {
        try { _ogg?.Close(); }
        finally
        {
            _ogg = null;
            _input?.Dispose();
            _input = null;
        }
    }

    private OpusOggReadStream Ogg =>
        _ogg ?? throw new ObjectDisposedException(nameof(OpusWaveStream));

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(OpusWaveStream));
    }

    private static OpusHeader ReadHeader(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            16 * 1024,
            FileOptions.SequentialScan);
        var count = (int)Math.Min(stream.Length, MaximumHeaderSearchBytes);
        if (count < 19) throw Invalid("The Ogg Opus header is truncated.");
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        ReadOnlySpan<byte> signature = "OpusHead"u8;
        var index = bytes.AsSpan().IndexOf(signature);
        if (index < 0 || index + 19 > bytes.Length)
            throw Invalid("The Ogg stream does not contain an OpusHead packet.");
        var channels = bytes[index + 9];
        if (channels is < 1 or > 2)
            throw new NotSupportedException(
                $"Managed Ogg Opus playback supports mono or stereo; this file declares {channels} channels.");
        var preSkip = bytes[index + 10] | (bytes[index + 11] << 8);
        return new OpusHeader(channels, preSkip);
    }

    private static InvalidDataException Invalid(string message) =>
        new($"Invalid Ogg Opus stream: {message}");

    private readonly record struct OpusHeader(
        int Channels,
        int PreSkipFrames);
}
