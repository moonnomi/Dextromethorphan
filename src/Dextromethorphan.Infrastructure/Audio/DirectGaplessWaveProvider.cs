using NAudio.Wave;

namespace Dextromethorphan.Infrastructure.Audio;

/// <summary>Byte-exact concatenation for adjacent tracks with identical decoded PCM formats.</summary>
internal sealed class DirectGaplessWaveProvider : IWaveProvider, IDisposable
{
    private readonly object _sync = new();
    private Source _current;
    private Source? _next;
    private bool _completedRaised;

    public DirectGaplessWaveProvider(WaveStream initial, IDisposable owner)
    {
        _current = new Source(initial, owner);
        WaveFormat = initial.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }
    public TimeSpan Position { get { lock (_sync) return _current.Stream.CurrentTime; } }
    public TimeSpan Duration { get { lock (_sync) return _current.Stream.TotalTime; } }
    public event EventHandler? SourceChanged;
    public event EventHandler? Completed;

    public bool CanQueue(WaveFormat format) => SameFormat(WaveFormat, format);

    public void QueueNext(WaveStream? stream, IDisposable? owner = null)
    {
        lock (_sync)
        {
            if (stream is not null && !CanQueue(stream.WaveFormat)) throw new ArgumentException("Direct gapless playback requires identical PCM formats.", nameof(stream));
            Retire(_next?.Owner);
            _next = stream is null ? null : new Source(stream, owner ?? stream);
            _completedRaised = false;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            var written = 0;
            while (written < count)
            {
                var read = _current.Stream.Read(buffer, offset + written, count - written);
                written += read;
                if (read > 0) continue;
                if (_next is null) { RaiseCompleted(); break; }
                var retired = _current.Owner;
                _current = _next;
                _next = null;
                Retire(retired);
                SourceChanged?.Invoke(this, EventArgs.Empty);
            }
            return written;
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_sync) _current.Stream.CurrentTime = position < TimeSpan.Zero ? TimeSpan.Zero : position > _current.Stream.TotalTime ? _current.Stream.TotalTime : position;
    }

    private void RaiseCompleted()
    {
        if (_completedRaised) return;
        _completedRaised = true;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private static bool SameFormat(WaveFormat a, WaveFormat b) =>
        a.SampleRate == b.SampleRate && a.BitsPerSample == b.BitsPerSample && a.Channels == b.Channels && a.Encoding == b.Encoding && a.BlockAlign == b.BlockAlign;
    private static void Retire(IDisposable? owner) { if (owner is not null) ThreadPool.QueueUserWorkItem(_ => owner.Dispose()); }

    public void Dispose()
    {
        lock (_sync) { _current.Owner.Dispose(); _next?.Owner.Dispose(); _next = null; }
    }

    private sealed record Source(WaveStream Stream, IDisposable Owner);
}
