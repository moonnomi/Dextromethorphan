using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Core.Playback;
using Dextromethorphan.Infrastructure.Audio.Dsp;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PlaybackState = Dextromethorphan.Core.Models.PlaybackState;

namespace Dextromethorphan.Infrastructure.Audio;

public sealed class WasapiAudioEngine : IAudioEngine
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _positionTimer;
    private WasapiOut? _output;
    private DirectGaplessWaveProvider? _direct;
    private TransitionSampleProvider? _transition;
    private VariableRateSampleProvider? _rate;
    private SmbPitchShiftingSampleProvider? _pitch;
    private FadeEnvelopeSampleProvider? _fade;
    private GainLimiterSampleProvider? _gain;
    private DecodedAudio? _incompatibleNext;
    private Track? _track;
    private Track? _nextTrack;
    private AudioOutputProfile _profile = new();
    private AudioPlaybackOptions _options = new();
    private AudioDiagnostics? _diagnostics;
    private double _volume = 0.82;
    private PlaybackState _state = PlaybackState.Stopped;
    private string? _error;
    private bool _disposed;
    private bool _pipelineCompleted;
    private int _recovering;

    public WasapiAudioEngine() => _positionTimer = new Timer(_ => Publish(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    public PlaybackSnapshot Snapshot => new(_track, _state, Position, Duration, _volume, _error, _diagnostics, _options.Speed, _gain?.Peak ?? 0);
    public AudioDiagnostics? Diagnostics => _diagnostics;
    public event EventHandler<PlaybackSnapshot>? StateChanged;
    public event EventHandler<TrackTransitionedEventArgs>? TrackTransitioned;
    public event EventHandler? PlaybackEnded;

    private TimeSpan Position => _direct?.Position ?? (_transition is null ? TimeSpan.Zero : TimeSpan.FromSeconds(_transition.PositionSamples / (double)(_transition.WaveFormat.SampleRate * _transition.WaveFormat.Channels)));
    private TimeSpan Duration => _direct?.Duration ?? _track?.Duration ?? TimeSpan.Zero;

    public Task<IReadOnlyList<AudioDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(device =>
        {
            using var client = device.AudioClient;
            return new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId, device.State.ToString(), FormatInfo(client.MixFormat).ToString());
        }).ToArray();
        return Task.FromResult<IReadOnlyList<AudioDeviceInfo>>(devices);
    }

    public Task<AudioDeviceCapabilities> GetDeviceCapabilitiesAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        using var device = ResolveDevice(deviceId);
        using var client = device.AudioClient;
        var supported = new List<AudioFormatInfo>();
        foreach (var rate in new[] { 44100, 48000, 88200, 96000, 176400, 192000 })
        foreach (var bits in new[] { 16, 24, 32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var format = new WaveFormat(rate, bits, 2);
            if (client.IsFormatSupported(AudioClientShareMode.Exclusive, format)) supported.Add(FormatInfo(format));
        }
        foreach (var rate in new[] { 44100, 48000, 96000, 192000 })
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
            if (client.IsFormatSupported(AudioClientShareMode.Exclusive, format)) supported.Add(FormatInfo(format));
        }
        return Task.FromResult(new AudioDeviceCapabilities(device.ID, device.FriendlyName, FormatInfo(client.MixFormat), supported, supported.Count > 0));
    }

    public async Task LoadAsync(Track track, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await LoadCoreAsync(track, TimeSpan.Zero, false, cancellationToken); }
        finally { _gate.Release(); Publish(); }
    }

    public async Task QueueNextAsync(Track? track, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _nextTrack = track;
            _incompatibleNext?.Dispose();
            _incompatibleNext = null;
            if (track is null) { _direct?.QueueNext(null); _transition?.QueueNext(null); return; }
            var decoded = AudioDecoderFactory.Open(track);
            if (_direct is not null)
            {
                if (_direct.CanQueue(decoded.Reader.WaveFormat)) _direct.QueueNext(decoded.Reader, decoded);
                else _incompatibleNext = decoded;
            }
            else if (_transition is not null)
            {
                var normalized = AudioDecoderFactory.Normalize(decoded, _transition.WaveFormat);
                _transition.QueueNext(normalized, AudioDecoderFactory.TotalSamples(decoded, _transition.WaveFormat), decoded);
            }
            else decoded.Dispose();
        }
        finally { _gate.Release(); }
    }

    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { if (_output is null) return; _output.Play(); _state = PlaybackState.Playing; _error = null; }
        finally { _gate.Release(); Publish(); }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { _output?.Pause(); _state = _track is null ? PlaybackState.Stopped : PlaybackState.Paused; }
        finally { _gate.Release(); Publish(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _pipelineCompleted = false;
            _output?.Stop();
            _direct?.Seek(TimeSpan.Zero);
            _state = PlaybackState.Stopped;
        }
        finally { _gate.Release(); Publish(); }
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_direct is not null) _direct.Seek(position);
            else if (_track is not null)
            {
                var wasPlaying = _state == PlaybackState.Playing;
                await LoadCoreAsync(_track, position, wasPlaying, cancellationToken);
            }
        }
        finally { _gate.Release(); Publish(); }
    }

    public async Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _volume = Math.Clamp(volume, 0, 1);
            if (_profile.HardwareVolume)
            {
                using var device = ResolveDevice(_profile.DeviceId);
                device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)_volume;
            }
            else if (_gain is not null) UpdateGain();
            else if (_track is not null && _profile.PreferBitPerfect && _volume < 0.999999)
                await RebuildAtCurrentPositionAsync(cancellationToken);
        }
        finally { _gate.Release(); Publish(); }
    }

    public async Task SetPlaybackOptionsAsync(AudioPlaybackOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var requiredModeChanged = _options.RequiresDsp(_profile.HardwareVolume ? 1 : _volume) != options.RequiresDsp(_profile.HardwareVolume ? 1 : _volume);
            _options = options.Copy();
            _options.Speed = Math.Clamp(_options.Speed, 0.5, 1.5);
            _options.PitchSemitones = Math.Clamp(_options.PitchSemitones, -12, 12);
            _options.CrossfadeSeconds = Math.Clamp(_options.CrossfadeSeconds, 0, 10);
            if (_track is not null && (requiredModeChanged || _direct is not null && _options.RequiresDsp(_profile.HardwareVolume ? 1 : _volume))) await RebuildAtCurrentPositionAsync(cancellationToken);
            else UpdateDspParameters();
        }
        finally { _gate.Release(); Publish(); }
    }

    public async Task ConfigureOutputAsync(AudioOutputProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _profile = profile;
            if (profile.CrossfadeSeconds > 0) { _options.TransitionMode = TransitionMode.Crossfade; _options.CrossfadeSeconds = profile.CrossfadeSeconds; }
            if (_track is not null) await RebuildAtCurrentPositionAsync(cancellationToken);
        }
        finally { _gate.Release(); Publish(); }
    }

    private async Task RebuildAtCurrentPositionAsync(CancellationToken cancellationToken)
    {
        if (_track is null) return;
        var position = Position;
        var playing = _state == PlaybackState.Playing;
        var next = _nextTrack;
        await LoadCoreAsync(_track, position, playing, cancellationToken);
        if (next is not null) await QueueNextCoreAsync(next, cancellationToken);
    }

    private async Task LoadCoreAsync(Track track, TimeSpan position, bool startPlaying, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        DisposePlayback();
        _track = track; _nextTrack = null; _state = PlaybackState.Buffering; _error = null; _pipelineCompleted = false;
        Publish();
        var decoded = AudioDecoderFactory.Open(track);
        if (position > TimeSpan.Zero) decoded.Reader.CurrentTime = position > decoded.Reader.TotalTime ? decoded.Reader.TotalTime : position;
        var isDsd = decoded.Reader is DsfDopWaveStream or DffDopWaveStream;
        if (isDsd && _profile.DsdMode != DsdMode.Dop) throw new NotSupportedException("Select DoP in this output device profile to play DSF files.");
        var useDsp = !_profile.PreferBitPerfect || _options.RequiresDsp(_profile.HardwareVolume ? 1 : _volume);
        if (isDsd && useDsp) throw new NotSupportedException("ReplayGain, software volume, crossfade, fades, and speed/pitch are unavailable during DoP. Use hardware volume and gapless direct mode.");
        IWaveProvider waveProvider;
        WaveFormat outputFormat;
        if (!useDsp)
        {
            _direct = new DirectGaplessWaveProvider(decoded.Reader, decoded);
            _direct.SourceChanged += OnPipelineSourceChanged;
            _direct.Completed += OnPipelineCompleted;
            waveProvider = _direct;
            outputFormat = decoded.Reader.WaveFormat;
        }
        else
        {
            var rate = _profile.PreferredSampleRate > 0 ? _profile.PreferredSampleRate : decoded.Reader.WaveFormat.SampleRate;
            var target = WaveFormat.CreateIeeeFloatWaveFormat(rate, decoded.Reader.WaveFormat.Channels);
            var normalized = AudioDecoderFactory.Normalize(decoded, target);
            var initialPositionSamples = (long)Math.Round(decoded.Reader.CurrentTime.TotalSeconds * target.SampleRate * target.Channels);
            _transition = new TransitionSampleProvider(normalized, AudioDecoderFactory.TotalSamples(decoded, target), _options.TransitionMode == TransitionMode.Crossfade ? _options.CrossfadeSeconds : 0, decoded, initialPositionSamples);
            _transition.SourceChanged += OnPipelineSourceChanged;
            _transition.Completed += OnPipelineCompleted;
            _rate = new VariableRateSampleProvider(_transition);
            _pitch = new SmbPitchShiftingSampleProvider(_rate);
            _fade = new FadeEnvelopeSampleProvider(_pitch, () => (Position, Duration));
            _gain = new GainLimiterSampleProvider(_fade) { PreventClipping = _options.PreventClipping };
            UpdateDspParameters();
            waveProvider = _gain.ToWaveProvider();
            outputFormat = waveProvider.WaveFormat;
        }

        var effectiveMode = ResolveEffectiveMode(outputFormat);
        _output = CreateOutput(_profile, effectiveMode);
        _output.PlaybackStopped += OutputOnPlaybackStopped;
        _output.Init(waveProvider);
        // WasapiOut.Volume is backed by the Windows audio endpoint. Setting it to 1
        // here reset the user's Windows volume whenever the pipeline was rebuilt.
        // Dextromethorphan applies normal volume in its own 64-bit/DSP gain stage;
        // endpoint volume is touched only by the explicit HardwareVolume profile.
        _diagnostics = BuildDiagnostics(decoded, outputFormat, useDsp, effectiveMode);
        _state = startPlaying ? PlaybackState.Playing : PlaybackState.Paused;
        if (startPlaying) _output.Play();
        await Task.CompletedTask;
    }

    private async Task QueueNextCoreAsync(Track track, CancellationToken cancellationToken)
    {
        _nextTrack = track;
        var decoded = AudioDecoderFactory.Open(track);
        if (_direct is not null)
        {
            if (_direct.CanQueue(decoded.Reader.WaveFormat)) _direct.QueueNext(decoded.Reader, decoded); else _incompatibleNext = decoded;
        }
        else if (_transition is not null)
            _transition.QueueNext(AudioDecoderFactory.Normalize(decoded, _transition.WaveFormat), AudioDecoderFactory.TotalSamples(decoded, _transition.WaveFormat), decoded);
        else decoded.Dispose();
        await Task.CompletedTask;
    }

    private WasapiMode ResolveEffectiveMode(WaveFormat format)
    {
        if (_profile.Mode == WasapiMode.Shared) return WasapiMode.Shared;
        using var device = ResolveDevice(_profile.DeviceId);
        using var client = device.AudioClient;
        if (client.IsFormatSupported(AudioClientShareMode.Exclusive, format)) return WasapiMode.Exclusive;
        if (_profile.FallbackPolicy == OutputFallbackPolicy.SharedMode) return WasapiMode.Shared;
        throw new NotSupportedException($"{device.FriendlyName} does not accept {FormatInfo(format)} in exclusive mode.");
    }

    private AudioDiagnostics BuildDiagnostics(DecodedAudio decoded, WaveFormat format, bool dsp, WasapiMode effective)
    {
        var direct = !dsp;
        var bitPerfect = direct && effective == WasapiMode.Exclusive && !_profile.HardwareVolume;
        var reason = bitPerfect ? (decoded.Reader is DsfDopWaveStream or DffDopWaveStream ? "Native DSD payload in DoP 1.1 frames, exclusive event-driven WASAPI, no software processing." : "Direct decoded PCM, exclusive event-driven WASAPI, no software processing.")
            : dsp ? DspReason()
            : effective == WasapiMode.Shared ? "The endpoint rejected the exact exclusive format; shared-mode fallback is active." : "Hardware volume is active.";
        return new AudioDiagnostics(dsp ? AudioPipelineMode.Dsp : AudioPipelineMode.Direct, _profile.Mode, effective, FormatInfo(decoded.Reader.WaveFormat), FormatInfo(format), bitPerfect, true, decoded.Decoder, reason);
    }

    private string DspReason()
    {
        var reasons = new List<string>();
        if (!_profile.PreferBitPerfect) reasons.Add("DSP was selected");
        if (!_profile.HardwareVolume && _volume < 0.999999) reasons.Add("software volume");
        if (_options.ReplayGainMode != ReplayGainMode.Off) reasons.Add("ReplayGain");
        if (_options.TransitionMode == TransitionMode.Crossfade) reasons.Add("crossfade");
        if (_options.FadeInSeconds > 0 || _options.FadeOutSeconds > 0) reasons.Add("fade envelope");
        if (Math.Abs(_options.Speed - 1) > 0.0001) reasons.Add("speed processing");
        if (Math.Abs(_options.PitchSemitones) > 0.0001) reasons.Add("pitch processing");
        return string.Join(", ", reasons) + " requires the 32-bit float DSP path.";
    }

    private void UpdateDspParameters()
    {
        if (_transition is not null) _transition.CrossfadeSeconds = _options.TransitionMode == TransitionMode.Crossfade ? _options.CrossfadeSeconds : 0;
        if (_rate is not null) _rate.Rate = _options.Speed;
        if (_pitch is not null)
        {
            var requestedPitch = Math.Pow(2, _options.PitchSemitones / 12d);
            var correction = _options.PreservePitch ? requestedPitch / _options.Speed : requestedPitch;
            _pitch.PitchFactor = (float)Math.Clamp(correction, 0.5, 2);
        }
        if (_gain is not null) { _gain.PreventClipping = _options.PreventClipping; UpdateGain(); }
        if (_fade is not null) { _fade.FadeInSeconds = Math.Clamp(_options.FadeInSeconds, 0, 10); _fade.FadeOutSeconds = Math.Clamp(_options.FadeOutSeconds, 0, 10); }
    }

    private void UpdateGain()
    {
        if (_gain is null || _track is null) return;
        var replayGain = ReplayGainCalculator.LinearGain(_track, _options);
        _gain.Gain = replayGain * (_profile.HardwareVolume ? 1 : _volume);
    }

    private void OnPipelineSourceChanged(object? sender, EventArgs e)
    {
        var previous = _track;
        var current = _nextTrack;
        if (previous is null || current is null) return;
        _track = current;
        _nextTrack = null;
        UpdateGain();
        TrackTransitioned?.Invoke(this, new TrackTransitionedEventArgs(previous, current, _transition?.CrossfadeSeconds > 0));
        Publish();
    }

    private void OnPipelineCompleted(object? sender, EventArgs e) => _pipelineCompleted = true;

    private void OutputOnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            _state = PlaybackState.Faulted;
            _error = FriendlyAudioError(e.Exception);
            if (IsRecoverable(e.Exception)) _ = RecoverAsync();
        }
        else if (_pipelineCompleted)
        {
            if (_incompatibleNext is not null) _ = ContinueWithIncompatibleNextAsync();
            else { _state = PlaybackState.Stopped; PlaybackEnded?.Invoke(this, EventArgs.Empty); }
        }
        else if (_state != PlaybackState.Paused) _state = PlaybackState.Stopped;
        Publish();
    }

    private async Task ContinueWithIncompatibleNextAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_incompatibleNext is null) return;
            var previous = _track;
            var next = _incompatibleNext.Track;
            _incompatibleNext.Dispose(); _incompatibleNext = null;
            await LoadCoreAsync(next, TimeSpan.Zero, true, CancellationToken.None);
            if (previous is not null) TrackTransitioned?.Invoke(this, new TrackTransitionedEventArgs(previous, next, false));
        }
        catch (Exception ex) { _state = PlaybackState.Faulted; _error = FriendlyAudioError(ex); }
        finally { _gate.Release(); Publish(); }
    }

    private async Task RecoverAsync()
    {
        if (Interlocked.Exchange(ref _recovering, 1) == 1) return;
        try
        {
            await Task.Delay(350);
            await _gate.WaitAsync();
            try { if (_track is not null) await LoadCoreAsync(_track, Position, true, CancellationToken.None); }
            finally { _gate.Release(); }
        }
        catch (Exception ex) { _state = PlaybackState.Faulted; _error = "Audio device recovery failed: " + FriendlyAudioError(ex); }
        finally { Interlocked.Exchange(ref _recovering, 0); Publish(); }
    }

    private static bool IsRecoverable(Exception error) => error.HResult is unchecked((int)0x88890004) or unchecked((int)0x8889000A);
    private static string FriendlyAudioError(Exception error) => error.HResult switch
    {
        unchecked((int)0x88890008) => "The output device does not support this exact format in exclusive mode.",
        unchecked((int)0x88890004) => "The output device was disconnected or became unavailable.",
        unchecked((int)0x8889000A) => "The audio service changed the device; reopening the endpoint.",
        unchecked((int)0xC00D36C4) => "Windows could not decode this audio format.",
        _ => error.Message
    };

    private static WasapiOut CreateOutput(AudioOutputProfile profile, WasapiMode mode)
    {
        var device = ResolveDevice(profile.DeviceId);
        return new WasapiOut(device, mode == WasapiMode.Exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared, true, Math.Clamp(profile.BufferMilliseconds, 20, 2000));
    }

    private static MMDevice ResolveDevice(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        return deviceId == "default" ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(deviceId);
    }

    private static AudioFormatInfo FormatInfo(WaveFormat format) => new(format.SampleRate, format.BitsPerSample, format.Channels, format.Encoding.ToString());

    private void DisposePlayback()
    {
        if (_output is not null) _output.PlaybackStopped -= OutputOnPlaybackStopped;
        _output?.Stop(); _output?.Dispose(); _output = null;
        if (_direct is not null) { _direct.SourceChanged -= OnPipelineSourceChanged; _direct.Completed -= OnPipelineCompleted; _direct.Dispose(); _direct = null; }
        if (_transition is not null) { _transition.SourceChanged -= OnPipelineSourceChanged; _transition.Completed -= OnPipelineCompleted; _transition.Dispose(); _transition = null; }
        _incompatibleNext?.Dispose(); _incompatibleNext = null;
        _rate = null; _pitch = null; _fade = null; _gain = null;
    }

    private void Publish() => StateChanged?.Invoke(this, Snapshot);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _positionTimer.DisposeAsync();
        await _gate.WaitAsync();
        try { DisposePlayback(); }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
