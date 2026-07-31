using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Infrastructure.Audio;

public enum DecoderCapabilityState
{
    Bundled,
    Available,
    Unavailable
}

public sealed record DecoderCapability(
    string Format,
    string Extensions,
    string Backend,
    DecoderCapabilityState State,
    string Detail);

/// <summary>
/// Verifies the installed decoder path with synthetic embedded audio. It never
/// opens media from the user's library.
/// </summary>
public sealed class AudioDecoderCapabilityService
{
    private const string ResourcePrefix =
        "Dextromethorphan.Infrastructure.Audio.Probes.";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<DecoderCapability>? _cached;

    public async Task<IReadOnlyList<DecoderCapability>> InspectAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _cached is { } cached) return cached;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cached is { } checkedAgain)
                return checkedAgain;
            var capabilities = await Task.Run(
                    () => InspectCore(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            _cached = capabilities;
            return capabilities;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<DecoderCapability> InspectCore(
        CancellationToken cancellationToken)
    {
        var result = new List<DecoderCapability>
        {
            Bundled("PCM", ".wav, .wave, .aif, .aiff", "NAudio readers"),
            Bundled("FLAC", ".flac", "BunLabs managed FLAC"),
            Bundled("Vorbis", ".ogg", "NVorbis managed decoder"),
            Bundled("Opus", ".opus", "Concentus managed decoder"),
            Bundled("DSD / DoP", ".dsf, .dff", "Native DSF/DSDIFF parser")
        };
        result.Add(Probe(
            "MP3",
            ".mp3",
            "NAudio MP3 frame decoder",
            "reference.mp3",
            cancellationToken));
        result.Add(Probe(
            "AAC (ADTS)",
            ".aac",
            "Windows Media Foundation",
            "aac.aac",
            cancellationToken));
        result.Add(Probe(
            "AAC (MP4)",
            ".m4a, .mp4",
            "Windows Media Foundation",
            "aac.m4a",
            cancellationToken));
        result.Add(Probe(
            "ALAC",
            ".m4a, .mp4",
            "Windows Media Foundation",
            "alac.m4a",
            cancellationToken));
        result.Add(Probe(
            "WMA",
            ".wma",
            "Windows Media Foundation",
            "reference.wma",
            cancellationToken));
        return result;
    }

    private static DecoderCapability Bundled(
        string format,
        string extensions,
        string backend) =>
        new(
            format,
            extensions,
            backend,
            DecoderCapabilityState.Bundled,
            "Shipped with Dextromethorphan");

    private static DecoderCapability Probe(
        string format,
        string extensions,
        string backend,
        string resourceFile,
        CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(
            Path.GetTempPath(),
            "Dextromethorphan",
            "decoder-probes",
            $"{Guid.NewGuid():N}{Path.GetExtension(resourceFile)}");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
            using (var resource = typeof(AudioDecoderCapabilityService)
                       .Assembly.GetManifestResourceStream(
                           ResourcePrefix + resourceFile)
                   ?? throw new InvalidDataException(
                       $"Embedded decoder probe {resourceFile} is missing."))
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                resource.CopyTo(output);
            }

            using var decoded = AudioDecoderFactory.Open(
                new Track
                {
                    Path = temporary,
                    Title = $"Decoder probe: {format}"
                });
            var buffer = new byte[Math.Max(
                4096,
                decoded.Reader.WaveFormat.BlockAlign * 128)];
            var read = decoded.Reader.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                throw new InvalidDataException(
                    "Decoder returned no PCM frames.");
            return new DecoderCapability(
                format,
                extensions,
                backend,
                DecoderCapabilityState.Available,
                $"Validated: {decoded.Reader.WaveFormat.SampleRate:N0} Hz, "
                + $"{decoded.Reader.WaveFormat.Channels} ch");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = exception.GetBaseException().Message
                .Replace(temporary, "<probe>", StringComparison.OrdinalIgnoreCase);
            return new DecoderCapability(
                format,
                extensions,
                backend,
                DecoderCapabilityState.Unavailable,
                $"{exception.GetBaseException().GetType().Name}: {message}");
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // The next temporary-directory cleanup can safely remove it.
            }
        }
    }
}
