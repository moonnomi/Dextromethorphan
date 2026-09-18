using System.Diagnostics;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Infrastructure.Audio;

public sealed record AudioFileProbeResult(bool Succeeded, string Decoder, string Format,
    double DurationSeconds, int HeadBytes, int TailBytes, double ElapsedMilliseconds, string? Error);

/// <summary>Short read-only decoder probe. Does not access the active playback decoder.</summary>
public static class AudioFileProbe
{
    private static readonly SemaphoreSlim Worker = new(1, 1);
    public static async Task<AudioFileProbeResult> RunAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (!await Worker.WaitAsync(0, cancellationToken))
            return new(false, "", "", 0, 0, 0, 0, "Another decoder test is still running.");
        try
        {
            return await Task.Run(() =>
            {
                var timer = Stopwatch.StartNew();
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var decoded = AudioDecoderFactory.Open(track);
                    var reader = decoded.Reader;
                    var buffer = new byte[Math.Clamp(reader.WaveFormat.AverageBytesPerSecond, 4096, 1048576)];
                    cancellationToken.ThrowIfCancellationRequested();
                    var head = reader.Read(buffer, 0, buffer.Length);
                    cancellationToken.ThrowIfCancellationRequested();
                    var tail = 0;
                    if (reader.CanSeek && reader.TotalTime.TotalSeconds > 2)
                    {
                        reader.CurrentTime = reader.TotalTime - TimeSpan.FromSeconds(1);
                        cancellationToken.ThrowIfCancellationRequested();
                        tail = reader.Read(buffer, 0, buffer.Length);
                    }
                    return new AudioFileProbeResult(head > 0, decoded.Decoder, reader.WaveFormat.ToString(),
                        reader.TotalTime.TotalSeconds, head, tail, timer.Elapsed.TotalMilliseconds,
                        head > 0 ? null : "Decoder returned no audio at the start.");
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    return new AudioFileProbeResult(false, "", "", 0, 0, 0, timer.Elapsed.TotalMilliseconds,
                        $"{error.GetType().Name}: {error.Message}");
                }
            }, cancellationToken);
        }
        finally { Worker.Release(); }
    }
}
