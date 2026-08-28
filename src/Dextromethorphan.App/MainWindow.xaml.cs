using System.ComponentModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Shell;
using Dextromethorphan.App.Performance;
using Dextromethorphan.App.Lyrics;
using Dextromethorphan.App.Diagnostics;
using Dextromethorphan.App.UI;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Core.Library;
using Microsoft.Win32;

namespace Dextromethorphan.App;

public partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _isSeekDragging;
    private bool _isVolumeDragging;
    private SettingsWindow? _settingsWindow;
    private CancellationTokenSource? _lyricScrollCancellation;
    private Point _queueDragStart;
    private Point _queueDragGrip;
    private QueueEntryViewModel? _queuePointerEntry;
    private bool _queueDragStarted;
    private Guid[] _queueDraggedIds = [];
    private readonly List<ListBoxItem> _queueDragSourceContainers = [];
    private QueueDragAdorner? _queueDragAdorner;
    private AdornerLayer? _queueDragAdornerLayer;
    private int _queueInsertionPlaybackIndex = -1;
    private DateTimeOffset _lastQueueEdgeScroll = DateTimeOffset.MinValue;
    private Guid? _lastQueueFollowedEntryId;
    private CancellationTokenSource? _queueScrollCancellation;
    private Point _groupCardDragStart;
    private FrameworkElement? _groupCardDragSource;
    private bool _groupCardDragStarted;
    private bool _trackDragPreviewActive;
    private DateTime _startupStartedAt;
    private DateTimeOffset? _firstGalleryArtworkRenderedAt;
    private readonly IShortcutService _shortcuts;
    private readonly ISystemMediaTransportService _systemMedia;
    private readonly DeveloperDiagnostics _diagnostics;
    private readonly ArtworkImageService _artworkImages;
    private readonly ArtworkPropertyUpdateBatcher _artworkUpdates;
    private readonly IMetadataMatchService _metadataMatcher;
    private readonly WindowPlacementService _windowPlacement;
    private readonly NavigationViewStateStore _viewStates = new();
    private readonly Dictionary<RadioButton, int> _topTabAnimationVersions = [];
    private bool _scrollRestorePending;
    private bool _restoringScrollState;
    private CancellationTokenSource? _galleryPageCancellation;
    private CancellationTokenSource? _sidebarPageCancellation;
    private CancellationTokenSource? _trackPageCancellation;
    private int _viewTransitionAnimationVersion;
    private readonly DispatcherTimer _idleCleanupTimer;
    private DateTimeOffset _lastInteraction = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastIdleCleanup = DateTimeOffset.MinValue;
    private bool _idleCleanupRunning;
    private EventHandler? _pendingGalleryScrollReapplication;
    private bool _isFullScreen;
    private bool _fullScreenTransitionRunning;
    private Rect _fullScreenRestoreBounds;
    private WindowState _fullScreenRestoreState;
    private ResizeMode _fullScreenRestoreResizeMode;
    private Grid? _titleBarGrid;
    private FrameworkElement? _titleSearchHost;
    private StackPanel? _titleUtilityHost;
    private UniformGrid? _topTabsHost;
    private FrameworkElement? _collectionDetailTabHost;
    private Button? _maximizeWindowButton;
    private Button? _compactSearchButton;
    private MenuItem? _overflowMissingFilesItem;
    private Border? _queuePanel;
    private Grid? _playerGrid;
    private SpectrumVisualizer? _spectrumVisualizer;
    private IReadOnlyList<Button> _secondaryTitleUtilities = [];

    public MainWindow(
        MainViewModel viewModel,
        IShortcutService shortcuts,
        ISystemMediaTransportService systemMedia,
        DeveloperDiagnostics diagnostics,
        ArtworkImageService artworkImages,
        ArtworkPropertyUpdateBatcher artworkUpdates,
        PerformanceOverlayViewModel performanceOverlay,
        IMetadataMatchService metadataMatcher,
        WindowPlacementService windowPlacement)
    {
        InitializeComponent();
        ViewModel = viewModel;
        _shortcuts = shortcuts;
        _systemMedia = systemMedia;
        _diagnostics = diagnostics;
        _artworkImages = artworkImages;
        _artworkUpdates = artworkUpdates;
        _metadataMatcher = metadataMatcher;
        _windowPlacement = windowPlacement;
        PerformanceOverlay = performanceOverlay;
        PerformanceOverlay.Attach(this);
        DataContext = viewModel;
        QueueList.SelectionMode = SelectionMode.Extended;
        QueueList.SelectionChanged += QueueList_SelectionChanged;
        QueueList.MouseDoubleClick += QueueList_MouseDoubleClick;
        QueueList.PreviewKeyDown += QueueList_PreviewKeyDown;
        QueueList.DragLeave += QueueList_DragLeave;
        ViewModel.Queue.CollectionChanged += QueueCollectionChanged;
        Loaded += ApplyAutomationNames;
        Loaded += MainWindow_Loaded;
        SizeChanged += MainWindow_SizeChanged;
        StateChanged += MainWindow_StateChanged;
        InstallChapterMarkers();
        ViewModel.PropertyChanged += ViewModelOnPropertyChanged;
        ViewModel.NavigationStarting += ViewModelOnNavigationStarting;
        ViewModel.MetadataEditRequested += ViewModel_MetadataEditRequested;
        ViewModel.PlaylistEditRequested += ViewModel_PlaylistEditRequested;
        ViewModel.SearchFocusRequested += ViewModel_SearchFocusRequested;
        AddHandler(ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(ContextMenu_Opening));
        PreviewMouseMove += RecordUserInteraction;
        PreviewMouseWheel += RecordUserInteraction;
        PreviewTouchDown += RecordUserInteraction;
        Activated += RecordWindowActivation;
        _idleCleanupTimer = new DispatcherTimer(
            TimeSpan.FromMinutes(1),
            DispatcherPriority.ApplicationIdle,
            IdleCleanupTimer_Tick,
            Dispatcher);
        _idleCleanupTimer.Start();
    }

    private void ApplyAutomationNames(object sender, RoutedEventArgs e)
    {
        AutomationProperties.SetName(SearchBox, "Search library");
        AutomationProperties.SetName(SeekSlider, "Playback position");
        AutomationProperties.SetName(VolumeSlider, "Volume");
        AutomationProperties.SetName(QueueList, "Playback queue");
        AutomationProperties.SetName(GalleryList, "Library collections");
        AutomationProperties.SetName(SidebarList, "Folders and playlists");
        AutomationProperties.SetName(FolderTreeView, "Music folder tree");
        foreach (var button in FindVisualChildren<Button>(this))
        {
            if (!string.IsNullOrWhiteSpace(
                    AutomationProperties.GetName(button))
                || button.ToolTip is not string tooltip
                || string.IsNullOrWhiteSpace(tooltip))
                continue;
            AutomationProperties.SetName(button, tooltip);
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ResolveResponsiveShellElements();
        InstallCompactTitleBarActions();
        ApplyResponsiveShellLayout();
        UpdateMaximizePresentation();
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) ApplyResponsiveShellLayout();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e) =>
        UpdateMaximizePresentation();

    private void ResolveResponsiveShellElements()
    {
        _titleBarGrid = TitleBarHost.Child as Grid;
        if (_titleBarGrid is not null)
        {
            _titleSearchHost = _titleBarGrid.Children
                .OfType<FrameworkElement>()
                .FirstOrDefault(value => Grid.GetColumn(value) == 2);
            _titleUtilityHost = _titleBarGrid.Children
                .OfType<StackPanel>()
                .FirstOrDefault(value => Grid.GetColumn(value) == 3);
            var navigationHost = _titleBarGrid.Children
                .OfType<Grid>()
                .FirstOrDefault(value => Grid.GetColumn(value) == 1);
            _topTabsHost = navigationHost?.Children.OfType<UniformGrid>().FirstOrDefault();
            _collectionDetailTabHost = navigationHost?.Children
                .OfType<Border>()
                .FirstOrDefault(value => Grid.GetColumn(value) == 1);
        }

        _maximizeWindowButton ??= FindVisualChildren<Button>(this)
            .FirstOrDefault(value =>
            {
                var name = AutomationProperties.GetName(value);
                return name.Contains("Maximize", StringComparison.OrdinalIgnoreCase)
                       || name.Contains("Restore window", StringComparison.OrdinalIgnoreCase);
            });

        _queuePanel = QueueInspectorPanel;

        _playerGrid = ShellRoot.Children
            .OfType<Border>()
            .Where(value => Grid.GetRow(value) == 2)
            .Select(value => value.Child as Grid)
            .FirstOrDefault(value => value?.ColumnDefinitions.Count == 3);
        _spectrumVisualizer = _playerGrid is null
            ? null
            : FindVisualChildren<SpectrumVisualizer>(_playerGrid).FirstOrDefault();
    }

    private void InstallCompactTitleBarActions()
    {
        if (_titleUtilityHost is null || _compactSearchButton is not null) return;

        _compactSearchButton = new Button
        {
            Content = "\uE721",
            ToolTip = "Search library (Ctrl+F)",
            Style = TryFindResource("IconButton") as Style,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(_compactSearchButton, "Search library");
        _compactSearchButton.Click += (_, _) => FocusSearchBox();
        _titleUtilityHost.Children.Insert(0, _compactSearchButton);

        var buttons = _titleUtilityHost.Children.OfType<Button>().ToArray();
        _secondaryTitleUtilities = buttons
            .Where(value => value != _compactSearchButton
                            && value.ToolTip is string tooltip
                            && tooltip is "Favorites" or "Missing files" or "Audio diagnostics")
            .ToArray();
        var more = buttons.FirstOrDefault(value =>
            string.Equals(value.ToolTip as string, "More library views", StringComparison.Ordinal));
        if (more?.ContextMenu is null
            || more.ContextMenu.Items.OfType<MenuItem>().Any(value =>
                Equals(value.Tag, "responsive-title-actions")))
            return;

        more.ContextMenu.Items.Add(new Separator());
        var search = new MenuItem
        {
            Header = "Search library",
            InputGestureText = "Ctrl+F",
            Tag = "responsive-title-actions"
        };
        search.Click += (_, _) => FocusSearchBox();
        more.ContextMenu.Items.Add(search);
        var favorites = new MenuItem { Header = "Favorites" };
        favorites.Click += (_, _) => ViewModel.NavigateCommand.Execute("Favorites");
        more.ContextMenu.Items.Add(favorites);
        _overflowMissingFilesItem = new MenuItem
        {
            Header = "Missing files",
            IsEnabled = ViewModel.HasMissingTracks
        };
        _overflowMissingFilesItem.Click += (_, _) =>
            ViewModel.NavigateCommand.Execute("Missing");
        more.ContextMenu.Items.Add(_overflowMissingFilesItem);
        var diagnostics = new MenuItem { Header = "Audio diagnostics" };
        diagnostics.Click += (_, _) => ViewModel.ToggleDiagnosticsCommand.Execute(null);
        more.ContextMenu.Items.Add(diagnostics);
    }

    private void FocusSearchBox()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void ApplyResponsiveShellLayout()
    {
        if (!IsLoaded || _titleBarGrid is null) return;
        var metrics = ResponsiveShellMetrics.ForWidth(ActualWidth);
        if (_titleSearchHost is not null)
            _titleSearchHost.Visibility = metrics.ShowFullSearch
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (_titleBarGrid.ColumnDefinitions.Count > 2)
            _titleBarGrid.ColumnDefinitions[2].Width = new GridLength(metrics.SearchWidth);
        if (_compactSearchButton is not null)
            _compactSearchButton.Visibility = metrics.ShowFullSearch
                ? Visibility.Collapsed
                : Visibility.Visible;
        foreach (var button in _secondaryTitleUtilities)
        {
            button.Visibility = metrics.ShowSecondaryUtilities
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (_topTabsHost is not null)
        {
            _topTabsHost.Visibility = metrics.WidthClass == ShellWidthClass.Compact
                                      && ViewModel.IsCollectionDetailOpen
                ? Visibility.Collapsed
                : Visibility.Visible;
            foreach (var tab in _topTabsHost.Children.OfType<RadioButton>())
            {
                if (metrics.WidthClass == ShellWidthClass.Compact)
                {
                    tab.SetCurrentValue(
                        Control.PaddingProperty,
                        new Thickness(3, 21, 3, 18));
                    tab.SetCurrentValue(Control.FontSizeProperty, 11d);
                }
                else
                {
                    tab.ClearValue(Control.PaddingProperty);
                    tab.ClearValue(Control.FontSizeProperty);
                }
            }
        }
        if (_collectionDetailTabHost is not null)
            _collectionDetailTabHost.MaxWidth = metrics.WidthClass == ShellWidthClass.Compact
                ? 210
                : double.PositiveInfinity;
        if (_queuePanel is not null)
            _queuePanel.MaxWidth = metrics.QueueMaximumWidth;
        if (_playerGrid?.ColumnDefinitions.Count >= 3)
        {
            _playerGrid.ColumnDefinitions[0].Width =
                new GridLength(metrics.PlayerIdentityWidth);
            _playerGrid.ColumnDefinitions[2].Width =
                new GridLength(metrics.PlayerUtilitiesWidth);
        }
        if (_spectrumVisualizer is not null)
        {
            _spectrumVisualizer.Width = metrics.VisualizerWidth;
            _spectrumVisualizer.Margin = metrics.VisualizerWidth > 0
                ? new Thickness(5, 0, 5, 0)
                : new Thickness(0);
        }
    }

    private void UpdateMaximizePresentation()
    {
        if (_maximizeWindowButton is null) return;
        var maximized = WindowState == WindowState.Maximized;
        _maximizeWindowButton.Content = maximized ? "\uE923" : "\uE922";
        _maximizeWindowButton.ToolTip = maximized ? "Restore" : "Maximize";
        AutomationProperties.SetName(
            _maximizeWindowButton,
            maximized ? "Restore window" : "Maximize window");
    }

    private void HandleMonitorEnvironmentChanged()
    {
        _windowPlacement.EnsureVisible(this);
        ApplyResponsiveShellLayout();
    }

    private async void ViewModel_MetadataEditRequested(object? sender, EventArgs e)
    {
        var request = MetadataEditDialog.Show(
            this,
            ViewModel.SelectedTracks,
            _metadataMatcher,
            ViewModel.SettingsWorkspace.DefaultMetadataWriteMode);
        if (request is null) return;
        try { await ViewModel.ApplyMetadataAsync(ViewModel.SelectedTracks, request.Patch, request.Mode); }
        catch (Exception exception) { ErrorDialog.Show(this, exception, "", true, "Metadata could not be updated"); }
    }

    private void ViewModel_SearchFocusRequested(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private async void ViewModel_PlaylistEditRequested(object? sender, PlaylistEditContext context)
    {
        var result = PlaylistEditDialog.Show(this, context.Existing, context.InitialTracks, ViewModel.AllTracks);
        if (result is null) return;
        try { await ViewModel.SavePlaylistAsync(context.Existing, result.Value.Request, result.Value.InitialTracks); }
        catch (Exception exception) { ErrorDialog.Show(this, exception, "", true, "Playlist could not be saved"); }
    }

    private void SearchBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = SearchBox.ContextMenu ??= new ContextMenu();
        menu.Items.Clear();
        menu.DataContext = ViewModel;
        menu.Items.Add(new MenuItem { Header = string.IsNullOrWhiteSpace(SearchBox.Text) ? "Recent searches" : "Search suggestions", IsEnabled = false });
        foreach (var suggestion in ViewModel.SearchSuggestions)
        {
            var item = new MenuItem { Header = suggestion, Command = ViewModel.UseSearchHistoryCommand, CommandParameter = suggestion, ToolTip = "Use this search" };
            menu.Items.Add(item);
        }
        if (ViewModel.SearchSuggestions.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No saved searches", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Clear search history", Command = ViewModel.ClearSearchHistoryCommand });
    }

    private void ContextMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement source && source.ContextMenu is { } menu)
        {
            menu.DataContext = source.DataContext;
            if (source.DataContext is QueueEntryViewModel entry
                && !menu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "locate-playback")))
            {
                var locate = new MenuItem { Header = "Locate replacement…", Tag = "locate-playback" };
                locate.Click += async (_, _) => await LocateQueueTrackAsync(entry);
                menu.Items.Insert(Math.Min(1, menu.Items.Count), locate);
            }
            if (menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Remove selected")))
            {
                foreach (var old in menu.Items.OfType<MenuItem>().Where(item => Equals(item.Tag, "queue-history")).ToArray())
                    menu.Items.Remove(old);
                var history = new MenuItem { Header = "Recently played", Tag = "queue-history", IsEnabled = ViewModel.QueueHistory.Count > 0 };
                foreach (var played in ViewModel.QueueHistory.Take(20))
                {
                    var item = new MenuItem { Header = $"{played.PlayedAtText}  {played.Track.Title}" };
                    item.Click += async (_, _) => await ViewModel.PlayHistoryTrackAsync(played.Track);
                    history.Items.Add(item);
                }
                menu.Items.Insert(0, history);
            }
        }
    }

    private async Task LocateQueueTrackAsync(QueueEntryViewModel entry)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Locate replacement for {entry.Track.Title}",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Audio files|*.flac;*.mp3;*.m4a;*.mp4;*.alac;*.wav;*.wave;*.aif;*.aiff;*.dsf;*.dff;*.ogg;*.opus;*.aac;*.wma|All files|*.*"
        };
        var directory = Path.GetDirectoryName(entry.Track.Path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) dialog.InitialDirectory = directory;
        if (dialog.ShowDialog(this) != true) return;
        try { await ViewModel.RelinkPlaybackTrackAsync(entry, dialog.FileName); }
        catch (Exception exception) { ErrorDialog.Show(this, exception, "", true, "Track could not be relinked"); }
    }

    private void QueueCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var currentId = ViewModel.Queue.FirstOrDefault(entry => entry.IsPlaying)?.Entry.Id;
        if (currentId is null)
        {
            _lastQueueFollowedEntryId = null;
            return;
        }
        if (_lastQueueFollowedEntryId == currentId) return;
        _lastQueueFollowedEntryId = currentId;
        Dispatcher.BeginInvoke(
            () => _ = FollowCurrentQueueEntryAsync(currentId.Value),
            DispatcherPriority.Loaded);
    }

    private async Task FollowCurrentQueueEntryAsync(Guid? expectedId = null)
    {
        if (!QueueList.IsVisible || _queueDragStarted) return;
        var current = ViewModel.Queue.FirstOrDefault(entry => entry.IsPlaying);
        if (current is null || expectedId is { } id && current.Entry.Id != id) return;

        _queueScrollCancellation?.Cancel();
        _queueScrollCancellation?.Dispose();
        _queueScrollCancellation = new CancellationTokenSource();
        var token = _queueScrollCancellation.Token;
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded, token);
            var viewer = FindVisualChild<ScrollViewer>(QueueList);
            if (viewer is null || viewer.ViewportHeight <= 0) return;
            var start = viewer.VerticalOffset;
            var container = QueueList.ItemContainerGenerator.ContainerFromItem(current) as ListBoxItem;
            if (container is null)
            {
                QueueList.ScrollIntoView(current);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded, token);
                container = QueueList.ItemContainerGenerator.ContainerFromItem(current) as ListBoxItem;
                if (container is null) return;
            }

            var currentTop = container.TranslatePoint(new Point(0, 0), viewer).Y;
            var target = Math.Clamp(viewer.VerticalOffset + currentTop - 2, 0, viewer.ScrollableHeight);
            if (Math.Abs(target - start) < 0.75) return;
            if (Math.Abs(viewer.VerticalOffset - start) > 0.75)
                viewer.ScrollToVerticalOffset(start);
            if (!MotionPolicy.IsEnabled(ViewModel.AnimationsEnabled))
            {
                viewer.ScrollToVerticalOffset(target);
                return;
            }

            var clock = Stopwatch.StartNew();
            var duration = Math.Clamp(150 + Math.Abs(target - start) * 0.08, 170, 260);
            while (clock.Elapsed.TotalMilliseconds < duration)
            {
                token.ThrowIfCancellationRequested();
                var progress = Math.Clamp(clock.Elapsed.TotalMilliseconds / duration, 0, 1);
                var eased = 1 - Math.Pow(1 - progress, 3);
                viewer.ScrollToVerticalOffset(start + ((target - start) * eased));
                await Task.Delay(16, token);
            }
            viewer.ScrollToVerticalOffset(target);
        }
        catch (OperationCanceledException) { }
    }

    private void OpenContextMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.ContextMenu is not { } menu) return;
        menu.PlacementTarget = element;
        menu.DataContext = element.DataContext;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OpenPlaybackMenu_Click(object sender, RoutedEventArgs e)
    {
        if (SeekSlider.ContextMenu is not { } menu) return;
        menu.PlacementTarget = sender as UIElement ?? SeekSlider;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private async void ImportPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import playlist", Filter = "Playlists|*.m3u;*.m3u8;*.pls;*.xspf|All files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try { await ViewModel.ImportPlaylistFileAsync(dialog.FileName); }
        catch (Exception exception) { ErrorDialog.Show(this, exception, "", true, "Playlist import failed"); }
    }

    private void ImportPlaylistMenuItem_Click(object sender, RoutedEventArgs e) => ImportPlaylist_Click(sender, e);

    private async void ExportPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCard?.PlaylistId is not { } playlistId) return;
        var dialog = new SaveFileDialog { Title = "Export playlist", Filter = "M3U8 playlist|*.m3u8|PLS playlist|*.pls|XSPF playlist|*.xspf", AddExtension = true, FileName = ViewModel.SelectedCard.Title };
        if (dialog.ShowDialog(this) != true) return;
        var format = Path.GetExtension(dialog.FileName).ToLowerInvariant() switch { ".pls" => PlaylistFormat.PLS, ".xspf" => PlaylistFormat.XSPF, _ => PlaylistFormat.M3U8 };
        try { await ViewModel.ExportPlaylistFileAsync(playlistId, dialog.FileName, format); }
        catch (Exception exception) { ErrorDialog.Show(this, exception, "", true, "Playlist export failed"); }
    }

    private void ExportPlaylistMenuItem_Click(object sender, RoutedEventArgs e) => ExportPlaylist_Click(sender, e);

    private void PerformanceOverlayMenuItem_Click(object sender, RoutedEventArgs e) => PerformanceOverlay.ToggleCommand.Execute(null);

    private async void SidebarCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not LibraryCardViewModel card || !e.Data.GetDataPresent("Dextromethorphan.TrackPaths")) return;
        var paths = e.Data.GetData("Dextromethorphan.TrackPaths") as string[] ?? [];
        try { await ViewModel.AddPathsToPlaylistAsync(card, paths); } catch (Exception exception) { ErrorDialog.Show(this, exception, "", true, "Tracks could not be added to playlist"); }
        e.Handled = true;
    }

    private void GroupCard_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        _groupCardDragStart = e.GetPosition(element);
        _groupCardDragSource = element;
        _groupCardDragStarted = false;
    }

    private void GroupCard_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not LibraryCardViewModel card)
            return;
        if (e.LeftButton != MouseButtonState.Pressed
            || _groupCardDragStarted
            || !ReferenceEquals(_groupCardDragSource, element)) return;
        var point = e.GetPosition(element);
        if (Math.Abs(point.X - _groupCardDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _groupCardDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var paths = ViewModel.GetCardPaths(card).ToArray();
        if (paths.Length == 0) return;
        _groupCardDragStarted = true;
        BeginTrackDragPreview(
            card.Title,
            card.Subtitle,
            card.ArtworkPath,
            card.Initial,
            paths.Length);
        try
        {
            DragDrop.DoDragDrop(
                element,
                new DataObject("Dextromethorphan.TrackPaths", paths),
                DragDropEffects.Copy);
        }
        finally
        {
            EndTrackDragPreview();
            _groupCardDragStarted = false;
            _groupCardDragSource = null;
        }
    }

    private void GroupCard_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        _groupCardDragSource = null;
        if (!_groupCardDragStarted) EndTrackDragPreview();
    }

    private void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderTreeNodeViewModel node)
            ViewModel.SelectFolderNode(node);
    }

    private async void LyricsSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: LyricsDocument document }
            || ReferenceEquals(document, ViewModel.CurrentLyricsDocument)) return;
        if (document.Kind == LyricsSourceKind.OnlineCache
            && !ConfirmationDialog.Show(
                this,
                "Use online lyrics?",
                $"Use this result from {document.Attribution ?? "LRCLIB"}? It will be remembered for this track, but your audio file and tags will not be changed.",
                "Use lyrics"))
        {
            ((ComboBox)sender).SelectedItem = ViewModel.CurrentLyricsDocument;
            return;
        }
        try { await ViewModel.SelectLyricsSourceAsync(document); }
        catch (Exception exception) { ErrorDialog.Show(this, exception, "", true, "Lyrics could not be selected"); }
    }

    private async void ChooseLyrics_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentTrack is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Choose lyrics",
            Filter = "Lyrics files|*.lrc;*.txt|LRC files|*.lrc|Text files|*.txt",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try { await ViewModel.ChooseLyricsFileAsync(dialog.FileName); }
        catch (Exception exception) { ErrorDialog.Show(this, exception, "", true, "Lyrics could not be opened"); }
    }

    private async void RemoveLyrics_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanRemoveLyrics
            || !ConfirmationDialog.Show(
                this,
                "Remove local lyrics?",
                "The selected LRC/TXT sidecar will be deleted. The audio file and embedded tags are not changed.",
                "Remove")) return;
        try { await ViewModel.RemoveCurrentLyricsAsync(); }
        catch (Exception exception) { ErrorDialog.Show(this, exception, "", true, "Lyrics could not be removed"); }
    }

    private void InstallChapterMarkers()
    {
        if (SeekSlider.Parent is not Grid seekGrid) return;
        var markers = new ChapterMarkerBar
        {
            IsHitTestVisible = false,
            Margin = new Thickness(6, 0, 6, 0),
            ToolTip = "Chapter markers"
        };
        markers.SetBinding(
            ChapterMarkerBar.ChaptersProperty,
            new Binding("CurrentTrack.Chapters"));
        markers.SetBinding(
            ChapterMarkerBar.DurationProperty,
            new Binding(nameof(MainViewModel.DurationSeconds)));
        markers.SetBinding(
            ChapterMarkerBar.BookmarksProperty,
            new Binding(nameof(MainViewModel.Bookmarks)));
        Grid.SetColumn(markers, 1);
        Panel.SetZIndex(markers, 2);
        seekGrid.Children.Add(markers);
        ViewModel.Bookmarks.CollectionChanged += (_, _) => markers.InvalidateVisual();
        var chapterMenu = new ContextMenu();
        chapterMenu.Opened += (_, _) => BuildPlaybackContextMenu(chapterMenu);
        SeekSlider.ContextMenu = chapterMenu;
        SeekSlider.ToolTip = "Seek · right-click to open chapters";
    }

    private void BuildPlaybackContextMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        menu.DataContext = ViewModel;
        var addBookmark = new MenuItem { Header = $"Add bookmark at {ViewModel.PositionText}" };
        addBookmark.Click += (_, _) =>
        {
            var name = TextPromptDialog.Show(this, "Add bookmark", "Bookmark name", $"Bookmark {ViewModel.PositionText}");
            if (name is not null) ViewModel.AddBookmarkCommand.Execute(name);
        };
        menu.Items.Add(addBookmark);
        var bookmarkMenu = new MenuItem { Header = "Bookmarks", IsEnabled = ViewModel.Bookmarks.Count > 0 };
        foreach (var bookmark in ViewModel.Bookmarks)
        {
            var item = new MenuItem { Header = $"{bookmark.PositionText}  {bookmark.Name}" };
            item.Items.Add(new MenuItem { Header = "Go to", Command = ViewModel.SeekBookmarkCommand, CommandParameter = bookmark });
            var rename = new MenuItem { Header = "Rename" };
            rename.Click += (_, _) =>
            {
                var name = TextPromptDialog.Show(this, "Rename bookmark", "Bookmark name", bookmark.Name);
                if (name is not null) ViewModel.RenameBookmarkCommand.Execute(new BookmarkRenameRequest(bookmark, name));
            };
            item.Items.Add(rename);
            item.Items.Add(new MenuItem { Header = "Remove", Command = ViewModel.DeleteBookmarkCommand, CommandParameter = bookmark });
            bookmarkMenu.Items.Add(item);
        }
        menu.Items.Add(bookmarkMenu);
        menu.Items.Add(new MenuItem { Header = "Resume tracks from last position", IsCheckable = true, IsChecked = ViewModel.ResumeTrackBookmarks, Command = ViewModel.ToggleBookmarkResumeCommand });
        var historyMenu = new MenuItem { Header = "Recently played", IsEnabled = ViewModel.QueueHistory.Count > 0 };
        foreach (var history in ViewModel.QueueHistory.Take(20))
        {
            var item = new MenuItem { Header = $"{history.PlayedAtText}  {history.Track.Title}" };
            item.Click += async (_, _) => await ViewModel.PlayHistoryTrackAsync(history.Track);
            historyMenu.Items.Add(item);
        }
        menu.Items.Add(historyMenu);
        menu.Items.Add(new Separator());
        var chaptersMenu = new MenuItem { Header = "Chapters" };
        var chapters = ViewModel.CurrentTrack?.Chapters ?? [];
        if (chapters.Count == 0)
            chaptersMenu.Items.Add(new MenuItem { Header = "No chapters in this track", IsEnabled = false });
        foreach (var chapter in chapters)
            chaptersMenu.Items.Add(new MenuItem { Header = $"{chapter.StartText}  {chapter.Title}", Command = ViewModel.SeekChapterCommand, CommandParameter = chapter });
        menu.Items.Add(chaptersMenu);
        menu.Items.Add(new Separator());
        var speed = new MenuItem { Header = $"Speed · {ViewModel.PlaybackSpeed:0.00}×" };
        foreach (var value in new[] { 0.5, 0.75, 1.0, 1.25, 1.5 })
            speed.Items.Add(new MenuItem { Header = $"{value:0.00}×", IsCheckable = true, IsChecked = Math.Abs(ViewModel.PlaybackSpeed - value) < 0.001, Command = ViewModel.SetPlaybackSpeedCommand, CommandParameter = value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        menu.Items.Add(speed);
        var pitch = new MenuItem { Header = $"Pitch · {ViewModel.PitchSemitones:+0.#;-0.#;0} st" };
        foreach (var value in new[] { -2d, 0d, 2d })
            pitch.Items.Add(new MenuItem { Header = $"{value:+0;-0;0} semitones", IsCheckable = true, IsChecked = Math.Abs(ViewModel.PitchSemitones - value) < 0.001, Command = ViewModel.SetPitchCommand, CommandParameter = value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        menu.Items.Add(pitch);
        menu.Items.Add(new MenuItem { Header = "Preserve pitch while changing speed", IsCheckable = true, IsChecked = ViewModel.PreservePitch, Command = ViewModel.TogglePreservePitchCommand });
        menu.Items.Add(new MenuItem { Header = "Reset speed and pitch", Command = ViewModel.ResetPlaybackProcessingCommand });
        menu.Items.Add(new MenuItem { Header = "Save settings for this track", Command = ViewModel.SaveTrackPlaybackOverrideCommand });
        menu.Items.Add(new MenuItem { Header = "Remove track override", IsEnabled = ViewModel.CurrentTrackHasPlaybackOverride, Command = ViewModel.ClearTrackPlaybackOverrideCommand });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Stop after current track", IsCheckable = true, IsChecked = ViewModel.StopAfterCurrent, Command = ViewModel.ToggleStopAfterCurrentCommand });
        menu.Items.Add(new MenuItem { Header = "Stop after queue", IsCheckable = true, IsChecked = ViewModel.StopAfterQueue, Command = ViewModel.ToggleStopAfterQueueCommand });
    }

    public MainViewModel ViewModel { get; }
    public PerformanceOverlayViewModel PerformanceOverlay { get; }
    internal DateTimeOffset? FirstGalleryArtworkRenderedAt => _firstGalleryArtworkRenderedAt;
    internal ArtworkRuntimeMetrics ArtworkMetrics => _artworkImages.GetRuntimeMetrics();

    private void RecordUserInteraction(
        object? sender,
        InputEventArgs args) =>
        _lastInteraction = DateTimeOffset.UtcNow;

    private void RecordWindowActivation(
        object? sender,
        EventArgs args) =>
        _lastInteraction = DateTimeOffset.UtcNow;

    private async void IdleCleanupTimer_Tick(
        object? sender,
        EventArgs args)
    {
        if (_idleCleanupRunning) return;
        var now = DateTimeOffset.UtcNow;
        if (!IdleCleanupPolicy.ShouldRun(
                now,
                _lastInteraction,
                _lastIdleCleanup,
                IsActive,
                ViewModel.IsScanning,
                ArtworkMetrics.QueueDepth))
            return;

        _idleCleanupRunning = true;
        try
        {
            await ViewModel.RunIdleCleanupAsync();
            _viewStates.Trim(
                new HashSet<string>(StringComparer.Ordinal)
                {
                    ViewModel.PrimaryViewStateKey,
                    ViewModel.ContentViewStateKey
                },
                now.AddMinutes(-20),
                maximumEntries: 32);
            _lastIdleCleanup = now;
        }
        catch (OperationCanceledException) { }
        finally
        {
            _idleCleanupRunning = false;
        }
    }

    internal void ApplySafeModePresentation()
    {
        if (!ViewModel.IsSafeMode) return;
        DisableEffects(this);
    }

    private static void DisableEffects(DependencyObject parent)
    {
        if (parent is UIElement element)
        {
            element.Effect = null;
            element.CacheMode = null;
        }
        for (var index = 0;
             index < VisualTreeHelper.GetChildrenCount(parent);
             index++)
            DisableEffects(VisualTreeHelper.GetChild(parent, index));
    }

    public void BeginStartupPresentation()
    {
        _startupStartedAt = DateTime.UtcNow;
        StartupOverlay.Visibility = Visibility.Visible;
        StartupOverlay.Opacity = 1;
        if (!MotionPolicy.IsEnabled(ViewModel.AnimationsEnabled))
            return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        StartupBrand.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        if (StartupBrand.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        }
        if (StartupOrbit.RenderTransform is RotateTransform orbit)
            orbit.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.4)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    public async Task CompleteStartupPresentationAsync()
    {
        if (!MotionPolicy.IsEnabled(ViewModel.AnimationsEnabled))
        {
            StopStartupMotion();
            StartupOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        var remaining = TimeSpan.FromMilliseconds(420) - (DateTime.UtcNow - _startupStartedAt);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) =>
        {
            StopStartupMotion();
            StartupOverlay.Visibility = Visibility.Collapsed;
            completion.TrySetResult();
        };
        StartupOverlay.BeginAnimation(OpacityProperty, fade);
        if (StartupBrand.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.035, fade.Duration.TimeSpan));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.035, fade.Duration.TimeSpan));
        }
        await completion.Task;
    }

    private void StopStartupMotion()
    {
        StartupOverlay.BeginAnimation(OpacityProperty, null);
        StartupBrand.BeginAnimation(OpacityProperty, null);
        if (StartupBrand.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
        if (StartupOrbit.RenderTransform is RotateTransform orbit)
            orbit.BeginAnimation(RotateTransform.AngleProperty, null);
        // Replace the formerly animated Freezables as well as clearing their
        // clocks. WPF's composition timing manager can otherwise retain the
        // forever-orbit clock after the startup overlay has been collapsed.
        StartupBrand.RenderTransform = new ScaleTransform(1, 1);
        StartupOrbit.RenderTransform = new RotateTransform();
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The seek thumb is intentionally previewed locally while the user
        // drags it. Once the drag has ended, force the OneWay binding to
        // refresh from the authoritative audio snapshot. WPF can otherwise
        // retain the locally assigned thumb value and make the progress bar
        // appear frozen while PositionText continues to advance.
        if (e.PropertyName == nameof(MainViewModel.PositionSeconds) && !_isSeekDragging)
            SeekSlider.GetBindingExpression(RangeBase.ValueProperty)?.UpdateTarget();
        if (e.PropertyName == nameof(MainViewModel.AlbumTileSize))
            Dispatcher.BeginInvoke(UpdateGalleryColumns, DispatcherPriority.Render);
        if (e.PropertyName == nameof(MainViewModel.QueueVisible) && ViewModel.QueueVisible)
            Dispatcher.BeginInvoke(
                () => _ = FollowCurrentQueueEntryAsync(),
                DispatcherPriority.Loaded);
        if (e.PropertyName == nameof(MainViewModel.HasMissingTracks)
            && _overflowMissingFilesItem is not null)
            _overflowMissingFilesItem.IsEnabled = ViewModel.HasMissingTracks;
        if (e.PropertyName == nameof(MainViewModel.HasLyrics))
        {
            Dispatcher.BeginInvoke(ResetLyricsView, DispatcherPriority.Loaded);
            return;
        }
        if (e.PropertyName == nameof(MainViewModel.ActiveLyricLine))
        {
            Dispatcher.BeginInvoke(() => ScrollToActiveLyric(ViewModel.ActiveLyricLine), DispatcherPriority.Loaded);
            return;
        }
        if (e.PropertyName is nameof(MainViewModel.CurrentView) or nameof(MainViewModel.IsCollectionDetailOpen))
        {
            if (e.PropertyName == nameof(MainViewModel.IsCollectionDetailOpen))
                ApplyResponsiveShellLayout();
            Dispatcher.BeginInvoke(AnimateViewTransition, DispatcherPriority.Render);
            if (_diagnostics.Enabled)
                _ = RecordViewRenderAsync(Stopwatch.GetTimestamp(), ViewModel.CurrentView, ViewModel.IsCollectionDetailOpen);
        }
        if (e.PropertyName is nameof(MainViewModel.PrimaryViewStateKey) or nameof(MainViewModel.ContentViewStateKey)
            or nameof(MainViewModel.CurrentView) or nameof(MainViewModel.IsCollectionDetailOpen))
            ScheduleScrollStateRestore();
    }

    private void ViewModelOnNavigationStarting(object? sender, EventArgs e) =>
        CaptureCurrentPrimaryViewState();

    private void CaptureCurrentPrimaryViewState()
    {
        if (_restoringScrollState || !IsLoaded) return;
        if (GalleryList.IsVisible
            && FindVisualChild<ScrollViewer>(GalleryList) is { } galleryViewer)
        {
            CaptureGalleryViewState(
                ViewModel.PrimaryViewStateKey,
                galleryViewer,
                ViewModel.GalleryGroups.Count);
            return;
        }
        if (SidebarList.IsVisible
            && FindVisualChild<ScrollViewer>(SidebarList) is { } sidebarViewer)
            _viewStates.Capture(
                ViewModel.PrimaryViewStateKey,
                sidebarViewer.VerticalOffset,
                ViewModel.SidebarCards.Count);
    }

    private void ResetLyricsView()
    {
        _lyricScrollCancellation?.Cancel();
        if (ViewModel.Lyrics.FirstOrDefault() is { } first) LyricsList.ScrollIntoView(first);
        LyricsList.UpdateLayout();
        FindVisualChild<ScrollViewer>(LyricsList)?.ScrollToTop();
    }

    private async void ScrollToActiveLyric(LyricLineViewModel? line)
    {
        if (line is null || !IsLoaded) return;
        _lyricScrollCancellation?.Cancel();
        _lyricScrollCancellation?.Dispose();
        _lyricScrollCancellation = new CancellationTokenSource();
        var token = _lyricScrollCancellation.Token;
        try
        {
            LyricsList.ScrollIntoView(line);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded, token);
            if (LyricsList.ItemContainerGenerator.ContainerFromItem(line) is not FrameworkElement container) return;
            var scroller = FindVisualChild<ScrollViewer>(LyricsList);
            if (scroller is null || scroller.ViewportHeight <= 0) return;
            var center = container.TranslatePoint(new Point(0, container.ActualHeight / 2), scroller).Y;
            var start = scroller.VerticalOffset;
            var target = Math.Clamp(start + center - (scroller.ViewportHeight / 2), 0, scroller.ScrollableHeight);
            if (!MotionPolicy.IsEnabled(ViewModel.AnimationsEnabled))
            {
                scroller.ScrollToVerticalOffset(target);
                return;
            }
            var clock = Stopwatch.StartNew();
            const double duration = 220;
            while (clock.Elapsed.TotalMilliseconds < duration)
            {
                token.ThrowIfCancellationRequested();
                var progress = Math.Clamp(clock.Elapsed.TotalMilliseconds / duration, 0, 1);
                var eased = 1 - Math.Pow(1 - progress, 3);
                scroller.ScrollToVerticalOffset(start + ((target - start) * eased));
                await Task.Delay(16, token);
            }
            scroller.ScrollToVerticalOffset(target);
        }
        catch (OperationCanceledException) { }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T result) return result;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void AnimateViewTransition()
    {
        var animationVersion = ++_viewTransitionAnimationVersion;
        if (!MotionPolicy.IsEnabled(ViewModel.AnimationsEnabled)
            || StartupOverlay.Visibility == Visibility.Visible)
        {
            ReleaseViewTransitionAnimations(animationVersion);
            ViewTransitionHost.Opacity = 1;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ViewTransitionHost.BeginAnimation(OpacityProperty, null);
        ViewTransitionHost.Opacity = 1;
        var opacityAnimation = new DoubleAnimation(0.76, 1, TimeSpan.FromMilliseconds(155))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        ViewTransitionHost.BeginAnimation(OpacityProperty, opacityAnimation);
        if (ViewTransitionHost.RenderTransform is TranslateTransform transform)
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = 0;
            var translationAnimation = new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(175))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            translationAnimation.Completed += (_, _) =>
                ReleaseViewTransitionAnimations(animationVersion);
            transform.BeginAnimation(
                TranslateTransform.YProperty,
                translationAnimation);
        }
        else
            opacityAnimation.Completed += (_, _) =>
                ReleaseViewTransitionAnimations(animationVersion);
    }

    private void TopTab_Checked(object sender, RoutedEventArgs e) =>
        AnimateTopTab(sender as RadioButton, selected: true);

    private void TopTab_Unchecked(object sender, RoutedEventArgs e) =>
        AnimateTopTab(sender as RadioButton, selected: false);

    private void AnimateTopTab(RadioButton? tab, bool selected)
    {
        if (tab is null) return;
        tab.ApplyTemplate();
        if (tab.Template.FindName("Indicator", tab) is not Border indicator)
            return;
        var version = _topTabAnimationVersions.GetValueOrDefault(tab) + 1;
        _topTabAnimationVersions[tab] = version;
        indicator.BeginAnimation(OpacityProperty, null);
        if (DataContext is not MainViewModel viewModel
            || !MotionPolicy.IsEnabled(viewModel.AnimationsEnabled))
            return;

        var animation = new DoubleAnimation(
            selected ? 0 : 1,
            selected ? 1 : 0,
            TimeSpan.FromMilliseconds(selected ? 140 : 100))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (_topTabAnimationVersions.GetValueOrDefault(tab) != version)
                return;
            indicator.BeginAnimation(OpacityProperty, null);
        };
        indicator.BeginAnimation(
            OpacityProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void ReleaseViewTransitionAnimations(int animationVersion)
    {
        if (animationVersion != _viewTransitionAnimationVersion) return;
        ViewTransitionHost.BeginAnimation(OpacityProperty, null);
        ViewTransitionHost.Opacity = 1;
        if (ViewTransitionHost.RenderTransform is not TranslateTransform transform)
            return;
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = 0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowPlacement.TryRestore(this);
        WindowMaximizeHelper.Install(
            this,
            () => _maximizeWindowButton,
            HandleMonitorEnvironmentChanged,
            ToggleMaximize);
        var handle = new WindowInteropHelper(this).Handle;
        _shortcuts.Attach(handle);
        _systemMedia.Attach(handle);
    }

    private void QueueList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _queueDragStart = e.GetPosition(QueueList);
        _queueDragStarted = false;
        var container = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        _queuePointerEntry = container?.DataContext as QueueEntryViewModel;
        _queueDragGrip = container is null
            ? new Point(24, 24)
            : e.GetPosition(container);
    }

    private void QueueList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_queuePointerEntry is null
            || !_queuePointerEntry.CanReorder
            || e.LeftButton != MouseButtonState.Pressed
            || _queueDragStarted)
            return;
        var position = e.GetPosition(QueueList);
        if (Math.Abs(position.X - _queueDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _queueDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _queueDragStarted = true;
        var entry = _queuePointerEntry;
        var entries = QueueList.SelectedItems
            .OfType<QueueEntryViewModel>()
            .Where(item => item.CanReorder)
            .ToArray();
        if (!entries.Contains(entry)) entries = [entry];
        _queueDraggedIds = entries.Select(item => item.Entry.Id).ToArray();
        var data = new DataObject();
        data.SetData(typeof(QueueEntryViewModel), entry);
        data.SetData("Dextromethorphan.QueueEntryIds", _queueDraggedIds);
        BeginQueueDragVisuals(entry, entries);
        try
        {
            DragDrop.DoDragDrop(QueueList, data, DragDropEffects.Move);
        }
        finally
        {
            _queueDragStarted = false;
            EndQueueDragVisuals();
            _queuePointerEntry = null;
        }
    }

    private void QueueList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _queuePointerEntry = null;
        if (!_queueDragStarted) ClearQueueInsertionMarker();
    }

    private void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.UpdateQueueSelection(QueueList.SelectedItems.OfType<QueueEntryViewModel>());

    private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not QueueEntryViewModel entry) return;
        if (ViewModel.PlayQueueEntryCommand.CanExecute(entry)) ViewModel.PlayQueueEntryCommand.Execute(entry);
        e.Handled = true;
    }

    private void QueueList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
        {
            QueueList.SelectAll();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.None
                 && e.Key == Key.Enter
                 && QueueList.SelectedItem is QueueEntryViewModel entry
                 && ViewModel.PlayQueueEntryCommand.CanExecute(entry))
        {
            ViewModel.PlayQueueEntryCommand.Execute(entry);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            ViewModel.RemoveSelectedQueueCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Up)
        {
            ViewModel.MoveSelectedQueueBy(-1);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Down)
        {
            ViewModel.MoveSelectedQueueBy(1);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            ViewModel.UndoQueueCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
        {
            ViewModel.RedoQueueCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void QueueList_DragOver(object sender, DragEventArgs e)
    {
        var isQueueMove = e.Data.GetDataPresent("Dextromethorphan.QueueEntryIds");
        var isTrackDrop = e.Data.GetDataPresent("Dextromethorphan.TrackPaths");
        if (!isQueueMove && !isTrackDrop)
        {
            e.Effects = DragDropEffects.None;
            ClearQueueInsertionMarker();
            e.Handled = true;
            return;
        }

        var pointer = e.GetPosition(QueueList);
        _queueDragAdorner?.MoveTo(e.GetPosition(ShellRoot), _queueDragGrip);
        UpdateQueueEdgeScroll(pointer);
        var hit = QueueList.InputHitTest(pointer) as DependencyObject;
        var targetContainer = FindVisualParent<ListBoxItem>(hit);
        if (isQueueMove
            && targetContainer?.DataContext is QueueEntryViewModel { IsPast: true })
        {
            e.Effects = DragDropEffects.None;
            ClearQueueInsertionMarker();
            e.Handled = true;
            return;
        }

        e.Effects = isQueueMove ? DragDropEffects.Move : DragDropEffects.Copy;
        UpdateQueueInsertionMarker(pointer, targetContainer);
        e.Handled = true;
    }

    private void QueueList_DragLeave(object sender, DragEventArgs e) => ClearQueueInsertionMarker();

    private void ClearQueueInsertionMarker()
    {
        QueueDropIndicator.BeginAnimation(OpacityProperty, null);
        QueueDropIndicator.Opacity = 0;
        QueueDropIndicator.Visibility = Visibility.Collapsed;
        _queueInsertionPlaybackIndex = -1;
    }

    private void QueueList_Drop(object sender, DragEventArgs e)
    {
        var targetIndex = _queueInsertionPlaybackIndex;
        ClearQueueInsertionMarker();
        if (e.Data.GetData("Dextromethorphan.TrackPaths") is string[] paths)
        {
            ViewModel.AddPathsToQueue(
                paths,
                targetIndex > 0 ? targetIndex : null);
            e.Handled = true;
            return;
        }
        if (e.Data.GetData("Dextromethorphan.QueueEntryIds") is Guid[] ids)
            ViewModel.MoveQueueEntries(ids, targetIndex < 1 ? 1 : targetIndex);
        e.Handled = true;
    }

    private void BeginQueueDragVisuals(
        QueueEntryViewModel primaryEntry,
        IReadOnlyCollection<QueueEntryViewModel> entries)
    {
        _queueDragSourceContainers.Clear();
        foreach (var item in entries)
        {
            if (QueueList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                continue;
            _queueDragSourceContainers.Add(container);
        }

        if (QueueList.ItemContainerGenerator.ContainerFromItem(primaryEntry) is ListBoxItem source)
        {
            _queueDragAdornerLayer = AdornerLayer.GetAdornerLayer(ShellRoot);
            if (_queueDragAdornerLayer is not null)
            {
                FrameworkElement previewSource =
                    FindVisualChild<ContentPresenter>(source) is { } content
                        ? content
                        : source;
                _queueDragAdorner = new QueueDragAdorner(
                    ShellRoot,
                    previewSource,
                    entries.Count);
                _queueDragAdornerLayer.Add(_queueDragAdorner);
                _queueDragAdorner.MoveTo(
                    Mouse.GetPosition(ShellRoot),
                    _queueDragGrip);
                _queueDragAdorner.Show(MotionPolicy.IsEnabled(ViewModel.AnimationsEnabled));
            }
        }

        foreach (var container in _queueDragSourceContainers)
            AnimateQueueSourceOpacity(container, 0.28, 90);
    }

    internal void BeginTrackDragPreview(
        string title,
        string subtitle,
        string? artworkPath,
        string initial,
        int itemCount)
    {
        EndTrackDragPreview();
        _queueDragAdornerLayer = AdornerLayer.GetAdornerLayer(ShellRoot);
        if (_queueDragAdornerLayer is null) return;

        _queueDragGrip = new Point(24, 24);
        _queueDragAdorner = new QueueDragAdorner(
            ShellRoot,
            title,
            subtitle,
            artworkPath,
            initial,
            Math.Max(1, itemCount));
        _queueDragAdornerLayer.Add(_queueDragAdorner);
        _queueDragAdorner.MoveTo(
            Mouse.GetPosition(ShellRoot),
            _queueDragGrip);
        _queueDragAdorner.Show(MotionPolicy.IsEnabled(ViewModel.AnimationsEnabled));
        _trackDragPreviewActive = true;
    }

    internal void EndTrackDragPreview()
    {
        if (!_trackDragPreviewActive) return;
        ClearQueueInsertionMarker();
        if (_queueDragAdorner is not null && _queueDragAdornerLayer is not null)
            _queueDragAdornerLayer.Remove(_queueDragAdorner);
        _queueDragAdorner = null;
        _queueDragAdornerLayer = null;
        _trackDragPreviewActive = false;
    }

    private void EndQueueDragVisuals()
    {
        ClearQueueInsertionMarker();
        foreach (var container in _queueDragSourceContainers)
            AnimateQueueSourceOpacity(container, 1, 90);
        _queueDragSourceContainers.Clear();
        if (_queueDragAdorner is not null && _queueDragAdornerLayer is not null)
            _queueDragAdornerLayer.Remove(_queueDragAdorner);
        _queueDragAdorner = null;
        _queueDragAdornerLayer = null;
        _queueDraggedIds = [];
        Dispatcher.BeginInvoke(
            () => _ = FollowCurrentQueueEntryAsync(),
            DispatcherPriority.Loaded);
    }

    private void AnimateQueueSourceOpacity(
        ListBoxItem container,
        double target,
        double durationMilliseconds)
    {
        container.BeginAnimation(OpacityProperty, null);
        var start = container.Opacity;
        container.Opacity = target;
        if (!MotionPolicy.IsEnabled(ViewModel.AnimationsEnabled)) return;
        container.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(durationMilliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    private void UpdateQueueInsertionMarker(
        Point pointer,
        ListBoxItem? targetContainer)
    {
        double lineY;
        if (targetContainer?.DataContext is QueueEntryViewModel target)
        {
            var targetTop = targetContainer.TranslatePoint(new Point(0, 0), QueueList).Y;
            var after = pointer.Y >= targetTop + targetContainer.ActualHeight / 2;
            if (target.IsPlaying)
            {
                _queueInsertionPlaybackIndex = 1;
                lineY = targetTop + targetContainer.ActualHeight;
            }
            else
            {
                _queueInsertionPlaybackIndex = Math.Max(
                    1,
                    target.PlaybackIndex + (after ? 1 : 0));
                lineY = targetTop + (after ? targetContainer.ActualHeight : 0);
            }
        }
        else
        {
            _queueInsertionPlaybackIndex = Math.Max(
                1,
                ViewModel.Queue.Count(entry => entry.PlaybackIndex >= 0));
            var last = ViewModel.Queue.LastOrDefault(entry => entry.PlaybackIndex >= 0);
            if (last is not null
                && QueueList.ItemContainerGenerator.ContainerFromItem(last) is ListBoxItem lastContainer)
            {
                lineY = lastContainer.TranslatePoint(
                    new Point(0, lastContainer.ActualHeight),
                    QueueList).Y;
            }
            else
                lineY = Math.Clamp(pointer.Y, 2, Math.Max(2, QueueList.ActualHeight - 2));
        }

        if (QueueDropIndicator.RenderTransform is TranslateTransform transform)
            transform.Y = Math.Clamp(lineY - 1, 0, Math.Max(0, QueueList.ActualHeight - 2));
        if (QueueDropIndicator.Visibility == Visibility.Visible) return;
        QueueDropIndicator.Visibility = Visibility.Visible;
        QueueDropIndicator.Opacity = 1;
        if (!MotionPolicy.IsEnabled(ViewModel.AnimationsEnabled)) return;
        QueueDropIndicator.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    private void UpdateQueueEdgeScroll(Point pointer)
    {
        if (DateTimeOffset.UtcNow - _lastQueueEdgeScroll < TimeSpan.FromMilliseconds(16)) return;
        var viewer = FindVisualChild<ScrollViewer>(QueueList);
        if (viewer is null || viewer.ScrollableHeight <= 0) return;
        const double edge = 52;
        const double step = 15;
        if (pointer.Y < edge)
            viewer.ScrollToVerticalOffset(Math.Max(0, viewer.VerticalOffset - step));
        else if (pointer.Y > QueueList.ActualHeight - edge)
            viewer.ScrollToVerticalOffset(Math.Min(viewer.ScrollableHeight, viewer.VerticalOffset + step));
        else
            return;
        _lastQueueEdgeScroll = DateTimeOffset.UtcNow;
    }

    private void Gallery_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_restoringScrollState
            && GalleryList.IsVisible
            && e.OriginalSource is ScrollViewer viewer)
            CaptureGalleryViewState(
                ViewModel.PrimaryViewStateKey,
                viewer,
                ViewModel.GalleryGroups.Count);
    }

    private void CaptureGalleryViewState(
        string stateKey,
        ScrollViewer viewer,
        int materializedItemCount)
    {
        if (TryGetGalleryViewportAnchor(viewer, out var anchor))
            _viewStates.Capture(
                stateKey,
                viewer.VerticalOffset,
                materializedItemCount,
                anchor.RowIndex,
                anchor.WithinRowOffset);
        else
            _viewStates.Capture(
                stateKey,
                viewer.VerticalOffset,
                materializedItemCount);
    }

    private void GalleryList_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateGalleryColumns();
        ScheduleScrollStateRestore();
    }

    private void GalleryList_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateGalleryColumns();

    private void UpdateGalleryColumns()
    {
        if (!GalleryList.IsLoaded || GalleryList.ActualWidth <= 0) return;
        var columns = Math.Max(
            1,
            (int)Math.Floor(GalleryList.ActualWidth / ViewModel.GalleryItemWidth));
        ViewModel.SetGalleryColumnCount(columns);
    }

    private void SidebarList_Loaded(object sender, RoutedEventArgs e) => ScheduleScrollStateRestore();

    private void SidebarList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_restoringScrollState && SidebarList.IsVisible && e.OriginalSource is ScrollViewer)
            _viewStates.Capture(ViewModel.PrimaryViewStateKey, e.VerticalOffset, ViewModel.SidebarCards.Count);
        if (e.ExtentHeight > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 260)
            SchedulePageLoad(SidebarList, PageTarget.Sidebar);
        else
            _sidebarPageCancellation?.Cancel();
    }

    private void TrackList_Loaded(object sender, RoutedEventArgs e) => ScheduleScrollStateRestore();

    private void TrackList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_restoringScrollState || sender is not ListBox list || !list.IsVisible || e.OriginalSource is not ScrollViewer) return;
        _viewStates.Capture(ViewModel.ContentViewStateKey, e.VerticalOffset, ViewModel.BrowseTracks.Count);
        if (e.ExtentHeight > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 360)
            SchedulePageLoad(list, PageTarget.Tracks);
        else
            _trackPageCancellation?.Cancel();
    }

    private void SchedulePageLoad(ListBox list, PageTarget target)
    {
        var previous = target switch
        {
            PageTarget.Gallery => _galleryPageCancellation,
            PageTarget.Sidebar => _sidebarPageCancellation,
            _ => _trackPageCancellation
        };
        previous?.Cancel();
        previous?.Dispose();
        var cancellation = new CancellationTokenSource();
        switch (target)
        {
            case PageTarget.Gallery: _galleryPageCancellation = cancellation; break;
            case PageTarget.Sidebar: _sidebarPageCancellation = cancellation; break;
            default: _trackPageCancellation = cancellation; break;
        }
        _ = LoadPageAfterScrollIdleAsync(list, target, cancellation.Token);
    }

    private async Task LoadPageAfterScrollIdleAsync(ListBox list, PageTarget target, CancellationToken cancellationToken)
    {
        try
        {
            var available = await DeferredPageLoadGate.WaitForIdleAsync(
                () => list.IsVisible,
                () => list.IsMouseCaptureWithin || SmoothScrollBehavior.IsAnimating(list),
                TimeSpan.FromMilliseconds(80),
                cancellationToken);
            if (!available) return;
            switch (target)
            {
                case PageTarget.Gallery: ViewModel.LoadMoreGalleryGroups(); break;
                case PageTarget.Sidebar: ViewModel.LoadMoreSidebarCards(); break;
                default: ViewModel.LoadMoreBrowseTracks(); break;
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ScheduleScrollStateRestore()
    {
        if (!IsLoaded) return;
        _restoringScrollState = true;
        if (_scrollRestorePending) return;
        _scrollRestorePending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _scrollRestorePending = false;
            RestoreVisibleScrollState();
        }, DispatcherPriority.Loaded);
    }

    private void RestoreVisibleScrollState()
    {
        try
        {
            if (GalleryList.IsVisible)
            {
                var stateKey = ViewModel.PrimaryViewStateKey;
                var state = _viewStates.Get(stateKey);
                ViewModel.EnsureGalleryGroupsLoaded(state.MaterializedItemCount);
                if (FindVisualChild<ScrollViewer>(GalleryList) is { } viewer)
                {
                    if (state.RequiresPreciseGalleryRestore)
                    {
                        GalleryList.UpdateLayout();
                        RestoreGalleryVerticalOffset(viewer, state);
                    }
                    else if (viewer.VerticalOffset > .5)
                        viewer.ScrollToTop();
                    if (state.RequiresVerticalRestore)
                        ReapplyGalleryScrollStateAtRender(
                            stateKey,
                            state,
                            _lastInteraction);
                }
            }

            if (SidebarList.IsVisible)
            {
                var state = _viewStates.Get(ViewModel.PrimaryViewStateKey);
                ViewModel.EnsureSidebarCardsLoaded(state.MaterializedItemCount);
                if (FindVisualChild<ScrollViewer>(SidebarList) is { } viewer)
                {
                    if (state.RequiresVerticalRestore)
                    {
                        SidebarList.UpdateLayout();
                        viewer.ScrollToVerticalOffset(state.VerticalOffset);
                    }
                    else if (viewer.VerticalOffset > .5)
                        viewer.ScrollToTop();
                }
            }

            foreach (var list in FindVisualChildren<ListBox>(ViewTransitionHost)
                         .Where(x => x.IsVisible
                             && !ReferenceEquals(x, GalleryList)
                             && !ReferenceEquals(x, SidebarList)
                             && !ReferenceEquals(x, LyricsList)))
            {
                var state = _viewStates.Get(ViewModel.ContentViewStateKey);
                ViewModel.EnsureBrowseTracksLoaded(state.MaterializedItemCount);
                if (FindVisualChild<ScrollViewer>(list) is { } viewer)
                {
                    if (state.RequiresVerticalRestore)
                    {
                        list.UpdateLayout();
                        viewer.ScrollToVerticalOffset(state.VerticalOffset);
                    }
                    else if (viewer.VerticalOffset > .5)
                        viewer.ScrollToTop();
                }
            }
        }
        finally { _restoringScrollState = false; }
    }

    private void ReapplyGalleryScrollStateAtRender(
        string stateKey,
        NavigationViewState state,
        DateTimeOffset interactionAtSchedule)
    {
        if (_pendingGalleryScrollReapplication is not null)
            CompositionTarget.Rendering -= _pendingGalleryScrollReapplication;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            if (ReferenceEquals(_pendingGalleryScrollReapplication, handler))
                _pendingGalleryScrollReapplication = null;
            if (!GalleryList.IsVisible
                || !stateKey.Equals(
                    ViewModel.PrimaryViewStateKey,
                    StringComparison.Ordinal)
                || _lastInteraction != interactionAtSchedule
                || GalleryList.IsMouseCaptureWithin)
                return;
            _restoringScrollState = true;
            try
            {
                GalleryList.UpdateLayout();
                if (FindVisualChild<ScrollViewer>(GalleryList) is not { } viewer)
                    return;
                RestoreGalleryVerticalOffset(viewer, state);
            }
            finally
            {
                _restoringScrollState = false;
            }
        };
        _pendingGalleryScrollReapplication = handler;
        CompositionTarget.Rendering += handler;
    }

    private void RestoreGalleryVerticalOffset(
        ScrollViewer viewer,
        NavigationViewState state)
    {
        if (ViewModel.GalleryRows.Count == 0)
        {
            viewer.ScrollToTop();
            return;
        }

        if (state.GalleryAnchorIndex < 0)
        {
            viewer.ScrollToVerticalOffset(state.VerticalOffset);
            return;
        }
        var rowIndex = Math.Clamp(
            state.GalleryAnchorIndex,
            0,
            ViewModel.GalleryRows.Count - 1);
        GalleryList.ScrollIntoView(ViewModel.GalleryRows[rowIndex]);
        GalleryList.UpdateLayout();
        if (GalleryList.ItemContainerGenerator.ContainerFromIndex(rowIndex)
            is not ListBoxItem rowContainer)
        {
            viewer.ScrollToVerticalOffset(state.VerticalOffset);
            return;
        }

        var rowTop = rowContainer.TransformToAncestor(viewer)
            .Transform(new Point(0, 0)).Y;
        viewer.ScrollToVerticalOffset(
            Math.Clamp(
                viewer.VerticalOffset + rowTop + state.GalleryAnchorOffset,
                0,
                viewer.ScrollableHeight));
    }

    private bool TryGetGalleryViewportAnchor(
        ScrollViewer viewer,
        out GalleryViewportAnchor anchor)
    {
        var firstBelowTop = (Index: -1, Top: double.MaxValue);
        for (var rowIndex = 0; rowIndex < ViewModel.GalleryRows.Count; rowIndex++)
        {
            if (GalleryList.ItemContainerGenerator.ContainerFromIndex(rowIndex)
                is not ListBoxItem container)
                continue;
            var top = container.TransformToAncestor(viewer)
                .Transform(new Point(0, 0)).Y;
            var bottom = top + Math.Max(1, container.ActualHeight);
            if (top <= 0 && bottom > 0)
            {
                anchor = new GalleryViewportAnchor(rowIndex, -top);
                return true;
            }
            if (top >= 0 && top < firstBelowTop.Top)
                firstBelowTop = (rowIndex, top);
        }
        if (firstBelowTop.Index >= 0)
        {
            anchor = new GalleryViewportAnchor(firstBelowTop.Index, 0);
            return true;
        }
        anchor = default;
        return false;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result) yield return result;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    private void GalleryArtwork_Loaded(object sender, RoutedEventArgs e)
    {
        if (_firstGalleryArtworkRenderedAt is null && sender is Image { Source: not null })
        {
            _firstGalleryArtworkRenderedAt = DateTimeOffset.UtcNow;
            if (_diagnostics.Enabled)
                _diagnostics.Mark("render", "first-gallery-artwork", new Dictionary<string, object?> { ["view"] = ViewModel.CurrentView });
        }
    }

    private async Task RecordViewRenderAsync(long started, string view, bool detailOpen)
    {
        try
        {
            var rendered = await NextRenderedFrameTimestampAsync(CancellationToken.None);
            _diagnostics.RecordDuration("render", "view-first-frame", Stopwatch.GetElapsedTime(started, rendered),
                new Dictionary<string, object?> { ["view"] = view, ["detailOpen"] = detailOpen });
        }
        catch (Exception exception)
        {
            _diagnostics.Error("render", "view-first-frame", exception,
                new Dictionary<string, object?> { ["view"] = view, ["detailOpen"] = detailOpen });
        }
    }

    internal async Task<IReadOnlyList<TabSwitchPerformanceSample>> MeasureTabSwitchPerformanceAsync(CancellationToken cancellationToken)
    {
        var samples = new List<TabSwitchPerformanceSample>();
        var views = new[] { "Artists", "Genres", "Songs", "Folders", "Playlists", "Albums" };
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var view in views)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timer = Stopwatch.StartNew();
                ViewModel.NavigateCommand.Execute(view);
                await NextRenderedFrameTimestampAsync(cancellationToken);
                timer.Stop();
                samples.Add(new TabSwitchPerformanceSample(view, pass == 0 ? "first" : "cached", Math.Round(timer.Elapsed.TotalMilliseconds, 3)));
            }
            if (pass == 0)
                await WaitForBackgroundIdleAsync(cancellationToken);
        }
        return samples;
    }

    internal async Task<GalleryVisualRegressionMetrics> CaptureGalleryVisualRegressionAsync(
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        ViewModel.NavigateCommand.Execute("Albums");
        await NextRenderedFrameTimestampAsync(cancellationToken);
        GalleryList.UpdateLayout();
        var viewer = FindVisualChild<ScrollViewer>(GalleryList)
            ?? throw new InvalidOperationException("The album gallery scroll viewer is unavailable.");

        var sourceCards = ViewModel.ActiveGroups.Count;
        var initialCards = ViewModel.GalleryGroups.Count;
        var pageAdvances = 0;
        var checkpoints = 0;
        var realizedCards = 0;
        var expectedArtwork = 0;
        var renderedArtwork = 0;
        var mappingFailures = 0;
        var missingArtwork = 0;
        var screenshots = new List<string>();

        async Task InspectAsync()
        {
            await WaitForBackgroundIdleAsync(cancellationToken);
            await NextRenderedFrameTimestampAsync(cancellationToken);
            GalleryList.UpdateLayout();
            var inspection = InspectRealizedGalleryCards();
            checkpoints++;
            realizedCards += inspection.RealizedCards;
            expectedArtwork += inspection.ExpectedArtwork;
            renderedArtwork += inspection.RenderedArtwork;
            mappingFailures += inspection.MappingFailures;
            missingArtwork += inspection.MissingArtwork;
        }

        await InspectAsync();
        var maximumPageAttempts = Math.Max(1, (int)Math.Ceiling(sourceCards / 28d) + 4);
        while (ViewModel.GalleryGroups.Count < sourceCards && pageAdvances < maximumPageAttempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = ViewModel.GalleryGroups.Count;
            viewer.ScrollToEnd();
            var wait = Stopwatch.StartNew();
            while (ViewModel.GalleryGroups.Count <= before
                   && wait.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(50, cancellationToken);
            if (ViewModel.GalleryGroups.Count <= before) break;
            pageAdvances++;
            GalleryList.UpdateLayout();
            await InspectAsync();
        }

        var capturedRatios = new Dictionary<double, string>
        {
            [0d] = "top",
            [0.5d] = "middle",
            [1d] = "bottom"
        };
        var capturedNames = new HashSet<string>(StringComparer.Ordinal);
        var traversalRatios = Enumerable.Range(0, 11)
            .Select(index => index / 10d)
            // Revisit distant ranges in both directions to exercise container
            // removal/recreation and async artwork cancellation, not just a
            // monotonic trip from the top to the bottom.
            .Concat([0.75d, 0.25d, 1d, 0d])
            .ToArray();
        foreach (var ratio in traversalRatios)
        {
            viewer.ScrollToVerticalOffset(viewer.ScrollableHeight * ratio);
            await NextRenderedFrameTimestampAsync(cancellationToken);
            await InspectAsync();
            if (!capturedRatios.TryGetValue(ratio, out var name)
                || !capturedNames.Add(name))
                continue;
            var screenshot = Path.Combine(
                outputDirectory,
                $"gallery-{name}.png");
            CaptureVisualPng(screenshot);
            screenshots.Add(screenshot);
        }

        // A settled monotonic traversal can hide races between rapid viewport
        // recycling, Unloaded cancellation, and the next artwork request.
        // Repeatedly jump between disjoint ranges, then require both endpoints
        // to recover all of their visible cards and artwork after the queues
        // settle. This mirrors fast wheel/scrollbar use in a real library.
        for (var cycle = 0; cycle < 4; cycle++)
        {
            foreach (var ratio in new[] { 0d, 1d, 0.2d, 0.8d, 0.4d, 0.6d, 1d })
            {
                viewer.ScrollToVerticalOffset(viewer.ScrollableHeight * ratio);
                await NextRenderedFrameTimestampAsync(cancellationToken);
            }
            await InspectAsync();
            viewer.ScrollToTop();
            await NextRenderedFrameTimestampAsync(cancellationToken);
            await InspectAsync();
        }
        var returnScreenshot = Path.Combine(
            outputDirectory,
            "gallery-return-top.png");
        CaptureVisualPng(returnScreenshot);
        screenshots.Add(returnScreenshot);

        var finalCards = ViewModel.GalleryGroups.Count;
        var status = finalCards != sourceCards
            ? $"Paging stopped at {finalCards:N0} of {sourceCards:N0} cards."
            : mappingFailures > 0
                ? $"{mappingFailures:N0} realized card mappings were incorrect."
                : missingArtwork > 0
                    ? $"{missingArtwork:N0} expected artwork sources were blank."
                    : $"Rendered all {sourceCards:N0} cards and every expected visible artwork source.";
        return new GalleryVisualRegressionMetrics(
            sourceCards,
            initialCards,
            finalCards,
            pageAdvances,
            checkpoints,
            realizedCards,
            expectedArtwork,
            renderedArtwork,
            mappingFailures,
            missingArtwork,
            screenshots,
            status);
    }

    private GalleryVisualInspection InspectRealizedGalleryCards()
    {
        var realized = 0;
        var expectedArtwork = 0;
        var renderedArtwork = 0;
        var mappingFailures = 0;
        var missingArtwork = 0;
        var containers = new HashSet<DependencyObject>();
        foreach (var cardContainer in EnumerateRealizedGalleryCards())
        {
            if (cardContainer.Container is not null)
                realized++;
            if (cardContainer.Container is null
                || !containers.Add(cardContainer.Container)
                || !cardContainer.MappingValid)
                mappingFailures++;

            var card = cardContainer.Card;
            if (string.IsNullOrWhiteSpace(card.ArtworkPath)
                || !File.Exists(card.ArtworkPath)
                || cardContainer.Container is null)
                continue;
            var artworkPath = card.ArtworkPath;
            expectedArtwork++;
            var image = FindVisualChildren<Image>(cardContainer.Container)
                .FirstOrDefault(candidate =>
                    string.Equals(
                        AsyncArtwork.GetPath(candidate),
                        artworkPath,
                        StringComparison.OrdinalIgnoreCase));
            if (image?.Source is not null)
                renderedArtwork++;
            else
                missingArtwork++;
        }
        return new GalleryVisualInspection(
            realized,
            expectedArtwork,
            renderedArtwork,
            mappingFailures,
            missingArtwork);
    }

    private IEnumerable<RealizedGalleryCard> EnumerateRealizedGalleryCards()
    {
        for (var rowIndex = 0; rowIndex < ViewModel.GalleryRows.Count; rowIndex++)
        {
            if (GalleryList.ItemContainerGenerator.ContainerFromIndex(rowIndex)
                is not ListBoxItem rowContainer)
                continue;
            var row = ViewModel.GalleryRows[rowIndex];
            var rowMappingValid = ReferenceEquals(rowContainer.DataContext, row);
            var items = FindVisualChild<ItemsControl>(rowContainer);
            items?.UpdateLayout();
            for (var localIndex = 0; localIndex < row.Cards.Count; localIndex++)
            {
                var card = row.Cards[localIndex];
                var container = items?.ItemContainerGenerator.ContainerFromIndex(localIndex);
                var mappingValid = rowMappingValid
                    && row.StartIndex + localIndex < ViewModel.GalleryGroups.Count
                    && ReferenceEquals(
                        card,
                        ViewModel.GalleryGroups[row.StartIndex + localIndex])
                    && container is FrameworkElement element
                    && ReferenceEquals(element.DataContext, card);
                yield return new RealizedGalleryCard(
                    row.StartIndex + localIndex,
                    card,
                    container,
                    mappingValid);
            }
        }
    }

    private void CaptureVisualPng(string path)
    {
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        encoder.Save(output);
    }

    internal async Task<WindowingSmokeReport> CaptureWindowingSmokeAsync(
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        ResolveResponsiveShellElements();
        InstallCompactTitleBarActions();
        var originalWidth = Width;
        var originalHeight = Height;
        var originalLeft = Left;
        var originalTop = Top;
        var originalState = WindowState;
        var originalQueue = ViewModel.QueueVisible;
        var originalQueueCompact = ViewModel.QueuePanelCompact;
        var results = new List<WindowingSmokeCase>();
        var snapAvailable = WindowMaximizeHelper.IsNativeSnapLayoutAvailable;
        var snapHitTest = default(SnapLayoutProbe);
        var nativeMaximizeClickPassed = !snapAvailable;
        var taskbarSafeFullScreenPassed = false;
        var scenarios = new[]
        {
            (Name: "minimum-queue-hidden", Width: 800d, Height: 600d, Queue: false),
            (Name: "minimum-queue-visible", Width: 800d, Height: 600d, Queue: true),
            (Name: "laptop-1366x768", Width: 1366d, Height: 768d, Queue: true),
            (Name: "laptop-1536x864", Width: 1536d, Height: 864d, Queue: false),
            (Name: "desktop-1920x1080", Width: 1920d, Height: 1080d, Queue: true),
            (Name: "ultrawide-3440x1440", Width: 3440d, Height: 1440d, Queue: true)
        };

        try
        {
            WindowState = WindowState.Normal;
            Left = SystemParameters.WorkArea.Left;
            Top = SystemParameters.WorkArea.Top;
            await WaitForBackgroundIdleAsync(cancellationToken);
            foreach (var scenario in scenarios)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Width = scenario.Width;
                Height = scenario.Height;
                ViewModel.QueueVisible = scenario.Queue;
                ViewModel.QueuePanelCompact = false;
                ApplyResponsiveShellLayout();
                UpdateLayout();
                await Dispatcher.InvokeAsync(
                    () => { },
                    DispatcherPriority.Render,
                    cancellationToken);
                await NextRenderedFrameTimestampAsync(cancellationToken);
                UpdateLayout();

                var metrics = ResponsiveShellMetrics.ForWidth(ActualWidth);
                var requestedMetrics = ResponsiveShellMetrics.ForWidth(scenario.Width);
                var searchAccess = metrics.ShowFullSearch
                    ? SearchBox as FrameworkElement
                    : _compactSearchButton;
                var transportButtons = FindVisualChildren<Button>(_playerGrid ?? ShellRoot)
                    .Where(button => button.Command == ViewModel.TogglePlaybackCommand
                                     || button.ToolTip is string tooltip
                                     && tooltip is "Previous" or "Next")
                    .Cast<FrameworkElement>()
                    .ToArray();
                var critical = new List<FrameworkElement>
                {
                    SeekSlider,
                    VolumeSlider,
                    SettingsButton,
                    FullScreenButton
                };
                critical.AddRange(transportButtons);
                if (searchAccess is not null) critical.Add(searchAccess);
                if (scenario.Queue && _queuePanel is not null) critical.Add(_queuePanel);
                var controlsInside = critical.All(IsInsideShell)
                                     && SeekSlider.ActualWidth >= 80
                                     && transportButtons.Length >= 3
                                     && (_topTabsHost?.ActualWidth ?? 0) >= 260
                                     && metrics.WidthClass == requestedMetrics.WidthClass;
                var screenshot = Path.Combine(outputDirectory, scenario.Name + ".png");
                CaptureVisualPng(screenshot);
                results.Add(new WindowingSmokeCase(
                    scenario.Name,
                    scenario.Width,
                    scenario.Height,
                    ActualWidth,
                    ActualHeight,
                    Math.Abs(ActualWidth - scenario.Width) <= 1
                    && Math.Abs(ActualHeight - scenario.Height) <= 1,
                    scenario.Queue,
                    metrics.WidthClass,
                    SeekSlider.ActualWidth,
                    _queuePanel?.ActualWidth ?? 0,
                    controlsInside,
                    screenshot));
            }
            if (_maximizeWindowButton is not null)
            {
                snapHitTest = WindowMaximizeHelper.ProbeSnapLayoutHitTest(
                    this,
                    _maximizeWindowButton);
                if (snapAvailable
                    && WindowMaximizeHelper.InvokeSnapLayoutButtonForSmoke(
                        this,
                        _maximizeWindowButton))
                {
                    await Dispatcher.InvokeAsync(
                        () => { },
                        DispatcherPriority.Input,
                        cancellationToken);
                    nativeMaximizeClickPassed = WindowState == WindowState.Maximized;
                    if (nativeMaximizeClickPassed)
                    {
                        WindowMaximizeHelper.InvokeSnapLayoutButtonForSmoke(
                            this,
                            _maximizeWindowButton);
                        await Dispatcher.InvokeAsync(
                            () => { },
                            DispatcherPriority.Input,
                            cancellationToken);
                        nativeMaximizeClickPassed = WindowState == WindowState.Normal;
                    }
                }
            }

            var expectedWorkArea = WindowMaximizeHelper.GetMonitorWorkArea(this);
            EnterFullScreen();
            UpdateLayout();
            taskbarSafeFullScreenPassed = _isFullScreen
                                          && Math.Abs(Left - expectedWorkArea.Left) <= 1
                                          && Math.Abs(Top - expectedWorkArea.Top) <= 1
                                          && Math.Abs(ActualWidth - expectedWorkArea.Width) <= 1
                                          && Math.Abs(ActualHeight - expectedWorkArea.Height) <= 1
                                          && IsInsideShell(SeekSlider)
                                          && IsInsideShell(FullScreenButton);
            ExitFullScreen();
        }
        finally
        {
            if (_isFullScreen) ExitFullScreen();
            ViewModel.QueueVisible = originalQueue;
            ViewModel.QueuePanelCompact = originalQueueCompact;
            Width = originalWidth;
            Height = originalHeight;
            Left = originalLeft;
            Top = originalTop;
            WindowState = originalState;
            ApplyResponsiveShellLayout();
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        return new WindowingSmokeReport(
            1,
            DateTimeOffset.UtcNow,
            dpi.DpiScaleX,
            dpi.DpiScaleY,
            snapAvailable,
            snapHitTest,
            nativeMaximizeClickPassed,
            taskbarSafeFullScreenPassed,
            results.All(value => value.CriticalControlsInsideWindow)
            && (!snapAvailable || snapHitTest.Result == 9)
            && nativeMaximizeClickPassed
            && taskbarSafeFullScreenPassed,
            results);

        bool IsInsideShell(FrameworkElement element)
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                return false;
            try
            {
                var origin = element.TransformToAncestor(ShellRoot)
                    .Transform(new Point(0, 0));
                return origin.X >= -1
                       && origin.Y >= -1
                       && origin.X + element.ActualWidth <= ShellRoot.ActualWidth + 1
                       && origin.Y + element.ActualHeight <= ShellRoot.ActualHeight + 1;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    private readonly record struct GalleryVisualInspection(
        int RealizedCards,
        int ExpectedArtwork,
        int RenderedArtwork,
        int MappingFailures,
        int MissingArtwork);

    private readonly record struct RealizedGalleryCard(
        int FlatIndex,
        LibraryCardViewModel Card,
        DependencyObject? Container,
        bool MappingValid);

    private readonly record struct GalleryViewportAnchor(
        int RowIndex,
        double WithinRowOffset);

    internal async Task<NavigationHistoryPerformanceMetrics> MeasureNavigationHistoryPerformanceAsync(CancellationToken cancellationToken)
    {
        ViewModel.NavigateCommand.Execute("Albums");
        await NextRenderedFrameTimestampAsync(cancellationToken);
        ViewModel.EnsureGalleryGroupsLoaded(Math.Min(140, ViewModel.ActiveGroups.Count));
        GalleryList.UpdateLayout();

        var selected = ViewModel.GalleryGroups.FirstOrDefault();
        if (selected is not null)
        {
            ViewModel.SelectGroupCommand.Execute(selected);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, cancellationToken);
            ViewModel.CloseCollectionCommand.Execute(null);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, cancellationToken);
        }

        var originalCollection = ViewModel.GalleryGroups;
        var viewer = FindVisualChild<ScrollViewer>(GalleryList)
            ?? throw new InvalidOperationException("The album gallery scroll viewer is unavailable.");
        var albumStateKey = ViewModel.PrimaryViewStateKey;
        var targetOffset = Math.Min(viewer.ScrollableHeight, Math.Max(0, viewer.ViewportHeight * 1.5));
        viewer.ScrollToVerticalOffset(targetOffset);
        await NextRenderedFrameTimestampAsync(cancellationToken);
        // A row virtualizer can replace estimated row heights after the first
        // distant realization. Reapply the target once after that layout so
        // the expected browser-history position represents the settled view a
        // user actually leaves, rather than a transient pre-layout estimate.
        GalleryList.UpdateLayout();
        viewer.ScrollToVerticalOffset(targetOffset);
        await NextRenderedFrameTimestampAsync(cancellationToken);
        var expectedCount = ViewModel.GalleryGroups.Count;
        var expectedSelection = ViewModel.SelectedCard?.Key;

        ViewModel.NavigateCommand.Execute("Artists");
        var expectedState = _viewStates.Get(albumStateKey);
        var expectedOffset = expectedState.VerticalOffset;
        await NextRenderedFrameTimestampAsync(cancellationToken);

        var backTimer = Stopwatch.StartNew();
        if (!ViewModel.NavigateBack())
            throw new InvalidOperationException("Navigation history did not contain the Albums view.");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, cancellationToken);
        await NextRenderedFrameTimestampAsync(cancellationToken);
        backTimer.Stop();

        GalleryList.UpdateLayout();
        viewer = FindVisualChild<ScrollViewer>(GalleryList)
            ?? throw new InvalidOperationException("The restored album gallery scroll viewer is unavailable.");
        var restoredOffset = viewer.VerticalOffset;
        var restoredCount = ViewModel.GalleryGroups.Count;
        var collectionReused = ReferenceEquals(originalCollection, ViewModel.GalleryGroups);
        var offsetRestored = expectedState.GalleryAnchorIndex >= 0
            ? TryGetGalleryViewportAnchor(viewer, out var restoredAnchor)
              && restoredAnchor.RowIndex == expectedState.GalleryAnchorIndex
              && Math.Abs(
                  restoredAnchor.WithinRowOffset
                  - expectedState.GalleryAnchorOffset) <= 3
            : Math.Abs(expectedOffset - restoredOffset) <= 3;
        var selectionRestored = expectedSelection is null || ViewModel.SelectedCard?.Key == expectedSelection;
        var countRestored = restoredCount >= expectedCount;

        var forwardTimer = Stopwatch.StartNew();
        if (!ViewModel.NavigateForward())
            throw new InvalidOperationException("Forward navigation did not contain the Artists view.");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, cancellationToken);
        await NextRenderedFrameTimestampAsync(cancellationToken);
        forwardTimer.Stop();

        return new NavigationHistoryPerformanceMetrics(
            Math.Round(backTimer.Elapsed.TotalMilliseconds, 3),
            Math.Round(forwardTimer.Elapsed.TotalMilliseconds, 3),
            collectionReused,
            offsetRestored,
            selectionRestored,
            countRestored,
            Math.Round(expectedOffset, 3),
            Math.Round(restoredOffset, 3),
            expectedCount,
            restoredCount);
    }

    internal async Task<HiddenViewReleaseMetrics> MeasureHiddenViewReleaseAsync(CancellationToken cancellationToken)
    {
        ViewModel.NavigateCommand.Execute("Albums");
        ViewModel.EnsureGalleryGroupsLoaded(Math.Min(56, ViewModel.ActiveGroups.Count));
        await NextRenderedFrameTimestampAsync(cancellationToken);

        var timeout = Stopwatch.StartNew();
        while (ArtworkMetrics.ActiveImageSources == 0 && timeout.Elapsed < TimeSpan.FromSeconds(2))
            await Task.Delay(16, cancellationToken);
        var beforeHide = ArtworkMetrics.ActiveImageSources;

        ViewModel.NavigateCommand.Execute("Songs");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, cancellationToken);
        await NextRenderedFrameTimestampAsync(cancellationToken);
        await Task.Delay(32, cancellationToken);
        return new HiddenViewReleaseMetrics(beforeHide, ArtworkMetrics.ActiveImageSources);
    }

    internal async Task<PagedSongsPerformanceMetrics> MeasurePagedSongsPerformanceAsync(CancellationToken cancellationToken)
    {
        ViewModel.NavigateCommand.Execute("Songs");
        await NextRenderedFrameTimestampAsync(cancellationToken);
        var sourceCount = ViewModel.BrowseTrackSourceCount;
        var initialCount = ViewModel.BrowseTracks.Count;
        ViewModel.LoadMoreBrowseTracks();
        await NextRenderedFrameTimestampAsync(cancellationToken);
        return new PagedSongsPerformanceMetrics(sourceCount, initialCount, ViewModel.BrowseTracks.Count);
    }

    internal async Task WaitForBackgroundIdleAsync(CancellationToken cancellationToken)
    {
        await ViewModel.WaitForBackgroundWorkAsync(cancellationToken);
        var timeout = Stopwatch.StartNew();
        while ((ArtworkMetrics.QueueDepth > 0
                || _artworkUpdates.GetMetrics().Pending > 0)
               && timeout.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(25, cancellationToken);
        await _diagnostics.WaitForIdleAsync(cancellationToken);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, cancellationToken);
        await Task.Delay(250, cancellationToken);
    }

    internal IReadOnlyList<string> CaptureActiveAnimationState()
    {
        var animated = new List<string>();
        CaptureAnimations(this, animated);
        return animated;
    }

    internal IReadOnlyList<string> CaptureCompositionState()
    {
        var result = new List<string>();
        try
        {
            var mediaContextType = typeof(CompositionTarget).Assembly.GetType(
                "System.Windows.Media.MediaContext");
            var current = mediaContextType?.GetProperty(
                "CurrentMediaContext",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null);
            if (mediaContextType is null || current is null)
                return ["MediaContext unavailable"];
            var callbackCount = mediaContextType.GetProperty(
                "InvokeOnRenderCallbacksCount",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(current);
            result.Add($"InvokeOnRenderCallbacks={callbackCount ?? "unknown"}");
            result.Add($"RenderTier={RenderCapability.Tier >> 16}");
            foreach (var fieldName in new[]
                     {
                         "_displayRefreshRate",
                         "_animationRenderRate",
                         "_isRendering",
                         "_needToCommitChannel",
                         "_commitPendingAfterRender",
                         "_interlockState"
                     })
            {
                var fieldValue = mediaContextType.GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(current);
                result.Add($"{fieldName}={fieldValue ?? "unknown"}");
            }
            if (mediaContextType.GetField(
                    "Rendering",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(current) is Delegate rendering)
            {
                result.AddRange(rendering.GetInvocationList().Select(handler =>
                    $"Rendering={handler.Target?.GetType().FullName ?? "static"}.{handler.Method.Name}"));
            }
        }
        catch (Exception exception)
        {
            result.Add($"Composition inspection failed: {exception.GetBaseException().Message}");
        }
        return result;
    }

    private static void CaptureAnimations(
        DependencyObject value,
        ICollection<string> animated)
    {
        if (value is IAnimatable { HasAnimatedProperties: true })
        {
            var name = value is FrameworkElement element
                && !string.IsNullOrWhiteSpace(element.Name)
                    ? $"#{element.Name}"
                    : "";
            var visibility = value is UIElement visual
                ? $" ({visual.Visibility})"
                : "";
            animated.Add($"{value.GetType().Name}{name}{visibility}");
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(value); index++)
            CaptureAnimations(VisualTreeHelper.GetChild(value, index), animated);
    }

    internal async Task<FramePerformanceMetrics> MeasureAlbumScrollPerformanceAsync(CancellationToken cancellationToken)
    {
        ViewModel.NavigateCommand.Execute("Albums");
        await NextRenderedFrameTimestampAsync(cancellationToken);
        // The production scroll path applies another page only after input and
        // smooth scrolling have gone idle. Materialize the benchmark window
        // before frame timing so the measurement still exercises uncached,
        // virtualized artwork without injecting collection/layout mutations
        // that the application deliberately keeps out of active scrolling.
        ViewModel.EnsureGalleryGroupsLoaded(Math.Min(500, ViewModel.ActiveGroups.Count));
        GalleryList.UpdateLayout();
        await NextRenderedFrameTimestampAsync(cancellationToken);
        var viewer = FindVisualChild<ScrollViewer>(GalleryList)
            ?? throw new InvalidOperationException("The album gallery scroll viewer is unavailable.");
        viewer.ScrollToTop();
        await NextRenderedFrameTimestampAsync(cancellationToken);

        const int sampleFrames = 180;
        var intervals = new List<double>(sampleFrames);
        var previous = Stopwatch.GetTimestamp();
        for (var frame = 0; frame < sampleFrames; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = Math.Max(32, viewer.ViewportHeight / 10);
            var target = Math.Min(viewer.ScrollableHeight, viewer.VerticalOffset + step);
            viewer.ScrollToVerticalOffset(target);
            var rendered = await NextRenderedFrameTimestampAsync(cancellationToken);
            intervals.Add(Stopwatch.GetElapsedTime(previous, rendered).TotalMilliseconds);
            previous = rendered;
        }
        var metrics = PerformanceStatistics.Frames(intervals, ViewModel.GalleryGroups.Count);
        await ValidateGalleryTraversalAsync(viewer, cancellationToken);
        viewer.ScrollToTop();
        await NextRenderedFrameTimestampAsync(cancellationToken);
        GalleryList.UpdateLayout();
        ValidateGalleryReturnToTop();
        return metrics;
    }

    private async Task ValidateGalleryTraversalAsync(
        ScrollViewer viewer,
        CancellationToken cancellationToken)
    {
        foreach (var offset in new[]
                 {
                     0d,
                     viewer.ScrollableHeight * 0.25,
                     viewer.ScrollableHeight * 0.5,
                     viewer.ScrollableHeight
                 })
        {
            viewer.ScrollToVerticalOffset(offset);
            await NextRenderedFrameTimestampAsync(cancellationToken);
            GalleryList.UpdateLayout();

            var realized = 0;
            var containers = new HashSet<DependencyObject>();
            foreach (var card in EnumerateRealizedGalleryCards())
            {
                if (card.Container is null)
                    throw new InvalidOperationException($"Gallery row omitted card {card.FlatIndex} near {offset:F0}px.");
                realized++;
                if (!containers.Add(card.Container))
                    throw new InvalidOperationException($"Gallery virtualization reused one container for multiple cards near {offset:F0}px.");
                if (!card.MappingValid)
                    throw new InvalidOperationException($"Gallery virtualization mapped card {card.FlatIndex} incorrectly near {offset:F0}px.");
            }
            if (realized == 0)
                throw new InvalidOperationException($"Gallery virtualization realized no cards near {offset:F0}px.");
        }
    }

    private void ValidateGalleryReturnToTop()
    {
        var expected = ViewModel.GalleryRows.FirstOrDefault()?.Cards.Count ?? 0;
        var realized = EnumerateRealizedGalleryCards()
            .Where(card => card.Container is not null)
            .ToDictionary(card => card.FlatIndex);
        for (var index = 0; index < expected; index++)
        {
            if (!realized.TryGetValue(index, out var card))
                throw new InvalidOperationException($"Gallery virtualization failed to restore item {index} after scrolling.");
            if (!card.MappingValid)
                throw new InvalidOperationException($"Gallery virtualization restored the wrong card at index {index}.");
        }
    }

    private static async Task<long> NextRenderedFrameTimestampAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            completion.TrySetResult(Stopwatch.GetTimestamp());
        };
        CompositionTarget.Rendering += handler;
        try
        {
            // Large pre-optimization fixtures can block the UI for several seconds.
            // Keep the timeout high enough to record that stall instead of hiding it.
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        finally
        {
            CompositionTarget.Rendering -= handler;
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a music folder", Multiselect = false };
        if (dialog.ShowDialog(this) == true) await ViewModel.AddLibraryFolderAsync(dialog.FolderName);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
        => OpenSettingsWindow();

    internal void OpenSettingsWindow()
    {
        try
        {
            if (_settingsWindow is not null)
            {
                if (_settingsWindow.WindowState == WindowState.Minimized)
                    _settingsWindow.WindowState = WindowState.Normal;

                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow { Owner = this, DataContext = ViewModel };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        catch (Exception exception)
        {
            _settingsWindow = null;
            ErrorDialog.Show(this, exception, string.Empty, true, "Settings could not be opened");
        }
    }

    private void Window_PreviewDragEnter(object sender, DragEventArgs e)
    {
        UpdateActiveDragPreview(e);
        UpdateFileDropFeedback(e);
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        UpdateActiveDragPreview(e);
        UpdateFileDropFeedback(e);
    }

    private void UpdateActiveDragPreview(DragEventArgs e)
    {
        if (_queueDragAdorner is null) return;
        _queueDragAdorner.MoveTo(e.GetPosition(ShellRoot), _queueDragGrip);
    }

    private void Window_PreviewDragLeave(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        await ViewModel.OpenLaunchTargetsAsync(paths);
    }

    private void UpdateFileDropFeedback(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var supported = paths.Count(path => Directory.Exists(path) || (File.Exists(path) && SupportedMediaFiles.IsSupported(path)));
        e.Effects = supported > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlayHint.Text = supported > 0
            ? $"{supported:N0} supported item{(supported == 1 ? string.Empty : "s")} · folders become sources, files enter the queue"
            : "No supported audio files or folders in this drop";
        DropOverlay.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void SeekSlider_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isSeekDragging = true;
        SeekSlider.CaptureMouse();
        ViewModel.BeginSeek();
        UpdateSeekFromPointer(e);
        e.Handled = true;
    }

    private void SeekSlider_MouseMove(object sender, MouseEventArgs e)
    {
        var width = Math.Max(1, SeekSlider.ActualWidth);
        var ratio = Math.Clamp(e.GetPosition(SeekSlider).X / width, 0, 1);
        var seconds = SeekSlider.Minimum + (SeekSlider.Maximum - SeekSlider.Minimum) * ratio;
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        SeekSlider.ToolTip = $"{time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss")} · right-click for bookmarks and playback options";
        if (!_isSeekDragging || e.LeftButton != MouseButtonState.Pressed) return;
        UpdateSeekFromPointer(e);
        e.Handled = true;
    }

    private async void SeekSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSeekDragging) return;
        UpdateSeekFromPointer(e);
        _isSeekDragging = false;
        SeekSlider.ReleaseMouseCapture();
        e.Handled = true;
        await ViewModel.CommitSeekAsync(SeekSlider.Value);
        SeekSlider.GetBindingExpression(RangeBase.ValueProperty)?.UpdateTarget();
    }

    private void UpdateSeekFromPointer(MouseEventArgs e)
    {
        SetSliderFromPointer(SeekSlider, e);
        ViewModel.PreviewSeek(SeekSlider.Value);
    }

    private void VolumeSlider_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isVolumeDragging = true;
        VolumeSlider.CaptureMouse();
        UpdateVolumeFromPointer(e);
        e.Handled = true;
    }

    private void VolumeSlider_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isVolumeDragging || e.LeftButton != MouseButtonState.Pressed) return;
        UpdateVolumeFromPointer(e);
        e.Handled = true;
    }

    private void VolumeSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isVolumeDragging) return;
        UpdateVolumeFromPointer(e);
        _isVolumeDragging = false;
        VolumeSlider.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void UpdateVolumeFromPointer(MouseEventArgs e)
    {
        SetSliderFromPointer(VolumeSlider, e);
        ViewModel.Volume = VolumeSlider.Value;
    }

    private static void SetSliderFromPointer(Slider slider, MouseEventArgs e)
    {
        var width = Math.Max(1, slider.ActualWidth);
        var ratio = Math.Clamp(e.GetPosition(slider).X / width, 0, 1);
        slider.SetCurrentValue(RangeBase.ValueProperty, slider.Minimum + ((slider.Maximum - slider.Minimum) * ratio));
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize()
    {
        // When navigation remains visible in full-screen mode, the same title
        // bar control should restore the saved window instead of combining a
        // maximized WindowState with the private full-screen state.
        if (_isFullScreen)
        {
            ExitFullScreen();
            return;
        }
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }
    private async void FullScreen_Click(object sender, RoutedEventArgs e) =>
        await ToggleFullScreenAsync();

    private async Task ToggleFullScreenAsync()
    {
        if (_fullScreenTransitionRunning) return;
        _fullScreenTransitionRunning = true;
        try
        {
            var animate = MotionPolicy.IsEnabled(ViewModel.AnimationsEnabled);
            if (animate)
                await AnimateShellAsync(.72, .985, 90, new QuadraticEase
                {
                    EasingMode = EasingMode.EaseIn
                });

            if (_isFullScreen) ExitFullScreen();
            else EnterFullScreen();

            ShellRoot.Opacity = animate ? .72 : 1;
            if (ShellRoot.RenderTransform is ScaleTransform scale)
            {
                scale.ScaleX = animate ? .985 : 1;
                scale.ScaleY = animate ? .985 : 1;
            }
            UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);

            if (animate)
                await AnimateShellAsync(1, 1, 150, new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                });
        }
        finally
        {
            _fullScreenTransitionRunning = false;
        }
    }

    private void EnterFullScreen()
    {
        _fullScreenRestoreState = WindowState;
        _fullScreenRestoreResizeMode = ResizeMode;
        _fullScreenRestoreBounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        // Full screen intentionally uses the monitor work area. The app keeps
        // its transport and seek controls above the taskbar while still
        // removing its own chrome/navigation according to the user setting.
        var monitorBounds = WindowMaximizeHelper.GetMonitorWorkArea(this);

        WindowState = WindowState.Normal;
        ResizeMode = ResizeMode.NoResize;
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
            chrome.ResizeBorderThickness = new Thickness(0);
        RootBorder.BorderThickness = new Thickness(0);
        var hideNavigation = ViewModel.SettingsWorkspace.FullscreenHideNavigation;
        TitleBarHost.Visibility = hideNavigation
            ? Visibility.Collapsed
            : Visibility.Visible;
        TitleBarRow.Height = hideNavigation
            ? new GridLength(0)
            : new GridLength(63);
        Left = monitorBounds.Left;
        Top = monitorBounds.Top;
        Width = monitorBounds.Width;
        Height = monitorBounds.Height;
        _isFullScreen = true;
        FullScreenButton.Content = "\uE73F";
        FullScreenButton.ToolTip = "Exit full screen (F11 or Esc)";
        AutomationProperties.SetName(FullScreenButton, "Exit full screen");
    }

    private void ExitFullScreen()
    {
        TitleBarHost.Visibility = Visibility.Visible;
        TitleBarRow.Height = new GridLength(63);
        RootBorder.BorderThickness = new Thickness(1);
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
            chrome.ResizeBorderThickness = new Thickness(6);
        ResizeMode = _fullScreenRestoreResizeMode;
        WindowState = WindowState.Normal;
        Left = _fullScreenRestoreBounds.Left;
        Top = _fullScreenRestoreBounds.Top;
        Width = _fullScreenRestoreBounds.Width;
        Height = _fullScreenRestoreBounds.Height;
        if (_fullScreenRestoreState == WindowState.Maximized)
            WindowState = WindowState.Maximized;
        _isFullScreen = false;
        FullScreenButton.Content = "\uE740";
        FullScreenButton.ToolTip = "Full screen (F11)";
        AutomationProperties.SetName(FullScreenButton, "Enter full screen");
    }

    private Task AnimateShellAsync(
        double opacity,
        double scale,
        int milliseconds,
        IEasingFunction easing)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var opacityAnimation = new DoubleAnimation(opacity, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        opacityAnimation.Completed += (_, _) =>
        {
            ShellRoot.BeginAnimation(OpacityProperty, null);
            ShellRoot.Opacity = opacity;
            if (ShellRoot.RenderTransform is ScaleTransform transform)
            {
                transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                transform.ScaleX = scale;
                transform.ScaleY = scale;
            }
            completion.TrySetResult(true);
        };
        ShellRoot.BeginAnimation(OpacityProperty, opacityAnimation);
        if (ShellRoot.RenderTransform is ScaleTransform transform)
        {
            transform.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(scale, duration)
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd
                });
            transform.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(scale, duration)
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd
                });
        }
        return completion.Task;
    }

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase or TextBox) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var pressedKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (pressedKey == Key.F11
            || pressedKey == Key.Escape && _isFullScreen)
        {
            await ToggleFullScreenAsync();
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is not DependencyObject source
            || FindVisualParent<ListBox>(source) is not { } list)
            return;
        var action = ListScrollKeyboardPolicy.ActionFor(
            e.Key == Key.System ? e.SystemKey : e.Key,
            Keyboard.Modifiers);
        if (action == ListScrollAction.None
            || FindVisualChild<ScrollViewer>(list) is not { } viewer)
            return;
        // Stop wheel/touch inertia, then let the ListBox perform its native
        // focus, selection, scrolling, and UI Automation behavior.
        SmoothScrollBehavior.Cancel(viewer);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F12 &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            PerformanceOverlay.ToggleCommand.Execute(null);
            e.Handled = true;
            return;
        }
        var modifiers = ShortcutModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= ShortcutModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= ShortcutModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= ShortcutModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= ShortcutModifiers.Windows;
        var gesture = new ShortcutGesture(modifiers, KeyInterop.VirtualKeyFromKey(key));
        if (!_shortcuts.TryGetInAppAction(gesture, out var action)) return;
        if (action == ShortcutActions.Search)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (!SearchBoxHasFocus() && ViewModel.ExecuteShortcut(action))
        {
            e.Handled = true;
        }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _lastInteraction = DateTimeOffset.UtcNow;
        var handled = e.ChangedButton switch
        {
            MouseButton.XButton1 => ViewModel.NavigateBack(),
            MouseButton.XButton2 => ViewModel.NavigateForward(),
            _ => false
        };
        if (handled) e.Handled = true;
    }

    private static bool SearchBoxHasFocus() => Keyboard.FocusedElement is System.Windows.Controls.TextBox;

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_isFullScreen) ExitFullScreen();
        _windowPlacement.Save(this);
        await ViewModel.ShutdownAsync();
        await _diagnostics.CompleteAsync();
        _lyricScrollCancellation?.Cancel();
        _lyricScrollCancellation?.Dispose();
        _queueScrollCancellation?.Cancel();
        _queueScrollCancellation?.Dispose();
        StopIdleCleanup();
        CancelDeferredPageLoads();
        PerformanceOverlay.Dispose();
        ViewModel.Queue.CollectionChanged -= QueueCollectionChanged;
        ViewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        ViewModel.NavigationStarting -= ViewModelOnNavigationStarting;
        ViewModel.SearchFocusRequested -= ViewModel_SearchFocusRequested;
        Loaded -= MainWindow_Loaded;
        SizeChanged -= MainWindow_SizeChanged;
        StateChanged -= MainWindow_StateChanged;
        _allowClose = true;
        Application.Current.Shutdown();
    }

    internal async Task CloseAfterBenchmarkAsync()
    {
        if (_allowClose) return;
        await ViewModel.ShutdownAsync();
        await _diagnostics.CompleteAsync();
        _lyricScrollCancellation?.Cancel();
        _lyricScrollCancellation?.Dispose();
        _queueScrollCancellation?.Cancel();
        _queueScrollCancellation?.Dispose();
        StopIdleCleanup();
        CancelDeferredPageLoads();
        PerformanceOverlay.Dispose();
        ViewModel.Queue.CollectionChanged -= QueueCollectionChanged;
        ViewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        ViewModel.NavigationStarting -= ViewModelOnNavigationStarting;
        ViewModel.SearchFocusRequested -= ViewModel_SearchFocusRequested;
        _allowClose = true;
        Application.Current.Shutdown();
    }

    private void CancelDeferredPageLoads()
    {
        if (_pendingGalleryScrollReapplication is not null)
        {
            CompositionTarget.Rendering -= _pendingGalleryScrollReapplication;
            _pendingGalleryScrollReapplication = null;
        }
        _galleryPageCancellation?.Cancel();
        _galleryPageCancellation?.Dispose();
        _sidebarPageCancellation?.Cancel();
        _sidebarPageCancellation?.Dispose();
        _trackPageCancellation?.Cancel();
        _trackPageCancellation?.Dispose();
    }

    private void StopIdleCleanup()
    {
        _idleCleanupTimer.Stop();
        PreviewMouseMove -= RecordUserInteraction;
        PreviewMouseWheel -= RecordUserInteraction;
        PreviewTouchDown -= RecordUserInteraction;
        Activated -= RecordWindowActivation;
    }

    private enum PageTarget
    {
        Gallery,
        Sidebar,
        Tracks
    }
}
