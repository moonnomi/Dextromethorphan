namespace Dextromethorphan.Core.Models;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Theme { get; set; } = "Dark";
    public string AccentColor { get; set; } = "#FF8A3D";
    public string FontFamily { get; set; } = "Segoe UI Variable Text";
    public double FontSize { get; set; } = 14;
    public bool AnimationsEnabled { get; set; } = true;
    public bool ResumeOnStartup { get; set; } = true;
    public bool StopAfterCurrent { get; set; }
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
    public double Volume { get; set; } = 0.82;
    public int AlbumTileSize { get; set; } = 172;
    public int ArtworkCacheMegabytes { get; set; } = 512;
    public bool QueuePanelVisible { get; set; } = true;
    public List<string> LibraryFolders { get; set; } = [];
    public List<string> ExcludedFolders { get; set; } = [];
    public List<AudioOutputProfile> OutputProfiles { get; set; } = [new()];
    public string ActiveOutputDeviceId { get; set; } = "default";
    public double SeekStepSeconds { get; set; } = 5;
    public double VolumeStep { get; set; } = 0.05;
    public List<ShortcutBinding> Shortcuts { get; set; } = DefaultShortcuts();
    public Dictionary<string, string>? KeyBindings { get; set; }

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

public sealed record ScanProgress(int Discovered, int Processed, int Added, int Updated, int Failed, string? CurrentPath, bool IsComplete);
public sealed record LibraryStats(long TrackCount, long AlbumCount, long ArtistCount, TimeSpan TotalDuration);
