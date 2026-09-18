using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Audio.Dsp;
using NAudio.Wave;

namespace Dextromethorphan.Infrastructure.Audio;

internal sealed class BoundaryEnvelopeAnalyzer
{
    private readonly SemaphoreSlim _worker = new(1, 1);
    private readonly Dictionary<string, BoundaryEnvelope> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _keys = new();

    public async Task<DynamicCrossfadePlan> AnalyzeAsync(Track outgoing, Track incoming, double maximum, CancellationToken token, bool trimSilence = false, bool adaptive = true)
    {
        await _worker.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var boundary = Read(outgoing, token);
                var incomingBoundary = Read(incoming, token);
                var trim = trimSilence ? DynamicCrossfadePlanner.TrailingSilenceSeconds(boundary) : 0;
                if (adaptive)
                    trim = Math.Max(trim, DynamicCrossfadePlanner.DynamicTailAdvanceSeconds(boundary));
                if (trim > 0)
                    boundary = boundary with
                    {
                        DurationSeconds = boundary.DurationSeconds - trim,
                        TailDb = TrimEnd(boundary.TailDb, trim, boundary.WindowSeconds),
                        TailPeakDb = boundary.TailPeakDb is null
                            ? null
                            : TrimEnd(boundary.TailPeakDb, trim, boundary.WindowSeconds)
                    };
                var leading = adaptive ? DynamicCrossfadePlanner.LeadingSilenceSeconds(incomingBoundary) : 0;
                if (leading > 0)
                    incomingBoundary = incomingBoundary with
                    {
                        DurationSeconds = incomingBoundary.DurationSeconds - leading,
                        HeadDb = TrimStart(incomingBoundary.HeadDb, leading, incomingBoundary.WindowSeconds),
                        HeadPeakDb = incomingBoundary.HeadPeakDb is null
                            ? null
                            : TrimStart(incomingBoundary.HeadPeakDb, leading, incomingBoundary.WindowSeconds)
                    };
                var plan = adaptive ? DynamicCrossfadePlanner.Plan(boundary, incomingBoundary, maximum)
                    : new DynamicCrossfadePlan(maximum, "Regular transition with trailing-silence detection");
                return plan with { TrimTrailingSeconds = trim, SkipLeadingSeconds = leading };
            }, token).ConfigureAwait(false);
        }
        finally { _worker.Release(); }
    }

    private BoundaryEnvelope Read(Track track, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(track.EffectiveMediaPath);
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
            throw new NotSupportedException("Demo analysis supports local fixed drives only");
        if (Path.GetExtension(path).ToLowerInvariant() is ".dsf" or ".dff")
            throw new NotSupportedException("DSD is not PCM envelope data");
        var info = new FileInfo(path);
        var key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{track.SegmentStart}|{track.SegmentEnd}";
        if (_cache.TryGetValue(key, out var cached)) return cached;
        using var decoded = AudioDecoderFactory.Open(track);
        var reader = decoded.Reader;
        var duration = reader.TotalTime.TotalSeconds;
        if (!reader.CanSeek || !double.IsFinite(duration) || duration <= 0)
            throw new NotSupportedException("Seekable finite audio is required");
        (double[] Rms, double[] Peaks) ReadWindow(double start, double seconds)
        {
            token.ThrowIfCancellationRequested();
            reader.CurrentTime = TimeSpan.FromSeconds(start);
            if (Math.Abs(reader.CurrentTime.TotalSeconds - start) > .01)
                throw new NotSupportedException("Decoder could not seek accurately");
            var source = reader.ToSampleProvider();
            var channels = source.WaveFormat.Channels;
            var rate = source.WaveFormat.SampleRate;
            if (channels is < 1 or > 8 || rate is < 8000 or > 384000)
                throw new NotSupportedException("Format outside demo analysis limits");
            var frames = Math.Max(1, (int)Math.Round(rate * .05));
            var buffer = new float[frames * channels];
            var envelope = new List<double>();
            var peaks = new List<double>();
            for (var window = 0; window < (int)Math.Floor(seconds / .05); window++)
            {
                token.ThrowIfCancellationRequested();
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = source.Read(buffer, read, buffer.Length - read);
                    if (n == 0) break;
                    read += n;
                    token.ThrowIfCancellationRequested();
                }
                if (read == 0) break;
                double largest = 0;
                double peak = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    double sum = 0; var count = 0;
                    for (var i = channel; i < read; i += channels)
                    {
                        if (!float.IsFinite(buffer[i])) throw new InvalidDataException("Non-finite PCM sample");
                        peak = Math.Max(peak, Math.Abs(buffer[i]));
                        sum += (double)buffer[i] * buffer[i]; count++;
                    }
                    largest = Math.Max(largest, Math.Sqrt(sum / Math.Max(1, count)));
                }
                envelope.Add(20 * Math.Log10(Math.Max(largest, 1e-6)));
                peaks.Add(20 * Math.Log10(Math.Max(peak, 1e-6)));
            }
            return (envelope.ToArray(), peaks.ToArray());
        }
        var head = ReadWindow(0, Math.Min(15, duration));
        var tailSeconds = Math.Floor(Math.Min(20, duration) / .05) * .05;
        var tail = ReadWindow(duration - tailSeconds, tailSeconds);
        var result = new BoundaryEnvelope(
            head.Rms,
            tail.Rms,
            duration,
            HeadPeakDb: head.Peaks,
            TailPeakDb: tail.Peaks);
        token.ThrowIfCancellationRequested();
        if (_cache.Count >= 32) _cache.Remove(_keys.Dequeue());
        _cache[key] = result; _keys.Enqueue(key);
        return result;
    }

    private static double[] TrimStart(double[] values, double seconds, double step)
    {
        var count = Math.Min(values.Length - 1, Math.Max(0, (int)Math.Round(seconds / step)));
        return values.Skip(count).ToArray();
    }

    private static double[] TrimEnd(double[] values, double seconds, double step)
    {
        var count = Math.Min(values.Length - 1, Math.Max(0, (int)Math.Round(seconds / step)));
        return values.Take(values.Length - count).ToArray();
    }
}
