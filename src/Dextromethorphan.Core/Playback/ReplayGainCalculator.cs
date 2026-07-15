using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Core.Playback;

public static class ReplayGainCalculator
{
    public static double GainDecibels(Track track, ReplayGainMode mode, double preampDb, bool preventClipping)
    {
        var taggedGain = mode switch
        {
            ReplayGainMode.Track => track.ReplayGainTrackDb,
            ReplayGainMode.Album => track.ReplayGainAlbumDb ?? track.ReplayGainTrackDb,
            _ => null
        };
        var requested = (taggedGain ?? 0) + preampDb;
        if (!preventClipping || track.ReplayPeak is not > 0) return requested;
        var maximum = -20 * Math.Log10(track.ReplayPeak.Value);
        return Math.Min(requested, maximum);
    }

    public static double LinearGain(Track track, AudioPlaybackOptions options) =>
        Math.Pow(10, GainDecibels(track, options.ReplayGainMode, options.ReplayGainPreampDb, options.PreventClipping) / 20d);
}
