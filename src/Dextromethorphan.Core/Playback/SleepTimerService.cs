using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Core.Playback;

public sealed class SleepTimerService : ISleepTimerService
{
    private readonly Timer _timer;
    private DateTimeOffset? _deadline;
    private bool _endOfTrack;
    public SleepTimerService() => _timer = new Timer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    public SleepTimerSnapshot Snapshot => new(_deadline is not null || _endOfTrack, _deadline is null ? null : Max(TimeSpan.Zero, _deadline.Value - DateTimeOffset.UtcNow), _endOfTrack);
    public event EventHandler<SleepTimerSnapshot>? Changed;
    public event EventHandler? Expired;

    public void Start(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        _endOfTrack = false;
        _deadline = DateTimeOffset.UtcNow + duration;
        _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Publish();
    }

    public void StopAtEndOfTrack()
    {
        _deadline = null;
        _endOfTrack = true;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Publish();
    }

    public void NotifyTrackEnded()
    {
        if (!_endOfTrack) return;
        Cancel();
        Expired?.Invoke(this, EventArgs.Empty);
    }

    public void Cancel()
    {
        _deadline = null; _endOfTrack = false;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Publish();
    }

    private void OnTick(object? state)
    {
        if (_deadline is null) return;
        if (DateTimeOffset.UtcNow >= _deadline)
        {
            Cancel();
            Expired?.Invoke(this, EventArgs.Empty);
        }
        else Publish();
    }

    private void Publish() => Changed?.Invoke(this, Snapshot);
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    public void Dispose() => _timer.Dispose();
}
