namespace Dextromethorphan.Core.Models;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 10;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Theme { get; set; } = "Dark";
    public string AccentColor { get; set; } = "#8290FF";
    public string FontFamily { get; set; } = "Segoe UI Variable Text";
    public double FontSize { get; set; } = 14;
    public double BackgroundOpacity { get; set; } = 1;
    public bool AnimationsEnabled { get; set; } = true;
    public bool VisualizerEnabled { get; set; }
    public bool ResumeOnStartup { get; set; } = true;
    public bool ResumeTrackBookmarks { get; set; } = true;
    public bool StopAfterCurrent { get; set; }
    public bool StopAfterQueue { get; set; }
    public ReplayGainMode ReplayGainMode { get; set; } = ReplayGainMode.Track;
    public double ReplayGainPreampDb { get; set; }
    public bool PreventClipping { get; set; } = true;
    public TransitionMode TransitionMode { get; set; } = TransitionMode.Gapless;
    public double CrossfadeSeconds { get; set; }
    public double FadeInSeconds { get; set; }
    public double FadeOutSeconds { get; set; }
    public double PlaybackSpeed { get; set; } = 1;
    public double PitchSemitones { get; set; }
    public bool PreservePitch { get; set; } = true;
    public Dictionary<string, TrackPlaybackOverrideSettings> TrackPlaybackOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double Volume { get; set; } = 0.82;
    public int AlbumTileSize { get; set; } = 172;
    public Dictionary<string, ViewSettings> ViewSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Modules shown in the focused Now Playing/dashboard surface.  Keeping
    /// this as a small ordered list makes the layout portable and lets the UI
    /// add modules without another settings schema migration.
    /// </summary>
    public List<string> DashboardModules { get; set; } = ["Artwork", "Lyrics", "Metadata"];
    public List<string> SearchHistory { get; set; } = [];
    public int ArtworkCacheMegabytes { get; set; } = 512;
    public bool QueuePanelVisible { get; set; } = true;
    public int QueuePanelWidth { get; set; } = 320;
    public bool QueuePanelCompact { get; set; }
    public PanelDockSide QueuePanelDockSide { get; set; } = PanelDockSide.Right;
    public bool FullscreenHideNavigation { get; set; } = true;
    public List<string> LibraryFolders { get; set; } = [];
    public List<string> ExcludedFolders { get; set; } = [];
    public List<LibrarySourceSettings> LibrarySources { get; set; } = [];
    public bool ScheduledLibraryScanEnabled { get; set; } = true;
    public int ScheduledLibraryScanIntervalMinutes { get; set; } = 60;
    public bool AllowScheduledScanOnBattery { get; set; }
    public bool AllowScheduledScanOnMeteredNetwork { get; set; }
    public string MultiValueSeparators { get; set; } = ";/";
    public MetadataWriteMode DefaultMetadataWriteMode { get; set; } = MetadataWriteMode.DatabaseOnly;
    public bool MetadataLookupEnabled { get; set; }
    public bool MusicBrainzLookupEnabled { get; set; }
    public bool DiscogsLookupEnabled { get; set; }
    public string DiscogsUserToken { get; set; } = "";
    public int MetadataCacheDays { get; set; } = 30;
    public LyricsDisplayMode LyricsDisplayMode { get; set; } = LyricsDisplayMode.Automatic;
    public double LyricsFontSize { get; set; } = 24;
    public LyricsTextAlignment LyricsAlignment { get; set; } = LyricsTextAlignment.Left;
    public double LyricsLineSpacing { get; set; } = 1.15;
    public double LyricsBlurStrength { get; set; } = 5;
    public bool KaraokeWordAnimation { get; set; } = true;
    public Dictionary<string, int> LyricOffsetsMilliseconds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> SelectedLyricFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> SelectedOnlineLyrics { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool OnlineLyricsEnabled { get; set; }
    public List<AudioOutputProfile> OutputProfiles { get; set; } = [new()];
    public string ActiveOutputDeviceId { get; set; } = "default";
    public double SeekStepSeconds { get; set; } = 5;
    public double VolumeStep { get; set; } = 0.05;
    public List<ShortcutBinding> Shortcuts { get; set; } = DefaultShortcuts();
    public Dictionary<string, string>? KeyBindings { get; set; }
    public PlaybackSessionSettings PlaybackSession { get; set; } = new();

    public static Dictionary<string, string> DefaultKeyBindings() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Playback.Toggle"] = "Space",
        ["Playback.Next"] = "Ctrl+Right",
        ["Playback.Previous"] = "Ctrl+Left",
        ["Playback.SeekForward"] = "Right",
        ["Playback.SeekBackward"] = "Left",
        ["Playback.VolumeUp"] = "Ctrl+Up",
        ["Playback.VolumeDown"] = "Ctrl+Down",
        ["Library.Search"] = "Ctrl+F",
        ["Playlist.New"] = "Ctrl+Shift+N",
        ["Track.Love"] = "Ctrl+L"
    };

    public static List<ShortcutBinding> DefaultShortcuts() =>
    [
        new() { Action = ShortcutActions.TogglePlayback, Gesture = "Space" },
        new() { Action = ShortcutActions.Next, Gesture = "Ctrl+Right" },
        new() { Action = ShortcutActions.Previous, Gesture = "Ctrl+Left" },
        new() { Action = ShortcutActions.SeekForward, Gesture = "Right" },
        new() { Action = ShortcutActions.SeekBackward, Gesture = "Left" },
        new() { Action = ShortcutActions.VolumeUp, Gesture = "Ctrl+Up" },
        new() { Action = ShortcutActions.VolumeDown, Gesture = "Ctrl+Down" },
        new() { Action = ShortcutActions.Search, Gesture = "Ctrl+F" },
        new() { Action = ShortcutActions.Love, Gesture = "Ctrl+L" },
        new() { Action = ShortcutActions.UndoQueue, Gesture = "Ctrl+Z" },
        new() { Action = ShortcutActions.TogglePlayback, Gesture = "Ctrl+Alt+Space", Global = true },
        new() { Action = ShortcutActions.Next, Gesture = "Ctrl+Alt+Right", Global = true },
        new() { Action = ShortcutActions.Previous, Gesture = "Ctrl+Alt+Left", Global = true },
        new() { Action = ShortcutActions.VolumeUp, Gesture = "Ctrl+Alt+Up", Global = true },
        new() { Action = ShortcutActions.VolumeDown, Gesture = "Ctrl+Alt+Down", Global = true }
    ];
}

