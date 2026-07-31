namespace Dextromethorphan.Infrastructure.Audio;

internal static class AudioRecoveryPolicy
{
    public static TimeSpan DelayForAttempt(
        int attempt,
        int initialDelayMilliseconds = 200)
    {
        var initial = Math.Clamp(
            initialDelayMilliseconds,
            50,
            2_000);
        var multiplier = attempt switch
        {
            <= 1 => 1,
            2 => 2.5,
            3 => 5,
            _ => 10 * Math.Pow(
                2,
                Math.Clamp(attempt - 4, 0, 8))
        };
        return TimeSpan.FromMilliseconds(
            Math.Min(5_000, initial * multiplier));
    }

    public static bool IsRecoverable(Exception error) =>
        error.GetBaseException().HResult is
            unchecked((int)0x88890004) or // device invalidated
            unchecked((int)0x8889000A) or // default device changed
            unchecked((int)0x88890010) or // audio service unavailable
            unchecked((int)0x88890026);   // endpoint resources invalidated
}
