using NAudio.Wave;

namespace Dextromethorphan.Infrastructure.Audio.Dsp;

/// <summary>Joins tracks without silence and optionally performs an equal-power crossfade.</summary>
public sealed class TransitionSampleProvider : ISampleProvider, IDisposable
{
    private readonly object _sync = new();
    private Source _current;
    private Source? _next;
    private long _crossfadeSamples;
    private long _crossfadeConsumed;
    private bool _completedRaised;
    private float[] _currentBuffer = [];
    private float[] _nextBuffer = [];

    public TransitionSampleProvider(ISampleProvider initial, long totalSamples, double crossfadeSeconds = 0, IDisposable? owner = null, long initialPositionSamples = 0)
    {
        ArgumentNullException.ThrowIfNull(initial);
        var alignedTotal = Align(totalSamples, initial.WaveFormat.Channels);
        var alignedPosition = Align(Math.Clamp(initialPositionSamples, 0, alignedTotal), initial.WaveFormat.Channels);
        _current = new Source(initial, alignedTotal, owner) { SamplesRead = alignedPosition };
        WaveFormat = initial.WaveFormat;
        CrossfadeSeconds = crossfadeSeconds;
    }

    public WaveFormat WaveFormat { get; }
    public event EventHandler? SourceChanged;
    public event EventHandler? Completed;
    public long PositionSamples { get { lock (_sync) return _current.SamplesRead; } }
    public long TotalSamples { get { lock (_sync) return _current.TotalSamples; } }

    public double CrossfadeSeconds
    {
        get => _crossfadeSamples / (double)(WaveFormat.SampleRate * WaveFormat.Channels);
        set
        {
            lock (_sync)
            {
                var requested = Math.Clamp(value, 0, 10) * WaveFormat.SampleRate * WaveFormat.Channels;
                _crossfadeSamples = Align((long)requested, WaveFormat.Channels);
            }
        }
    }

    public void QueueNext(ISampleProvider? provider, long totalSamples = 0, IDisposable? owner = null)
    {
        lock (_sync)
        {
            if (provider is not null && !FormatsMatch(provider.WaveFormat, WaveFormat))
                throw new ArgumentException("The next source must be normalized to the pipeline format.", nameof(provider));
            Retire(_next?.Owner);
            _next = provider is null ? null : new Source(provider, Align(totalSamples, WaveFormat.Channels), owner);
            _crossfadeConsumed = 0;
            _completedRaised = false;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            var channels = WaveFormat.Channels;
            count = Align(count, channels);
            var written = 0;
            while (written < count)
            {
                if (_next is not null && _crossfadeSamples > 0 && Remaining(_current) <= _crossfadeSamples)
                {
                    var mixed = ReadCrossfade(buffer, offset + written, count - written);
                    written += mixed;
                    if (mixed > 0) continue;
                }

                var beforeFade = _next is null || _crossfadeSamples == 0
                    ? count - written
                    : (int)Math.Min(count - written, Math.Max(0, Remaining(_current) - _crossfadeSamples));
                beforeFade = Align(beforeFade, channels);
                if (beforeFade > 0)
                {
                    var read = _current.Provider.Read(buffer, offset + written, beforeFade);
                    _current.SamplesRead += read;
                    written += read;
                    if (read > 0) continue;
                }

                if (_next is not null)
                {
                    SwitchToNext();
                    continue;
                }

                var tail = _current.Provider.Read(buffer, offset + written, count - written);
                _current.SamplesRead += tail;
                written += tail;
                if (tail == 0) RaiseCompleted();
                break;
            }
            return written;
        }
    }

    private int ReadCrossfade(float[] destination, int offset, int count)
    {
        if (_next is null) return 0;
        var channels = WaveFormat.Channels;
        var remainingFade = _crossfadeSamples - _crossfadeConsumed;
        var requested = Align((int)Math.Min(count, remainingFade), channels);
        if (requested <= 0) { SwitchToNext(); return 0; }
        EnsureBuffer(ref _currentBuffer, requested);
        EnsureBuffer(ref _nextBuffer, requested);
        var currentRead = _current.Provider.Read(_currentBuffer, 0, requested);
        var nextRead = _next.Provider.Read(_nextBuffer, 0, requested);
        _current.SamplesRead += currentRead;
        _next.SamplesRead += nextRead;
        var produced = Math.Max(currentRead, nextRead);
        for (var sample = 0; sample < produced; sample++)
        {
            var frameProgress = (_crossfadeConsumed + sample - (sample % channels)) / (double)Math.Max(channels, _crossfadeSamples);
            var angle = Math.Clamp(frameProgress, 0, 1) * Math.PI / 2;
            var outgoing = sample < currentRead ? _currentBuffer[sample] : 0;
            var incoming = sample < nextRead ? _nextBuffer[sample] : 0;
            destination[offset + sample] = (float)((outgoing * Math.Cos(angle)) + (incoming * Math.Sin(angle)));
        }
        _crossfadeConsumed += produced;
        if (_crossfadeConsumed >= _crossfadeSamples || currentRead == 0) SwitchToNext();
        return produced;
    }

    private void SwitchToNext()
    {
        if (_next is null) return;
        var retired = _current.Owner;
        _current = _next;
        _next = null;
        _crossfadeConsumed = 0;
        _completedRaised = false;
        Retire(retired);
        SourceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseCompleted()
    {
        if (_completedRaised) return;
        _completedRaised = true;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private static long Remaining(Source source) => Math.Max(0, source.TotalSamples - source.SamplesRead);
    private static int Align(int value, int channels) => value - (value % channels);
    private static long Align(long value, int channels) => value - (value % channels);
    private static bool FormatsMatch(WaveFormat a, WaveFormat b) => a.SampleRate == b.SampleRate && a.Channels == b.Channels && a.Encoding == b.Encoding;
    private static void EnsureBuffer(ref float[] buffer, int required) { if (buffer.Length < required) buffer = new float[required]; }
    private static void Retire(IDisposable? disposable) { if (disposable is not null) ThreadPool.QueueUserWorkItem(_ => disposable.Dispose()); }

    public void Dispose()
    {
        lock (_sync)
        {
            _current.Owner?.Dispose();
            _next?.Owner?.Dispose();
            _next = null;
        }
    }

    private sealed class Source(ISampleProvider provider, long totalSamples, IDisposable? owner)
    {
        public ISampleProvider Provider { get; } = provider;
        public long TotalSamples { get; } = totalSamples;
        public IDisposable? Owner { get; } = owner;
        public long SamplesRead { get; set; }
    }
}
