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
            if (File.Exists(paths.SettingsFile))
            {
                await using var stream = new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, true);
                Current = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _json, cancellationToken) ?? new AppSettings();
            }
            Normalize(Current);
        }
        catch (JsonException)
        {
            var backup = paths.SettingsFile + $".invalid-{DateTimeOffset.Now:yyyyMMddHHmmss}";
            File.Move(paths.SettingsFile, backup, true);
            Current = new AppSettings();
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

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        var temporary = paths.SettingsFile + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, true))
        {
            await JsonSerializer.SerializeAsync(stream, Current, _json, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporary, paths.SettingsFile, true);
    }

    private static void Normalize(AppSettings settings)
    {
        var previousSchema = settings.SchemaVersion;
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        settings.Volume = Math.Clamp(settings.Volume, 0, 1);
        settings.FontSize = Math.Clamp(settings.FontSize, 9, 32);
        settings.AlbumTileSize = Math.Clamp(settings.AlbumTileSize, 80, 400);
        settings.ArtworkCacheMegabytes = Math.Clamp(settings.ArtworkCacheMegabytes, 64, 4096);
        settings.CrossfadeSeconds = Math.Clamp(settings.CrossfadeSeconds, 0, 10);
        settings.FadeInSeconds = Math.Clamp(settings.FadeInSeconds, 0, 10);
        settings.FadeOutSeconds = Math.Clamp(settings.FadeOutSeconds, 0, 10);
        settings.PlaybackSpeed = Math.Clamp(settings.PlaybackSpeed, 0.5, 1.5);
        settings.PitchSemitones = Math.Clamp(settings.PitchSemitones, -12, 12);
        settings.SeekStepSeconds = Math.Clamp(settings.SeekStepSeconds, 1, 60);
        settings.VolumeStep = Math.Clamp(settings.VolumeStep, 0.01, 0.25);
        settings.LibraryFolders = settings.LibraryFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        settings.ExcludedFolders = settings.ExcludedFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        settings.OutputProfiles ??= [new AudioOutputProfile()];
        settings.PlaybackSession ??= new PlaybackSessionSettings();
        settings.PlaybackSession.QueuePaths ??= [];
        settings.PlaybackSession.QueuePaths = settings.PlaybackSession.QueuePaths.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        settings.PlaybackSession.CurrentIndex = Math.Max(0, settings.PlaybackSession.CurrentIndex);
        settings.PlaybackSession.PositionSeconds = Math.Max(0, settings.PlaybackSession.PositionSeconds);
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
            normalized.Add(new ShortcutBinding { Action = binding.Action.Trim(), Gesture = gestureText, Global = binding.Global, Enabled = binding.Enabled });
        }
        settings.Shortcuts = normalized.Count == 0 ? AppSettings.DefaultShortcuts() : normalized;
        settings.KeyBindings = null;
    }
}
