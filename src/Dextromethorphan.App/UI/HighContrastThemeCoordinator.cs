using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Dextromethorphan.App.UI;

/// <summary>
/// Keeps the application palette synchronized with Windows High Contrast.
/// The coordinator changes colors only; reduced-motion policy remains owned by
/// <see cref="MotionPolicy"/> and the user's animation preference.
/// </summary>
internal sealed class HighContrastThemeCoordinator : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _isHighContrast;
    private readonly Action _applyHighContrast;
    private readonly Action _restoreRequestedTheme;
    private readonly bool _subscribeToSystemEvents;
    private bool _wasHighContrast;
    private bool _started;
    private bool _disposed;

    public HighContrastThemeCoordinator(Application application)
        : this(
            application?.Dispatcher
                ?? throw new ArgumentNullException(nameof(application)),
            () => SystemParameters.HighContrast,
            () => ThemeManager.ApplyHighContrastToApplication(application),
            () => ThemeManager.RestoreRequestedTheme(application),
            subscribeToSystemEvents: true)
    {
    }

    internal HighContrastThemeCoordinator(
        Dispatcher dispatcher,
        Func<bool> isHighContrast,
        Action applyHighContrast,
        Action restoreRequestedTheme,
        bool subscribeToSystemEvents = false)
    {
        _dispatcher = dispatcher
            ?? throw new ArgumentNullException(nameof(dispatcher));
        _isHighContrast = isHighContrast
            ?? throw new ArgumentNullException(nameof(isHighContrast));
        _applyHighContrast = applyHighContrast
            ?? throw new ArgumentNullException(nameof(applyHighContrast));
        _restoreRequestedTheme = restoreRequestedTheme
            ?? throw new ArgumentNullException(nameof(restoreRequestedTheme));
        _subscribeToSystemEvents = subscribeToSystemEvents;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _started = true;
        _wasHighContrast = _isHighContrast();
        if (_subscribeToSystemEvents)
        {
            SystemParameters.StaticPropertyChanged += SystemPreferenceChanged;
            SystemEvents.UserPreferenceChanged += SystemUserPreferenceChanged;
        }
        if (_wasHighContrast)
            _applyHighContrast();
    }

    internal void RefreshNow()
    {
        if (_disposed || !_started) return;
        var isHighContrast = _isHighContrast();
        if (isHighContrast)
        {
            // Reapply even when the mode itself did not change: Windows can
            // replace individual High Contrast colors while it remains active.
            _applyHighContrast();
        }
        else if (_wasHighContrast)
        {
            _restoreRequestedTheme();
        }
        _wasHighContrast = isHighContrast;
    }

    private void SystemPreferenceChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
        => QueueRefresh();

    private void SystemUserPreferenceChanged(
        object sender,
        UserPreferenceChangedEventArgs eventArgs)
        => QueueRefresh();

    private void QueueRefresh()
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        if (_dispatcher.CheckAccess())
        {
            RefreshNow();
            return;
        }
        _ = _dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            RefreshNow);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_subscribeToSystemEvents && _started)
        {
            SystemParameters.StaticPropertyChanged -= SystemPreferenceChanged;
            SystemEvents.UserPreferenceChanged -= SystemUserPreferenceChanged;
        }
    }
}

internal sealed record HighContrastThemePalette(
    Color Window,
    Color WindowText,
    Color Highlight,
    Color HighlightText,
    Color GrayText)
{
    public static HighContrastThemePalette FromSystemColors() => new(
        SystemColors.WindowColor,
        SystemColors.WindowTextColor,
        SystemColors.HighlightColor,
        SystemColors.HighlightTextColor,
        SystemColors.GrayTextColor);
}
