using NAudio.Wave;

namespace Dextromethorphan.Infrastructure.Audio.Dsp;

/// <summary>Changes playback rate while preserving the declared output format.</summary>
public sealed class VariableRateSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float[] _input = new float[16 * 1024];
    private int _framesInBuffer;
    private double _positionFrames;
    private bool _endOfSource;
    private double _rate = 1;

    public VariableRateSampleProvider(ISampleProvider source) { _source = source; WaveFormat = source.WaveFormat; }
    public WaveFormat WaveFormat { get; }
    public double Rate { get => Volatile.Read(ref _rate); set => Volatile.Write(ref _rate, Math.Clamp(value, 0.5, 1.5)); }

    public int Read(float[] buffer, int offset, int count)
    {
        var channels = WaveFormat.Channels;
        var requestedFrames = count / channels;
        var writtenFrames = 0;
        while (writtenFrames < requestedFrames)
        {
            Compact();
            var baseFrame = (int)_positionFrames;
            if (!EnsureFrame(baseFrame + 1) && baseFrame >= _framesInBuffer) break;
            var nextFrame = Math.Min(baseFrame + 1, _framesInBuffer - 1);
            var fraction = _positionFrames - baseFrame;
            for (var channel = 0; channel < channels; channel++)
            {
                var a = _input[(baseFrame * channels) + channel];
                var b = _input[(nextFrame * channels) + channel];
                buffer[offset + (writtenFrames * channels) + channel] = (float)(a + ((b - a) * fraction));
            }
            _positionFrames += Rate;
            writtenFrames++;
        }
        return writtenFrames * channels;
    }

    private bool EnsureFrame(int frame)
    {
        var channels = WaveFormat.Channels;
        while (_framesInBuffer <= frame && !_endOfSource)
        {
            var requiredSamples = (_framesInBuffer + 4096) * channels;
            if (_input.Length < requiredSamples) Array.Resize(ref _input, Math.Max(requiredSamples, _input.Length * 2));
            var read = _source.Read(_input, _framesInBuffer * channels, Math.Min(4096 * channels, _input.Length - (_framesInBuffer * channels)));
            if (read == 0) { _endOfSource = true; break; }
            _framesInBuffer += read / channels;
        }
        return _framesInBuffer > frame;
    }

    private void Compact()
    {
        var consumed = Math.Max(0, (int)_positionFrames - 1);
        if (consumed < 4096) return;
        var channels = WaveFormat.Channels;
        var remaining = _framesInBuffer - consumed;
        Array.Copy(_input, consumed * channels, _input, 0, remaining * channels);
        _framesInBuffer = remaining;
        _positionFrames -= consumed;
    }
}
