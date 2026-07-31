using System.Windows;
using System.Windows.Controls;
using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Models;
using Microsoft.Win32;

namespace Dextromethorphan.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

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
            await RunAsync(viewModel.SaveOutputProfileAsync);
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
            await viewModel.ExportDiagnosticsAsync(dialog.FileName);
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

    private async Task RunAsync(Func<Task> operation)
    {
        try
        {
            await operation();
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
}
