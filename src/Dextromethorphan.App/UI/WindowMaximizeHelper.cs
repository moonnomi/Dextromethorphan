using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Dextromethorphan.App.UI;

/// <summary>
/// Keeps borderless WPF windows inside the monitor work area when they are
/// maximized. WindowChrome otherwise allows a custom title bar to cover the
/// taskbar on some Windows 10/11 configurations.
/// </summary>
internal static class WindowMaximizeHelper
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int WmCaptureChanged = 0x0215;
    private const int WmDpiChanged = 0x02E0;
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
    private const int HtMaxButton = 9;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private static long _nextSubclassId;

    private delegate nint SubclassProcedure(
        nint windowHandle,
        int message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nint referenceData);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitorHandle, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(
        nint windowHandle,
        int message,
        nint wParam,
        nint lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint windowHandle,
        SubclassProcedure callback,
        nuint subclassId,
        nint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint windowHandle,
        SubclassProcedure callback,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint windowHandle,
        int message,
        nint wParam,
        nint lParam);

    public static void Install(Window window)
        => Install(window, null, null, null);

    /// <summary>
    /// Installs borderless-window corrections. On Windows 11 the custom
    /// maximize button is surfaced as HTMAXBUTTON, which lets the shell show
    /// its native Snap Layout flyout and keeps Win+Arrow snapping native.
    /// </summary>
    public static void Install(
        Window window,
        Func<FrameworkElement?>? maximizeButtonProvider,
        Action? monitorEnvironmentChanged,
        Action? maximizeRequested = null)
    {
        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            var hook = new WindowHook(
                window,
                source,
                maximizeButtonProvider,
                monitorEnvironmentChanged,
                maximizeRequested);
            hook.Attach();
        }
    }

    public static Rect GetMonitorBounds(Window window)
        => GetMonitorRect(window, useWorkArea: false);

    /// <summary>
    /// Returns the usable bounds of the monitor containing the window. Unlike
    /// the monitor bounds, this excludes the taskbar and docked app bars.
    /// </summary>
    public static Rect GetMonitorWorkArea(Window window)
        => GetMonitorRect(window, useWorkArea: true);

    private static Rect GetMonitorRect(Window window, bool useWorkArea)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info))
            return useWorkArea
                ? SystemParameters.WorkArea
                : new Rect(0, 0, SystemParameters.PrimaryScreenWidth,
                    SystemParameters.PrimaryScreenHeight);

        var source = HwndSource.FromHwnd(handle);
        var transform = source?.CompositionTarget?.TransformFromDevice
                        ?? Matrix.Identity;
        var bounds = useWorkArea ? info.Work : info.Monitor;
        var topLeft = transform.Transform(
            new Point(bounds.Left, bounds.Top));
        var bottomRight = transform.Transform(
            new Point(bounds.Right, bounds.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    internal static bool IsNativeSnapLayoutAvailable =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    internal static SnapLayoutProbe ProbeSnapLayoutHitTest(
        Window window,
        FrameworkElement maximizeButton)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(maximizeButton);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero
            || !maximizeButton.IsVisible
            || !GetWindowRect(handle, out var windowRect))
            return default;
        var relative = maximizeButton.TranslatePoint(new Point(0, 0), window);
        var dpi = VisualTreeHelper.GetDpi(window);
        var centerX = windowRect.Left
                      + ((relative.X + (maximizeButton.ActualWidth / 2)) * dpi.DpiScaleX);
        var centerY = windowRect.Top
                      + ((relative.Y + (maximizeButton.ActualHeight / 2)) * dpi.DpiScaleY);
        var x = unchecked((ushort)(short)Math.Round(centerX));
        var y = unchecked((ushort)(short)Math.Round(centerY));
        var packed = new nint(x | ((long)y << 16));
        var result = (int)SendMessage(handle, WmNcHitTest, nint.Zero, packed);
        return new SnapLayoutProbe(
            result,
            (short)x,
            (short)y,
            relative.X,
            relative.Y,
            maximizeButton.ActualWidth,
            maximizeButton.ActualHeight,
            dpi.DpiScaleX,
            dpi.DpiScaleY);
    }

    /// <summary>
    /// Exercises the exact non-client message path used by a physical click
    /// on the custom maximize button. This is used only by the windowing smoke
    /// run; the resulting action is still dispatched by the installed hook.
    /// </summary>
    internal static bool InvokeSnapLayoutButtonForSmoke(
        Window window,
        FrameworkElement maximizeButton)
    {
        var probe = ProbeSnapLayoutHitTest(window, maximizeButton);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero || probe.Result != HtMaxButton) return false;
        var x = unchecked((ushort)(short)probe.ScreenX);
        var y = unchecked((ushort)(short)probe.ScreenY);
        var packed = new nint(x | ((long)y << 16));
        SendMessage(handle, WmNcLButtonDown, new nint(HtMaxButton), packed);
        SendMessage(handle, WmNcLButtonUp, new nint(HtMaxButton), packed);
        return true;
    }

    private sealed class WindowHook(
        Window window,
        HwndSource source,
        Func<FrameworkElement?>? maximizeButtonProvider,
        Action? monitorEnvironmentChanged,
        Action? maximizeRequested)
    {
        private bool _environmentCallbackPending;
        private bool _snapButtonPressed;
        private SubclassProcedure? _subclassProcedure;
        private nuint _subclassId;
        private bool _nativeSubclassInstalled;

        public void Attach()
        {
            _subclassProcedure = NativeSubclassProcedure;
            _subclassId = (nuint)Interlocked.Increment(ref _nextSubclassId);
            _nativeSubclassInstalled = SetWindowSubclass(
                source.Handle,
                _subclassProcedure,
                _subclassId,
                nint.Zero);
            if (!_nativeSubclassInstalled)
                source.AddHook(MessageHook);
            window.Closed += WindowOnClosed;
        }

        private void WindowOnClosed(object? sender, EventArgs e)
        {
            window.Closed -= WindowOnClosed;
            if (_nativeSubclassInstalled && _subclassProcedure is not null)
            {
                RemoveWindowSubclass(source.Handle, _subclassProcedure, _subclassId);
                _nativeSubclassInstalled = false;
            }
            else
            {
                source.RemoveHook(MessageHook);
            }
            _subclassProcedure = null;
        }

        private nint NativeSubclassProcedure(
            nint hwnd,
            int message,
            nint wParam,
            nint lParam,
            nuint subclassId,
            nint referenceData)
        {
            switch (message)
            {
                case WmGetMinMaxInfo when lParam != nint.Zero:
                {
                    var handled = false;
                    ApplyMinMaxInfo(hwnd, lParam, ref handled);
                    if (handled) return nint.Zero;
                    break;
                }
                case WmNcHitTest when lParam != nint.Zero:
                    if (TryHitTestSnapButton(lParam)) return HtMaxButton;
                    break;
                case WmNcLButtonDown when maximizeRequested is not null
                                              && (int)wParam == HtMaxButton:
                    // Returning HTMAXBUTTON enables the Windows 11 Snap Layout
                    // flyout, but it also turns this into a non-client click.
                    // Consume that click here so the borderless WPF window does
                    // not lose its maximize action when no native caption exists.
                {
                    var transition = CaptionButtonInteractionPolicy.Apply(
                        _snapButtonPressed,
                        CaptionButtonPointerEvent.Press,
                        pointerOverButton: true);
                    _snapButtonPressed = transition.IsPressed;
                    if (transition.Consume) return nint.Zero;
                    break;
                }
                case WmNcLButtonUp when maximizeRequested is not null
                                            && _snapButtonPressed:
                {
                    var transition = CaptionButtonInteractionPolicy.Apply(
                        _snapButtonPressed,
                        CaptionButtonPointerEvent.Release,
                        (int)wParam == HtMaxButton && TryHitTestSnapButton(lParam));
                    _snapButtonPressed = transition.IsPressed;
                    if (transition.Invoke) ScheduleMaximizeRequest();
                    if (transition.Consume) return nint.Zero;
                    break;
                }
                case WmCaptureChanged:
                    _snapButtonPressed = CaptionButtonInteractionPolicy.Apply(
                            _snapButtonPressed,
                            CaptionButtonPointerEvent.Cancel,
                            pointerOverButton: false)
                        .IsPressed;
                    break;
                case WmDpiChanged:
                case WmDisplayChange:
                case WmSettingChange:
                    ScheduleEnvironmentCallback();
                    break;
            }
            return DefSubclassProc(hwnd, message, wParam, lParam);
        }

        private nint MessageHook(
            nint hwnd,
            int message,
            nint wParam,
            nint lParam,
            ref bool handled)
        {
            switch (message)
            {
                case WmGetMinMaxInfo when lParam != nint.Zero:
                    ApplyMinMaxInfo(hwnd, lParam, ref handled);
                    break;
                case WmNcHitTest when lParam != nint.Zero:
                    if (TryHitTestSnapButton(lParam))
                    {
                        handled = true;
                        return HtMaxButton;
                    }
                    break;
                case WmNcLButtonDown when maximizeRequested is not null
                                              && (int)wParam == HtMaxButton:
                {
                    var transition = CaptionButtonInteractionPolicy.Apply(
                        _snapButtonPressed,
                        CaptionButtonPointerEvent.Press,
                        pointerOverButton: true);
                    _snapButtonPressed = transition.IsPressed;
                    handled = transition.Consume;
                    break;
                }
                case WmNcLButtonUp when maximizeRequested is not null
                                            && _snapButtonPressed:
                {
                    var transition = CaptionButtonInteractionPolicy.Apply(
                        _snapButtonPressed,
                        CaptionButtonPointerEvent.Release,
                        (int)wParam == HtMaxButton && TryHitTestSnapButton(lParam));
                    _snapButtonPressed = transition.IsPressed;
                    if (transition.Invoke) ScheduleMaximizeRequest();
                    handled = transition.Consume;
                    break;
                }
                case WmCaptureChanged:
                    _snapButtonPressed = CaptionButtonInteractionPolicy.Apply(
                            _snapButtonPressed,
                            CaptionButtonPointerEvent.Cancel,
                            pointerOverButton: false)
                        .IsPressed;
                    break;
                case WmDpiChanged:
                case WmDisplayChange:
                case WmSettingChange:
                    ScheduleEnvironmentCallback();
                    break;
            }
            return nint.Zero;
        }

        private bool TryHitTestSnapButton(nint lParam)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                || window.ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize
                || maximizeButtonProvider?.Invoke() is not { } button
                || !button.IsVisible
                || !button.IsEnabled
                || button.ActualWidth <= 0
                || button.ActualHeight <= 0)
                return false;

            var packed = lParam.ToInt64();
            var screenPoint = new Point(
                unchecked((short)(packed & 0xFFFF)),
                unchecked((short)((packed >> 16) & 0xFFFF)));
            try
            {
                var point = button.PointFromScreen(screenPoint);
                return point.X >= 0
                       && point.Y >= 0
                       && point.X < button.ActualWidth
                       && point.Y < button.ActualHeight;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private void ScheduleEnvironmentCallback()
        {
            if (monitorEnvironmentChanged is null || _environmentCallbackPending)
                return;
            _environmentCallbackPending = true;
            window.Dispatcher.BeginInvoke(
                () =>
                {
                    _environmentCallbackPending = false;
                    if (window.IsLoaded) monitorEnvironmentChanged();
                },
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ScheduleMaximizeRequest()
        {
            if (maximizeRequested is null) return;
            window.Dispatcher.BeginInvoke(
                maximizeRequested,
                System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private static void ApplyMinMaxInfo(
        nint hwnd,
        nint lParam,
        ref bool handled)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == nint.Zero) return;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var workWidth = info.Work.Right - info.Work.Left;
        var workHeight = info.Work.Bottom - info.Work.Top;
        var monitorWidth = info.Monitor.Right - info.Monitor.Left;
        var monitorHeight = info.Monitor.Bottom - info.Monitor.Top;

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMax.MaxPosition = new NativePoint
        {
            X = info.Work.Left - info.Monitor.Left,
            Y = info.Work.Top - info.Monitor.Top
        };
        minMax.MaxSize = new NativePoint
        {
            X = Math.Clamp(workWidth, 1, monitorWidth),
            Y = Math.Clamp(workHeight, 1, monitorHeight)
        };
        Marshal.StructureToPtr(minMax, lParam, false);
        handled = true;
    }
}

internal readonly record struct SnapLayoutProbe(
    int Result,
    int ScreenX,
    int ScreenY,
    double RelativeX,
    double RelativeY,
    double Width,
    double Height,
    double DpiScaleX,
    double DpiScaleY);

internal enum CaptionButtonPointerEvent
{
    Press,
    Release,
    Cancel
}

internal readonly record struct CaptionButtonTransition(
    bool IsPressed,
    bool Consume,
    bool Invoke);

/// <summary>
/// Models the native press/release sequence generated after WM_NCHITTEST
/// returns HTMAXBUTTON. Keeping it deterministic prevents both dropped clicks
/// and a double maximize when the pointer is released outside the button.
/// </summary>
internal static class CaptionButtonInteractionPolicy
{
    public static CaptionButtonTransition Apply(
        bool isPressed,
        CaptionButtonPointerEvent pointerEvent,
        bool pointerOverButton) => pointerEvent switch
    {
        CaptionButtonPointerEvent.Press when pointerOverButton =>
            new CaptionButtonTransition(true, true, false),
        CaptionButtonPointerEvent.Release when isPressed =>
            new CaptionButtonTransition(false, true, pointerOverButton),
        CaptionButtonPointerEvent.Cancel =>
            new CaptionButtonTransition(false, false, false),
        _ => new CaptionButtonTransition(isPressed, false, false)
    };
}
