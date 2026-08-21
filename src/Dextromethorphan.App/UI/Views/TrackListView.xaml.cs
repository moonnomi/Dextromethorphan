using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Models;
using Microsoft.Win32;

namespace Dextromethorphan.App.UI.Views;

public partial class TrackListView : UserControl
{
    private Point _dragStart;
    public TrackListView()
    {
        InitializeComponent();
        DataContextChanged += TrackListView_DataContextChanged;
    }

    public event RoutedEventHandler? TrackListReady;
    public event ScrollChangedEventHandler? TrackScrollChanged;

    private void TrackListView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldViewModel)
            oldViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (e.NewValue is MainViewModel viewModel)
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Dispatcher.BeginInvoke(ApplyVisibleRows, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.TrackColumnOrder) or nameof(MainViewModel.VisibleTrackColumns) or nameof(MainViewModel.TrackColumnWidths))
            Dispatcher.BeginInvoke(ApplyVisibleRows, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void TrackRow_Loaded(object sender, RoutedEventArgs e) => ApplyRowLayout(sender as Grid);

    private void ApplyVisibleRows()
    {
        foreach (var item in TrackList.Items)
        {
            if (TrackList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container
                && FindVisualChild<Grid>(container) is { } row)
                ApplyRowLayout(row);
        }
    }

    private void ApplyRowLayout(Grid? row)
    {
        if (row is null || DataContext is not MainViewModel viewModel || row.ColumnDefinitions.Count < 6) return;
        var defaults = new[] { "Track", "Title", "Album", "Quality", "Rating", "Duration" };
        var order = viewModel.TrackColumnOrder
            .Where(column => defaults.Contains(column, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Concat(defaults)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(defaults.Length)
            .ToArray();
        var children = row.Children.Cast<UIElement>().ToArray();
        var semanticIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Track"] = 0, ["Title"] = 1, ["Album"] = 2, ["Quality"] = 3, ["Rating"] = 4, ["Duration"] = 5
        };
        var defaultWidths = new GridLength[]
        {
            new(36), new(1.8, GridUnitType.Star), new(1.05, GridUnitType.Star), new(146), new(112), new(58)
        };
        for (var position = 0; position < order.Length; position++)
        {
            var key = order[position];
            var child = children[semanticIndex[key]];
            Grid.SetColumn(child, position);
            var definition = row.ColumnDefinitions[position];
            definition.Width = !IsColumnVisible(viewModel, key)
                ? new GridLength(0)
                : viewModel.TrackColumnWidths.TryGetValue(key, out var width)
                    ? new GridLength(width)
                    : defaultWidths[semanticIndex[key]];
        }
    }

    private static bool IsColumnVisible(MainViewModel viewModel, string key) => key switch
    {
        "Title" => viewModel.VisibleTrackColumns.Contains("Title", StringComparer.OrdinalIgnoreCase) || viewModel.VisibleTrackColumns.Contains("Artist", StringComparer.OrdinalIgnoreCase),
        "Album" => viewModel.VisibleTrackColumns.Contains("Album", StringComparer.OrdinalIgnoreCase) || viewModel.VisibleTrackColumns.Contains("Year", StringComparer.OrdinalIgnoreCase),
        "Quality" => viewModel.VisibleTrackColumns.Contains("Quality", StringComparer.OrdinalIgnoreCase) || viewModel.VisibleTrackColumns.Contains("Codec", StringComparer.OrdinalIgnoreCase) || viewModel.VisibleTrackColumns.Contains("Source", StringComparer.OrdinalIgnoreCase),
        _ => viewModel.VisibleTrackColumns.Contains(key, StringComparer.OrdinalIgnoreCase)
    };

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void TrackList_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SetSelectedTracks(TrackList.SelectedItems.OfType<Track>());
        TrackListReady?.Invoke(TrackList, e);
    }

    private void TrackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SetSelectedTracks(TrackList.SelectedItems.OfType<Track>());
    }

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

    private void TrackList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
        {
            TrackList.SelectAll();
            if (DataContext is MainViewModel viewModel) viewModel.SetSelectedTracks(TrackList.SelectedItems.OfType<Track>());
            e.Handled = true;
        }
    }

    private void TrackList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragStart = e.GetPosition(TrackList); return; }
        var point = e.GetPosition(TrackList);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var tracks = TrackList.SelectedItems.OfType<Track>().ToArray();
        if (tracks.Length == 0) return;
        DragDrop.DoDragDrop(TrackList, new DataObject("Dextromethorphan.TrackPaths", tracks.Select(track => track.Path).ToArray()), DragDropEffects.Copy);
    }

    private void TrackList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("Dextromethorphan.TrackPaths") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void TrackList_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || !e.Data.GetDataPresent("Dextromethorphan.TrackPaths")) return;
        var paths = e.Data.GetData("Dextromethorphan.TrackPaths") as string[] ?? [];
        if (viewModel.IsPlaylistView && viewModel.SelectedCard?.PlaylistId is not null)
        {
            var target = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
            var index = target is null ? TrackList.Items.Count : TrackList.ItemContainerGenerator.IndexFromContainer(target);
            var isReorder = paths.Any(path => viewModel.BrowseTracks.Any(track => track.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));
            if (isReorder) await viewModel.MoveTracksInPlaylistAsync(viewModel.SelectedCard, paths, index);
            else await viewModel.AddPathsToPlaylistAsync(viewModel.SelectedCard, paths);
        }
        else
        {
            viewModel.AddPathsToQueue(paths);
        }
        e.Handled = true;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        TrackList.SelectAll();
        if (DataContext is MainViewModel viewModel) viewModel.SetSelectedTracks(TrackList.SelectedItems.OfType<Track>());
    }

    private async void RatingStar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Track track } button
            || DataContext is not MainViewModel viewModel
            || !int.TryParse(button.Tag?.ToString(), out var rating))
            return;
        e.Handled = true;
        await viewModel.SetTrackRatingAsync(track, track.Rating == rating ? 0 : rating);
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