public enum LibraryDensity
{
    Comfortable,
    Compact,
    Grid
}

public enum PanelDockSide
{
    Left,
    Right
}

public sealed class ViewSettings
{
    public string SortBy { get; set; } = "Title";
    public bool SortDescending { get; set; }
    public LibraryDensity Density { get; set; } = LibraryDensity.Comfortable;
    public int CoverSize { get; set; } = 172;
    public string QuickFilter { get; set; } = "";
    public List<string> VisibleColumns { get; set; } = ["Track", "Title", "Artist", "Album", "Quality", "Rating", "Duration"];
    public List<string> ColumnOrder { get; set; } = ["Track", "Title", "Artist", "Album", "Quality", "Rating", "Duration"];
    public Dictionary<string, double> ColumnWidths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LibrarySourceSettings
{
    public string Path { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool WatchEnabled { get; set; } = true;
}

public enum SettingsResetScope
{
    Appearance,
    Playback,
    Library,
    Metadata,
    Shortcuts,
    Session,
    Lyrics,
    All
}

public enum LyricsDisplayMode
{
    Automatic,
    Synced,
    Static
}

public enum LyricsTextAlignment
{
    Left,
    Center,
    Right
}

public sealed class PlaybackSessionSettings
{
    public List<string> QueuePaths { get; set; } = [];
    public List<string> QueueHistoryPaths { get; set; } = [];
    public List<string> ShuffleUpcomingPaths { get; set; } = [];
    public int CurrentIndex { get; set; }
    public double PositionSeconds { get; set; }
    public bool WasPlaying { get; set; }
    public bool Shuffle { get; set; }
    public RepeatMode RepeatMode { get; set; }
    public string LastView { get; set; } = "Albums";
}

public enum ScanLifecycleState
{
    Idle,
    Running,
    Paused,
    Cancelling
}

public enum LibrarySourceKind
{
    Local,
    Removable,
    Network,
    Unknown
}

public sealed record ScanProgress(
    int Discovered,
    int Processed,
    int Added,
    int Updated,
    int Failed,
    string? CurrentPath,
    bool IsComplete,
    ScanLifecycleState State = ScanLifecycleState.Running,
    bool ResumedFromCheckpoint = false);

public sealed record LibrarySourceStatus(
    string Root,
    LibrarySourceKind Kind,
    bool IsOnline,
    bool IsWatching,
    DateTimeOffset? LastSuccessfulScan,
    string? Error,
    long TrackCount = 0);

public sealed record LibraryScanFailure(
    string SourceRoot,
    string Path,
    string Message,
    DateTimeOffset OccurredAt);

public enum LibraryFileChangeKind
{
    AddedOrUpdated,
    Missing,
    Relinked,
    FullRefresh
}

public sealed record LibraryFileChange(
    LibraryFileChangeKind Kind,
    string Path,
    string? PreviousPath = null);

public sealed class LibraryFilesChangedEventArgs(
    IReadOnlyList<LibraryFileChange> changes) : EventArgs
{
    public IReadOnlyList<LibraryFileChange> Changes { get; } = changes;
    public bool RequiresFullRefresh =>
        Changes.Any(change => change.Kind == LibraryFileChangeKind.FullRefresh);
}
public sealed record LibraryStats(long TrackCount, long AlbumCount, long ArtistCount, TimeSpan TotalDuration);
