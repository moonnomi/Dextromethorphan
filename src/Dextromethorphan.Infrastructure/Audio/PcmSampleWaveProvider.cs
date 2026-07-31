using NAudio.Wave;

namespace Dextromethorphan.Infrastructure.Audio;

internal sealed class PcmSampleWaveProvider : IWaveProvider
{
    private readonly ISampleProvider _source;
    private readonly int _bytesPerSample;
    private float[] _samples = [];

    public PcmSampleWaveProvider(
        ISampleProvider source,
        int bitsPerSample)
    {
        if (bitsPerSample is not (16 or 24 or 32))
            throw new ArgumentOutOfRangeException(
                nameof(bitsPerSample));
        _source = source;
        _bytesPerSample = bitsPerSample / 8;
        WaveFormat = new WaveFormat(
            source.WaveFormat.SampleRate,
            bitsPerSample,
            source.WaveFormat.Channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        var alignedBytes = count - count % WaveFormat.BlockAlign;
        var sampleCount = alignedBytes / _bytesPerSample;
        if (_samples.Length < sampleCount)
            _samples = new float[sampleCount];
        var read = _source.Read(_samples, 0, sampleCount);
        for (var index = 0; index < read; index++)
        {
            var sample = Math.Clamp(_samples[index], -1f, 1f);
            var destination = offset + index * _bytesPerSample;
            switch (WaveFormat.BitsPerSample)
            {
                case 16:
                    var value16 = (short)Math.Round(
                        sample * (sample < 0 ? 32768 : 32767));
                    buffer[destination] = (byte)value16;
                    buffer[destination + 1] = (byte)(value16 >> 8);
                    break;
                case 24:
                    var value24 = (int)Math.Round(
                        sample * (sample < 0 ? 8_388_608 : 8_388_607));
                    buffer[destination] = (byte)value24;
                    buffer[destination + 1] = (byte)(value24 >> 8);
                    buffer[destination + 2] = (byte)(value24 >> 16);
                    break;
                case 32:
                    var value32 = (long)Math.Round(
                        sample * (sample < 0
                            ? 2_147_483_648d
                            : 2_147_483_647d));
                    buffer[destination] = (byte)value32;
                    buffer[destination + 1] = (byte)(value32 >> 8);
                    buffer[destination + 2] = (byte)(value32 >> 16);
                    buffer[destination + 3] = (byte)(value32 >> 24);
                    break;
            }
        }
        return read * _bytesPerSample;
    }
}
