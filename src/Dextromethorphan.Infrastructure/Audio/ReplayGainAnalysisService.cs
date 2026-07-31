using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using NAudio.Wave;

namespace Dextromethorphan.Infrastructure.Audio;

public sealed record ReplayGainAnalysisProgress(
    int Completed,
    int Total,
    string CurrentTrack,
    string State);

public sealed record ReplayGainAnalysisSummary(
    int Analyzed,
    int Updated,
    int Failed);

/// <summary>
/// Calculates ReplayGain 2.0 values using an EBU R128 / ITU-R BS.1770
/// integrated-loudness measurement. Results are written only to the local
/// library index; media files are opened read-only.
/// </summary>
public sealed class ReplayGainAnalysisService(
    ILibraryRepository repository,
    IAudioEngine audioEngine)
{
    public async Task<ReplayGainAnalysisSummary> AnalyzeMissingAsync(
        IReadOnlyCollection<Track> tracks,
        IProgress<ReplayGainAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var available = tracks
            .Where(track => !track.IsMissing)
            .ToArray();
        var groups = available
            .GroupBy(AlbumKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.ToArray())
            .Where(group => group.Any(NeedsAnalysis))
            .ToArray();
        var planned = groups.Sum(group =>
            group.Any(track => track.ReplayGainAlbumDb is null)
                ? group.Length
                : group.Count(track =>
                    track.ReplayGainTrackDb is null
                    || track.ReplayPeak is null));
        if (planned == 0)
            return new ReplayGainAnalysisSummary(0, 0, 0);

        var completed = 0;
        var analyzed = 0;
        var updated = 0;
        var failed = 0;
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var needsAlbum = group.Any(
                track => track.ReplayGainAlbumDb is null);
            var toAnalyze = needsAlbum
                ? group
                : group.Where(track =>
                        track.ReplayGainTrackDb is null
                        || track.ReplayPeak is null)
                    .ToArray();
            var results = new Dictionary<string, EbuR128Result>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var track in toAnalyze)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ReplayGainAnalysisProgress(
                    completed,
                    planned,
                    track.Title,
                    IsPlaybackActive()
                        ? "Waiting for playback to pause"
                        : "Analyzing loudness"));
                try
                {
                    var result = await Task.Run(
                        () => AnalyzeTrack(track, cancellationToken),
                        cancellationToken);
                    results[track.Path] = result;
                    analyzed++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    failed++;
                }
                finally
                {
                    completed++;
                }
            }

            var albumResult = needsAlbum
                && results.Count == group.Length
                ? EbuR128Analyzer.Combine(results.Values)
                : null;
            var changes = new List<Track>();
            foreach (var track in group)
            {
                if (!results.TryGetValue(track.Path, out var result))
                    continue;
                var changed = track with
                {
                    ReplayGainTrackDb = track.ReplayGainTrackDb
                        ?? result.ReplayGainDb,
                    ReplayGainAlbumDb = track.ReplayGainAlbumDb
                        ?? albumResult?.ReplayGainDb,
                    ReplayPeak = track.ReplayPeak ?? result.SamplePeak
                };
                changes.Add(changed);
            }
            if (changes.Count > 0)
            {
                await repository.UpsertBatchAsync(
                    changes,
                    cancellationToken);
                updated += changes.Count;
            }
        }

        progress?.Report(new ReplayGainAnalysisProgress(
            completed,
            planned,
            string.Empty,
            "Complete"));
        return new ReplayGainAnalysisSummary(analyzed, updated, failed);
    }

    private EbuR128Result AnalyzeTrack(
        Track track,
        CancellationToken cancellationToken)
    {
        using var decoded = AudioDecoderFactory.Open(track);
        var channels = Math.Clamp(
            decoded.Reader.WaveFormat.Channels,
            1,
            2);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(
            EbuR128Analyzer.SampleRate,
            channels);
        var provider = AudioDecoderFactory.Normalize(decoded, format);
        return EbuR128Analyzer.Analyze(
            provider,
            () => WaitUntilPlaybackIsIdle(cancellationToken),
            cancellationToken);
    }

    private bool IsPlaybackActive() =>
        audioEngine.Snapshot.State is
            Dextromethorphan.Core.Models.PlaybackState.Playing
            or Dextromethorphan.Core.Models.PlaybackState.Buffering;

    private void WaitUntilPlaybackIsIdle(
        CancellationToken cancellationToken)
    {
        while (IsPlaybackActive())
        {
            cancellationToken.ThrowIfCancellationRequested();
            cancellationToken.WaitHandle.WaitOne(
                TimeSpan.FromMilliseconds(200));
        }
    }

    private static bool NeedsAnalysis(Track track) =>
        track.ReplayGainTrackDb is null
        || track.ReplayGainAlbumDb is null
        || track.ReplayPeak is null;

    private static string AlbumKey(Track track)
    {
        var artist = string.IsNullOrWhiteSpace(track.AlbumArtist)
            ? track.Artist
            : track.AlbumArtist;
        return $"{artist.Trim()}\u001f{track.Album.Trim()}";
    }
}

internal sealed record EbuR128Result(
    double IntegratedLufs,
    double ReplayGainDb,
    double SamplePeak,
    IReadOnlyList<double> BlockEnergies,
    long Frames);

