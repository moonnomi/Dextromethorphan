using NAudio.Wave;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Infrastructure.Audio.Dsp;

public sealed record TransitionTrace(string Operation, long PositionSamples, long EndSamples, long NextSamplesRead, double ConfiguredSeconds, double ActualOverlapSeconds);

/// <summary>Joins tracks without silence and optionally performs an equal-power crossfade.</summary>
public sealed class TransitionSampleProvider : ISampleProvider, IDisposable
{
    private readonly object _sync = new();
    private Source _current;
    private Source? _next;
    private long _crossfadeSamples;
    private long _activeCrossfadeSamples;
    private long _crossfadeConsumed;
    private CrossfadeShape _shape = new();
    private CrossfadeShape _activeShape = new();
    public CrossfadeShape Shape
    {
        get { lock (_sync) return _shape; }
        set { lock (_sync) _shape = (value ?? new()).Normalize(); }
    }
    private bool _completedRaised;
    private float[] _currentBuffer = [];
    private float[] _nextBuffer = [];
    private double _outgoingEnergy, _incomingEnergy;
    private long _mixedFrames;
    private bool _readOverlapped;
    private CrossfadeMeasurement _measurement = new(0, 0, 0, false);
    public CrossfadeMeasurement Measurement => Volatile.Read(ref _measurement);

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
    public event EventHandler<TransitionTrace>? Trace;
    public double LastTransitionOverlapSeconds { get; private set; }
    private void EmitTrace(string operation, double overlap = 0) => Trace?.Invoke(this,
        new(operation, _current.SamplesRead, _current.EndSamples, _next?.SamplesRead ?? 0, CrossfadeSeconds, overlap));
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
            if (_activeCrossfadeSamples > 0) EmitTrace("next-replaced-during-overlap", _crossfadeConsumed / (double)(WaveFormat.SampleRate * WaveFormat.Channels));
            Retire(_next?.Owner);
            _next = provider is null ? null : new Source(provider, Align(totalSamples, WaveFormat.Channels), owner);
            _crossfadeConsumed = 0;
            _activeCrossfadeSamples = 0;
            _completedRaised = false;
            _current.EndSamples = _current.TotalSamples;
            EmitTrace(provider is null ? "next-cleared" : "next-ready");
        }
    }

    public bool TrySetPlannedCrossfade(double seconds, double trimTrailingSeconds = 0)
    {
        lock (_sync)
        {
            if (_next is null || _activeCrossfadeSamples > 0) return false;
            if (!double.IsFinite(trimTrailingSeconds) || trimTrailingSeconds < 0 || trimTrailingSeconds > 20) return false;
            var end = _current.TotalSamples - Align((long)(trimTrailingSeconds * WaveFormat.SampleRate * WaveFormat.Channels), WaveFormat.Channels);
            var needed = (Math.Max(CrossfadeSeconds, seconds) + .25) * WaveFormat.SampleRate * WaveFormat.Channels;
            if (end - _current.SamplesRead <= needed) return false;
            _current.EndSamples = end;
            CrossfadeSeconds = seconds;
            return true;
        }
    }

    public void RestoreFullEnding()
    {
        lock (_sync)
            if (_activeCrossfadeSamples == 0) _current.EndSamples = _current.TotalSamples;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            var channels = WaveFormat.Channels;
            count = Align(count, channels);
            var written = 0;
            _outgoingEnergy = _incomingEnergy = 0;
            _readOverlapped = false;
            while (written < count)
            {
                var fadeTarget = CrossfadeTargetSamples();
                if (_next is not null
                    && (_activeCrossfadeSamples > 0 || (fadeTarget > 0
                    && Remaining(_current) <= fadeTarget)))
                {
                    var mixed = ReadCrossfade(buffer, offset + written, count - written);
                    written += mixed;
                    if (mixed > 0) continue;
                }

                var beforeFade = _next is null || _crossfadeSamples == 0
                    ? (int)Math.Min(count - written, Remaining(_current))
                    : (int)Math.Min(
                        count - written,
                        Math.Max(0, Remaining(_current) - fadeTarget));
                beforeFade = Align(beforeFade, channels);
                if (beforeFade > 0)
                {
                    var read = _current.Provider.Read(buffer, offset + written, beforeFade);
                    for (var i = 0; i < read; i++) _outgoingEnergy += (double)buffer[offset + written + i] * buffer[offset + written + i];
                    _current.SamplesRead += read;
                    written += read;
                    if (read > 0) continue;
                }

                if (_next is not null)
                {
                    SwitchToNext();
                    continue;
                }

                var tail = _current.EndSamples < _current.TotalSamples ? 0 : _current.Provider.Read(buffer, offset + written, count - written);
                for (var i = 0; i < tail; i++) _outgoingEnergy += (double)buffer[offset + written + i] * buffer[offset + written + i];
                _current.SamplesRead += tail;
                written += tail;
                if (tail == 0) RaiseCompleted();
                break;
            }
            Volatile.Write(ref _measurement, new(written > 0 ? Math.Sqrt(_outgoingEnergy / written) : 0,
                written > 0 ? Math.Sqrt(_incomingEnergy / written) : 0, _mixedFrames, _readOverlapped));
            return written;
        }
    }

    private int ReadCrossfade(float[] destination, int offset, int count)
    {
        if (_next is null) return 0;
        var channels = WaveFormat.Channels;
        if (_activeCrossfadeSamples == 0)
        {
            _activeShape = _shape;
            _activeCrossfadeSamples = Align(
                Math.Min(
                    CrossfadeTargetSamples(),
                    Math.Min(Remaining(_current), Remaining(_next))),
                channels);
            EmitTrace("overlap-started", _activeCrossfadeSamples / (double)(WaveFormat.SampleRate * channels));
        }
        var remainingFade =
            _activeCrossfadeSamples - _crossfadeConsumed;
        var requested = Align((int)Math.Min(count, remainingFade), channels);
        if (requested <= 0) { SwitchToNext(); return 0; }
        EnsureBuffer(ref _currentBuffer, requested);
        EnsureBuffer(ref _nextBuffer, requested);
        var currentRead = ReadFully(
            _current.Provider,
            _currentBuffer,
            requested);
        var nextRead = ReadFully(
            _next.Provider,
            _nextBuffer,
            requested);
        _current.SamplesRead += currentRead;
        _next.SamplesRead += nextRead;
        var produced = Math.Max(currentRead, nextRead);
        for (var sample = 0; sample < produced; sample++)
        {
            var fadeFrames = _activeCrossfadeSamples / channels;
            var frame = (_crossfadeConsumed + sample) / channels;
            var frameProgress = fadeFrames <= 1
                ? 1
                : frame / (double)(fadeFrames - 1);
            var gains = _activeShape.Gains(frameProgress);
            var outgoing = sample < currentRead ? _currentBuffer[sample] : 0;
            var incoming = sample < nextRead ? _nextBuffer[sample] : 0;
            _outgoingEnergy += Math.Pow(outgoing * gains.Outgoing, 2);
            _incomingEnergy += Math.Pow(incoming * gains.Incoming, 2);
            destination[offset + sample] = (float)((outgoing * gains.Outgoing) + (incoming * gains.Incoming));
        }
        _crossfadeConsumed += produced;
        _readOverlapped |= produced > 0;
        _mixedFrames += Math.Min(currentRead, nextRead) / channels;
        if (_crossfadeConsumed >= _activeCrossfadeSamples
            || currentRead == 0)
            SwitchToNext();
        return produced;
    }

    private void SwitchToNext()
    {
        if (_next is null) return;
        LastTransitionOverlapSeconds = _crossfadeConsumed / (double)(WaveFormat.SampleRate * WaveFormat.Channels);
        EmitTrace(LastTransitionOverlapSeconds > 0 ? "overlap-completed" : "gapless-switch", LastTransitionOverlapSeconds);
        var retired = _current.Owner;
        _current = _next;
        _next = null;
        _crossfadeConsumed = 0;
        _activeCrossfadeSamples = 0;
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

    private static long Remaining(Source source) => Math.Max(0, source.EndSamples - source.SamplesRead);
    private long CrossfadeTargetSamples() => _next is null
        ? 0
        : Align(
            Math.Min(
                _crossfadeSamples,
                Math.Min(_current.TotalSamples, _next.TotalSamples)),
            WaveFormat.Channels);
    private static int Align(int value, int channels) => value - (value % channels);
    private static long Align(long value, int channels) => value - (value % channels);
    private static bool FormatsMatch(WaveFormat a, WaveFormat b) => a.SampleRate == b.SampleRate && a.Channels == b.Channels && a.Encoding == b.Encoding;
    private static void EnsureBuffer(ref float[] buffer, int required) { if (buffer.Length < required) buffer = new float[required]; }
    private static int ReadFully(
        ISampleProvider source,
        float[] buffer,
        int requested)
    {
        var read = 0;
        while (read < requested)
        {
            var current = source.Read(
                buffer,
                read,
                requested - read);
            if (current == 0) break;
            read += current;
        }
        return read;
    }
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
        public long EndSamples { get; set; } = totalSamples;
        public IDisposable? Owner { get; } = owner;
        public long SamplesRead { get; set; }
    }
}
