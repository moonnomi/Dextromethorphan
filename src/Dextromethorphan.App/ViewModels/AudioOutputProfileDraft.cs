using Dextromethorphan.Core.Models;

namespace Dextromethorphan.App.ViewModels;

public sealed class AudioOutputProfileDraft : ObservableObject
{
    private string _deviceId = "default";
    private string _name = "System default";
    private WasapiMode _mode;
    private int _bufferMilliseconds = 100;
    private SampleRatePolicy _sampleRatePolicy;
    private int _preferredSampleRate = 48_000;
    private BitDepthPolicy _bitDepthPolicy;
    private int _preferredBitDepth = 24;
    private ChannelPolicy _channelPolicy = ChannelPolicy.DownmixToStereo;
    private OutputFallbackPolicy _fallbackPolicy = OutputFallbackPolicy.SharedMode;
    private VolumeControlMode _volumeControl;
    private DsdMode _dsdMode;
    private bool _preferBitPerfect = true;
    private double _crossfadeSeconds;
    private int _recoveryMaximumAttempts = 4;
    private int _recoveryInitialDelayMilliseconds = 200;

    public string DeviceId { get => _deviceId; set => Set(ref _deviceId, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public WasapiMode Mode { get => _mode; set => Set(ref _mode, value); }
    public int BufferMilliseconds { get => _bufferMilliseconds; set => Set(ref _bufferMilliseconds, value); }
    public SampleRatePolicy SampleRatePolicy { get => _sampleRatePolicy; set => Set(ref _sampleRatePolicy, value); }
    public int PreferredSampleRate { get => _preferredSampleRate; set => Set(ref _preferredSampleRate, value); }
    public BitDepthPolicy BitDepthPolicy { get => _bitDepthPolicy; set => Set(ref _bitDepthPolicy, value); }
    public int PreferredBitDepth { get => _preferredBitDepth; set => Set(ref _preferredBitDepth, value); }
    public ChannelPolicy ChannelPolicy { get => _channelPolicy; set => Set(ref _channelPolicy, value); }
    public OutputFallbackPolicy FallbackPolicy { get => _fallbackPolicy; set => Set(ref _fallbackPolicy, value); }
    public VolumeControlMode VolumeControl { get => _volumeControl; set => Set(ref _volumeControl, value); }
    public DsdMode DsdMode { get => _dsdMode; set => Set(ref _dsdMode, value); }
    public bool PreferBitPerfect { get => _preferBitPerfect; set => Set(ref _preferBitPerfect, value); }
    public double CrossfadeSeconds { get => _crossfadeSeconds; set => Set(ref _crossfadeSeconds, value); }
    public int RecoveryMaximumAttempts { get => _recoveryMaximumAttempts; set => Set(ref _recoveryMaximumAttempts, value); }
    public int RecoveryInitialDelayMilliseconds { get => _recoveryInitialDelayMilliseconds; set => Set(ref _recoveryInitialDelayMilliseconds, value); }

    public void Load(AudioOutputProfile profile)
    {
        DeviceId = profile.DeviceId;
        Name = profile.Name;
        Mode = profile.Mode;
        BufferMilliseconds = profile.BufferMilliseconds;
        SampleRatePolicy = profile.SampleRatePolicy;
        PreferredSampleRate = profile.PreferredSampleRate == 0
            ? 48_000
            : profile.PreferredSampleRate;
        BitDepthPolicy = profile.BitDepthPolicy;
        PreferredBitDepth = profile.PreferredBitDepth == 0
            ? 24
            : profile.PreferredBitDepth;
        ChannelPolicy = profile.ChannelPolicy;
        FallbackPolicy = profile.FallbackPolicy;
        VolumeControl = profile.HardwareVolume
            ? VolumeControlMode.Hardware
            : profile.VolumeControl;
        DsdMode = profile.DsdMode;
        PreferBitPerfect = profile.PreferBitPerfect;
        CrossfadeSeconds = profile.CrossfadeSeconds;
        RecoveryMaximumAttempts = profile.RecoveryMaximumAttempts;
        RecoveryInitialDelayMilliseconds =
            profile.RecoveryInitialDelayMilliseconds;
    }

    public AudioOutputProfile ToProfile() => new()
    {
        DeviceId = DeviceId,
        Name = Name,
        Mode = Mode,
        BufferMilliseconds = Math.Clamp(BufferMilliseconds, 2, 1_000),
        SampleRatePolicy = SampleRatePolicy,
        PreferredSampleRate =
            SampleRatePolicy == SampleRatePolicy.Fixed
                ? PreferredSampleRate
                : 0,
        BitDepthPolicy = BitDepthPolicy,
        PreferredBitDepth =
            BitDepthPolicy == BitDepthPolicy.Fixed
                ? PreferredBitDepth
                : 0,
        ChannelPolicy = ChannelPolicy,
        FallbackPolicy = FallbackPolicy,
        VolumeControl = VolumeControl,
        HardwareVolume = VolumeControl == VolumeControlMode.Hardware,
        DsdMode = DsdMode,
        PreferBitPerfect = PreferBitPerfect,
        CrossfadeSeconds = Math.Clamp(CrossfadeSeconds, 0, 10),
        RecoveryMaximumAttempts =
            Math.Clamp(RecoveryMaximumAttempts, 1, 8),
        RecoveryInitialDelayMilliseconds =
            Math.Clamp(RecoveryInitialDelayMilliseconds, 50, 2_000)
    };
}
