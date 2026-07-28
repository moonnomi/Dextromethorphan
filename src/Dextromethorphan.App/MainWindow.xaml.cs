using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Dextromethorphan.App.Performance;
using Dextromethorphan.App.Diagnostics;
using Dextromethorphan.App.UI;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Models;
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
    private QueueEntryViewModel? _queuePointerEntry;
    private bool _queueDragStarted;
    private DateTime _startupStartedAt;
    private DateTimeOffset? _firstGalleryArtworkRenderedAt;
    private readonly IShortcutService _shortcuts;
    private readonly ISystemMediaTransportService _systemMedia;
    private readonly DeveloperDiagnostics _diagnostics;
    private readonly ArtworkImageService _artworkImages;
    private readonly NavigationViewStateStore _viewStates = new();
    private bool _scrollRestorePending;
    private bool _restoringScrollState;
    private CancellationTokenSource? _galleryPageCancellation;
    private CancellationTokenSource? _trackPageCancellation;
    private int _viewTransitionAnimationVersion;

    public MainWindow(
        MainViewModel viewModel,
        IShortcutService shortcuts,
        ISystemMediaTransportService systemMedia,
        DeveloperDiagnostics diagnostics,
        ArtworkImageService artworkImages,
        PerformanceOverlayViewModel performanceOverlay)
    {
        InitializeComponent();
        ViewModel = viewModel;
        _shortcuts = shortcuts;
        _systemMedia = systemMedia;
        _diagnostics = diagnostics;
        _artworkImages = artworkImages;
        PerformanceOverlay = performanceOverlay;
        PerformanceOverlay.Attach(this);
        DataContext = viewModel;
        ViewModel.PropertyChanged += ViewModelOnPropertyChanged;
    }

    public MainViewModel ViewModel { get; }
    public PerformanceOverlayViewModel PerformanceOverlay { get; }
    internal DateTimeOffset? FirstGalleryArtworkRenderedAt => _firstGalleryArtworkRenderedAt;
    internal ArtworkRuntimeMetrics ArtworkMetrics => _artworkImages.GetRuntimeMetrics();

    public void BeginStartupPresentation()
    {
        _startupStartedAt = DateTime.UtcNow;
        StartupOverlay.Visibility = Visibility.Visible;
        StartupOverlay.Opacity = 1;

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
        var remaining = TimeSpan.FromMilliseconds(420) - (DateTime.UtcNow - _startupStartedAt);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);

        if (!ViewModel.AnimationsEnabled)
        {
            StopStartupMotion();
            StartupOverlay.Visibility = Visibility.Collapsed;
            return;
        }

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
            Dispatcher.BeginInvoke(AnimateViewTransition, DispatcherPriority.Render);
            if (_diagnostics.Enabled)
                _ = RecordViewRenderAsync(Stopwatch.GetTimestamp(), ViewModel.CurrentView, ViewModel.IsCollectionDetailOpen);
        }
        if (e.PropertyName is nameof(MainViewModel.PrimaryViewStateKey) or nameof(MainViewModel.ContentViewStateKey)
            or nameof(MainViewModel.CurrentView) or nameof(MainViewModel.IsCollectionDetailOpen))
            ScheduleScrollStateRestore();
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
            if (!ViewModel.AnimationsEnabled)
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
        if (!ViewModel.AnimationsEnabled || StartupOverlay.Visibility == Visibility.Visible)
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
        var handle = new WindowInteropHelper(this).Handle;
        _shortcuts.Attach(handle);
        _systemMedia.Attach(handle);
    }

    private void TrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.PlaySelectedCommand.CanExecute(null)) ViewModel.PlaySelectedCommand.Execute(null);
    }

    private void QueueList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _queueDragStart = e.GetPosition(QueueList);
        _queueDragStarted = false;
        _queuePointerEntry = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as QueueEntryViewModel;
    }

    private void QueueList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_queuePointerEntry is null || e.LeftButton != MouseButtonState.Pressed || _queueDragStarted) return;
        var position = e.GetPosition(QueueList);
        if (Math.Abs(position.X - _queueDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _queueDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _queueDragStarted = true;
        var entry = _queuePointerEntry;
        DragDrop.DoDragDrop(QueueList, new DataObject(typeof(QueueEntryViewModel), entry), DragDropEffects.Move);
        _queuePointerEntry = null;
    }

    private void QueueList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var entry = _queuePointerEntry;
        _queuePointerEntry = null;
        if (_queueDragStarted || entry is null) return;
        if (ViewModel.PlayQueueEntryCommand.CanExecute(entry)) ViewModel.PlayQueueEntryCommand.Execute(entry);
        e.Handled = true;
    }

    private void QueueList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(QueueEntryViewModel)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void QueueList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(QueueEntryViewModel)) is not QueueEntryViewModel source) return;
        var hit = QueueList.InputHitTest(e.GetPosition(QueueList)) as DependencyObject;
        if (FindVisualParent<ListBoxItem>(hit)?.DataContext is QueueEntryViewModel target)
            ViewModel.MoveQueueEntry(source.Entry.Id, target.Entry.Id);
        e.Handled = true;
    }

    private void Gallery_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_restoringScrollState && GalleryList.IsVisible && e.OriginalSource is ScrollViewer)
            _viewStates.Capture(ViewModel.PrimaryViewStateKey, e.VerticalOffset, ViewModel.GalleryGroups.Count);
        if (e.ExtentHeight <= 0 || e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 260)
        {
            _galleryPageCancellation?.Cancel();
            return;
        }
        SchedulePageLoad(GalleryList, true);
    }

    private void GalleryList_Loaded(object sender, RoutedEventArgs e) => ScheduleScrollStateRestore();

    private void SidebarList_Loaded(object sender, RoutedEventArgs e) => ScheduleScrollStateRestore();

    private void SidebarList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_restoringScrollState && SidebarList.IsVisible && e.OriginalSource is ScrollViewer)
            _viewStates.Capture(ViewModel.PrimaryViewStateKey, e.VerticalOffset);
    }

    private void TrackList_Loaded(object sender, RoutedEventArgs e) => ScheduleScrollStateRestore();

    private void TrackList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_restoringScrollState || sender is not ListBox list || !list.IsVisible || e.OriginalSource is not ScrollViewer) return;
        _viewStates.Capture(ViewModel.ContentViewStateKey, e.VerticalOffset, ViewModel.BrowseTracks.Count);
        if (e.ExtentHeight > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 360)
            SchedulePageLoad(list, false);
        else
            _trackPageCancellation?.Cancel();
    }

    private void SchedulePageLoad(ListBox list, bool gallery)
    {
        var previous = gallery ? _galleryPageCancellation : _trackPageCancellation;
        previous?.Cancel();
        previous?.Dispose();
        var cancellation = new CancellationTokenSource();
        if (gallery) _galleryPageCancellation = cancellation;
        else _trackPageCancellation = cancellation;
        _ = LoadPageAfterScrollIdleAsync(list, gallery, cancellation.Token);
    }

    private async Task LoadPageAfterScrollIdleAsync(ListBox list, bool gallery, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(80, cancellationToken);
            if (!list.IsVisible || list.IsMouseCaptureWithin || SmoothScrollBehavior.IsAnimating(list))
            {
                await Task.Delay(80, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (!list.IsVisible || list.IsMouseCaptureWithin || SmoothScrollBehavior.IsAnimating(list)) return;
            if (gallery) ViewModel.LoadMoreGalleryGroups();
            else ViewModel.LoadMoreBrowseTracks();
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
                var state = _viewStates.Get(ViewModel.PrimaryViewStateKey);
                ViewModel.EnsureGalleryGroupsLoaded(state.MaterializedItemCount);
                GalleryList.UpdateLayout();
                FindVisualChild<ScrollViewer>(GalleryList)?.ScrollToVerticalOffset(state.VerticalOffset);
            }

            if (SidebarList.IsVisible)
            {
                SidebarList.UpdateLayout();
                var state = _viewStates.Get(ViewModel.PrimaryViewStateKey);
                FindVisualChild<ScrollViewer>(SidebarList)?.ScrollToVerticalOffset(state.VerticalOffset);
            }

            foreach (var list in FindVisualChildren<ListBox>(ViewTransitionHost)
                         .Where(x => x.IsVisible
                             && !ReferenceEquals(x, GalleryList)
                             && !ReferenceEquals(x, SidebarList)
                             && !ReferenceEquals(x, LyricsList)))
            {
                var state = _viewStates.Get(ViewModel.ContentViewStateKey);
                ViewModel.EnsureBrowseTracksLoaded(state.MaterializedItemCount);
                list.UpdateLayout();
                FindVisualChild<ScrollViewer>(list)?.ScrollToVerticalOffset(state.VerticalOffset);
            }
        }
        finally { _restoringScrollState = false; }
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
        }
        return samples;
    }

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
        var targetOffset = Math.Min(viewer.ScrollableHeight, Math.Max(0, viewer.ViewportHeight * 1.5));
        viewer.ScrollToVerticalOffset(targetOffset);
        await NextRenderedFrameTimestampAsync(cancellationToken);
        var expectedOffset = viewer.VerticalOffset;
        var expectedCount = ViewModel.GalleryGroups.Count;
        var expectedSelection = ViewModel.SelectedCard?.Key;

        ViewModel.NavigateCommand.Execute("Artists");
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
        var offsetRestored = Math.Abs(expectedOffset - restoredOffset) <= 3;
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
        while (ArtworkMetrics.QueueDepth > 0 && timeout.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(25, cancellationToken);
        await _diagnostics.WaitForIdleAsync(cancellationToken);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, cancellationToken);
        await Task.Delay(250, cancellationToken);
    }

    internal async Task<FramePerformanceMetrics> MeasureAlbumScrollPerformanceAsync(CancellationToken cancellationToken)
    {
        ViewModel.NavigateCommand.Execute("Albums");
        await NextRenderedFrameTimestampAsync(cancellationToken);
        GalleryList.UpdateLayout();
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
            if (frame % 15 == 0) ViewModel.LoadMoreGalleryGroups();
            var step = Math.Max(32, viewer.ViewportHeight / 10);
            var target = Math.Min(viewer.ScrollableHeight, viewer.VerticalOffset + step);
            if (target >= viewer.ScrollableHeight - 1 && ViewModel.GalleryGroups.Count < ViewModel.ActiveGroups.Count)
            {
                ViewModel.LoadMoreGalleryGroups();
                target = viewer.ScrollableHeight;
            }
            viewer.ScrollToVerticalOffset(target);
            var rendered = await NextRenderedFrameTimestampAsync(cancellationToken);
            intervals.Add(Stopwatch.GetElapsedTime(previous, rendered).TotalMilliseconds);
            previous = rendered;
        }
        viewer.ScrollToTop();
        await NextRenderedFrameTimestampAsync(cancellationToken);
        GalleryList.UpdateLayout();
        ValidateGalleryReturnToTop();
        return PerformanceStatistics.Frames(intervals, ViewModel.GalleryGroups.Count);
    }

    private void ValidateGalleryReturnToTop()
    {
        var expected = Math.Min(ViewModel.GalleryGroups.Count, 16);
        for (var index = 0; index < expected; index++)
        {
            if (GalleryList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem container)
                throw new InvalidOperationException($"Gallery virtualization failed to restore item {index} after scrolling.");
            if (!ReferenceEquals(container.DataContext, ViewModel.GalleryGroups[index]))
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
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase or TextBox) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
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
        await ViewModel.ShutdownAsync();
        await _diagnostics.CompleteAsync();
        _lyricScrollCancellation?.Cancel();
        _lyricScrollCancellation?.Dispose();
        CancelDeferredPageLoads();
        PerformanceOverlay.Dispose();
        ViewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        _allowClose = true;
        Close();
    }

    internal async Task CloseAfterBenchmarkAsync()
    {
        if (_allowClose) return;
        await ViewModel.ShutdownAsync();
        await _diagnostics.CompleteAsync();
        _lyricScrollCancellation?.Cancel();
        _lyricScrollCancellation?.Dispose();
        CancelDeferredPageLoads();
        PerformanceOverlay.Dispose();
        ViewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        _allowClose = true;
        Close();
    }

    private void CancelDeferredPageLoads()
    {
        _galleryPageCancellation?.Cancel();
        _galleryPageCancellation?.Dispose();
        _trackPageCancellation?.Cancel();
        _trackPageCancellation?.Dispose();
    }
}
