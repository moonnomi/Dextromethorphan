namespace Dextromethorphan.Infrastructure.Audio.Dsp;

public sealed record BoundaryEnvelope(
    double[] HeadDb,
    double[] TailDb,
    double DurationSeconds,
    double WindowSeconds = .05,
    double[]? HeadPeakDb = null,
    double[]? TailPeakDb = null);

public sealed record DynamicCrossfadePlan(
    double Seconds,
    string Reason,
    double TrimTrailingSeconds = 0,
    double SkipLeadingSeconds = 0);

/// <summary>Conservative waveform-boundary heuristic; it does not infer beats or musical phrases.</summary>
public static class DynamicCrossfadePlanner
{
    private const double DigitalSilenceDb = -60;

    public static double TrailingSilenceSeconds(BoundaryEnvelope envelope)
    {
        var peaks = envelope.TailPeakDb;
        if (peaks is null || peaks.Length == 0 || peaks.Any(x => !double.IsFinite(x))) return 0;
        var count = 0;
        for (var i = peaks.Length - 1; i >= 0 && peaks[i] < DigitalSilenceDb; i--) count++;
        // Only confidently bounded silence: retain all-silent windows and quiet musical decays.
        if (count == peaks.Length || count * envelope.WindowSeconds < 1) return 0;
        return Math.Max(0, count * envelope.WindowSeconds - .2);
    }

    /// <summary>
    /// Finds a confidently low-energy suffix for smart-fade cueing. Unlike digital
    /// silence removal, this is relative to the track's own tail and therefore only
    /// runs when adaptive crossfade was explicitly enabled. A 500 ms guard preserves
    /// reverb and the located content boundary; an entirely quiet tail is untouched
    /// because there is no trustworthy cue point inside the analysis window.
    /// </summary>
    public static double DynamicTailAdvanceSeconds(BoundaryEnvelope envelope)
    {
        var rms = envelope.TailDb;
        var peaks = envelope.TailPeakDb;
        if (!Usable(rms, envelope.WindowSeconds) || peaks is null || peaks.Length != rms.Length
            || peaks.Any(x => !double.IsFinite(x))) return 0;

        var reference = Percentile(rms, .8);
        if (reference < -45) return 0;
        var rmsThreshold = Math.Max(-50, reference - 30);
        var peakThreshold = Math.Max(-45, reference - 25);
        var lastContent = -1;
        for (var i = 0; i < rms.Length; i++)
            if (rms[i] >= rmsThreshold || peaks[i] >= peakThreshold)
                lastContent = i;

        if (lastContent < 0) return 0;
        var quietSuffix = (rms.Length - lastContent - 1) * envelope.WindowSeconds;
        if (quietSuffix < 1.5) return 0;
        return Math.Min(10, Math.Max(0, quietSuffix - .5));
    }

    /// <summary>Skips only sustained digital or near-digital silence at an incoming boundary.</summary>
    public static double LeadingSilenceSeconds(BoundaryEnvelope envelope)
    {
        var peaks = envelope.HeadPeakDb;
        if (peaks is null || peaks.Length == 0 || peaks.Any(x => !double.IsFinite(x))) return 0;
        var count = 0;
        while (count < peaks.Length && peaks[count] < DigitalSilenceDb) count++;
        // No located attack means there is no safe place to seek to.
        if (count == 0 || count == peaks.Length || count * envelope.WindowSeconds < .5) return 0;
        return Math.Min(10, Math.Max(0, count * envelope.WindowSeconds - .1));
    }

    public static DynamicCrossfadePlan Plan(BoundaryEnvelope outgoing, BoundaryEnvelope incoming, double maximumSeconds)
    {
        var step = outgoing.WindowSeconds;
        if (step <= 0 || !double.IsFinite(step) || step != incoming.WindowSeconds
            || outgoing.TailDb.Length < 10 || incoming.HeadDb.Length < 10
            || outgoing.TailDb.Concat(incoming.HeadDb).Any(x => !double.IsFinite(x)))
            return new(0, "Unusable boundary envelope; gapless fallback");
        var max = Math.Min(double.IsFinite(maximumSeconds) ? Math.Clamp(maximumSeconds, 0, 10) : 0,
            Math.Min(outgoing.DurationSeconds / 4, incoming.DurationSeconds / 4));
        max = Math.Min(max, Math.Min(outgoing.TailDb.Length, incoming.HeadDb.Length) * step);
        if (max < .25) return new(0, "Tracks or maximum overlap too short; gapless");
        static double Threshold(double[] values) => Math.Max(-55, Percentile(values, .8) - 10);
        var outThreshold = Threshold(outgoing.TailDb);
        var inThreshold = Threshold(incoming.HeadDb);
        if (outgoing.TailDb.Max() < -70 || incoming.HeadDb.Max() < -70)
            return new(0, "Silent or near-silent boundary; preserved without trimming");
        // A 250 ms local maximum bridges brief drum gaps and avoids false quiet spots.
        static bool Strong(double[] values, int index, double threshold)
        {
            for (var j = Math.Max(0, index - 2); j <= Math.Min(values.Length - 1, index + 2); j++)
                if (values[j] > threshold) return true;
            return false;
        }
        for (var count = (int)Math.Floor(max / step); count >= 5; count--)
        {
            var collision = 0;
            for (var i = 0; i < count; i++)
                if (Strong(outgoing.TailDb, outgoing.TailDb.Length - count + i, outThreshold)
                    && Strong(incoming.HeadDb, i, inThreshold)) collision++;
            if (collision <= count * .1)
                return new(count * step, $"Energy-matched overlap; <=10% simultaneous strong windows (thresholds {outThreshold:0.#}/{inThreshold:0.#} dBFS)");
        }
        return new(Math.Min(.25, max), "Both boundaries stay strong; short 250ms blend");
    }

    private static bool Usable(double[] values, double step) =>
        step > 0 && double.IsFinite(step) && values.Length >= 10 && values.All(double.IsFinite);

    private static double Percentile(double[] values, double percentile)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Clamp(Math.Round((sorted.Length - 1) * percentile), 0, sorted.Length - 1)];
    }
}
