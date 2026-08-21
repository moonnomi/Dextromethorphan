using Dextromethorphan.Core.Models;
using NAudio.Wave;

namespace Dextromethorphan.Infrastructure.Audio;

/// <summary>
/// Copies decoded samples into a small analysis ring while returning the exact
/// bytes supplied by the source. Disabled analysis is a single volatile check.
/// </summary>
internal sealed class AudioVisualizationTapWaveProvider : IWaveProvider
{
    private const int RingSize = 8192;
    private const int FftSize = 2048;
    private readonly IWaveProvider _source;
    private readonly bool _canAnalyze;
    private readonly float[] _ring = new float[RingSize];
    private readonly double[] _real = new double[FftSize];
    private readonly double[] _imaginary = new double[FftSize];
    private readonly double[] _smoothed = new double[96];
    private readonly object _analysisGate = new();
    private int _writeIndex;
    private int _sampleCount;
    private volatile bool _enabled;
    private static readonly Guid IeeeFloatSubFormat =
        new("00000003-0000-0010-8000-00AA00389B71");

    public AudioVisualizationTapWaveProvider(
        IWaveProvider source,
        bool canAnalyze = true)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _canAnalyze = canAnalyze && Supports(WaveFormat);
    }

    public WaveFormat WaveFormat { get; }
    public bool Enabled { get => _enabled; set => _enabled = value; }

    public int Read(byte[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (_enabled && _canAnalyze && read > 0)
            Capture(buffer, offset, read);
        return read;
    }

    public void Reset()
    {
        Array.Clear(_ring);
        Array.Clear(_smoothed);
        Volatile.Write(ref _writeIndex, 0);
        Volatile.Write(ref _sampleCount, 0);
    }

    public AudioVisualizationSnapshot Snapshot(int requestedBandCount)
    {
        var bandCount = Math.Clamp(requestedBandCount, 8, 96);
        if (!_enabled)
            return AudioVisualizationSnapshot.Empty(bandCount);
        if (!_canAnalyze)
            return AudioVisualizationSnapshot.Empty(
                bandCount,
                "Spectrum analysis is unavailable for this encoded stream.");
        if (Volatile.Read(ref _sampleCount) < FftSize)
            return AudioVisualizationSnapshot.Empty(bandCount);

        lock (_analysisGate)
        {
            CopyLatestSamples();
            double peak = 0;
            for (var i = 0; i < FftSize; i++)
                peak = Math.Max(peak, Math.Abs(_real[i]));
            ApplyWindow();
            Transform();

            var bands = new double[bandCount];
            var nyquist = WaveFormat.SampleRate / 2d;
            var maximumFrequency = Math.Min(18000d, nyquist);
            const double minimumFrequency = 45d;
            for (var band = 0; band < bandCount; band++)
            {
                var startFrequency = minimumFrequency * Math.Pow(
                    maximumFrequency / minimumFrequency,
                    band / (double)bandCount);
                var endFrequency = minimumFrequency * Math.Pow(
                    maximumFrequency / minimumFrequency,
                    (band + 1d) / bandCount);
                var startBin = Math.Clamp(
                    (int)Math.Floor(startFrequency * FftSize / WaveFormat.SampleRate),
                    1,
                    FftSize / 2 - 1);
                var endBin = Math.Clamp(
                    (int)Math.Ceiling(endFrequency * FftSize / WaveFormat.SampleRate),
                    startBin + 1,
                    FftSize / 2);
                double maximum = 0;
                for (var bin = startBin; bin < endBin; bin++)
                {
                    var magnitude = Math.Sqrt(
                        _real[bin] * _real[bin]
                        + _imaginary[bin] * _imaginary[bin]) / (FftSize / 2d);
                    var normalized = Math.Clamp(
                        (20 * Math.Log10(magnitude + 1e-9) + 72) / 72,
                        0,
                        1);
                    maximum = Math.Max(maximum, Math.Sqrt(normalized));
                }

                var previous = _smoothed[band];
                var next = maximum > previous
                    ? previous + (maximum - previous) * .68
                    : previous * .82;
                _smoothed[band] = next;
                bands[band] = next;
            }

            return new(bands, Math.Clamp(peak, 0, 1), true);
        }
    }

    private void Capture(byte[] buffer, int offset, int count)
    {
        var bytesPerSample = WaveFormat.BitsPerSample / 8;
        var channels = Math.Max(1, WaveFormat.Channels);
        var frameSize = bytesPerSample * channels;
        var end = offset + count - frameSize + 1;
        var write = Volatile.Read(ref _writeIndex);
        var captured = 0;
        for (var frame = offset; frame < end; frame += frameSize)
        {
            double mixed = 0;
            for (var channel = 0; channel < channels; channel++)
                mixed += ReadSample(buffer, frame + channel * bytesPerSample);
            _ring[write] = (float)(mixed / channels);
            write = (write + 1) & (RingSize - 1);
            captured++;
        }
        Volatile.Write(ref _writeIndex, write);
        if (captured > 0)
            Volatile.Write(
                ref _sampleCount,
                Math.Min(RingSize, Volatile.Read(ref _sampleCount) + captured));
    }

    private double ReadSample(byte[] buffer, int offset)
    {
        if (IsFloatFormat(WaveFormat))
            return WaveFormat.BitsPerSample == 64
                ? BitConverter.ToDouble(buffer, offset)
                : BitConverter.ToSingle(buffer, offset);
        return WaveFormat.BitsPerSample switch
        {
            8 => (buffer[offset] - 128) / 128d,
            16 => BitConverter.ToInt16(buffer, offset) / 32768d,
            24 => ReadInt24(buffer, offset) / 8388608d,
            32 => BitConverter.ToInt32(buffer, offset) / 2147483648d,
            _ => 0
        };
    }

    private static int ReadInt24(byte[] buffer, int offset)
    {
        var value = buffer[offset]
                    | buffer[offset + 1] << 8
                    | buffer[offset + 2] << 16;
        return (value & 0x800000) == 0 ? value : value | unchecked((int)0xFF000000);
    }

    private void CopyLatestSamples()
    {
        var end = Volatile.Read(ref _writeIndex);
        var start = (end - FftSize + RingSize) & (RingSize - 1);
        for (var i = 0; i < FftSize; i++)
        {
            _real[i] = _ring[(start + i) & (RingSize - 1)];
            _imaginary[i] = 0;
        }
    }

    private void ApplyWindow()
    {
        for (var i = 0; i < FftSize; i++)
            _real[i] *= .5 - .5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
    }

    private void Transform()
    {
        var j = 0;
        for (var i = 1; i < FftSize; i++)
        {
            var bit = FftSize >> 1;
            while ((j & bit) != 0) { j ^= bit; bit >>= 1; }
            j ^= bit;
            if (i >= j) continue;
            (_real[i], _real[j]) = (_real[j], _real[i]);
            (_imaginary[i], _imaginary[j]) = (_imaginary[j], _imaginary[i]);
        }
        for (var length = 2; length <= FftSize; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var stepReal = Math.Cos(angle);
            var stepImaginary = Math.Sin(angle);
            for (var start = 0; start < FftSize; start += length)
            {
                double wr = 1, wi = 0;
                for (var offset = 0; offset < length / 2; offset++)
                {
                    var even = start + offset;
                    var odd = even + length / 2;
                    var tr = wr * _real[odd] - wi * _imaginary[odd];
                    var ti = wr * _imaginary[odd] + wi * _real[odd];
                    _real[odd] = _real[even] - tr;
                    _imaginary[odd] = _imaginary[even] - ti;
                    _real[even] += tr;
                    _imaginary[even] += ti;
                    (wr, wi) = (wr * stepReal - wi * stepImaginary,
                        wr * stepImaginary + wi * stepReal);
                }
            }
        }
    }

    private static bool Supports(WaveFormat format)
    {
        if (IsFloatFormat(format))
            return format.BitsPerSample is 32 or 64;
        return format.Encoding is WaveFormatEncoding.Pcm
                or WaveFormatEncoding.Extensible
            && format.BitsPerSample is 8 or 16 or 24 or 32;
    }

    private static bool IsFloatFormat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat
        || format is WaveFormatExtensible extensible
        && extensible.SubFormat == IeeeFloatSubFormat;
}
