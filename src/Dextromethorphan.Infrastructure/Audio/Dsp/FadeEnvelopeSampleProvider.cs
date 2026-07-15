using NAudio.Wave;

namespace Dextromethorphan.Infrastructure.Audio.Dsp;

public sealed class FadeEnvelopeSampleProvider(ISampleProvider source, Func<(TimeSpan Position, TimeSpan Duration)> clock) : ISampleProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;
    public double FadeInSeconds { get; set; }
    public double FadeOutSeconds { get; set; }

    public int Read(float[] buffer, int offset, int count)
    {
        var before = clock();
        var read = source.Read(buffer, offset, count);
        var after = clock();
        var channels = WaveFormat.Channels;
        var frames = read / channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var fraction = frames <= 1 ? 1d : frame / (double)(frames - 1);
            var position = before.Position <= after.Position
                ? before.Position + ((after.Position - before.Position) * fraction)
                : after.Position;
            var envelope = 1d;
            if (FadeInSeconds > 0) envelope = Math.Min(envelope, position.TotalSeconds / FadeInSeconds);
            if (FadeOutSeconds > 0 && before.Duration > TimeSpan.Zero)
                envelope = Math.Min(envelope, Math.Max(0, (before.Duration - position).TotalSeconds / FadeOutSeconds));
            envelope = Math.Clamp(envelope, 0, 1);
            for (var channel = 0; channel < channels; channel++) buffer[offset + (frame * channels) + channel] *= (float)envelope;
        }
        return read;
    }
}
