using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Dextromethorphan.App.ViewModels;
using Dextromethorphan.App.UI;
using Dextromethorphan.Core.Models;
using Microsoft.Win32;

namespace Dextromethorphan.App;

public partial class SettingsWindow : Window
{
    private bool _decoderCheckStarted;
    private bool _allowClose;
    private bool _closeInProgress;

    private static readonly IReadOnlyList<SettingsSearchEntry> SearchIndex =
    [
        new("Output device", "WASAPI endpoint, mode, format, buffer, DSD, and recovery", "Audio"),
        new("Bit-perfect playback", "Exclusive mode, direct path, and reported endpoint formats", "Audio"),
        new("Resume playback", "Startup session and per-track bookmark behavior", "Playback"),
        new("Crossfade and fades", "Gapless, crossfade, fade-in, and fade-out", "Playback"),
        new("ReplayGain", "Track or album normalization, preamp, and clipping guard", "Playback"),
        new("Speed and pitch", "Tempo, pitch preservation, seek, and volume steps", "Playback"),
        new("Music sources", "Local, mounted, network, SMB, watchers, and exclusions", "Library"),
        new("Background scanning", "Schedule, power, and metered-network rules", "Library"),
        new("Artwork cache", "Cache size, clear, rebuild, and duplicate analysis", "Library"),
        new("Metadata editing", "Multi-artist separators and database/file write default", "Metadata"),
        new("MusicBrainz and Discogs", "Opt-in provider lookup, token, and cache lifetime", "Metadata"),
        new("Lyrics display", "Mode, alignment, typography, blur, and karaoke", "Lyrics"),
        new("Online lyrics", "Manual LRCLIB lookup and local cache", "Lyrics"),
        new("Theme and accent", "Dark, Light, AMOLED, accent color, and contrast", "Appearance"),
        new("Font and opacity", "Interface font, size, and surface opacity", "Appearance"),
        new("Queue and fullscreen", "Panel width, visualizer, animations, and navigation", "Appearance"),
        new("Per-view settings", "Sort, density, cover size, quick filter, and columns", "Views"),
        new("Diagnostics", "Decoder check, safe mode, and private diagnostic bundle", "Diagnostics"),
        new("Import, backup, reset", "Portable settings and user-data recovery", "Data"),
        new("Keyboard shortcuts", "Global hotkeys, media keys, presets, and conflicts", "Shortcuts"),
        new("Version and privacy", "Build, runtime, local paths, licenses, and project link", "About")
    ];

