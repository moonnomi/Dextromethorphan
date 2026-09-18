using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Core.Playback;
using Dextromethorphan.Infrastructure.Audio.Dsp;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Microsoft.Win32;
using PlaybackState = Dextromethorphan.Core.Models.PlaybackState;

namespace Dextromethorphan.Infrastructure.Audio;

public sealed class WasapiAudioEngine : IAudioEngine
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IApplicationLog? _log;
    private readonly Timer _positionTimer;
    private readonly MMDeviceEnumerator _endpointEnumerator;
    private readonly AudioEndpointNotificationClient _endpointNotifications;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CallbackTimingAccumulator _callbackTiming = new();
    private readonly BoundaryEnvelopeAnalyzer _boundaryAnalyzer = new();
    private CancellationTokenSource? _boundaryCancellation;
    private CancellationToken _boundaryToken;
    private double? _dynamicOverlap;
    private string _dynamicStatus = "Dynamic crossfade: off";
    private WasapiOut? _output;
    private MMDevice? _outputDevice;
    private DirectGaplessWaveProvider? _direct;
    private TransitionSampleProvider? _transition;
    private SoundTouchSampleProvider? _tempo;
    private SoundTouchSampleProvider? _nextTempo;
    private FadeEnvelopeSampleProvider? _fade;
    private GainLimiterSampleProvider? _gain;
    private TimingWaveProvider? _timedProvider;
    private AudioVisualizationTapWaveProvider? _visualizationTap;
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
    private int _recoveryAttempts;
    private bool _resumePlaybackAfterSuspend;
    private volatile bool _visualizationEnabled;
    private long _lastOutputLogTick;

    public WasapiAudioEngine(IApplicationLog? log = null)
    {
        _log = log;
        _endpointEnumerator = new MMDeviceEnumerator();
        _endpointNotifications = new AudioEndpointNotificationClient(
            OnEndpointChanged);
        _endpointEnumerator.RegisterEndpointNotificationCallback(
            _endpointNotifications);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _positionTimer = new Timer(
            _ =>
        {
            if (_state is PlaybackState.Playing or PlaybackState.Buffering)
                Publish();
        },
        null,
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(100));
    }
    public PlaybackSnapshot Snapshot => new(_track, _state, Position, Duration, _volume, _error, Diagnostics, _options.Speed, _gain?.Peak ?? 0);
    public AudioDiagnostics? Diagnostics
    {
        get
        {
            var diagnostics = _diagnostics;
            if (diagnostics is null) return null;
            var timed = _timedProvider;
            return diagnostics with
            {
                Reason = diagnostics.Reason + $" · Transition: {_options.TransitionMode}, configured {_options.CrossfadeSeconds:0.##}s, effective {_transition?.CrossfadeSeconds ?? 0:0.##}s, next {(_nextTrack is null ? "not queued" : "queued")}, last actual overlap {_transition?.LastTransitionOverlapSeconds ?? 0:0.##}s" + (_options.CrossfadeShape.DynamicEnabled || _options.CrossfadeShape.SkipTrailingSilence ? " · " + _dynamicStatus : ""),
                RecoveryAttempts = _recoveryAttempts,
                LastCallbackMilliseconds =
                    timed?.LastReadMilliseconds ?? 0,
                MaximumCallbackMilliseconds =
                    _callbackTiming.MaximumMilliseconds(timed),
                Underruns = _callbackTiming.DeadlineMisses(timed),
                OutputMeasurement = _visualizationTap?.Measurement,
                CrossfadeMeasurement = _transition?.Measurement
            };
        }
    }
    public event EventHandler<PlaybackSnapshot>? StateChanged;
    public event EventHandler<TrackTransitionedEventArgs>? TrackTransitioned;
    public event EventHandler? PlaybackEnded;
    public event EventHandler<AudioEndpointChangedEventArgs>? OutputDevicesChanged;

    public void SetVisualizationEnabled(bool enabled)
    {
        _visualizationEnabled = enabled;
        if (_visualizationTap is not { } tap) return;
        tap.Enabled = enabled;
        if (!enabled) tap.Reset();
    }

    public AudioVisualizationSnapshot GetVisualizationSnapshot(int bandCount = 40)
    {
        var tap = _visualizationTap;
        return tap is null || _state != PlaybackState.Playing
            ? AudioVisualizationSnapshot.Empty(bandCount)
            : tap.Snapshot(bandCount);
    }

    private TimeSpan Position => _direct?.Position
        ?? (_transition is null
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(
                _transition.PositionSamples
                / (double)(_transition.WaveFormat.SampleRate
                           * _transition.WaveFormat.Channels)
                * (_tempo?.Speed ?? 1)));
    private TimeSpan Duration => _direct?.Duration ?? _track?.Duration ?? TimeSpan.Zero;

    public Task<IReadOnlyList<AudioDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var defaultDevice = enumerator.GetDefaultAudioEndpoint(
            DataFlow.Render,
            Role.Multimedia);
        using var defaultClient = defaultDevice.AudioClient;
        var defaultId = defaultDevice.ID;
        var physical = enumerator.EnumerateAudioEndPoints(
            DataFlow.Render,
            DeviceState.Active).Select(device =>
        {
            using var client = device.AudioClient;
            return new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId, device.State.ToString(), FormatInfo(client.MixFormat).ToString());
        }).ToArray();
        IReadOnlyList<AudioDeviceInfo> devices =
        [
            new(
                "default",
                $"System default — {defaultDevice.FriendlyName}",
                true,
                defaultDevice.State.ToString(),
                FormatInfo(defaultClient.MixFormat).ToString()),
            .. physical
        ];
        return Task.FromResult(devices);
    }

    public Task<AudioDeviceCapabilities> GetDeviceCapabilitiesAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        using var device = ResolveDevice(deviceId);
        using var client = device.AudioClient;
        var supported = new List<AudioFormatInfo>();
        foreach (var channels in new[] { 1, 2 })
        foreach (var rate in new[]
                 {
                     44_100, 48_000, 88_200, 96_000, 176_400, 192_000,
                     352_800, 384_000, 705_600, 768_000
                 })
        foreach (var bits in new[] { 16, 24, 32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var format = new WaveFormat(rate, bits, channels);
            if (client.IsFormatSupported(AudioClientShareMode.Exclusive, format)) supported.Add(FormatInfo(format));
        }
        foreach (var channels in new[] { 1, 2 })
        foreach (var rate in new[]
                 {
                     44_100, 48_000, 88_200, 96_000, 176_400, 192_000,
                     352_800, 384_000, 705_600, 768_000
                 })
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
            if (client.IsFormatSupported(AudioClientShareMode.Exclusive, format)) supported.Add(FormatInfo(format));
        }
        return Task.FromResult(new AudioDeviceCapabilities(
            deviceId,
            device.FriendlyName,
            FormatInfo(client.MixFormat),
            supported
                .Distinct()
                .OrderBy(format => format.Channels)
                .ThenBy(format => format.SampleRate)
                .ThenBy(format => format.BitsPerSample)
                .ToArray(),
            supported.Count > 0));
    }

    public async Task LoadAsync(Track track, CancellationToken cancellationToken = default)
    {
        LogTransition("load-request", new Dictionary<string, object?> { ["requestedTitle"] = track.Title });
        await _gate.WaitAsync(cancellationToken);
        try { await LoadCoreAsync(track, TimeSpan.Zero, false, cancellationToken); }
        finally { _gate.Release(); Publish(); }
    }

    public async Task QueueNextAsync(Track? track, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            CancelBoundaryAnalysis();
            _nextTrack = track;
            LogTransition("next-request");
            _incompatibleNext?.Dispose();
            _incompatibleNext = null;
            if (track is null) { _direct?.QueueNext(null); _transition?.QueueNext(null); return; }
            await QueueNextCoreAsync(track, cancellationToken);
        }
        catch (Exception exception)
        {
            LogTransition("predecode-failed", new Dictionary<string, object?> { ["errorType"] = exception.GetType().Name, ["message"] = exception.Message });
            throw;
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
            _visualizationTap?.Reset();
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
                var next = _nextTrack;
                await LoadCoreAsync(_track, position, wasPlaying, cancellationToken);
                if (next is not null) await QueueNextCoreAsync(next, cancellationToken);
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
            if (UsesHardwareVolume(_profile))
            {
                using var device = ResolveDevice(_profile.DeviceId);
                device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)_volume;
            }
            else if (_profile.VolumeControl == VolumeControlMode.Fixed)
            {
                if (_gain is not null) UpdateGain();
            }
            else if (_gain is not null) UpdateGain();
            else if (_track is not null
                     && _profile.PreferBitPerfect
                     && _volume < 0.999999)
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
            var effectiveVolume = EffectiveSoftwareVolume();
            var tempoSettingsChanged =
                Math.Abs(_options.Speed - options.Speed) > 0.0001
                || Math.Abs(
                    _options.PitchSemitones
                    - options.PitchSemitones) > 0.0001
                || _options.PreservePitch != options.PreservePitch;
            var requiredModeChanged =
                _options.RequiresDsp(effectiveVolume)
                != options.RequiresDsp(effectiveVolume);
            _options = options.Copy();
            CancelBoundaryAnalysis();
            _options.Speed = Math.Clamp(_options.Speed, 0.5, 1.5);
            _options.PitchSemitones = Math.Clamp(_options.PitchSemitones, -12, 12);
            _options.CrossfadeSeconds = Math.Clamp(_options.CrossfadeSeconds, 0, 10);
            LogTransition("settings-applied");
            if (_track is not null
                && (requiredModeChanged
                    || tempoSettingsChanged
                    || _direct is not null
                    && _options.RequiresDsp(effectiveVolume)))
                await RebuildAtCurrentPositionAsync(cancellationToken);
            else UpdateDspParameters();
            if (_nextTrack is { } queued) StartBoundaryAnalysis(queued);
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
        if (isDsd && _profile.DsdMode != DsdMode.Dop) throw new NotSupportedException("Select DoP in this output device profile to play DSF or DFF files.");
        if (_profile.ChannelPolicy == ChannelPolicy.RejectNonStereo
            && decoded.Reader.WaveFormat.Channels != 2)
            throw new NotSupportedException(
                $"This output profile accepts stereo files only; the source has {decoded.Reader.WaveFormat.Channels} channels.");
        var requiresFormatConversion =
            _profile.SampleRatePolicy != SampleRatePolicy.MatchSource
            || _profile.BitDepthPolicy == BitDepthPolicy.Fixed
            || _profile.ChannelPolicy == ChannelPolicy.DownmixToStereo
            && decoded.Reader.WaveFormat.Channels != 2;
        var useDsp = !_profile.PreferBitPerfect
                     || requiresFormatConversion
                     || _options.RequiresDsp(EffectiveSoftwareVolume());
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
            var rate = ResolveTargetSampleRate(
                decoded.Reader.WaveFormat.SampleRate);
            var channels =
                _profile.ChannelPolicy == ChannelPolicy.DownmixToStereo
                    ? 2
                    : decoded.Reader.WaveFormat.Channels;
            var target = WaveFormat.CreateIeeeFloatWaveFormat(
                rate,
                channels);
            var normalized = AudioDecoderFactory.Normalize(decoded, target);
            var totalInputSamples = AudioDecoderFactory.TotalSamples(
                decoded,
                target);
            var initialInputSamples = (long)Math.Round(
                decoded.Reader.CurrentTime.TotalSeconds
                * target.SampleRate
                * target.Channels);
            ISampleProvider processed = normalized;
            var transitionTotalSamples = totalInputSamples;
            var initialPositionSamples = initialInputSamples;
            if (NeedsTempoProcessing())
            {
                _tempo = new SoundTouchSampleProvider(
                    normalized,
                    Math.Max(0, totalInputSamples - initialInputSamples),
                    _options.Speed,
                    _options.PitchSemitones,
                    _options.PreservePitch);
                processed = _tempo;
                initialPositionSamples = AlignSamples(
                    (long)Math.Round(
                        initialInputSamples / _options.Speed),
                    target.Channels);
                transitionTotalSamples = initialPositionSamples
                                         + _tempo.MaximumOutputFrames
                                         * target.Channels;
            }
            _transition = new TransitionSampleProvider(
                processed,
                transitionTotalSamples,
                _options.TransitionMode == TransitionMode.Crossfade
                    ? _options.CrossfadeSeconds
                    : 0,
                decoded,
                initialPositionSamples,
                ReplayGainCalculator.LinearGain(track, _options));
            _transition.SourceChanged += OnPipelineSourceChanged;
            _transition.Trace += OnTransitionTrace;
            _transition.Completed += OnPipelineCompleted;
            _fade = new FadeEnvelopeSampleProvider(
                _transition,
                () => (Position, Duration),
                () => _transition?.Measurement.Overlapping == true);
            _gain = new GainLimiterSampleProvider(_fade) { PreventClipping = _options.PreventClipping };
            UpdateDspParameters();
            waveProvider =
                _profile.BitDepthPolicy == BitDepthPolicy.Fixed
                    ? new PcmSampleWaveProvider(
                        _gain,
                        NormalizeBitDepth(_profile.PreferredBitDepth))
                    : _gain.ToWaveProvider();
            outputFormat = waveProvider.WaveFormat;
        }

        _visualizationTap = new AudioVisualizationTapWaveProvider(
            waveProvider,
            canAnalyze: !isDsd)
        {
            Enabled = _visualizationEnabled
        };
        waveProvider = _visualizationTap;
        _timedProvider = new TimingWaveProvider(waveProvider);
        waveProvider = _timedProvider;
        var effectiveOutput = ResolveEffectiveOutput(outputFormat);
        _output = CreateOutput(_profile, effectiveOutput);
        _output.PlaybackStopped += OutputOnPlaybackStopped;
        _output.Init(waveProvider);
        // WasapiOut.Volume is backed by the Windows audio endpoint. Setting it to 1
        // here reset the user's Windows volume whenever the pipeline was rebuilt.
        // Dextromethorphan applies normal volume in its own 64-bit/DSP gain stage;
        // endpoint volume is touched only by the explicit HardwareVolume profile.
        _diagnostics = BuildDiagnostics(
            decoded,
            outputFormat,
            useDsp,
            effectiveOutput);
        _state = startPlaying ? PlaybackState.Playing : PlaybackState.Paused;
        LogTransition("pipeline-ready");
        if (startPlaying) _output.Play();
        await Task.CompletedTask;
    }

    private async Task QueueNextCoreAsync(Track track, CancellationToken cancellationToken)
    {
        _nextTrack = track;
        LogTransition("predecode-start");
        var decoded = AudioDecoderFactory.Open(track);
        if (_direct is not null)
        {
            if (_direct.CanQueue(decoded.Reader.WaveFormat)) _direct.QueueNext(decoded.Reader, decoded); else _incompatibleNext = decoded;
        }
        else if (_transition is not null)
        {
            var normalized = AudioDecoderFactory.Normalize(
                decoded,
                _transition.WaveFormat);
            ISampleProvider processed = normalized;
            var totalSamples = AudioDecoderFactory.TotalSamples(
                decoded,
                _transition.WaveFormat);
            if (NeedsTempoProcessing())
            {
                _nextTempo = new SoundTouchSampleProvider(
                    normalized,
                    totalSamples,
                    _options.Speed,
                    _options.PitchSemitones,
                    _options.PreservePitch);
                processed = _nextTempo;
                totalSamples = _nextTempo.MaximumOutputFrames
                               * _transition.WaveFormat.Channels;
            }
            _transition.QueueNext(
                processed,
                totalSamples,
                decoded,
                ReplayGainCalculator.LinearGain(track, _options),
                seconds =>
                {
                    if (!decoded.Reader.CanSeek) return false;
                    decoded.Reader.CurrentTime = TimeSpan.FromSeconds(seconds);
                    return Math.Abs(decoded.Reader.CurrentTime.TotalSeconds - seconds) <= .05;
                });
            StartBoundaryAnalysis(track);
        }
        else decoded.Dispose();
        LogTransition("predecode-completed");
        await Task.CompletedTask;
    }

    private void CancelBoundaryAnalysis()
    {
        _boundaryCancellation?.Cancel();
        _boundaryCancellation?.Dispose();
        _boundaryCancellation = null;
        _boundaryToken = default;
        _dynamicOverlap = null;
    }

    private void StartBoundaryAnalysis(Track incoming)
    {
        CancelBoundaryAnalysis();
        if (_transition is { } existing)
        {
            existing.CrossfadeSeconds = _options.TransitionMode == TransitionMode.Crossfade ? _options.CrossfadeSeconds : 0;
            existing.RestoreFullEnding();
        }
        var trim = _options.CrossfadeShape.SkipTrailingSilence;
        var adaptive = _options.CrossfadeShape.DynamicEnabled && _options.TransitionMode == TransitionMode.Crossfade && _options.CrossfadeSeconds > 0;
        if (!adaptive && !trim) { _dynamicStatus = "Boundary analysis: off"; return; }
        if (_transition is not { } pipeline || _track is not { } outgoing
            || NeedsTempoProcessing())
        {
            _dynamicStatus = "Boundary analysis inactive: requires PCM DSP and normal speed/pitch";
            return;
        }
        _boundaryCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _boundaryCancellation.CancelAfter(TimeSpan.FromSeconds(5));
        var token = _boundaryCancellation.Token;
        _boundaryToken = token;
        _dynamicStatus = "Dynamic demo: analyzing boundaries; regular crossfade remains ready";
        _ = ApplyBoundaryPlanAsync(outgoing, incoming, pipeline, _options.TransitionMode == TransitionMode.Crossfade ? _options.CrossfadeSeconds : 0, token, trim, adaptive);
    }

    private async Task ApplyBoundaryPlanAsync(Track outgoing, Track incoming, TransitionSampleProvider pipeline, double maximum, CancellationToken token, bool trim, bool adaptive)
    {
        try
        {
            var plan = await _boundaryAnalyzer.AnalyzeAsync(outgoing, incoming, maximum, token, trim, adaptive).ConfigureAwait(false);
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_transition, pipeline) || !ReferenceEquals(_track, outgoing)
                    || !ReferenceEquals(_nextTrack, incoming)) return;
                if (pipeline.TrySetPlannedCrossfade(
                        plan.Seconds,
                        plan.TrimTrailingSeconds,
                        plan.SkipLeadingSeconds))
                {
                    _dynamicOverlap = plan.Seconds;
                    _dynamicStatus = $"Boundary plan: {plan.Seconds:0.##}s overlap | {plan.TrimTrailingSeconds:0.##}s outgoing tail advanced | {plan.SkipLeadingSeconds:0.##}s incoming silence skipped | {plan.Reason}";
                }
                else _dynamicStatus = "Dynamic demo: plan arrived after transition deadline; regular crossfade retained";
                LogTransition("boundary-plan", new Dictionary<string, object?> { ["result"] = _dynamicStatus });
            }
            finally { _gate.Release(); }
            Publish();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Decoder failures or cancellation never escape into playback. A stale request must not overwrite a newer status.
            if (ReferenceEquals(_transition, pipeline) && ReferenceEquals(_track, outgoing)
                && ReferenceEquals(_nextTrack, incoming) && _boundaryToken == token)
                _dynamicStatus = error is OperationCanceledException
                    ? "Dynamic demo: analysis budget expired; regular crossfade retained"
                    : $"Dynamic demo: regular crossfade fallback ({error.GetType().Name})";
            LogTransition("boundary-analysis-ended", new Dictionary<string, object?> { ["errorType"] = error.GetType().Name, ["requestCancelled"] = token.IsCancellationRequested });
        }
    }

    private EffectiveOutput ResolveEffectiveOutput(WaveFormat format)
    {
        MMDevice device;
        try
        {
            device = ResolveDevice(_profile.DeviceId);
        }
        catch when (
            _profile.FallbackPolicy
            == OutputFallbackPolicy.SystemDefaultShared)
        {
            device = ResolveDevice("default");
            return new(
                WasapiMode.Shared,
                device.ID,
                device.FriendlyName,
                true,
                "The configured endpoint is unavailable; the system default shared endpoint is active.",
                device);
        }

        if (_profile.Mode == WasapiMode.Shared)
            return new(
                WasapiMode.Shared,
                device.ID,
                device.FriendlyName,
                false,
                "",
                device);
        using var client = device.AudioClient;
        if (client.IsFormatSupported(
                AudioClientShareMode.Exclusive,
                format))
            return new(
                WasapiMode.Exclusive,
                device.ID,
                device.FriendlyName,
                false,
                "",
                device);
        if (_profile.FallbackPolicy == OutputFallbackPolicy.SharedMode)
            return new(
                WasapiMode.Shared,
                device.ID,
                device.FriendlyName,
                true,
                $"The endpoint rejected {FormatInfo(format)} in exclusive mode; shared mode is active.",
                device);
        if (_profile.FallbackPolicy
            == OutputFallbackPolicy.SystemDefaultShared)
        {
            device.Dispose();
            var systemDefault = ResolveDevice("default");
            return new(
                WasapiMode.Shared,
                systemDefault.ID,
                systemDefault.FriendlyName,
                true,
                $"The requested endpoint rejected {FormatInfo(format)}; the system default shared endpoint is active.",
                systemDefault);
        }
        var name = device.FriendlyName;
        device.Dispose();
        throw new NotSupportedException(
            $"{name} does not accept {FormatInfo(format)} in exclusive mode.");
    }

    private AudioDiagnostics BuildDiagnostics(
        DecodedAudio decoded,
        WaveFormat format,
        bool dsp,
        EffectiveOutput output)
    {
        var direct = !dsp;
        var bitPerfect =
            direct
            && output.Mode == WasapiMode.Exclusive
            && (_profile.VolumeControl != VolumeControlMode.Software
                || _volume >= 0.999999);
        var reason = bitPerfect ? (decoded.Reader is DsfDopWaveStream or DffDopWaveStream ? "Native DSD payload in DoP 1.1 frames, exclusive event-driven WASAPI, no software processing." : "Direct decoded PCM, exclusive event-driven WASAPI, no software processing.")
            : dsp ? DspReason()
            : output.Mode == WasapiMode.Shared
                ? output.Fallback
                    ? output.FallbackReason
                    : "Shared WASAPI uses the Windows audio engine."
                : "Software volume is below unity.";
        return new AudioDiagnostics(
            dsp ? AudioPipelineMode.Dsp : AudioPipelineMode.Direct,
            _profile.Mode,
            output.Mode,
            FormatInfo(decoded.Reader.WaveFormat),
            FormatInfo(format),
            bitPerfect,
            true,
            decoded.Decoder,
            reason,
            _profile.Name,
            output.Name,
            output.Fallback,
            output.FallbackReason,
            _recoveryAttempts,
            Processor: _tempo is null
                ? "None"
                : "SoundTouch.Net 2.3.2 (high-quality mode)",
            ProcessingLatencyMilliseconds:
                _tempo?.ProcessingLatencyMilliseconds ?? 0,
            TimelineClock: _tempo is null
                ? "Decoded source frames"
                : "Presented output frames × media speed");
    }

    private string DspReason()
    {
        var reasons = new List<string>();
        if (!_profile.PreferBitPerfect) reasons.Add("DSP was selected");
        if (_profile.VolumeControl == VolumeControlMode.Software
            && _volume < 0.999999)
            reasons.Add("software volume");
        if (_options.ReplayGainMode != ReplayGainMode.Off) reasons.Add("ReplayGain");
        if (_options.TransitionMode == TransitionMode.Crossfade) reasons.Add("crossfade");
        if (_options.FadeInSeconds > 0 || _options.FadeOutSeconds > 0) reasons.Add("fade envelope");
        if (Math.Abs(_options.Speed - 1) > 0.0001) reasons.Add("speed processing");
        if (Math.Abs(_options.PitchSemitones) > 0.0001) reasons.Add("pitch processing");
        return string.Join(", ", reasons) + " requires the 32-bit float DSP path.";
    }

    private void UpdateDspParameters()
    {
        if (_transition is not null)
        {
            _transition.CrossfadeSeconds = _options.TransitionMode == TransitionMode.Crossfade ? (_dynamicOverlap ?? _options.CrossfadeSeconds) : 0;
            _transition.Shape = _options.CrossfadeShape;
        }
        if (_gain is not null) { _gain.PreventClipping = _options.PreventClipping; UpdateGain(); }
        if (_fade is not null) { _fade.FadeInSeconds = Math.Clamp(_options.FadeInSeconds, 0, 10); _fade.FadeOutSeconds = Math.Clamp(_options.FadeOutSeconds, 0, 10); }
    }

    private void UpdateGain()
    {
        if (_gain is null) return;
        // Track normalization belongs before the transition mixer so each side of
        // an overlap retains its own ReplayGain. The final stage is only the user's
        // software volume plus clipping protection.
        _gain.Gain = EffectiveSoftwareVolume();
        if (_transition is not null && _track is not null)
            _transition.SetSourceGains(
                ReplayGainCalculator.LinearGain(_track, _options),
                _nextTrack is null ? null : ReplayGainCalculator.LinearGain(_nextTrack, _options));
    }

    private void LogTransition(string operation, IReadOnlyDictionary<string, object?>? extra = null)
    {
        if (_log is null) return;
        var data = new Dictionary<string, object?>
        {
            ["currentTitle"] = _track?.Title,
            ["nextTitle"] = _nextTrack?.Title,
            ["mode"] = _options.TransitionMode.ToString(),
            ["configuredSeconds"] = _options.CrossfadeSeconds,
            ["effectiveSeconds"] = _transition?.CrossfadeSeconds ?? 0,
            ["curve"] = _options.CrossfadeShape.Curve.ToString(),
            ["dynamic"] = _options.CrossfadeShape.DynamicEnabled,
            ["skipSilence"] = _options.CrossfadeShape.SkipTrailingSilence,
            ["pipeline"] = _transition is not null ? "PCM DSP" : _direct is not null ? "Direct gapless" : "None",
            ["speed"] = _options.Speed
        };
        if (extra is not null) foreach (var pair in extra) data[pair.Key] = pair.Value;
        _log.Write(ApplicationLogLevel.Information, "audio-transition", operation, data);
    }

    private void OnTransitionTrace(object? sender, TransitionTrace trace) => LogTransition(trace.Operation,
        new Dictionary<string, object?>
        {
            ["positionSamples"] = trace.PositionSamples,
            ["effectiveEndSamples"] = trace.EndSamples,
            ["incomingSamplesConsumed"] = trace.NextSamplesRead,
            ["actualOverlapSeconds"] = trace.ActualOverlapSeconds,
            ["sampleRate"] = _transition?.WaveFormat.SampleRate,
            ["channels"] = _transition?.WaveFormat.Channels
        });

    private void OnPipelineSourceChanged(object? sender, EventArgs e)
    {
        var previous = _track;
        var current = _nextTrack;
        if (previous is null || current is null) return;
        _track = current;
        _nextTrack = null;
        _tempo = _nextTempo;
        _nextTempo = null;
        UpdateGain();
        TrackTransitioned?.Invoke(this, new TrackTransitionedEventArgs(previous, current, _transition?.LastTransitionOverlapSeconds > 0));
        Publish();
    }

    private void OnPipelineCompleted(object? sender, EventArgs e) => _pipelineCompleted = true;

    private void OutputOnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            _state = PlaybackState.Faulted;
            _error = FriendlyAudioError(e.Exception);
            if (AudioRecoveryPolicy.IsRecoverable(e.Exception))
                _ = RecoverAsync();
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

    private async Task RecoverAsync(bool? resumePlayingOverride = null)
    {
        if (Interlocked.Exchange(ref _recovering, 1) == 1) return;
        var resumePosition = Position;
        var resumePlaying = resumePlayingOverride
                            ?? _state is
                                PlaybackState.Playing
                                or PlaybackState.Faulted;
        try
        {
            Exception? last = null;
            for (var attempt = 1;
                 attempt <= _profile.RecoveryMaximumAttempts;
                 attempt++)
            {
                _recoveryAttempts = attempt;
                await Task.Delay(
                    AudioRecoveryPolicy.DelayForAttempt(
                        attempt,
                        _profile.RecoveryInitialDelayMilliseconds),
                    _lifetime.Token);
                await _gate.WaitAsync(_lifetime.Token);
                try
                {
                    if (_track is null) return;
                    await LoadCoreAsync(
                        _track,
                        resumePosition,
                        resumePlaying,
                        _lifetime.Token);
                    _error = null;
                    return;
                }
                catch (Exception exception) when (
                    AudioRecoveryPolicy.IsRecoverable(exception))
                {
                    last = exception;
                }
                finally
                {
                    _gate.Release();
                }
            }
            throw new InvalidOperationException(
                $"Audio endpoint recovery exhausted {_profile.RecoveryMaximumAttempts} attempts.",
                last);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            _state = PlaybackState.Faulted;
            _error =
                "Audio device recovery failed: " + FriendlyAudioError(ex);
        }
        finally { Interlocked.Exchange(ref _recovering, 0); Publish(); }
    }

    private static string FriendlyAudioError(Exception error) => error.HResult switch
    {
        unchecked((int)0x88890008) => "The output device does not support this exact format in exclusive mode.",
        unchecked((int)0x88890004) => "The output device was disconnected or became unavailable.",
        unchecked((int)0x8889000A) => "The audio service changed the device; reopening the endpoint.",
        unchecked((int)0xC00D36C4) => "Windows could not decode this audio format.",
        _ => error.Message
    };

    private WasapiOut CreateOutput(
        AudioOutputProfile profile,
        EffectiveOutput output)
    {
        _outputDevice = output.Device;
        return new WasapiOut(
            output.Device,
            output.Mode == WasapiMode.Exclusive
                ? AudioClientShareMode.Exclusive
                : AudioClientShareMode.Shared,
            true,
            Math.Clamp(profile.BufferMilliseconds, 2, 1_000));
    }

    private static MMDevice ResolveDevice(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        return deviceId == "default" ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(deviceId);
    }

    private static AudioFormatInfo FormatInfo(WaveFormat format) => new(format.SampleRate, format.BitsPerSample, format.Channels, format.Encoding.ToString());

    private bool NeedsTempoProcessing() =>
        Math.Abs(_options.Speed - 1) > 0.0001
        || Math.Abs(_options.PitchSemitones) > 0.0001;

    private static long AlignSamples(long samples, int channels) =>
        samples - samples % channels;

    private double EffectiveSoftwareVolume() =>
        _profile.VolumeControl == VolumeControlMode.Software
        && !UsesHardwareVolume(_profile)
            ? _volume
            : 1;

    private static bool UsesHardwareVolume(AudioOutputProfile profile) =>
        profile.VolumeControl == VolumeControlMode.Hardware
        || profile.HardwareVolume;

    private int ResolveTargetSampleRate(int sourceRate)
    {
        if (_profile.SampleRatePolicy == SampleRatePolicy.Fixed)
            return _profile.PreferredSampleRate > 0
                ? _profile.PreferredSampleRate
                : sourceRate;
        if (_profile.SampleRatePolicy
            == SampleRatePolicy.EndpointMixFormat)
        {
            using var device = ResolveDevice(_profile.DeviceId);
            using var client = device.AudioClient;
            return client.MixFormat.SampleRate;
        }
        return sourceRate;
    }

    private static int NormalizeBitDepth(int requested) =>
        requested switch
        {
            <= 16 => 16,
            <= 24 => 24,
            _ => 32
        };

    private void OnEndpointChanged(AudioEndpointChangedEventArgs change)
    {
        OutputDevicesChanged?.Invoke(this, change);
        var affectsActive =
            _profile.DeviceId.Equals(
                "default",
                StringComparison.OrdinalIgnoreCase)
            && change.Kind == AudioEndpointChangeKind.DefaultChanged
            || change.DeviceId.Equals(
                _profile.DeviceId,
                StringComparison.OrdinalIgnoreCase)
            && change.Kind is
                AudioEndpointChangeKind.Removed
                or AudioEndpointChangeKind.StateChanged;
        if (affectsActive && _track is not null)
            _ = RecoverAsync();
    }

    private void OnPowerModeChanged(
        object sender,
        PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _resumePlaybackAfterSuspend =
                _state == PlaybackState.Playing;
            _ = PauseAsync(_lifetime.Token);
            return;
        }
        if (e.Mode != PowerModes.Resume || _track is null) return;
        OutputDevicesChanged?.Invoke(
            this,
            new(
                AudioEndpointChangeKind.StateChanged,
                _profile.DeviceId,
                "System resumed"));
        _ = RecoverAsync(_resumePlaybackAfterSuspend);
    }

    private void DisposePlayback()
    {
        LogTransition("pipeline-disposed");
        CancelBoundaryAnalysis();
        if (_output is not null) _output.PlaybackStopped -= OutputOnPlaybackStopped;
        _output?.Stop(); _output?.Dispose(); _output = null;
        if (_timedProvider is { } timed)
            _callbackTiming.Capture(timed);
        _outputDevice?.Dispose(); _outputDevice = null;
        if (_direct is not null) { _direct.SourceChanged -= OnPipelineSourceChanged; _direct.Completed -= OnPipelineCompleted; _direct.Dispose(); _direct = null; }
        if (_transition is not null) { _transition.Trace -= OnTransitionTrace; _transition.SourceChanged -= OnPipelineSourceChanged; _transition.Completed -= OnPipelineCompleted; _transition.Dispose(); _transition = null; }
        _incompatibleNext?.Dispose(); _incompatibleNext = null;
        _tempo = null; _nextTempo = null; _fade = null; _gain = null;
        _visualizationTap = null;
        _timedProvider = null;
    }

    private void Publish()
    {
        var snapshot = Snapshot;
        if (snapshot.Diagnostics?.OutputMeasurement is { Available: true } output && _state == PlaybackState.Playing)
        {
            var mixed = snapshot.Diagnostics.CrossfadeMeasurement;
            var now = Environment.TickCount64;
            var previous = Interlocked.Read(ref _lastOutputLogTick);
            if (now - previous >= (mixed?.Overlapping == true ? 100 : 1000)
                && Interlocked.CompareExchange(ref _lastOutputLogTick, now, previous) == previous)
                LogTransition("output-measured", new Dictionary<string, object?>
                {
                    ["submittedFrames"] = output.Frames, ["outputRms"] = output.Rms,
                    ["outputPeak"] = output.Peak, ["nonFiniteSamples"] = output.NonFiniteSamples,
                    ["outgoingRms"] = mixed?.OutgoingRms, ["incomingRms"] = mixed?.IncomingRms,
                    ["mixedFrames"] = mixed?.MixedFrames, ["overlapping"] = mixed?.Overlapping,
                    ["sampleRate"] = snapshot.Diagnostics.OutputFormat?.SampleRate
                });
        }
        StateChanged?.Invoke(this, snapshot);
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _endpointEnumerator.UnregisterEndpointNotificationCallback(
            _endpointNotifications);
        _endpointEnumerator.Dispose();
        await _positionTimer.DisposeAsync();
        await _gate.WaitAsync();
        try { DisposePlayback(); }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _lifetime.Dispose();
        }
    }

    private sealed record EffectiveOutput(
        WasapiMode Mode,
        string DeviceId,
        string Name,
        bool Fallback,
        string FallbackReason,
        MMDevice Device);
}
