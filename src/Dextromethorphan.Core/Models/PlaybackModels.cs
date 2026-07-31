namespace Dextromethorphan.Core.Models;

public enum PlaybackState { Stopped, Playing, Paused, Buffering, Faulted }
public enum RepeatMode { Off, All, One }
public enum ReplayGainMode { Off, Track, Album }
public enum WasapiMode { Shared, Exclusive }
public enum DsdMode { Disabled, Dop, Native }
public enum AudioPipelineMode { Direct, Dsp }
public enum TransitionMode { Gapless, Crossfade }
public enum OutputFallbackPolicy { Never, SharedMode, SystemDefaultShared }
public enum SampleRatePolicy { MatchSource, EndpointMixFormat, Fixed }
public enum BitDepthPolicy { MatchSource, Fixed }
public enum ChannelPolicy { RejectNonStereo, DownmixToStereo, MatchSource }
public enum VolumeControlMode { Software, Hardware, Fixed }
public enum AudioEndpointChangeKind { Added, Removed, StateChanged, DefaultChanged }

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault, string State, string MixFormat = "");

public sealed record AudioFormatInfo(int SampleRate, int BitsPerSample, int Channels, string Encoding)
{
    public override string ToString() => $"{Encoding} · {SampleRate / 1000d:0.#} kHz · {BitsPerSample}-bit · {Channels}ch";
}

public sealed record AudioDeviceCapabilities(
    string DeviceId,
    string DeviceName,
    AudioFormatInfo MixFormat,
    IReadOnlyList<AudioFormatInfo> SupportedExclusiveFormats,
    bool SupportsEventDrivenExclusive);

public sealed record AudioEndpointChangedEventArgs(
    AudioEndpointChangeKind Kind,
    string DeviceId,
    string? Detail = null);

public sealed record AudioDiagnostics(
    AudioPipelineMode PipelineMode,
    WasapiMode RequestedMode,
    WasapiMode EffectiveMode,
    AudioFormatInfo? SourceFormat,
    AudioFormatInfo? OutputFormat,
    bool IsBitPerfect,
    bool IsEventDriven,
    string Decoder,
    string Reason,
    string RequestedDevice = "",
    string EffectiveDevice = "",
    bool FallbackActive = false,
    string FallbackReason = "",
    int RecoveryAttempts = 0,
    long Underruns = 0,
    double LastCallbackMilliseconds = 0,
    double MaximumCallbackMilliseconds = 0,
    string Processor = "None",
    double ProcessingLatencyMilliseconds = 0,
    string TimelineClock = "Source position");

public sealed record PlaybackSnapshot(
    Track? Track,
    PlaybackState State,
    TimeSpan Position,
    TimeSpan Duration,
    double Volume,
    string? Error = null,
    AudioDiagnostics? Diagnostics = null,
    double Speed = 1.0,
    double Peak = 0);

public sealed record TrackTransitionedEventArgs(Track Previous, Track Current, bool Crossfaded);
public sealed record SleepTimerSnapshot(bool IsActive, TimeSpan? Remaining, bool StopAtEndOfTrack);

public sealed class AudioPlaybackOptions
{
    public ReplayGainMode ReplayGainMode { get; set; } = ReplayGainMode.Track;
    public double ReplayGainPreampDb { get; set; }
    public bool PreventClipping { get; set; } = true;
    public TransitionMode TransitionMode { get; set; } = TransitionMode.Gapless;
    public double CrossfadeSeconds { get; set; }
    public double FadeInSeconds { get; set; }
    public double FadeOutSeconds { get; set; }
    public double Speed { get; set; } = 1.0;
    public double PitchSemitones { get; set; }
    public bool PreservePitch { get; set; } = true;

    public AudioPlaybackOptions Copy() => (AudioPlaybackOptions)MemberwiseClone();
    public bool RequiresDsp(double volume) =>
        ReplayGainMode != ReplayGainMode.Off || TransitionMode == TransitionMode.Crossfade ||
        FadeInSeconds > 0 || FadeOutSeconds > 0 || Math.Abs(Speed - 1) > 0.0001 ||
        Math.Abs(PitchSemitones) > 0.0001 || volume < 0.999999;
}

public sealed record QueueEntry(Guid Id, Track Track, DateTimeOffset AddedAt, bool IsPlaying = false);

public sealed class AudioOutputProfile
{
    public string DeviceId { get; set; } = "default";
    public string Name { get; set; } = "System default";
    public WasapiMode Mode { get; set; } = WasapiMode.Shared;
    public int BufferMilliseconds { get; set; } = 100;
    public SampleRatePolicy SampleRatePolicy { get; set; } = SampleRatePolicy.MatchSource;
    public int PreferredSampleRate { get; set; }
    public BitDepthPolicy BitDepthPolicy { get; set; } = BitDepthPolicy.MatchSource;
    public int PreferredBitDepth { get; set; }
    public ChannelPolicy ChannelPolicy { get; set; } = ChannelPolicy.DownmixToStereo;
    public DsdMode DsdMode { get; set; } = DsdMode.Disabled;
    public double CrossfadeSeconds { get; set; }
    public VolumeControlMode VolumeControl { get; set; } = VolumeControlMode.Software;
    // Retained for settings compatibility. New code writes VolumeControl.
    public bool HardwareVolume { get; set; }
    public bool PreferBitPerfect { get; set; } = true;
    public OutputFallbackPolicy FallbackPolicy { get; set; } = OutputFallbackPolicy.SharedMode;
    public int RecoveryMaximumAttempts { get; set; } = 4;
    public int RecoveryInitialDelayMilliseconds { get; set; } = 200;
}
