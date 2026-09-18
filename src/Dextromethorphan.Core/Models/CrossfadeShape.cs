namespace Dextromethorphan.Core.Models;

public enum CrossfadeCurve { EqualPower, Linear, Smoothstep, Custom }

/// <summary>Immutable envelope settings shared by audio rendering and its preview.</summary>
public sealed record CrossfadeShape
{
    public bool DynamicEnabled { get; init; }
    public bool SkipTrailingSilence { get; init; }
    public CrossfadeCurve Curve { get; init; } = CrossfadeCurve.EqualPower;
    public double IncomingPower { get; init; } = 1;
    public double OutgoingPower { get; init; } = 1;

    public CrossfadeShape Normalize() => this with
    {
        Curve = Enum.IsDefined(Curve) ? Curve : CrossfadeCurve.EqualPower,
        IncomingPower = double.IsFinite(IncomingPower) ? Math.Clamp(IncomingPower, .25, 4) : 1,
        OutgoingPower = double.IsFinite(OutgoingPower) ? Math.Clamp(OutgoingPower, .25, 4) : 1
    };

    public (double Outgoing, double Incoming) Gains(double progress)
    {
        var t = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        return Curve switch
        {
            CrossfadeCurve.Linear => (1 - t, t),
            CrossfadeCurve.Smoothstep => (1 - t * t * (3 - 2 * t), t * t * (3 - 2 * t)),
            CrossfadeCurve.Custom => (Math.Pow(1 - t, OutgoingPower), Math.Pow(t, IncomingPower)),
            _ => (Math.Cos(t * Math.PI / 2), Math.Sin(t * Math.PI / 2))
        };
    }
}