    public SettingsWindow() => InitializeComponent();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowMaximizeHelper.Install(this);
    }

    private void SettingsTitleBar_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject source
            && (FindAncestor<TextBoxBase>(source) is not null
                || FindAncestor<ButtonBase>(source) is not null))
            return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }
        DragMove();
    }

    private void SettingsMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void SettingsMaximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void SettingsClose_Click(object sender, RoutedEventArgs e) =>
        Close();

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_decoderCheckStarted || DataContext is not MainViewModel viewModel)
            return;
        _decoderCheckStarted = true;
        viewModel.SettingsWorkspace.Reload();
        await RunAsync(() => viewModel.RefreshDecoderCapabilitiesAsync());
    }

    private async void SettingsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose
            || Application.Current.Dispatcher.HasShutdownStarted)
            return;
        if (DataContext is not MainViewModel viewModel)
        {
            _allowClose = true;
            return;
        }
        e.Cancel = true;
        if (_closeInProgress) return;
        if (viewModel.SettingsWorkspace.HasUnappliedChanges
            && !ConfirmationDialog.Show(
                this,
                "Discard unapplied settings?",
                "Playback, output, metadata, shortcut, or view edits are still drafts. Live appearance changes are already saved.",
                "Discard"))
            return;
        _closeInProgress = true;
        try
        {
            viewModel.SettingsWorkspace.RevertAllDrafts();
            await viewModel.SettingsWorkspace.FlushLiveChangesAsync();
            _allowClose = true;
            Close();
        }
        catch (Exception exception)
        {
            _closeInProgress = false;
            ErrorDialog.Show(this, exception, "", true, "Settings could not finish saving before close.");
        }
    }

    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SettingsSearchBox.Focus();
            SettingsSearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void SettingsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SettingsSearchResults is null || SettingsSearchPopup is null) return;
        var query = SettingsSearchBox.Text.Trim();
        if (query.Length < 2)
        {
            SettingsSearchPopup.IsOpen = false;
            return;
        }
        var results = SearchIndex
            .Where(entry => entry.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || entry.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || entry.TabHeader.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Take(16)
            .ToArray();
        SettingsSearchResults.ItemsSource = results;
        SettingsSearchPopup.IsOpen = results.Length > 0;
    }

    private void SettingsSearchResult_Click(object sender, RoutedEventArgs e)
    {
        var result = sender switch
        {
            FrameworkElement { DataContext: SettingsSearchEntry entry } => entry,
            _ => SettingsSearchResults.SelectedItem as SettingsSearchEntry
        };
        if (result is null) return;
        var tab = SettingsTabs.Items.OfType<TabItem>().FirstOrDefault(item =>
            string.Equals(item.Header?.ToString(), result.TabHeader, StringComparison.Ordinal));
        if (tab is not null)
        {
            SettingsTabs.SelectedItem = tab;
            tab.Focus();
        }
        SettingsSearchPopup.IsOpen = false;
    }

    private async void CheckDecoders_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            await RunAsync(
                () => viewModel.RefreshDecoderCapabilitiesAsync(
                    forceRefresh: true));
    }

    private async void OutputDevice_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel
            {
                IsOutputProfileBusy: false
            } viewModel
            && viewModel.SelectedOutputDevice is { } device)
            await RunAsync(() => viewModel.SelectOutputDeviceAsync(device));
    }

    private async void RefreshOutputs_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            await RunAsync(viewModel.RefreshOutputDevicesAsync);
    }

    private async void SaveOutputProfile_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            await RunAsync(viewModel.SaveOutputProfileAsync, "Audio output profile saved");
    }

    private void RevertOutputProfile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.RevertOutputProfileDraft();
    }

    private async void ApplyPlayback_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            await RunAsync(() => viewModel.SettingsWorkspace.ApplyPlaybackAsync(), "Playback settings applied");
    }

    private void RevertPlayback_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SettingsWorkspace.RevertPlayback();
    }

    private async void ApplyMetadataPreferences_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            await RunAsync(() => viewModel.SettingsWorkspace.ApplyMetadataAsync(), "Metadata settings applied");
    }

    private void RevertMetadataPreferences_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SettingsWorkspace.RevertMetadata();
    }

    private void AccentSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel
            && sender is Button { Tag: string accent })
            viewModel.SettingsWorkspace.AccentColor = accent;
    }

    private async void ApplyViewProfile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            await RunAsync(() => viewModel.SettingsWorkspace.ApplySelectedViewAsync(), "View settings applied");
    }

    private void RevertViewProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SettingsWorkspace.RevertViewDrafts();
    }

    private void ResetViewProfile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SettingsWorkspace.ResetSelectedViewDraft();
    }

    private async void ResetAllViews_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel
            || !ConfirmationDialog.Show(
                this,
                "Reset every library view?",
                "Sorting, density, cover size, filters, columns, order, and widths will return to defaults. Your library is not changed.",
                "Reset views"))
            return;
        await RunAsync(() => viewModel.SettingsWorkspace.ResetAllViewsAsync());
    }

    private void MoveColumnUp_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel
            && sender is FrameworkElement { DataContext: ViewColumnEditorViewModel column })
            viewModel.SettingsWorkspace.MoveSelectedColumn(column, -1);
    }

    private void MoveColumnDown_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel
            && sender is FrameworkElement { DataContext: ViewColumnEditorViewModel column })
            viewModel.SettingsWorkspace.MoveSelectedColumn(column, 1);
    }

    private void AddShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SettingsWorkspace.AddShortcut();
    }

    private void RemoveShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel
            && sender is FrameworkElement { DataContext: ShortcutBindingEditorViewModel binding })
            viewModel.SettingsWorkspace.RemoveShortcut(binding);
    }

    private async void ApplyShortcuts_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            await RunAsync(() => viewModel.SettingsWorkspace.ApplyShortcutsAsync(), "Keyboard shortcuts applied");
    }

    private void RevertShortcuts_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SettingsWorkspace.RevertShortcuts();
    }

    private void ResetShortcuts_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SettingsWorkspace.ResetShortcutDraft();
    }

    private async void ImportShortcuts_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new OpenFileDialog
        {
            Title = "Import shortcut preset",
            Filter = "Dextromethorphan shortcuts|*.json|JSON files|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            await RunAsync(() => viewModel.SettingsWorkspace.ImportShortcutsAsync(dialog.FileName));
    }

    private async void ExportShortcuts_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export shortcut preset",
            Filter = "Dextromethorphan shortcuts|*.json",
            AddExtension = true,
            DefaultExt = ".json",
            FileName = "Dextromethorphan-shortcuts.json"
        };
        if (dialog.ShowDialog(this) == true)
            await RunAsync(() => viewModel.SettingsWorkspace.ExportShortcutsAsync(dialog.FileName));
    }

    private void ShortcutGesture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ShortcutBindingEditorViewModel binding }) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0) return;
        var modifiers = ShortcutModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= ShortcutModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= ShortcutModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= ShortcutModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= ShortcutModifiers.Windows;
        binding.Gesture = new ShortcutGesture(modifiers, virtualKey).ToString();
        e.Handled = true;
    }

    private async void ExportDiagnostics_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export Dextromethorphan diagnostics",
            Filter = "ZIP archive|*.zip",
            AddExtension = true,
            DefaultExt = ".zip",
            FileName =
                $"Dextromethorphan-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog(this) == true)
            await RunAsync(() => viewModel.ExportDiagnosticsAsync(dialog.FileName));
    }

    private async void ExportSettings_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export Dextromethorphan settings",
            Filter = "JSON settings|*.json",
            AddExtension = true,
            DefaultExt = ".json",
            FileName = "Dextromethorphan-settings.json"
        };
        if (dialog.ShowDialog(this) == true)
            await RunAsync(
                () => viewModel.ExportSettingsAsync(dialog.FileName));
    }

    private async void ImportSettings_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new OpenFileDialog
        {
            Title = "Import Dextromethorphan settings",
            Filter = "JSON settings|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true
            || !ConfirmationDialog.Show(
                this,
                "Import settings?",
                "Validated settings from this file will replace the current configuration. Your library database and music files are not changed.",
                "Import"))
            return;
        await RunAsync(
            () => viewModel.ImportSettingsAsync(dialog.FileName));
    }

    private async void ExportBackup_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new SaveFileDialog
        {
            Title = "Back up Dextromethorphan user data",
            Filter = "Dextromethorphan backup|*.dexbackup",
            AddExtension = true,
            DefaultExt = ".dexbackup",
            FileName =
                $"Dextromethorphan-backup-{DateTime.Now:yyyyMMdd}.dexbackup"
        };
        if (dialog.ShowDialog(this) == true)
            await RunAsync(
                () => viewModel.ExportUserDataBackupAsync(
                    dialog.FileName));
    }

    private async void RestoreBackup_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new OpenFileDialog
        {
            Title = "Restore Dextromethorphan user data",
            Filter = "Dextromethorphan backup|*.dexbackup",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true
            || !ConfirmationDialog.Show(
                this,
                "Restore backup?",
                "Settings, playlists, ratings, love state, play history, bookmarks, and the local library index will be replaced. A safety backup of the current database is created first. Music files are never overwritten.",
                "Restore"))
            return;
        await RunAsync(
            () => viewModel.RestoreUserDataBackupAsync(
                dialog.FileName));
    }

    private async void ResetSettings_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel
            || sender is not Button
            {
                Tag: string scopeText
            }
            || !Enum.TryParse<SettingsResetScope>(
                scopeText,
                out var scope)
            || !ConfirmationDialog.Show(
                this,
                $"Reset {scopeText.ToLowerInvariant()} settings?",
                "Only this settings section will return to defaults. The library database and music files are not deleted.",
                "Reset"))
            return;
        await RunAsync(() => viewModel.ResetSettingsAsync(scope));
    }

    private async void FindDuplicates_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            await RunAsync(viewModel.FindContentDuplicatesAsync);
    }

    private async void AddLibrarySource_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new OpenFolderDialog { Title = "Add a music source", Multiselect = false };
        if (dialog.ShowDialog(this) == true)
            await RunAsync(() => viewModel.AddLibraryFolderAsync(dialog.FolderName));
    }

    private async void AddExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new OpenFolderDialog { Title = "Exclude a folder from library scans", Multiselect = false };
        if (dialog.ShowDialog(this) == true)
            await RunAsync(() => viewModel.AddLibraryExclusionAsync(dialog.FolderName));
    }

    private async void RemoveLibrarySource_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel
            || sender is not Button { Tag: LibrarySourceViewModel source }
            || !ConfirmationDialog.Show(
                this,
                "Remove music source?",
                $"{source.Root}\n\nIts entries will be removed from Dextromethorphan. Music files are never deleted.",
                "Remove"))
            return;
        await RunAsync(() => viewModel.RemoveLibrarySourceAsync(source));
    }

    private async void SaveReplayGain_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            await RunAsync(viewModel.SaveReplayGainSettingsAsync);
    }

    private async void AnalyzeReplayGain_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            await RunAsync(viewModel.AnalyzeMissingReplayGainAsync);
    }

    private void CancelReplayGain_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.CancelReplayGainAnalysis();
    }

    private void OpenAppData_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            OpenLocation(viewModel.SettingsWorkspace.AppDataPath);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            OpenLocation(viewModel.SettingsWorkspace.LogsPath);
    }

    private void OpenLicenses_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var requested = viewModel.SettingsWorkspace.LicensesPath;
        OpenLocation(Directory.Exists(requested) ? requested : AppContext.BaseDirectory);
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e) =>
        OpenLocation("https://github.com/moonnomi/Dextromethorphan");

    private void OpenLocation(string location)
    {
        try
        {
            if (!Uri.TryCreate(location, UriKind.Absolute, out var uri)
                || uri.IsFile)
                Directory.CreateDirectory(location);
            Process.Start(new ProcessStartInfo(location)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ErrorDialog.Show(this, exception, "", true, "The requested location could not be opened.");
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private async Task RunAsync(Func<Task> operation, string? successMessage = null)
    {
        try
        {
            await operation();
            if (!string.IsNullOrWhiteSpace(successMessage)
                && DataContext is MainViewModel viewModel)
                viewModel.ShowNotice(successMessage, ToastSeverity.Success);
        }
        catch (Exception exception)
        {
            ErrorDialog.Show(
                this,
                exception,
                "",
                canContinue: true,
                "No files were intentionally deleted. Review the error details and try again.");
        }
    }

    private sealed record SettingsSearchEntry(
        string Title,
        string Description,
        string TabHeader);
}