internal static class EbuR128Analyzer
{
    public const int SampleRate = 48_000;
    public const double ReplayGainTargetLufs = -18;
    private const int BlockFrames = SampleRate * 4 / 10;
    private const int HopFrames = SampleRate / 10;
    private const double LoudnessOffset = -0.691;
    private static readonly double AbsoluteGateEnergy =
        Math.Pow(10, (-70 - LoudnessOffset) / 10);

    public static EbuR128Result Analyze(
        ISampleProvider source,
        Action? beforeRead = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.WaveFormat.SampleRate != SampleRate)
            throw new ArgumentException(
                $"EBU R128 input must be {SampleRate} Hz.",
                nameof(source));
        var channels = source.WaveFormat.Channels;
        if (channels is < 1 or > 2)
            throw new ArgumentException(
                "EBU R128 analysis currently supports mono and stereo input.",
                nameof(source));

        var filters = Enumerable.Range(0, channels)
            .Select(_ => new KWeightingFilter())
            .ToArray();
        var samples = new float[8192 - (8192 % channels)];
        var energyWindow = new double[BlockFrames];
        var blockEnergies = new List<double>();
        var windowPosition = 0;
        var windowCount = 0;
        var framesSinceBlock = 0;
        var windowEnergy = 0d;
        var samplePeak = 0d;
        long totalFrames = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeRead?.Invoke();
            var read = source.Read(samples, 0, samples.Length);
            if (read == 0) break;
            var frames = read / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var energy = 0d;
                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = samples[frame * channels + channel];
                    samplePeak = Math.Max(samplePeak, Math.Abs(sample));
                    var weighted = filters[channel].Process(sample);
                    energy += weighted * weighted;
                }

                if (windowCount < BlockFrames)
                {
                    energyWindow[windowPosition] = energy;
                    windowEnergy += energy;
                    windowPosition = (windowPosition + 1) % BlockFrames;
                    windowCount++;
                    if (windowCount == BlockFrames)
                    {
                        blockEnergies.Add(windowEnergy / BlockFrames);
                        framesSinceBlock = 0;
                    }
                }
                else
                {
                    windowEnergy -= energyWindow[windowPosition];
                    energyWindow[windowPosition] = energy;
                    windowEnergy += energy;
                    windowPosition = (windowPosition + 1) % BlockFrames;
                    framesSinceBlock++;
                    if (framesSinceBlock == HopFrames)
                    {
                        blockEnergies.Add(windowEnergy / BlockFrames);
                        framesSinceBlock = 0;
                    }
                }
                totalFrames++;
            }
        }

        if (blockEnergies.Count == 0)
            throw new InvalidDataException(
                "Loudness analysis requires at least 400 ms of decoded audio.");
        var integrated = IntegratedLoudness(blockEnergies);
        return new EbuR128Result(
            integrated,
            ReplayGainTargetLufs - integrated,
            samplePeak,
            blockEnergies,
            totalFrames);
    }

    public static EbuR128Result Combine(
        IEnumerable<EbuR128Result> tracks)
    {
        var material = tracks.ToArray();
        var energies = material
            .SelectMany(track => track.BlockEnergies)
            .ToArray();
        if (energies.Length == 0)
            throw new InvalidDataException(
                "Album loudness analysis produced no complete blocks.");
        var integrated = IntegratedLoudness(energies);
        return new EbuR128Result(
            integrated,
            ReplayGainTargetLufs - integrated,
            material.Max(track => track.SamplePeak),
            energies,
            material.Sum(track => track.Frames));
    }

    internal static double IntegratedLoudness(
        IReadOnlyCollection<double> blockEnergies)
    {
        var absoluteGated = blockEnergies
            .Where(energy => energy > AbsoluteGateEnergy)
            .ToArray();
        if (absoluteGated.Length == 0)
            throw new InvalidDataException(
                "Audio is below the EBU R128 absolute gate.");
        var absoluteMean = absoluteGated.Average();
        var relativeGate = absoluteMean / 10d;
        var gated = absoluteGated
            .Where(energy => energy > relativeGate)
            .ToArray();
        var mean = gated.Length == 0
            ? absoluteMean
            : gated.Average();
        return LoudnessOffset + 10 * Math.Log10(mean);
    }

    private sealed class KWeightingFilter
    {
        private readonly Biquad _preFilter = new(
            1.53512485958697,
            -2.69169618940638,
            1.19839281085285,
            -1.69065929318241,
            0.73248077421585);
        private readonly Biquad _rlbFilter = new(
            1,
            -2,
            1,
            -1.99004745483398,
            0.9900722503662);

        public double Process(double sample) =>
            _rlbFilter.Process(_preFilter.Process(sample));
    }

    private sealed class Biquad(
        double b0,
        double b1,
        double b2,
        double a1,
        double a2)
    {
        private double _x1;
        private double _x2;
        private double _y1;
        private double _y2;

        public double Process(double input)
        {
            var output = b0 * input
                         + b1 * _x1
                         + b2 * _x2
                         - a1 * _y1
                         - a2 * _y2;
            _x2 = _x1;
            _x1 = input;
            _y2 = _y1;
            _y1 = output;
            return output;
        }
    }
}
