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
        settings.AccentColor = NormalizeColor(settings.AccentColor, "#FF8A3D");
        settings.FontFamily = NormalizeText(
            settings.FontFamily,
            "Segoe UI Variable Text",
            128);
        settings.Volume = FiniteClamp(settings.Volume, 0, 1, 0.82);
        settings.FontSize = FiniteClamp(settings.FontSize, 9, 32, 14);
        settings.AlbumTileSize = Math.Clamp(settings.AlbumTileSize, 80, 400);
        settings.ArtworkCacheMegabytes = Math.Clamp(settings.ArtworkCacheMegabytes, 64, 4096);
        settings.ReplayGainPreampDb = FiniteClamp(settings.ReplayGainPreampDb, -20, 20, 0);
        settings.CrossfadeSeconds = FiniteClamp(settings.CrossfadeSeconds, 0, 10, 0);
        settings.FadeInSeconds = FiniteClamp(settings.FadeInSeconds, 0, 10, 0);
        settings.FadeOutSeconds = FiniteClamp(settings.FadeOutSeconds, 0, 10, 0);
        settings.PlaybackSpeed = FiniteClamp(settings.PlaybackSpeed, 0.5, 1.5, 1);
        settings.PitchSemitones = FiniteClamp(settings.PitchSemitones, -12, 12, 0);
        settings.SeekStepSeconds = FiniteClamp(settings.SeekStepSeconds, 1, 60, 5);
        settings.VolumeStep = FiniteClamp(settings.VolumeStep, 0.01, 0.25, 0.05);
        if (!Enum.IsDefined(settings.ReplayGainMode))
            settings.ReplayGainMode = ReplayGainMode.Track;
        if (!Enum.IsDefined(settings.TransitionMode))
            settings.TransitionMode = TransitionMode.Gapless;
        settings.LibraryFolders = NormalizePaths(settings.LibraryFolders);
        settings.ExcludedFolders = NormalizePaths(settings.ExcludedFolders);
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
                target.AnimationsEnabled = defaults.AnimationsEnabled;
                target.AlbumTileSize = defaults.AlbumTileSize;
                target.QueuePanelVisible = defaults.QueuePanelVisible;
                target.ArtworkCacheMegabytes =
                    defaults.ArtworkCacheMegabytes;
                break;
            case SettingsResetScope.Playback:
                target.ResumeOnStartup = defaults.ResumeOnStartup;
                target.StopAfterCurrent = defaults.StopAfterCurrent;
                target.ReplayGainMode = defaults.ReplayGainMode;
                target.ReplayGainPreampDb = defaults.ReplayGainPreampDb;
                target.PreventClipping = defaults.PreventClipping;
                target.TransitionMode = defaults.TransitionMode;
                target.CrossfadeSeconds = defaults.CrossfadeSeconds;
                target.FadeInSeconds = defaults.FadeInSeconds;
                target.FadeOutSeconds = defaults.FadeOutSeconds;
                target.PlaybackSpeed = defaults.PlaybackSpeed;
                target.PitchSemitones = defaults.PitchSemitones;
                target.PreservePitch = defaults.PreservePitch;
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
                target.ArtworkCacheMegabytes =
                    defaults.ArtworkCacheMegabytes;
                break;
            case SettingsResetScope.Shortcuts:
                target.Shortcuts = defaults.Shortcuts;
                target.KeyBindings = defaults.KeyBindings;
                break;
            case SettingsResetScope.Session:
                target.PlaybackSession = defaults.PlaybackSession;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(scope),
                    scope,
                    null);
        }
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
