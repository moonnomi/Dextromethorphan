using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Models;
using Microsoft.Win32;

namespace Dextromethorphan.App.UI.Views;

public partial class TrackListView : UserControl
{
    public TrackListView() => InitializeComponent();

    public event RoutedEventHandler? TrackListReady;
    public event ScrollChangedEventHandler? TrackScrollChanged;

    private void TrackList_Loaded(object sender, RoutedEventArgs e) =>
        TrackListReady?.Invoke(TrackList, e);

    private void TrackList_ScrollChanged(
        object sender,
        ScrollChangedEventArgs e) =>
        TrackScrollChanged?.Invoke(TrackList, e);

    private void TrackList_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel
            && viewModel.PlaySelectedCommand.CanExecute(null))
            viewModel.PlaySelectedCommand.Execute(null);
    }

    private async void LocateMissing_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (TrackList.SelectedItem is not Track
            {
                IsMissing: true
            } track
            || DataContext is not MainViewModel viewModel)
            return;

        var dialog = new OpenFileDialog
        {
            Title = $"Locate {track.Title}",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Audio files|*.flac;*.mp3;*.m4a;*.mp4;*.alac;*.wav;*.wave;*.aif;*.aiff;*.dsf;*.dff;*.ogg;*.opus;*.aac;*.wma|All files|*.*"
        };
        var oldDirectory = Path.GetDirectoryName(track.Path);
        if (!string.IsNullOrWhiteSpace(oldDirectory)
            && Directory.Exists(oldDirectory))
            dialog.InitialDirectory = oldDirectory;

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            await viewModel.RelinkMissingTrackAsync(
                track,
                dialog.FileName);
    }
}
