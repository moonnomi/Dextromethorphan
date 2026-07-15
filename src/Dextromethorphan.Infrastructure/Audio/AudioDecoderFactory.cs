using Dextromethorphan.Core.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Vorbis;

namespace Dextromethorphan.Infrastructure.Audio;

internal static class AudioDecoderFactory
{
    public static DecodedAudio Open(Track track)
    {
        var extension = Path.GetExtension(track.Path).ToLowerInvariant();
        WaveStream reader = extension switch
        {
            ".wav" or ".wave" => new WaveFileReader(track.Path),
            ".aif" or ".aiff" => new AiffFileReader(track.Path),
            ".mp3" => new Mp3FileReader(track.Path),
            ".ogg" => new VorbisWaveReader(track.Path),
            ".dsf" => new DsfDopWaveStream(track.Path),
            ".dff" => new DffDopWaveStream(track.Path),
            _ => new MediaFoundationReader(track.Path)
        };
        var decoder = extension switch { ".dsf" => "Native DSF → DoP 1.1", ".dff" => "Native DSDIFF → DoP 1.1", ".ogg" => "NVorbis managed decoder", ".wav" or ".wave" or ".aif" or ".aiff" or ".mp3" => "NAudio native", _ => "Windows Media Foundation" };
        return new DecodedAudio(track, reader, decoder);
    }

    public static ISampleProvider Normalize(DecodedAudio decoded, WaveFormat target)
    {
        ISampleProvider provider = decoded.Reader.ToSampleProvider();
        if (provider.WaveFormat.Channels != target.Channels)
        {
            provider = (provider.WaveFormat.Channels, target.Channels) switch
            {
                (1, 2) => new MonoToStereoSampleProvider(provider),
                (2, 1) => new StereoToMonoSampleProvider(provider),
                _ => throw new NotSupportedException($"Channel conversion {provider.WaveFormat.Channels} → {target.Channels} is not supported.")
            };
        }
        if (provider.WaveFormat.SampleRate != target.SampleRate) provider = new WdlResamplingSampleProvider(provider, target.SampleRate);
        return provider;
    }

    public static long TotalSamples(DecodedAudio decoded, WaveFormat format) =>
        (long)Math.Ceiling(decoded.Reader.TotalTime.TotalSeconds * format.SampleRate * format.Channels);
}

internal sealed class DecodedAudio(Track track, WaveStream reader, string decoder) : IDisposable
{
    public Track Track { get; } = track;
    public WaveStream Reader { get; } = reader;
    public string Decoder { get; } = decoder;
    public void Dispose() => Reader.Dispose();
}
