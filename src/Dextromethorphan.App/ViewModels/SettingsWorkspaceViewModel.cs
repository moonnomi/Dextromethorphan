using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.App.ViewModels;

/// <summary>
/// Owns the Settings-window editing session. Visual preferences are previewed
/// live; signal-path, metadata, shortcut, and per-view changes remain drafts
/// until the user explicitly applies them.
/// </summary>
public sealed class SettingsWorkspaceViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions ShortcutJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly ISettingsService _settings;
    private readonly IShortcutService _shortcuts;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _liveSaveCancellation;
    private bool _loading;
    private string _theme = "Dark";
    private string _accentColor = "#8290FF";
    private string _fontFamily = "Segoe UI Variable Text";
    private double _interfaceFontSize = 14;
    private double _backgroundOpacity = 1;
    private int _queuePanelWidth = 320;
    private bool _queuePanelCompact;
    private PanelDockSide _queuePanelDockSide = PanelDockSide.Right;
    private bool _fullscreenHideNavigation = true;
    private string _appearanceStatus = "Visual changes apply immediately.";
    private PlaybackDraft _playbackBaseline = PlaybackDraft.Default;
    private bool _resumeOnStartup = true;
    private bool _resumeTrackBookmarks = true;
    private bool _stopAfterCurrent;
    private bool _stopAfterQueue;
    private TransitionMode _transitionMode = TransitionMode.Gapless;
    private CrossfadeShape _crossfadeShape = new();
    public IReadOnlyList<CrossfadeCurve> CrossfadeCurves { get; } = Enum.GetValues<CrossfadeCurve>();
    public CrossfadeShape CrossfadeShape => _crossfadeShape;
    public bool SkipTrailingSilence
    {
        get => _crossfadeShape.SkipTrailingSilence;
        set => SetCrossfadeShape(_crossfadeShape with { SkipTrailingSilence = value });
    }
    public bool DynamicCrossfadeEnabled
    {
        get => _crossfadeShape.DynamicEnabled;
        set => SetCrossfadeShape(_crossfadeShape with { DynamicEnabled = value });
    }
    public CrossfadeCurve CrossfadeCurve
    {
        get => _crossfadeShape.Curve;
        set => SetCrossfadeShape(_crossfadeShape with { Curve = value });
    }
    public double CrossfadeIncomingPower
    {
        get => _crossfadeShape.IncomingPower;
        set => SetCrossfadeShape(_crossfadeShape with { IncomingPower = value, Curve = CrossfadeCurve.Custom });
    }
    public double CrossfadeOutgoingPower
    {
        get => _crossfadeShape.OutgoingPower;
        set => SetCrossfadeShape(_crossfadeShape with { OutgoingPower = value, Curve = CrossfadeCurve.Custom });
    }
    private void SetCrossfadeShape(CrossfadeShape shape)
    {
        if (!SetPlayback(ref _crossfadeShape, shape.Normalize(), nameof(CrossfadeShape))) return;
        Raise(nameof(CrossfadeCurve));
        Raise(nameof(CrossfadeIncomingPower));
        Raise(nameof(CrossfadeOutgoingPower));
        Raise(nameof(DynamicCrossfadeEnabled));
        Raise(nameof(SkipTrailingSilence));
    }
    private double _crossfadeSeconds;
    private double _fadeInSeconds;
    private double _fadeOutSeconds;
    private ReplayGainMode _replayGainMode = ReplayGainMode.Track;
    private double _replayGainPreampDb;
    private bool _preventClipping = true;
    private double _playbackSpeed = 1;
    private double _pitchSemitones;
    private bool _preservePitch = true;
    private double _seekStepSeconds = 5;
    private double _volumeStep = .05;
    private string _playbackStatus = "No unapplied playback changes.";
    private MetadataDraft _metadataBaseline = MetadataDraft.Default;
    private string _multiValueSeparators = ";/";
    private MetadataWriteMode _defaultMetadataWriteMode;
    private bool _metadataLookupEnabled;
    private bool _musicBrainzLookupEnabled;
    private bool _discogsLookupEnabled;
    private string _discogsUserToken = "";
    private int _metadataCacheDays = 30;
    private string _metadataStatus = "No unapplied metadata changes.";
    private string _shortcutBaseline = "[]";
    private string _shortcutStatus = "Shortcuts are ready.";
    private ViewProfileEditorViewModel? _selectedViewProfile;
    private bool _validatingShortcuts;

    public SettingsWorkspaceViewModel(
        ISettingsService settings,
        IShortcutService shortcuts,
        MainViewModel main)
    {
        _settings = settings;
        _shortcuts = shortcuts;
        _main = main;
        ShortcutActions = ShortcutActionOption.All;
        FontFamilies = Fonts.SystemFontFamilies
            .Select(font => font.Source)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        foreach (var view in SettingsOptionCoverage.ConfigurableViews)
        {
            var profile = new ViewProfileEditorViewModel(view);
            profile.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ViewProfileEditorViewModel.IsDirty))
                    Raise(nameof(HasUnappliedChanges));
            };
            ViewProfiles.Add(profile);
        }
        SelectedViewProfile = ViewProfiles[0];
    }

    public IReadOnlyList<string> ThemeOptions { get; } = ["Dark", "Light", "Amoled"];
    public IReadOnlyList<string> FontFamilies { get; }
    public IReadOnlyList<TransitionMode> TransitionModes { get; } = Enum.GetValues<TransitionMode>();
    public IReadOnlyList<ReplayGainMode> ReplayGainModes { get; } = Enum.GetValues<ReplayGainMode>();
    public IReadOnlyList<MetadataWriteMode> MetadataWriteModes { get; } = Enum.GetValues<MetadataWriteMode>();
    public IReadOnlyList<LibraryDensity> DensityOptions { get; } = Enum.GetValues<LibraryDensity>();
    public IReadOnlyList<string> SortOptions { get; } = ["Title", "Artist", "Album", "Year", "Duration", "Date added", "Last played", "Play count", "Rating", "Codec"];
    public IReadOnlyList<ShortcutActionOption> ShortcutActions { get; }
    public ObservableCollection<ShortcutBindingEditorViewModel> ShortcutBindings { get; } = [];
    public ObservableCollection<ViewProfileEditorViewModel> ViewProfiles { get; } = [];

    public string Theme
    {
        get => _theme;
        set
        {
            var normalized = ThemeOptions.Contains(value) ? value : "Dark";
            if (Set(ref _theme, normalized)) ApplyLiveAppearance();
        }
    }

    public string AccentColor
    {
        get => _accentColor;
        set
        {
            var normalized = NormalizeAccent(value);
            if (!Set(ref _accentColor, normalized)) return;
            ApplyLiveAppearance();
        }
    }

    public string FontFamily
    {
        get => _fontFamily;
        set
        {
            var requested = value?.Trim() ?? "";
            var normalized = FontFamilies.FirstOrDefault(font =>
                    font.Equals(requested, StringComparison.CurrentCultureIgnoreCase))
                ?? "Segoe UI Variable Text";
            if (Set(ref _fontFamily, normalized)) ApplyLiveAppearance();
        }
    }

    public double InterfaceFontSize
    {
        get => _interfaceFontSize;
        set
        {
            var normalized = Math.Clamp(double.IsFinite(value) ? value : 14, 11, 20);
            if (Set(ref _interfaceFontSize, normalized)) ApplyLiveAppearance();
        }
    }

    public double BackgroundOpacity
    {
        get => _backgroundOpacity;
        set
        {
            var normalized = Math.Clamp(double.IsFinite(value) ? value : 1, .72, 1);
            if (Set(ref _backgroundOpacity, normalized)) ApplyLiveAppearance();
        }
    }

    public int QueuePanelWidth
    {
        get => _queuePanelWidth;
        set
        {
            var normalized = Math.Clamp(value, 280, 520);
            if (!Set(ref _queuePanelWidth, normalized)) return;
            Raise(nameof(QueuePanelWidthText));
            ApplyLiveAppearance();
        }
    }

    public string QueuePanelWidthText => $"{QueuePanelWidth} px";

    public bool QueuePanelCompact
    {
        get => _queuePanelCompact;
        set
        {
            if (!Set(ref _queuePanelCompact, value)) return;
            _main.QueuePanelCompact = value;
            ApplyLiveAppearance();
        }
    }

    public PanelDockSide QueuePanelDockSide
    {
        get => _queuePanelDockSide;
        set
        {
            var normalized = Enum.IsDefined(value)
                ? value
                : PanelDockSide.Right;
            if (!Set(ref _queuePanelDockSide, normalized)) return;
            _main.QueuePanelDockSide = normalized;
            ApplyLiveAppearance();
        }
    }

    public IReadOnlyList<PanelDockSide> PanelDockSides { get; } =
        Enum.GetValues<PanelDockSide>();

    public bool FullscreenHideNavigation
    {
        get => _fullscreenHideNavigation;
        set
        {
            if (Set(ref _fullscreenHideNavigation, value))
                ApplyLiveAppearance();
        }
    }

    public string AppearanceStatus
    {
        get => _appearanceStatus;
        private set => Set(ref _appearanceStatus, value);
    }

    public bool ResumeOnStartup { get => _resumeOnStartup; set => SetPlayback(ref _resumeOnStartup, value); }
    public bool ResumeTrackBookmarks { get => _resumeTrackBookmarks; set => SetPlayback(ref _resumeTrackBookmarks, value); }
    public bool StopAfterCurrent
    {
        get => _stopAfterCurrent;
        set
        {
            if (!SetPlayback(ref _stopAfterCurrent, value)) return;
            if (value && _stopAfterQueue)
            {
                _stopAfterQueue = false;
                Raise(nameof(StopAfterQueue));
            }
        }
    }

    public bool StopAfterQueue
    {
        get => _stopAfterQueue;
        set
        {
            if (!SetPlayback(ref _stopAfterQueue, value)) return;
            if (value && _stopAfterCurrent)
            {
                _stopAfterCurrent = false;
                Raise(nameof(StopAfterCurrent));
            }
        }
    }

    public TransitionMode TransitionMode { get => _transitionMode; set { if (SetPlayback(ref _transitionMode, value)) Raise(nameof(TransitionSummary)); } }
    public double CrossfadeSeconds { get => _crossfadeSeconds; set { if (SetPlayback(ref _crossfadeSeconds, Clamp(value, 0, 10, 0))) Raise(nameof(TransitionSummary)); } }
    public string TransitionSummary => TransitionMode == TransitionMode.Gapless
        ? "Gapless selected — automatic crossfade is off. The duration below is saved for Crossfade mode."
        : CrossfadeSeconds <= 0 ? "Crossfade duration is zero — increase it to overlap queued tracks."
        : $"Automatic crossfade: up to {CrossfadeSeconds:0.##} seconds between queued tracks. Pressing Next starts the next track immediately. Output profiles can override duration.";
    public double FadeInSeconds { get => _fadeInSeconds; set => SetPlayback(ref _fadeInSeconds, Clamp(value, 0, 10, 0)); }
    public double FadeOutSeconds { get => _fadeOutSeconds; set => SetPlayback(ref _fadeOutSeconds, Clamp(value, 0, 10, 0)); }
    public ReplayGainMode ReplayGainMode { get => _replayGainMode; set => SetPlayback(ref _replayGainMode, value); }
    public double ReplayGainPreampDb { get => _replayGainPreampDb; set => SetPlayback(ref _replayGainPreampDb, Clamp(value, -20, 20, 0)); }
    public bool PreventClipping { get => _preventClipping; set => SetPlayback(ref _preventClipping, value); }
    public double PlaybackSpeed { get => _playbackSpeed; set => SetPlayback(ref _playbackSpeed, Clamp(value, .5, 1.5, 1)); }
    public double PitchSemitones { get => _pitchSemitones; set => SetPlayback(ref _pitchSemitones, Clamp(value, -12, 12, 0)); }
    public bool PreservePitch { get => _preservePitch; set => SetPlayback(ref _preservePitch, value); }
    public double SeekStepSeconds { get => _seekStepSeconds; set => SetPlayback(ref _seekStepSeconds, Clamp(value, 1, 60, 5)); }
    public double VolumeStepPercent { get => _volumeStep * 100; set => SetPlayback(ref _volumeStep, Clamp(value / 100, .01, .25, .05)); }
    public bool IsPlaybackDirty => CurrentPlayback() != _playbackBaseline;
    public string PlaybackStatus { get => _playbackStatus; private set => Set(ref _playbackStatus, value); }

    public string MultiValueSeparators { get => _multiValueSeparators; set => SetMetadata(ref _multiValueSeparators, value ?? ""); }
    public MetadataWriteMode DefaultMetadataWriteMode { get => _defaultMetadataWriteMode; set => SetMetadata(ref _defaultMetadataWriteMode, value); }
    public bool MetadataLookupEnabled { get => _metadataLookupEnabled; set => SetMetadata(ref _metadataLookupEnabled, value); }
    public bool MusicBrainzLookupEnabled { get => _musicBrainzLookupEnabled; set => SetMetadata(ref _musicBrainzLookupEnabled, value); }
    public bool DiscogsLookupEnabled { get => _discogsLookupEnabled; set => SetMetadata(ref _discogsLookupEnabled, value); }
    public string DiscogsUserToken { get => _discogsUserToken; set => SetMetadata(ref _discogsUserToken, value ?? ""); }
    public int MetadataCacheDays { get => _metadataCacheDays; set => SetMetadata(ref _metadataCacheDays, Math.Clamp(value, 1, 3650)); }
    public bool IsMetadataDirty => CurrentMetadata() != _metadataBaseline;
    public string MetadataStatus { get => _metadataStatus; private set => Set(ref _metadataStatus, value); }

    public bool HasShortcutErrors => ShortcutBindings.Any(binding => binding.HasError);
    public bool IsShortcutsDirty => ShortcutFingerprint() != _shortcutBaseline;
    public string ShortcutStatus { get => _shortcutStatus; private set => Set(ref _shortcutStatus, value); }
    public bool HasUnappliedChanges => IsPlaybackDirty || IsMetadataDirty || IsShortcutsDirty || ViewProfiles.Any(profile => profile.IsDirty) || _main.HasUnsavedOutputProfileChanges();

    public void NotifyOutputProfileDraftChanged() => Raise(nameof(HasUnappliedChanges));

    public ViewProfileEditorViewModel? SelectedViewProfile
    {
        get => _selectedViewProfile;
        set => Set(ref _selectedViewProfile, value);
    }

    public string ApplicationVersion => typeof(SettingsWorkspaceViewModel).Assembly.GetName().Version?.ToString(3) ?? "Development";
    public string RuntimeDescription => $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSArchitecture}";
    public string BuildCommit
    {
        get
        {
            var informational = typeof(SettingsWorkspaceViewModel).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            var marker = informational?.IndexOf('+') ?? -1;
            if (marker < 0 || informational is null || marker + 1 >= informational.Length)
                return "Local development build";
            var suffix = informational[(marker + 1)..];
            return suffix[..Math.Min(12, suffix.Length)];
        }
    }
    public string AppDataPath => new AppPaths().Root;
    public string LogsPath => new AppPaths().Logs;
    public string LicensesPath => Path.Combine(AppContext.BaseDirectory, "licenses");
    public string UpdateStatus => "Manual updates · no background network checks";
    public string PrivacySummary => "Offline-first, no telemetry, and no provider request without explicit opt-in.";

    public void Reload()
    {
        _loading = true;
        try
        {
            var settings = _settings.Current;
            _theme = settings.Theme;
            _accentColor = NormalizeAccent(settings.AccentColor);
            _fontFamily = FontFamilies.FirstOrDefault(font => font.Equals(settings.FontFamily, StringComparison.CurrentCultureIgnoreCase)) ?? "Segoe UI Variable Text";
            _interfaceFontSize = settings.FontSize;
            _backgroundOpacity = settings.BackgroundOpacity;
            _queuePanelWidth = settings.QueuePanelWidth;
            _queuePanelCompact = settings.QueuePanelCompact;
            _queuePanelDockSide = settings.QueuePanelDockSide;
            _fullscreenHideNavigation = settings.FullscreenHideNavigation;
            RaiseAppearance();
            LoadPlayback(settings);
            LoadMetadata(settings);
            LoadShortcuts(settings.Shortcuts);
            foreach (var profile in ViewProfiles)
                profile.Load(settings.ViewSettings.GetValueOrDefault(profile.ViewName) ?? new ViewSettings());
            SelectedViewProfile ??= ViewProfiles[0];
        }
        finally
        {
            _loading = false;
        }
        Raise(nameof(HasUnappliedChanges));
    }

    public async Task FlushLiveChangesAsync(CancellationToken cancellationToken = default)
    {
        _liveSaveCancellation?.Cancel();
        _liveSaveCancellation?.Dispose();
        _liveSaveCancellation = null;
        await PersistLiveAppearanceAsync(cancellationToken);
    }

    public async Task ApplyPlaybackAsync(CancellationToken cancellationToken = default)
    {
        var draft = CurrentPlayback();
        await _settings.UpdateAsync(settings => draft.Apply(settings), cancellationToken);
        await _main.ApplyPlaybackSettingsFromStoreAsync();
        _playbackBaseline = draft;
        PlaybackStatus = "Playback settings applied.";
        RaiseDirtyState();
    }

    public void RevertPlayback()
    {
        LoadPlayback(_settings.Current);
        PlaybackStatus = "Playback edits reverted.";
    }

    public async Task ApplyMetadataAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(MultiValueSeparators))
            throw new InvalidOperationException("Enter at least one multi-value separator.");
        var draft = CurrentMetadata();
        await _settings.UpdateAsync(settings => draft.Apply(settings), cancellationToken);
        await _main.ApplyMetadataSettingsFromStoreAsync();
        _metadataBaseline = CurrentMetadata();
        MetadataStatus = "Metadata preferences applied.";
        RaiseDirtyState();
    }

    public void RevertMetadata()
    {
        LoadMetadata(_settings.Current);
        MetadataStatus = "Metadata edits reverted.";
    }

    public void AddShortcut()
    {
        AddShortcutEditor(new ShortcutBinding
        {
            Action = Dextromethorphan.Core.Models.ShortcutActions.TogglePlayback,
            Gesture = "Ctrl+Shift+P",
            Enabled = false
        });
        ValidateShortcuts();
    }

    public void RemoveShortcut(ShortcutBindingEditorViewModel? binding)
    {
        if (binding is null) return;
        binding.PropertyChanged -= ShortcutOnPropertyChanged;
        ShortcutBindings.Remove(binding);
        ValidateShortcuts();
    }

    public void ResetShortcutDraft()
    {
        LoadShortcuts(AppSettings.DefaultShortcuts(), acceptChanges: false);
        ShortcutStatus = "Default shortcut preset loaded. Apply to save it.";
    }

    public void RevertShortcuts()
    {
        LoadShortcuts(_settings.Current.Shortcuts);
        ShortcutStatus = "Shortcut edits reverted.";
    }

    public async Task ApplyShortcutsAsync(CancellationToken cancellationToken = default)
    {
        ValidateShortcuts();
        if (HasShortcutErrors)
            throw new InvalidOperationException("Resolve shortcut conflicts and invalid gestures before applying.");
        var bindings = ShortcutBindings.Select(binding => binding.ToBinding()).ToList();
        await _settings.UpdateAsync(settings => settings.Shortcuts = bindings, cancellationToken);
        _shortcuts.Refresh(bindings);
        ApplyRegistrationResults();
        _shortcutBaseline = ShortcutFingerprint();
        ShortcutStatus = _shortcuts.Registrations.Any(result => !result.Registered)
            ? "Saved with one or more Windows registration conflicts."
            : "Shortcuts applied.";
        RaiseDirtyState();
    }

    public async Task ExportShortcutsAsync(string path, CancellationToken cancellationToken = default)
    {
        var bindings = ShortcutBindings.Select(binding => binding.ToBinding()).ToList();
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, true);
        await JsonSerializer.SerializeAsync(stream, bindings, ShortcutJson, cancellationToken);
        ShortcutStatus = "Shortcut preset exported.";
    }

    public async Task ImportShortcutsAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Shortcut preset was not found.", path);
        if (info.Length > 1024 * 1024) throw new InvalidDataException("Shortcut preset exceeds the 1 MiB safety limit.");
        await using var stream = info.OpenRead();
        var bindings = await JsonSerializer.DeserializeAsync<List<ShortcutBinding>>(stream, ShortcutJson, cancellationToken)
            ?? throw new InvalidDataException("Shortcut preset is empty.");
        if (bindings.Count is 0 or > 128) throw new InvalidDataException("Shortcut preset must contain between 1 and 128 bindings.");
        LoadShortcuts(bindings, acceptChanges: false);
        ShortcutStatus = "Shortcut preset loaded. Review conflicts, then apply.";
    }

    public async Task ApplySelectedViewAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedViewProfile is not { } profile) return;
        await _settings.UpdateAsync(settings => settings.ViewSettings[profile.ViewName] = profile.ToSettings(), cancellationToken);
        profile.AcceptChanges();
        _main.ApplyViewSettingsFromStore();
        Raise(nameof(HasUnappliedChanges));
    }

    public void ResetSelectedViewDraft()
    {
        SelectedViewProfile?.ResetToDefaultsDraft();
        Raise(nameof(HasUnappliedChanges));
    }

    public async Task ResetAllViewsAsync(CancellationToken cancellationToken = default)
    {
        await _settings.UpdateAsync(settings => settings.ViewSettings.Clear(), cancellationToken);
        foreach (var profile in ViewProfiles) profile.Load(new ViewSettings());
        _main.ApplyViewSettingsFromStore();
        Raise(nameof(HasUnappliedChanges));
    }

    public void RevertViewDrafts()
    {
        foreach (var profile in ViewProfiles)
            profile.Load(_settings.Current.ViewSettings.GetValueOrDefault(profile.ViewName) ?? new ViewSettings());
        Raise(nameof(HasUnappliedChanges));
    }

    public void MoveSelectedColumn(ViewColumnEditorViewModel? column, int direction)
    {
        if (SelectedViewProfile is null || column is null) return;
        SelectedViewProfile.MoveColumn(column, direction);
        Raise(nameof(HasUnappliedChanges));
    }

    public void RevertAllDrafts()
    {
        RevertPlayback();
        RevertMetadata();
        RevertShortcuts();
        foreach (var profile in ViewProfiles)
            profile.Load(_settings.Current.ViewSettings.GetValueOrDefault(profile.ViewName) ?? new ViewSettings());
        _main.RevertOutputProfileDraft();
        Raise(nameof(HasUnappliedChanges));
    }

    private void ApplyLiveAppearance()
    {
        if (_loading) return;
        AppearanceStatus = "Previewing visual changes…";
        ThemeApplyRequested?.Invoke(this, EventArgs.Empty);
        ScheduleLiveSave();
    }

    public event EventHandler? ThemeApplyRequested;

    private void ScheduleLiveSave()
    {
        _liveSaveCancellation?.Cancel();
        _liveSaveCancellation?.Dispose();
        var cancellation = _liveSaveCancellation = new CancellationTokenSource();
        _ = SaveAfterDelayAsync(cancellation);
    }

    private async Task SaveAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(220, cancellation.Token);
            await PersistLiveAppearanceAsync(cancellation.Token);
            AppearanceStatus = "Visual preferences saved.";
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_liveSaveCancellation, cancellation))
                _liveSaveCancellation = null;
            cancellation.Dispose();
        }
    }

    private Task PersistLiveAppearanceAsync(CancellationToken cancellationToken) =>
        _settings.UpdateAsync(settings =>
        {
            settings.Theme = Theme;
            settings.AccentColor = AccentColor;
            settings.FontFamily = FontFamily;
            settings.FontSize = InterfaceFontSize;
            settings.BackgroundOpacity = BackgroundOpacity;
            settings.QueuePanelWidth = QueuePanelWidth;
            settings.QueuePanelCompact = QueuePanelCompact;
            settings.QueuePanelDockSide = QueuePanelDockSide;
            settings.FullscreenHideNavigation = FullscreenHideNavigation;
        }, cancellationToken);

    private void LoadPlayback(AppSettings settings)
    {
        _resumeOnStartup = settings.ResumeOnStartup;
        _resumeTrackBookmarks = settings.ResumeTrackBookmarks;
        _stopAfterCurrent = settings.StopAfterCurrent;
        _stopAfterQueue = settings.StopAfterQueue;
        _transitionMode = settings.TransitionMode;
        _crossfadeSeconds = settings.CrossfadeSeconds;
        _crossfadeShape = settings.CrossfadeShape;
        _fadeInSeconds = settings.FadeInSeconds;
        _fadeOutSeconds = settings.FadeOutSeconds;
        _replayGainMode = settings.ReplayGainMode;
        _replayGainPreampDb = settings.ReplayGainPreampDb;
        _preventClipping = settings.PreventClipping;
        _playbackSpeed = settings.PlaybackSpeed;
        _pitchSemitones = settings.PitchSemitones;
        _preservePitch = settings.PreservePitch;
        _seekStepSeconds = settings.SeekStepSeconds;
        _volumeStep = settings.VolumeStep;
        _playbackBaseline = CurrentPlayback();
        foreach (var property in PlaybackPropertyNames) Raise(property);
        PlaybackStatus = "No unapplied playback changes.";
        RaiseDirtyState();
    }

    private void LoadMetadata(AppSettings settings)
    {
        _multiValueSeparators = settings.MultiValueSeparators;
        _defaultMetadataWriteMode = settings.DefaultMetadataWriteMode;
        _metadataLookupEnabled = settings.MetadataLookupEnabled;
        _musicBrainzLookupEnabled = settings.MusicBrainzLookupEnabled;
        _discogsLookupEnabled = settings.DiscogsLookupEnabled;
        _discogsUserToken = settings.DiscogsUserToken;
        _metadataCacheDays = settings.MetadataCacheDays;
        _metadataBaseline = CurrentMetadata();
        foreach (var property in MetadataPropertyNames) Raise(property);
        MetadataStatus = "No unapplied metadata changes.";
        RaiseDirtyState();
    }

    private void LoadShortcuts(
        IEnumerable<ShortcutBinding> bindings,
        bool acceptChanges = true)
    {
        var previousBaseline = _shortcutBaseline;
        foreach (var editor in ShortcutBindings)
            editor.PropertyChanged -= ShortcutOnPropertyChanged;
        ShortcutBindings.Clear();
        foreach (var binding in bindings) AddShortcutEditor(binding);
        ValidateShortcuts();
        _shortcutBaseline = acceptChanges
            ? ShortcutFingerprint()
            : previousBaseline;
        RaiseDirtyState();
    }

    private void AddShortcutEditor(ShortcutBinding binding)
    {
        var editor = new ShortcutBindingEditorViewModel(binding);
        editor.PropertyChanged += ShortcutOnPropertyChanged;
        ShortcutBindings.Add(editor);
    }

    private void ShortcutOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_validatingShortcuts
            || e.PropertyName is nameof(ShortcutBindingEditorViewModel.ValidationText)
                or nameof(ShortcutBindingEditorViewModel.HasError))
            return;
        ValidateShortcuts();
    }

    private void ValidateShortcuts()
    {
        if (_validatingShortcuts) return;
        _validatingShortcuts = true;
        try
        {
            var seen = new Dictionary<ShortcutGesture, ShortcutBindingEditorViewModel>();
            foreach (var binding in ShortcutBindings)
            {
                binding.ValidationText = "";
                if (!binding.Enabled) continue;
                if (!ShortcutGesture.TryParse(binding.Gesture, out var gesture))
                {
                    binding.ValidationText = "Press a valid key combination.";
                    continue;
                }
                binding.Gesture = gesture.ToString();
                if (binding.Global
                    && gesture.Modifiers == ShortcutModifiers.None
                    && gesture.VirtualKey is < 0xAD or > 0xB3)
                {
                    binding.ValidationText = "Global shortcuts need a modifier or a media key.";
                    continue;
                }
                if (seen.TryGetValue(gesture, out var conflict))
                {
                    binding.ValidationText = $"Conflicts with {conflict.ActionLabel}.";
                    conflict.ValidationText = $"Conflicts with {binding.ActionLabel}.";
                }
                else
                {
                    seen[gesture] = binding;
                }
            }
        }
        finally
        {
            _validatingShortcuts = false;
        }
        Raise(nameof(HasShortcutErrors));
        RaiseDirtyState();
    }

    private void ApplyRegistrationResults()
    {
        foreach (var result in _shortcuts.Registrations.Where(result => !result.Registered))
        {
            var editor = ShortcutBindings.FirstOrDefault(binding =>
                binding.Action == result.Action
                && binding.Global == result.Global);
            if (editor is not null) editor.ValidationText = result.Error ?? "Windows rejected this shortcut.";
        }
        Raise(nameof(HasShortcutErrors));
    }

    private string ShortcutFingerprint() => JsonSerializer.Serialize(
        ShortcutBindings.Select(binding => binding.ToBinding()),
        ShortcutJson);

    private PlaybackDraft CurrentPlayback() => new(
        ResumeOnStartup,
        ResumeTrackBookmarks,
        StopAfterCurrent,
        StopAfterQueue,
        TransitionMode,
        CrossfadeSeconds,
        CrossfadeShape,
        FadeInSeconds,
        FadeOutSeconds,
        ReplayGainMode,
        ReplayGainPreampDb,
        PreventClipping,
        PlaybackSpeed,
        PitchSemitones,
        PreservePitch,
        SeekStepSeconds,
        _volumeStep);

    private MetadataDraft CurrentMetadata() => new(
        MultiValueSeparators.Trim(),
        DefaultMetadataWriteMode,
        MetadataLookupEnabled,
        MusicBrainzLookupEnabled,
        DiscogsLookupEnabled,
        DiscogsUserToken.Trim(),
        MetadataCacheDays);

    private bool SetPlayback<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name)) return false;
        PlaybackStatus = "Playback changes are waiting to be applied.";
        RaiseDirtyState();
        return true;
    }

    private bool SetMetadata<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name)) return false;
        MetadataStatus = "Metadata changes are waiting to be applied.";
        RaiseDirtyState();
        return true;
    }

    private void RaiseDirtyState()
    {
        Raise(nameof(IsPlaybackDirty));
        Raise(nameof(IsMetadataDirty));
        Raise(nameof(IsShortcutsDirty));
        Raise(nameof(HasUnappliedChanges));
    }

    private void RaiseAppearance()
    {
        Raise(nameof(Theme));
        Raise(nameof(AccentColor));
        Raise(nameof(FontFamily));
        Raise(nameof(InterfaceFontSize));
        Raise(nameof(BackgroundOpacity));
        Raise(nameof(QueuePanelWidth));
        Raise(nameof(QueuePanelWidthText));
        Raise(nameof(QueuePanelCompact));
        Raise(nameof(QueuePanelDockSide));
        Raise(nameof(FullscreenHideNavigation));
    }

    private static string NormalizeAccent(string? value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 9) text = "#" + text[3..];
        if (text.Length == 7
            && text[0] == '#'
            && text[1..].All(character => Uri.IsHexDigit(character)))
            return text.ToUpperInvariant();
        return "#8290FF";
    }

    private static double Clamp(double value, double minimum, double maximum, double fallback) =>
        Math.Clamp(double.IsFinite(value) ? value : fallback, minimum, maximum);

    private static readonly string[] PlaybackPropertyNames =
    [
        nameof(ResumeOnStartup), nameof(ResumeTrackBookmarks), nameof(StopAfterCurrent),
        nameof(StopAfterQueue), nameof(TransitionMode), nameof(CrossfadeSeconds),
        nameof(CrossfadeShape), nameof(CrossfadeCurve), nameof(CrossfadeIncomingPower), nameof(CrossfadeOutgoingPower),
        nameof(DynamicCrossfadeEnabled),
        nameof(TransitionSummary),
        nameof(SkipTrailingSilence),
        nameof(FadeInSeconds), nameof(FadeOutSeconds), nameof(ReplayGainMode),
        nameof(ReplayGainPreampDb), nameof(PreventClipping), nameof(PlaybackSpeed),
        nameof(PitchSemitones), nameof(PreservePitch), nameof(SeekStepSeconds),
        nameof(VolumeStepPercent), nameof(IsPlaybackDirty)
    ];

    private static readonly string[] MetadataPropertyNames =
    [
        nameof(MultiValueSeparators), nameof(DefaultMetadataWriteMode),
        nameof(MetadataLookupEnabled), nameof(MusicBrainzLookupEnabled),
        nameof(DiscogsLookupEnabled), nameof(DiscogsUserToken),
        nameof(MetadataCacheDays), nameof(IsMetadataDirty)
    ];

    private sealed record PlaybackDraft(
        bool ResumeOnStartup,
        bool ResumeTrackBookmarks,
        bool StopAfterCurrent,
        bool StopAfterQueue,
        TransitionMode TransitionMode,
        double CrossfadeSeconds,
        CrossfadeShape CrossfadeShape,
        double FadeInSeconds,
        double FadeOutSeconds,
        ReplayGainMode ReplayGainMode,
        double ReplayGainPreampDb,
        bool PreventClipping,
        double PlaybackSpeed,
        double PitchSemitones,
        bool PreservePitch,
        double SeekStepSeconds,
        double VolumeStep)
    {
        internal static PlaybackDraft Default { get; } = new(
            true, true, false, false, TransitionMode.Gapless, 0, new(), 0, 0,
            ReplayGainMode.Track, 0, true, 1, 0, true, 5, .05);

        internal void Apply(AppSettings settings)
        {
            settings.ResumeOnStartup = ResumeOnStartup;
            settings.ResumeTrackBookmarks = ResumeTrackBookmarks;
            settings.StopAfterCurrent = StopAfterCurrent;
            settings.StopAfterQueue = StopAfterQueue;
            settings.TransitionMode = TransitionMode;
            settings.CrossfadeSeconds = CrossfadeSeconds;
            settings.CrossfadeShape = CrossfadeShape;
            settings.FadeInSeconds = FadeInSeconds;
            settings.FadeOutSeconds = FadeOutSeconds;
            settings.ReplayGainMode = ReplayGainMode;
            settings.ReplayGainPreampDb = ReplayGainPreampDb;
            settings.PreventClipping = PreventClipping;
            settings.PlaybackSpeed = PlaybackSpeed;
            settings.PitchSemitones = PitchSemitones;
            settings.PreservePitch = PreservePitch;
            settings.SeekStepSeconds = SeekStepSeconds;
            settings.VolumeStep = VolumeStep;
        }
    }

    private sealed record MetadataDraft(
        string MultiValueSeparators,
        MetadataWriteMode DefaultMetadataWriteMode,
        bool MetadataLookupEnabled,
        bool MusicBrainzLookupEnabled,
        bool DiscogsLookupEnabled,
        string DiscogsUserToken,
        int MetadataCacheDays)
    {
        internal static MetadataDraft Default { get; } = new(
            ";/", MetadataWriteMode.DatabaseOnly, false, false, false, "", 30);

        internal void Apply(AppSettings settings)
        {
            settings.MultiValueSeparators = MultiValueSeparators;
            settings.DefaultMetadataWriteMode = DefaultMetadataWriteMode;
            settings.MetadataLookupEnabled = MetadataLookupEnabled;
            settings.MusicBrainzLookupEnabled = MusicBrainzLookupEnabled;
            settings.DiscogsLookupEnabled = DiscogsLookupEnabled;
            settings.DiscogsUserToken = DiscogsUserToken;
            settings.MetadataCacheDays = MetadataCacheDays;
        }
    }
}

