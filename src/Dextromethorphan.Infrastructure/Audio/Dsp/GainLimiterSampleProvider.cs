using NAudio.Wave;

namespace Dextromethorphan.Infrastructure.Audio.Dsp;

/// <summary>Applies gain using double precision and provides a final clipping guard.</summary>
public sealed class GainLimiterSampleProvider(ISampleProvider source) : ISampleProvider
{
    private double _gain = 1;
    private double _peak;
    public WaveFormat WaveFormat => source.WaveFormat;
    public double Gain { get => Volatile.Read(ref _gain); set => Volatile.Write(ref _gain, Math.Clamp(value, 0, 16)); }
    public bool PreventClipping { get; set; } = true;
    public double Peak => Volatile.Read(ref _peak);

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        var gain = Gain;
        double blockPeak = 0;
        for (var i = offset; i < offset + read; i++)
        {
            var mixed = (double)buffer[i] * gain;
            blockPeak = Math.Max(blockPeak, Math.Abs(mixed));
            if (PreventClipping) mixed = Math.Clamp(mixed, -1d, 1d);
            buffer[i] = (float)mixed;
        }
        Volatile.Write(ref _peak, blockPeak);
        return read;
    }
}
