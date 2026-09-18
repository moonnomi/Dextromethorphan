namespace Dextromethorphan.Infrastructure.Audio.Dsp;

public sealed record BoundaryEnvelope(double[] HeadDb, double[] TailDb, double DurationSeconds, double WindowSeconds = .05, double[]? TailPeakDb = null);
public sealed record DynamicCrossfadePlan(double Seconds, string Reason, double TrimTrailingSeconds = 0);

/// <summary>Conservative energy-overlap heuristic; does not infer beats or musical phrases.</summary>
public static class DynamicCrossfadePlanner
{
    public static double TrailingSilenceSeconds(BoundaryEnvelope envelope)
    {
        var peaks = envelope.TailPeakDb;
        if (peaks is null || peaks.Length == 0 || peaks.Any(x => !double.IsFinite(x))) return 0;
        var count = 0;
        for (var i = peaks.Length - 1; i >= 0 && peaks[i] < -60; i--) count++;
        // Only confidently bounded silence: retain all-silent windows and quiet musical decays.
        if (count == peaks.Length || count * envelope.WindowSeconds < 1) return 0;
        return Math.Max(0, count * envelope.WindowSeconds - .2);
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
        static double Threshold(double[] values)
        {
            var sorted = values.Order().ToArray();
            return Math.Max(-55, sorted[(int)((sorted.Length - 1) * .8)] - 10);
        }
        var outThreshold = Threshold(outgoing.TailDb);
        var inThreshold = Threshold(incoming.HeadDb);
        if (outgoing.TailDb.Max() < -70 || incoming.HeadDb.Max() < -70)
            return new(0, "Silent or near-silent boundary; preserved without trimming");
        // A 250ms local maximum bridges brief drum gaps and avoids false quiet spots.
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
                return new(count * step, $"Energy-matched overlap; ≤10% simultaneous strong windows (thresholds {outThreshold:0.#}/{inThreshold:0.#} dBFS)");
        }
        return new(Math.Min(.25, max), "Both boundaries stay strong; short 250ms blend");
    }
}
