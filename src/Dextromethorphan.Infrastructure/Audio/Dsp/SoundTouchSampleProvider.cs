using NAudio.Wave;
using SoundTouch;

namespace Dextromethorphan.Infrastructure.Audio.Dsp;

/// <summary>
/// Pull-based NAudio adapter for SoundTouch's high-quality tempo and pitch
/// processor. One SoundTouch sample is one complete mono/stereo frame.
/// </summary>
public sealed class SoundTouchSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly SoundTouchProcessor _processor = new();
    private readonly long _maximumOutputFrames;
    private float[] _inputBuffer = [];
    private bool _sourceEnded;
    private bool _flushed;
    private long _outputFrames;

    public SoundTouchSampleProvider(
        ISampleProvider source,
        long totalInputSamples,
        double speed,
        double pitchSemitones,
        bool preservePitch)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.WaveFormat.Channels is < 1 or > 2)
            throw new NotSupportedException(
                "SoundTouch tempo/pitch processing supports mono and stereo audio.");
        _source = source;
        WaveFormat = source.WaveFormat;
        Speed = Math.Clamp(speed, 0.5, 1.5);
        PitchSemitones = Math.Clamp(pitchSemitones, -12, 12);
        PreservePitch = preservePitch;
        _processor.SampleRate = WaveFormat.SampleRate;
        _processor.Channels = WaveFormat.Channels;
        _processor.SetSetting(SettingId.UseQuickSeek, 0);
        _processor.SetSetting(SettingId.UseAntiAliasFilter, 1);
        _processor.SetSetting(SettingId.AntiAliasFilterLength, 64);
        if (PreservePitch)
        {
            _processor.Rate = 1;
            _processor.Tempo = Speed;
        }
        else
        {
            _processor.Rate = Speed;
            _processor.Tempo = 1;
        }
        _processor.PitchSemiTones = PitchSemitones;
        _maximumOutputFrames = (long)Math.Ceiling(
            totalInputSamples
            / (double)WaveFormat.Channels
            / Speed);
        InitialLatencyFrames = _processor.GetSetting(
            SettingId.InitialLatency);
        var nominalOutput = _processor.GetSetting(
            SettingId.NominalOutputSequence);
        AverageLatencyFrames = Math.Max(
            0,
            InitialLatencyFrames - nominalOutput / 2);
    }

    public WaveFormat WaveFormat { get; }
    public double Speed { get; }
    public double PitchSemitones { get; }
    public bool PreservePitch { get; }
    public int InitialLatencyFrames { get; }
    public int AverageLatencyFrames { get; }
    public long MaximumOutputFrames => _maximumOutputFrames;
    public long OutputFrames => _outputFrames;
    public double ProcessingLatencyMilliseconds =>
        AverageLatencyFrames * 1000d / WaveFormat.SampleRate;

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        var channels = WaveFormat.Channels;
        var requestedFrames = count / channels;
        var remainingFrames = _maximumOutputFrames - _outputFrames;
        requestedFrames = (int)Math.Min(requestedFrames, remainingFrames);
        if (requestedFrames <= 0) return 0;

        var writtenFrames = 0;
        while (writtenFrames < requestedFrames)
        {
            var output = buffer.AsSpan(
                offset + writtenFrames * channels,
                (requestedFrames - writtenFrames) * channels);
            var received = _processor.ReceiveSamples(
                output,
                requestedFrames - writtenFrames);
            if (received > 0)
            {
                writtenFrames += received;
                continue;
            }
            if (_flushed) break;

            if (!_sourceEnded)
            {
                const int inputFrames = 4096;
                var required = inputFrames * channels;
                if (_inputBuffer.Length < required)
                    _inputBuffer = new float[required];
                var read = _source.Read(
                    _inputBuffer,
                    0,
                    required);
                if (read > 0)
                {
                    _processor.PutSamples(
                        _inputBuffer.AsSpan(0, read),
                        read / channels);
                    continue;
                }
                _sourceEnded = true;
            }

            _processor.Flush();
            _flushed = true;
        }
        _outputFrames += writtenFrames;
        return writtenFrames * channels;
    }
}
