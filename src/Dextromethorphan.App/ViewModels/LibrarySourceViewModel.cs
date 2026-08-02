using Dextromethorphan.Core.Models;
using System.IO;

namespace Dextromethorphan.App.ViewModels;

public sealed class LibrarySourceViewModel : ObservableObject
{
    private bool _enabled;
    private bool _watchEnabled;
    private bool _isOnline;
    private bool _isWatching;
    private DateTimeOffset? _lastSuccessfulScan;
    private string? _error;
    private long _trackCount;

    public LibrarySourceViewModel(LibrarySourceSettings settings, LibrarySourceStatus? status)
    {
        Root = settings.Path;
        Kind = status?.Kind ?? LibrarySourceKind.Unknown;
        _enabled = settings.Enabled;
        _watchEnabled = settings.WatchEnabled;
        Update(status);
    }

    public string Root { get; }
    public LibrarySourceKind Kind { get; private set; }
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) Raise(nameof(EnabledActionText)); } }
    public bool WatchEnabled { get => _watchEnabled; set { if (Set(ref _watchEnabled, value)) Raise(nameof(WatcherActionText)); } }
    public bool IsOnline { get => _isOnline; private set => Set(ref _isOnline, value); }
    public bool IsWatching { get => _isWatching; private set => Set(ref _isWatching, value); }
    public DateTimeOffset? LastSuccessfulScan { get => _lastSuccessfulScan; private set => Set(ref _lastSuccessfulScan, value); }
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public long TrackCount { get => _trackCount; private set => Set(ref _trackCount, value); }
    public string EnabledActionText => Enabled ? "Disable" : "Enable";
    public string WatcherActionText => WatchEnabled ? "Stop watching" : "Watch changes";
    public string StateText => !Enabled ? "Disabled" : !IsOnline ? "Offline" : IsWatching ? "Watching" : "Ready";

    public void Update(LibrarySourceStatus? status)
    {
        if (status is not null) Kind = status.Kind;
        IsOnline = status?.IsOnline ?? Directory.Exists(Root);
        IsWatching = status?.IsWatching == true;
        LastSuccessfulScan = status?.LastSuccessfulScan;
        Error = status?.Error;
        TrackCount = status?.TrackCount ?? TrackCount;
        Raise(nameof(Kind));
        Raise(nameof(StateText));
    }
}
