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
    private const uint MonitorDefaultToNearest = 0x00000002;

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

    public static void Install(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource source)
            source.AddHook(WindowMessageHook);
    }

    public static Rect GetMonitorBounds(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info))
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth,
                SystemParameters.PrimaryScreenHeight);

        var source = HwndSource.FromHwnd(handle);
        var transform = source?.CompositionTarget?.TransformFromDevice
                        ?? Matrix.Identity;
        var topLeft = transform.Transform(
            new Point(info.Monitor.Left, info.Monitor.Top));
        var bottomRight = transform.Transform(
            new Point(info.Monitor.Right, info.Monitor.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private static nint WindowMessageHook(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message != WmGetMinMaxInfo || lParam == nint.Zero)
            return nint.Zero;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == nint.Zero)
            return nint.Zero;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return nint.Zero;

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
        minMax.MaxSize = new NativePoint { X = workWidth, Y = workHeight };
        // Keep the values valid even on a monitor whose work area has been
        // reported incorrectly by a display driver.
        minMax.MaxSize.X = Math.Clamp(minMax.MaxSize.X, 1, monitorWidth);
        minMax.MaxSize.Y = Math.Clamp(minMax.MaxSize.Y, 1, monitorHeight);
        Marshal.StructureToPtr(minMax, lParam, false);
        handled = true;
        return nint.Zero;
    }
}
