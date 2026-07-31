using NAudio.Wave;

namespace Dextromethorphan.Infrastructure.Audio;

internal sealed class SegmentWaveStream : WaveStream
{
    private readonly WaveStream _source;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public SegmentWaveStream(
        WaveStream source,
        TimeSpan start,
        TimeSpan? end)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(start, TimeSpan.Zero);
        _source = source;
        _start = ToAlignedBytes(start);
        var requestedEnd = end is { } value
            ? ToAlignedBytes(value)
            : source.Length;
        var boundedEnd = Math.Clamp(
            requestedEnd,
            _start,
            source.Length);
        _length = boundedEnd - _start;
        Position = 0;
    }

    public override WaveFormat WaveFormat => _source.WaveFormat;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            var aligned = Math.Clamp(value, 0, _length);
            aligned -= aligned % WaveFormat.BlockAlign;
            _source.Position = _start + aligned;
            _position = aligned;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - _position;
        if (remaining <= 0) return 0;
        var requested = (int)Math.Min(count, remaining);
        requested -= requested % WaveFormat.BlockAlign;
        if (requested <= 0) return 0;
        var read = _source.Read(buffer, offset, requested);
        _position += read;
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _source.Dispose();
        base.Dispose(disposing);
    }

    private long ToAlignedBytes(TimeSpan time)
    {
        var bytes = (long)Math.Round(
            time.TotalSeconds * WaveFormat.AverageBytesPerSecond,
            MidpointRounding.AwayFromZero);
        return bytes - bytes % WaveFormat.BlockAlign;
    }
}