public sealed class ShortcutBindingEditorViewModel : ObservableObject
{
    private string _action;
    private string _gesture;
    private bool _global;
    private bool _enabled;
    private string _validationText = "";

    public ShortcutBindingEditorViewModel(ShortcutBinding binding)
    {
        _action = binding.Action;
        _gesture = binding.Gesture;
        _global = binding.Global;
        _enabled = binding.Enabled;
    }

    public string Action { get => _action; set { if (Set(ref _action, value)) Raise(nameof(ActionLabel)); } }
    public string ActionLabel => ShortcutActionOption.LabelFor(Action);
    public string Gesture { get => _gesture; set => Set(ref _gesture, value ?? ""); }
    public bool Global { get => _global; set => Set(ref _global, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string ValidationText { get => _validationText; set { if (Set(ref _validationText, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ValidationText);

    public ShortcutBinding ToBinding() => new()
    {
        Action = Action,
        Gesture = Gesture,
        Global = Global,
        Enabled = Enabled
    };
}

public sealed record ShortcutActionOption(string Action, string Label)
{
    public static IReadOnlyList<ShortcutActionOption> All { get; } =
    [
        new(Dextromethorphan.Core.Models.ShortcutActions.TogglePlayback, "Play or pause"),
        new(Dextromethorphan.Core.Models.ShortcutActions.Play, "Play"),
        new(Dextromethorphan.Core.Models.ShortcutActions.Pause, "Pause"),
        new(Dextromethorphan.Core.Models.ShortcutActions.Stop, "Stop"),
        new(Dextromethorphan.Core.Models.ShortcutActions.Next, "Next track"),
        new(Dextromethorphan.Core.Models.ShortcutActions.Previous, "Previous track"),
        new(Dextromethorphan.Core.Models.ShortcutActions.SeekForward, "Seek forward"),
        new(Dextromethorphan.Core.Models.ShortcutActions.SeekBackward, "Seek backward"),
        new(Dextromethorphan.Core.Models.ShortcutActions.VolumeUp, "Volume up"),
        new(Dextromethorphan.Core.Models.ShortcutActions.VolumeDown, "Volume down"),
        new(Dextromethorphan.Core.Models.ShortcutActions.RatingUp, "Increase rating"),
        new(Dextromethorphan.Core.Models.ShortcutActions.RatingDown, "Decrease rating"),
        new(Dextromethorphan.Core.Models.ShortcutActions.Love, "Toggle love"),
        new(Dextromethorphan.Core.Models.ShortcutActions.Search, "Focus library search"),
        new(Dextromethorphan.Core.Models.ShortcutActions.UndoQueue, "Undo queue change")
    ];

    public static string LabelFor(string action) =>
        All.FirstOrDefault(option => option.Action.Equals(action, StringComparison.OrdinalIgnoreCase))?.Label
        ?? action;
}

public sealed class ViewProfileEditorViewModel : ObservableObject
{
    private string _sortBy = "Title";
    private bool _sortDescending;
    private LibraryDensity _density;
    private int _coverSize = 172;
    private string _quickFilter = "";
    private string _baseline = "";

    public ViewProfileEditorViewModel(string viewName)
    {
        ViewName = viewName;
        foreach (var column in SettingsOptionCoverage.TrackColumns)
        {
            var editor = new ViewColumnEditorViewModel(column);
            editor.PropertyChanged += (_, _) => MarkDirty();
            Columns.Add(editor);
        }
    }

    public string ViewName { get; }
    public string SortBy { get => _sortBy; set { if (Set(ref _sortBy, value)) MarkDirty(); } }
    public bool SortDescending { get => _sortDescending; set { if (Set(ref _sortDescending, value)) MarkDirty(); } }
    public LibraryDensity Density { get => _density; set { if (Set(ref _density, value)) MarkDirty(); } }
    public int CoverSize { get => _coverSize; set { if (Set(ref _coverSize, Math.Clamp(value, 80, 400))) MarkDirty(); } }
    public string QuickFilter { get => _quickFilter; set { if (Set(ref _quickFilter, value ?? "")) MarkDirty(); } }
    public ObservableCollection<ViewColumnEditorViewModel> Columns { get; } = [];
    public bool IsDirty => Fingerprint() != _baseline;

    public void Load(ViewSettings settings)
    {
        _sortBy = settings.SortBy;
        _sortDescending = settings.SortDescending;
        _density = settings.Density;
        _coverSize = settings.CoverSize;
        _quickFilter = settings.QuickFilter;
        var order = settings.ColumnOrder
            .Concat(SettingsOptionCoverage.TrackColumns)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var byName = Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        Columns.Clear();
        foreach (var name in order)
        {
            if (!byName.TryGetValue(name, out var column)) continue;
            column.IsVisible = settings.VisibleColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
            column.Width = settings.ColumnWidths.GetValueOrDefault(name, ViewColumnEditorViewModel.DefaultWidth(name));
            Columns.Add(column);
        }
        Raise(nameof(SortBy));
        Raise(nameof(SortDescending));
        Raise(nameof(Density));
        Raise(nameof(CoverSize));
        Raise(nameof(QuickFilter));
        _baseline = Fingerprint();
        Raise(nameof(IsDirty));
    }

    public void ResetToDefaultsDraft()
    {
        var baseline = _baseline;
        Load(new ViewSettings());
        _baseline = baseline;
        Raise(nameof(IsDirty));
    }

    public ViewSettings ToSettings() => new()
    {
        SortBy = SortBy,
        SortDescending = SortDescending,
        Density = Density,
        CoverSize = CoverSize,
        QuickFilter = QuickFilter.Trim(),
        VisibleColumns = Columns.Where(column => column.IsVisible).Select(column => column.Name).ToList(),
        ColumnOrder = Columns.Select(column => column.Name).ToList(),
        ColumnWidths = Columns.ToDictionary(column => column.Name, column => column.Width, StringComparer.OrdinalIgnoreCase)
    };

    public void AcceptChanges()
    {
        _baseline = Fingerprint();
        Raise(nameof(IsDirty));
    }

    public void MarkDirty() => Raise(nameof(IsDirty));

    public void MoveColumn(ViewColumnEditorViewModel column, int direction)
    {
        var index = Columns.IndexOf(column);
        var destination = Math.Clamp(index + Math.Sign(direction), 0, Columns.Count - 1);
        if (index < 0 || index == destination) return;
        Columns.Move(index, destination);
        MarkDirty();
    }

    private string Fingerprint() => JsonSerializer.Serialize(ToSettings());
}

public sealed class ViewColumnEditorViewModel : ObservableObject
{
    private bool _isVisible;
    private double _width;

    public ViewColumnEditorViewModel(string name)
    {
        Name = name;
        _isVisible = name is "Track" or "Title" or "Artist" or "Album" or "Quality" or "Rating" or "Duration";
        _width = DefaultWidth(name);
    }

    public string Name { get; }
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }
    public double Width { get => _width; set => Set(ref _width, Math.Clamp(double.IsFinite(value) ? value : DefaultWidth(Name), 40, 800)); }

    internal static double DefaultWidth(string name) => name switch
    {
        "Title" => 300,
        "Artist" or "Album" => 180,
        "Quality" => 150,
        _ => 84
    };
}

public static class SettingsOptionCoverage
{
    public static IReadOnlyList<string> ConfigurableViews { get; } =
    [
        "Albums", "Artists", "Genres", "Songs", "Folders", "Playlists",
        "Favorites", "Missing", "Recently Added", "Recently Played",
        "Most Played", "Never Played", "History", "Now Playing"
    ];

    public static IReadOnlyList<string> TrackColumns { get; } =
    ["Track", "Title", "Artist", "Album", "Quality", "Rating", "Duration", "Year", "Codec", "Source"];

    public static IReadOnlySet<string> InternalProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(AppSettings.SchemaVersion),
        nameof(AppSettings.LibraryFolders),
        nameof(AppSettings.SearchHistory),
        nameof(AppSettings.TrackPlaybackOverrides),
        nameof(AppSettings.LyricOffsetsMilliseconds),
        nameof(AppSettings.SelectedLyricFiles),
        nameof(AppSettings.SelectedOnlineLyrics),
        nameof(AppSettings.KeyBindings),
        nameof(AppSettings.PlaybackSession)
    };

    public static IReadOnlySet<string> UserFacingProperties { get; } = new HashSet<string>(
        typeof(AppSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Except(InternalProperties),
        StringComparer.Ordinal);
}
