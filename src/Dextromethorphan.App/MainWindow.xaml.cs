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
    private readonly IShortcutService _shortcuts;
    private readonly ISystemMediaTransportService _systemMedia;

    public MainWindow(MainViewModel viewModel, IShortcutService shortcuts, ISystemMediaTransportService systemMedia)
    {
        InitializeComponent();
        ViewModel = viewModel;
        _shortcuts = shortcuts;
        _systemMedia = systemMedia;
        DataContext = viewModel;
        ViewModel.PropertyChanged += ViewModelOnPropertyChanged;
    }

    public MainViewModel ViewModel { get; }

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
        if (StartupOrbit.RenderTransform is RotateTransform orbit) orbit.BeginAnimation(RotateTransform.AngleProperty, null);
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
            Dispatcher.BeginInvoke(AnimateViewTransition, DispatcherPriority.Render);
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
        if (!ViewModel.AnimationsEnabled || StartupOverlay.Visibility == Visibility.Visible)
        {
            ViewTransitionHost.Opacity = 1;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ViewTransitionHost.BeginAnimation(OpacityProperty, null);
        ViewTransitionHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.76, 1, TimeSpan.FromMilliseconds(155)) { EasingFunction = ease });
        if (ViewTransitionHost.RenderTransform is TranslateTransform transform)
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(175)) { EasingFunction = ease });
        }
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
        if (e.ExtentHeight <= 0 || e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 260) return;
        ViewModel.LoadMoreGalleryGroups();
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
        _lyricScrollCancellation?.Cancel();
        _lyricScrollCancellation?.Dispose();
        ViewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        _allowClose = true;
        Close();
    }
}
