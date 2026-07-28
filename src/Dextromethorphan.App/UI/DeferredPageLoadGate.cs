namespace Dextromethorphan.App.UI;

internal static class DeferredPageLoadGate
{
    internal static async Task<bool> WaitForIdleAsync(
        Func<bool> isAvailable,
        Func<bool> isBusy,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isAvailable);
        ArgumentNullException.ThrowIfNull(isBusy);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);

        do
        {
            // Preserve the caller's dispatcher context: WPF availability and
            // animation predicates must be evaluated on the UI thread.
            await Task.Delay(pollInterval, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        while (isAvailable() && isBusy());

        return isAvailable();
    }
}
