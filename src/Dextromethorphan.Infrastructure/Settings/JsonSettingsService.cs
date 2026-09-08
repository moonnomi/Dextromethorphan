using System.Text.Json;
using System.Text.Json.Serialization;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Infrastructure.Settings;

public sealed class JsonSettingsService(AppPaths paths) : ISettingsService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public AppSettings Current { get; private set; } = new();
    public event EventHandler<AppSettings>? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RecoverInterruptedSave();
            if (File.Exists(paths.SettingsFile))
                Current = await ReadAsync(paths.SettingsFile, cancellationToken);
            Normalize(Current);
        }
        catch (JsonException)
        {
            var backup = paths.SettingsFile + $".invalid-{DateTimeOffset.Now:yyyyMMddHHmmss}";
            File.Move(paths.SettingsFile, backup, true);
            Current = await TryReadBackupAsync(cancellationToken) ?? new AppSettings();
            Normalize(Current);
        }
        finally { _gate.Release(); }
        await SaveAsync(cancellationToken);
    }

    public async Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            update(Current);
            Normalize(Current);
            await SaveCoreAsync(cancellationToken);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, Current);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await SaveCoreAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task ExportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(path);
        Directory.CreateDirectory(
            Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException(
                "Settings export destination has no parent directory."));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteSettingsFileAsync(
                destination,
                Current,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ImportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(path);
        var info = new FileInfo(source);
        if (!info.Exists)
            throw new FileNotFoundException(
                "Settings import file was not found.",
                source);
        if (info.Length > 4 * 1024 * 1024)
            throw new InvalidDataException(
                "Settings import exceeds the 4 MiB safety limit.");
        var imported = await ReadAsync(source, cancellationToken);
        Normalize(imported);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Current = imported;
            await SaveCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, Current);
    }

    public async Task ResetAsync(
        SettingsResetScope scope,
        CancellationToken cancellationToken = default)
    {
        var defaults = new AppSettings();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (scope == SettingsResetScope.All)
            {
                Current = defaults;
            }
            else
            {
                ApplyReset(Current, defaults, scope);
                Normalize(Current);
            }
            await SaveCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, Current);
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        var temporary = paths.SettingsFile + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, true))
        {
            await JsonSerializer.SerializeAsync(stream, Current, _json, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        var backup = paths.SettingsFile + ".bak";
        if (File.Exists(paths.SettingsFile))
        {
            if (File.Exists(backup)) File.Delete(backup);
            File.Replace(temporary, paths.SettingsFile, backup, ignoreMetadataErrors: true);
        }
        else
            File.Move(temporary, paths.SettingsFile);
    }

    private async Task WriteSettingsFileAsync(
        string destination,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var temporary = destination + ".tmp";
        await using (var stream = new FileStream(
                         temporary,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         true))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                settings,
                _json,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, destination, overwrite: true);
    }

    private async Task<AppSettings> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            true);
        return await JsonSerializer.DeserializeAsync<AppSettings>(
                   stream,
                   _json,
                   cancellationToken)
               ?? new AppSettings();
    }

    private async Task<AppSettings?> TryReadBackupAsync(
        CancellationToken cancellationToken)
    {
        var backup = paths.SettingsFile + ".bak";
        if (!File.Exists(backup)) return null;
        try { return await ReadAsync(backup, cancellationToken); }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private void RecoverInterruptedSave()
    {
        var temporary = paths.SettingsFile + ".tmp";
        if (!File.Exists(temporary)) return;
        if (!File.Exists(paths.SettingsFile))
        {
            File.Move(temporary, paths.SettingsFile);
            return;
        }
        try { File.Delete(temporary); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var previousSchema = settings.SchemaVersion;
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        settings.Theme = settings.Theme?.Trim() switch
        {
            "Dark" => "Dark",
            "Light" => "Light",
            "Amoled" or "AMOLED" => "Amoled",
            _ => "Dark"
        };
        settings.AccentColor = NormalizeOpaqueColor(
            previousSchema < 9
            && string.Equals(
                settings.AccentColor,
                "#FF8A3D",
                StringComparison.OrdinalIgnoreCase)
                ? "#8290FF"
                : settings.AccentColor,
            "#8290FF");
        settings.FontFamily = NormalizeText(
            settings.FontFamily,
            "Segoe UI Variable Text",
            128);
        settings.Volume = FiniteClamp(settings.Volume, 0, 1, 0.82);
        settings.FontSize = FiniteClamp(settings.FontSize, 9, 32, 14);
        settings.BackgroundOpacity = FiniteClamp(
            settings.BackgroundOpacity,
            0.72,
            1,
            1);
        settings.AlbumTileSize = Math.Clamp(settings.AlbumTileSize, 80, 400);
        settings.QueuePanelWidth = Math.Clamp(settings.QueuePanelWidth, 280, 520);
        if (!Enum.IsDefined(settings.QueuePanelDockSide))
            settings.QueuePanelDockSide = PanelDockSide.Right;
        settings.ViewSettings ??= new Dictionary<string, ViewSettings>(StringComparer.OrdinalIgnoreCase);
        var normalizedViews = new Dictionary<string, ViewSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.ViewSettings.Take(32))
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null) continue;
            var view = pair.Value;
            view.SortBy = NormalizeText(view.SortBy, "Title", 64);
            view.CoverSize = Math.Clamp(view.CoverSize, 80, 400);
            view.QuickFilter = NormalizeText(view.QuickFilter, "", 256);
            if (!Enum.IsDefined(view.Density)) view.Density = LibraryDensity.Comfortable;
            view.VisibleColumns = NormalizeColumnNames(view.VisibleColumns);
            view.ColumnOrder = NormalizeColumnNames(view.ColumnOrder);
            view.ColumnWidths = (view.ColumnWidths ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase))
                .Where(item => item.Key.Length <= 64 && double.IsFinite(item.Value))
                .Take(32)
                .ToDictionary(item => item.Key, item => Math.Clamp(item.Value, 40, 800), StringComparer.OrdinalIgnoreCase);
            normalizedViews[pair.Key.Trim()] = view;
        }
        settings.ViewSettings = normalizedViews;
        var dashboardOptions = new[] { "Artwork", "Lyrics", "Metadata" };
        settings.DashboardModules = (settings.DashboardModules ?? [])
            .Where(item => dashboardOptions.Contains(item, StringComparer.OrdinalIgnoreCase))
            .Select(item => dashboardOptions.First(option => option.Equals(item, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (settings.DashboardModules.Count == 0)
            settings.DashboardModules = ["Artwork", "Lyrics", "Metadata"];
        settings.SearchHistory = (settings.SearchHistory ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Where(item => item.Length <= 512)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();
        settings.ArtworkCacheMegabytes = Math.Clamp(settings.ArtworkCacheMegabytes, 64, 4096);
        settings.ReplayGainPreampDb = FiniteClamp(settings.ReplayGainPreampDb, -20, 20, 0);
        settings.CrossfadeSeconds = FiniteClamp(settings.CrossfadeSeconds, 0, 10, 0);
        settings.CrossfadeShape = (settings.CrossfadeShape ?? new()).Normalize();
        settings.FadeInSeconds = FiniteClamp(settings.FadeInSeconds, 0, 10, 0);
        settings.FadeOutSeconds = FiniteClamp(settings.FadeOutSeconds, 0, 10, 0);
        settings.PlaybackSpeed = FiniteClamp(settings.PlaybackSpeed, 0.5, 1.5, 1);
        settings.PitchSemitones = FiniteClamp(settings.PitchSemitones, -12, 12, 0);
        if (settings.StopAfterCurrent && settings.StopAfterQueue)
            settings.StopAfterQueue = false;
        settings.TrackPlaybackOverrides = (settings.TrackPlaybackOverrides
                ?? new Dictionary<string, TrackPlaybackOverrideSettings>(StringComparer.OrdinalIgnoreCase))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            .Take(10_000)
            .Select(pair => new KeyValuePair<string, TrackPlaybackOverrideSettings>(
                pair.Key.Trim(),
                new TrackPlaybackOverrideSettings
                {
                    Speed = FiniteClamp(pair.Value.Speed, 0.5, 1.5, 1),
                    PitchSemitones = FiniteClamp(pair.Value.PitchSemitones, -12, 12, 0),
                    PreservePitch = pair.Value.PreservePitch
                }))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
        settings.SeekStepSeconds = FiniteClamp(settings.SeekStepSeconds, 1, 60, 5);
        settings.VolumeStep = FiniteClamp(settings.VolumeStep, 0.01, 0.25, 0.05);
        if (!Enum.IsDefined(settings.ReplayGainMode))
            settings.ReplayGainMode = ReplayGainMode.Track;
        if (!Enum.IsDefined(settings.TransitionMode))
            settings.TransitionMode = TransitionMode.Gapless;
        settings.LibraryFolders = NormalizePaths(settings.LibraryFolders);
        settings.ExcludedFolders = NormalizePaths(settings.ExcludedFolders);
        settings.LibrarySources ??= [];
        var sources = new List<LibrarySourceSettings>();
        foreach (var source in settings.LibrarySources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Path)) continue;
            string path;
            try { path = Path.GetFullPath(source.Path.Trim()); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (sources.Any(existing => existing.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            sources.Add(new LibrarySourceSettings { Path = path, Enabled = source.Enabled, WatchEnabled = source.WatchEnabled });
        }
        foreach (var path in settings.LibraryFolders)
            if (!sources.Any(source => source.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                sources.Add(new LibrarySourceSettings { Path = path });
        settings.LibrarySources = sources
            .Where(source => !sources.Any(parent =>
                !ReferenceEquals(parent, source)
                && parent.Enabled == source.Enabled
                && parent.WatchEnabled == source.WatchEnabled
                && IsNestedUnder(source.Path, parent.Path)))
            .ToList();
        settings.LibraryFolders = settings.LibrarySources
            .Where(source => source.Enabled)
            .Select(source => source.Path)
            .ToList();
        settings.ScheduledLibraryScanIntervalMinutes = Math.Clamp(settings.ScheduledLibraryScanIntervalMinutes, 15, 1440);
        settings.MultiValueSeparators = NormalizeSeparators(settings.MultiValueSeparators);
        if (!Enum.IsDefined(settings.DefaultMetadataWriteMode))
            settings.DefaultMetadataWriteMode = MetadataWriteMode.DatabaseOnly;
        settings.DiscogsUserToken = NormalizeText(settings.DiscogsUserToken, "", 512);
        settings.MetadataCacheDays = Math.Clamp(settings.MetadataCacheDays, 1, 3650);
        if (!Enum.IsDefined(settings.LyricsDisplayMode)) settings.LyricsDisplayMode = LyricsDisplayMode.Automatic;
        if (!Enum.IsDefined(settings.LyricsAlignment)) settings.LyricsAlignment = LyricsTextAlignment.Left;
        settings.LyricsFontSize = FiniteClamp(settings.LyricsFontSize, 14, 52, 24);
        settings.LyricsLineSpacing = FiniteClamp(settings.LyricsLineSpacing, 0.8, 2, 1.15);
        settings.LyricsBlurStrength = FiniteClamp(settings.LyricsBlurStrength, 0, 20, 5);
        settings.LyricOffsetsMilliseconds = NormalizeLyricOffsets(settings.LyricOffsetsMilliseconds);
        settings.SelectedLyricFiles = NormalizeLyricFiles(settings.SelectedLyricFiles);
        settings.SelectedOnlineLyrics = NormalizeOnlineLyrics(settings.SelectedOnlineLyrics);
        settings.OutputProfiles = NormalizeOutputProfiles(settings.OutputProfiles);
        settings.ActiveOutputDeviceId = NormalizeText(
            settings.ActiveOutputDeviceId,
            settings.OutputProfiles[0].DeviceId,
            1_024);
        if (!settings.OutputProfiles.Any(profile =>
                profile.DeviceId.Equals(
                    settings.ActiveOutputDeviceId,
                    StringComparison.OrdinalIgnoreCase)))
            settings.ActiveOutputDeviceId = settings.OutputProfiles[0].DeviceId;
        settings.PlaybackSession ??= new PlaybackSessionSettings();
        settings.PlaybackSession.QueuePaths ??= [];
        settings.PlaybackSession.QueuePaths = NormalizePaths(
            settings.PlaybackSession.QueuePaths,
            distinct: false);
        settings.PlaybackSession.QueueHistoryPaths = NormalizePaths(
                settings.PlaybackSession.QueueHistoryPaths ?? [],
                distinct: false)
            .Take(100)
            .ToList();
        settings.PlaybackSession.ShuffleUpcomingPaths = NormalizePaths(
            settings.PlaybackSession.ShuffleUpcomingPaths ?? [],
            distinct: false);
        settings.PlaybackSession.CurrentIndex = Math.Clamp(
            settings.PlaybackSession.CurrentIndex,
            0,
            Math.Max(0, settings.PlaybackSession.QueuePaths.Count - 1));
        settings.PlaybackSession.PositionSeconds = FiniteClamp(
            settings.PlaybackSession.PositionSeconds,
            0,
            TimeSpan.MaxValue.TotalSeconds,
            0);
        settings.PlaybackSession.LastView = settings.PlaybackSession.LastView?.Trim() switch
        {
            "Albums" => "Albums",
            "Artists" => "Artists",
            "Genres" => "Genres",
            "Songs" => "Songs",
            "Folders" => "Folders",
            "Playlists" => "Playlists",
            "Favorites" => "Favorites",
            "Now Playing" => "Now Playing",
            _ => "Albums"
        };
        if (!Enum.IsDefined(settings.PlaybackSession.RepeatMode))
            settings.PlaybackSession.RepeatMode = RepeatMode.Off;
        if (previousSchema < 2)
        {
            var migrated = (settings.KeyBindings ?? AppSettings.DefaultKeyBindings())
                .Select(x => new ShortcutBinding { Action = x.Key, Gesture = x.Value })
                .ToList();
            migrated.AddRange(AppSettings.DefaultShortcuts().Where(x => x.Global));
            settings.Shortcuts = migrated;
        }
        settings.Shortcuts ??= AppSettings.DefaultShortcuts();
        var normalized = new List<ShortcutBinding>();
        foreach (var binding in settings.Shortcuts)
        {
            if (string.IsNullOrWhiteSpace(binding.Action) || string.IsNullOrWhiteSpace(binding.Gesture)) continue;
            var gestureText = ShortcutGesture.TryParse(binding.Gesture, out var gesture) ? gesture.ToString() : binding.Gesture.Trim();
            if (gestureText.Length > 128) continue;
            normalized.Add(new ShortcutBinding
            {
                Action = binding.Action.Trim()[..Math.Min(128, binding.Action.Trim().Length)],
                Gesture = gestureText,
                Global = binding.Global,
                Enabled = binding.Enabled
            });
        }
        settings.Shortcuts = (normalized.Count == 0
                ? AppSettings.DefaultShortcuts()
                : normalized)
            .GroupBy(
                binding => (binding.Action, binding.Global),
                ShortcutIdentityComparer.Instance)
            .Select(group => group.Last())
            .ToList();
        settings.KeyBindings = null;
    }

    private static void ApplyReset(
        AppSettings target,
        AppSettings defaults,
        SettingsResetScope scope)
    {
        switch (scope)
        {
            case SettingsResetScope.Appearance:
                target.Theme = defaults.Theme;
                target.AccentColor = defaults.AccentColor;
                target.FontFamily = defaults.FontFamily;
                target.FontSize = defaults.FontSize;
                target.BackgroundOpacity = defaults.BackgroundOpacity;
                target.AnimationsEnabled = defaults.AnimationsEnabled;
                target.VisualizerEnabled = defaults.VisualizerEnabled;
                target.AmbienceEnabled = defaults.AmbienceEnabled;
                target.PlayerArtworkGlow = defaults.PlayerArtworkGlow;
                target.NowPlayingArtworkGlow = defaults.NowPlayingArtworkGlow;
                target.AlbumTileSize = defaults.AlbumTileSize;
                target.ViewSettings = defaults.ViewSettings;
                target.DashboardModules = defaults.DashboardModules;
                target.QueuePanelVisible = defaults.QueuePanelVisible;
                target.QueuePanelWidth = defaults.QueuePanelWidth;
                target.QueuePanelCompact = defaults.QueuePanelCompact;
                target.QueuePanelDockSide = defaults.QueuePanelDockSide;
                target.FullscreenHideNavigation =
                    defaults.FullscreenHideNavigation;
                target.ArtworkCacheMegabytes =
                    defaults.ArtworkCacheMegabytes;
                break;
            case SettingsResetScope.Playback:
                target.ResumeOnStartup = defaults.ResumeOnStartup;
                target.ResumeTrackBookmarks = defaults.ResumeTrackBookmarks;
                target.StopAfterCurrent = defaults.StopAfterCurrent;
                target.StopAfterQueue = defaults.StopAfterQueue;
                target.ReplayGainMode = defaults.ReplayGainMode;
                target.ReplayGainPreampDb = defaults.ReplayGainPreampDb;
                target.PreventClipping = defaults.PreventClipping;
                target.TransitionMode = defaults.TransitionMode;
                target.CrossfadeSeconds = defaults.CrossfadeSeconds;
                target.CrossfadeShape = defaults.CrossfadeShape;
                target.FadeInSeconds = defaults.FadeInSeconds;
                target.FadeOutSeconds = defaults.FadeOutSeconds;
                target.PlaybackSpeed = defaults.PlaybackSpeed;
                target.PitchSemitones = defaults.PitchSemitones;
                target.PreservePitch = defaults.PreservePitch;
                target.TrackPlaybackOverrides = defaults.TrackPlaybackOverrides;
                target.Volume = defaults.Volume;
                target.OutputProfiles = defaults.OutputProfiles;
                target.ActiveOutputDeviceId =
                    defaults.ActiveOutputDeviceId;
                target.SeekStepSeconds = defaults.SeekStepSeconds;
                target.VolumeStep = defaults.VolumeStep;
                break;
            case SettingsResetScope.Library:
                target.LibraryFolders = defaults.LibraryFolders;
                target.ExcludedFolders = defaults.ExcludedFolders;
                target.LibrarySources = defaults.LibrarySources;
                target.ScheduledLibraryScanEnabled = defaults.ScheduledLibraryScanEnabled;
                target.ScheduledLibraryScanIntervalMinutes = defaults.ScheduledLibraryScanIntervalMinutes;
                target.AllowScheduledScanOnBattery = defaults.AllowScheduledScanOnBattery;
                target.AllowScheduledScanOnMeteredNetwork = defaults.AllowScheduledScanOnMeteredNetwork;
                target.ArtworkCacheMegabytes =
                    defaults.ArtworkCacheMegabytes;
                break;
            case SettingsResetScope.Metadata:
                target.MultiValueSeparators = defaults.MultiValueSeparators;
                target.DefaultMetadataWriteMode =
                    defaults.DefaultMetadataWriteMode;
                target.MetadataLookupEnabled =
                    defaults.MetadataLookupEnabled;
                target.MusicBrainzLookupEnabled =
                    defaults.MusicBrainzLookupEnabled;
                target.DiscogsLookupEnabled =
                    defaults.DiscogsLookupEnabled;
                target.DiscogsUserToken = defaults.DiscogsUserToken;
                target.MetadataCacheDays = defaults.MetadataCacheDays;
                break;
            case SettingsResetScope.Shortcuts:
                target.Shortcuts = defaults.Shortcuts;
                target.KeyBindings = defaults.KeyBindings;
                break;
            case SettingsResetScope.Session:
                target.PlaybackSession = defaults.PlaybackSession;
                break;
            case SettingsResetScope.Lyrics:
                target.LyricsDisplayMode = defaults.LyricsDisplayMode;
                target.LyricsFontSize = defaults.LyricsFontSize;
                target.LyricsAlignment = defaults.LyricsAlignment;
                target.LyricsLineSpacing = defaults.LyricsLineSpacing;
                target.LyricsBlurStrength = defaults.LyricsBlurStrength;
                target.KaraokeWordAnimation = defaults.KaraokeWordAnimation;
                target.LyricOffsetsMilliseconds = defaults.LyricOffsetsMilliseconds;
                target.SelectedLyricFiles = defaults.SelectedLyricFiles;
                target.SelectedOnlineLyrics = defaults.SelectedOnlineLyrics;
                target.OnlineLyricsEnabled = defaults.OnlineLyricsEnabled;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(scope),
                    scope,
                    null);
        }
    }

    private static Dictionary<string, int> NormalizeLyricOffsets(Dictionary<string, int>? values)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in (values ?? []).Take(100_000))
        {
            string path;
            try { path = Path.GetFullPath(pair.Key); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            result[path] = Math.Clamp(pair.Value, -600_000, 600_000);
        }
        return result;
    }

    private static Dictionary<string, string> NormalizeLyricFiles(Dictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in (values ?? []).Take(100_000))
        {
            try
            {
                var track = Path.GetFullPath(pair.Key);
                var lyric = Path.GetFullPath(pair.Value);
                if (Path.GetExtension(lyric) is not { } extension
                    || (!extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase)
                        && !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)))
                    continue;
                result[track] = lyric;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return result;
    }

    private static Dictionary<string, long> NormalizeOnlineLyrics(Dictionary<string, long>? values)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in (values ?? []).Take(100_000))
        {
            if (pair.Value <= 0) continue;
            try { result[Path.GetFullPath(pair.Key)] = pair.Value; }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return result;
    }

    private static string NormalizeSeparators(string? value)
    {
        var separators = new string((value ?? ";/")
            .Where(character => character is ';' or '/' or '|' or ',')
            .Distinct()
            .Take(4)
            .ToArray());
        return separators.Length == 0 ? ";/" : separators;
    }

    private static List<string> NormalizeColumnNames(IEnumerable<string>? values)
    {
        var allowed = new HashSet<string>(["Track", "Title", "Artist", "Album", "Quality", "Rating", "Duration", "Year", "Codec", "Source"], StringComparer.OrdinalIgnoreCase);
        return (values ?? ["Track", "Title", "Artist", "Album", "Quality", "Rating", "Duration"])
            .Where(value => allowed.Contains(value))
            .Select(value => allowed.First(item => item.Equals(value, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList();
    }

    private static List<AudioOutputProfile> NormalizeOutputProfiles(
        IEnumerable<AudioOutputProfile?>? profiles)
    {
        var result = new List<AudioOutputProfile>();
        foreach (var profile in profiles ?? [])
        {
            if (profile is null) continue;
            var deviceId = NormalizeText(profile.DeviceId, "", 1_024);
            if (deviceId.Length == 0
                || result.Any(existing => existing.DeviceId.Equals(
                    deviceId,
                    StringComparison.OrdinalIgnoreCase)))
                continue;
            profile.DeviceId = deviceId;
            profile.Name = NormalizeText(
                profile.Name,
                deviceId.Equals("default", StringComparison.OrdinalIgnoreCase)
                    ? "System default"
                    : "Audio output",
                256);
            if (!Enum.IsDefined(profile.Mode))
                profile.Mode = WasapiMode.Shared;
            if (!Enum.IsDefined(profile.DsdMode))
                profile.DsdMode = DsdMode.Disabled;
            if (!Enum.IsDefined(profile.FallbackPolicy))
                profile.FallbackPolicy = OutputFallbackPolicy.SharedMode;
            if (!Enum.IsDefined(profile.SampleRatePolicy))
                profile.SampleRatePolicy = SampleRatePolicy.MatchSource;
            if (!Enum.IsDefined(profile.BitDepthPolicy))
                profile.BitDepthPolicy = BitDepthPolicy.MatchSource;
            if (!Enum.IsDefined(profile.ChannelPolicy))
                profile.ChannelPolicy = ChannelPolicy.DownmixToStereo;
            if (!Enum.IsDefined(profile.VolumeControl))
                profile.VolumeControl = VolumeControlMode.Software;
            if (profile.HardwareVolume)
                profile.VolumeControl = VolumeControlMode.Hardware;
            profile.HardwareVolume =
                profile.VolumeControl == VolumeControlMode.Hardware;
            profile.BufferMilliseconds = Math.Clamp(
                profile.BufferMilliseconds,
                2,
                1_000);
            profile.RecoveryMaximumAttempts = Math.Clamp(
                profile.RecoveryMaximumAttempts,
                1,
                8);
            profile.RecoveryInitialDelayMilliseconds = Math.Clamp(
                profile.RecoveryInitialDelayMilliseconds,
                50,
                2_000);
            profile.PreferredSampleRate =
                profile.PreferredSampleRate == 0
                    ? 0
                    : Math.Clamp(profile.PreferredSampleRate, 8_000, 768_000);
            profile.PreferredBitDepth =
                profile.PreferredBitDepth == 0
                    ? 0
                    : Math.Clamp(profile.PreferredBitDepth, 8, 64);
            profile.CrossfadeSeconds = FiniteClamp(
                profile.CrossfadeSeconds,
                0,
                10,
                0);
            result.Add(profile);
        }
        if (result.Count == 0)
            result.Add(new AudioOutputProfile());
        return result;
    }

    private static List<string> NormalizePaths(
        IEnumerable<string?>? values,
        bool distinct = true)
    {
        var result = new List<string>();
        foreach (var value in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            try
            {
                var trimmed = value.Trim();
                if (!Path.IsPathFullyQualified(trimmed)) continue;
                var path = Path.GetFullPath(trimmed)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (path.Length == 2 && path[1] == ':')
                    path += Path.DirectorySeparatorChar;
                if (!distinct
                    || !result.Contains(path, StringComparer.OrdinalIgnoreCase))
                    result.Add(path);
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
            {
                // Invalid persisted paths are ignored. Valid but currently
                // disconnected paths are deliberately retained.
            }
        }
        return result;
    }

    private static bool IsNestedUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "."
               && relative != ".."
               && !relative.StartsWith(
                   ".." + Path.DirectorySeparatorChar,
                   StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }

    private static string NormalizeText(
        string? value,
        string fallback,
        int maximumLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? fallback
            : new string(value.Trim()
                .Where(character => !char.IsControl(character))
                .ToArray());
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = fallback;
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }

    private static double FiniteClamp(
        double value,
        double minimum,
        double maximum,
        double fallback) =>
        double.IsFinite(value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;

    private static string NormalizeColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var color = value.Trim();
        if (color.Length is not (7 or 9) || color[0] != '#')
            return fallback;
        return color[1..].All(character =>
                character is >= '0' and <= '9'
                    or >= 'a' and <= 'f'
                    or >= 'A' and <= 'F')
            ? color.ToUpperInvariant()
            : fallback;
    }

    private static string NormalizeOpaqueColor(
        string? value,
        string fallback)
    {
        var normalized = NormalizeColor(value, fallback);
        return normalized.Length == 9
            ? "#" + normalized[3..]
            : normalized;
    }

    private sealed class ShortcutIdentityComparer
        : IEqualityComparer<(string Action, bool Global)>
    {
        internal static ShortcutIdentityComparer Instance { get; } = new();

        public bool Equals(
            (string Action, bool Global) x,
            (string Action, bool Global) y) =>
            x.Global == y.Global
            && x.Action.Equals(y.Action, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Action, bool Global) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Action),
                value.Global);
    }
}
