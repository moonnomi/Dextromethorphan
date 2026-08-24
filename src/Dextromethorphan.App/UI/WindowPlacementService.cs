using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.App.UI;

/// <summary>
/// Persists the main window in physical pixels and restores it against the
/// monitors that exist now. Keeping the policy in pixel space avoids the
/// coordinate virtualization errors that otherwise appear after moving a
/// PerMonitorV2 window between differently-scaled displays.
/// </summary>
public sealed class WindowPlacementService(AppPaths paths)
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private readonly string _path = Path.Combine(paths.Root, "window-placement.json");

    public bool TryRestore(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!File.Exists(_path)) return false;

        try
        {
            var placement = JsonSerializer.Deserialize<WindowPlacementRecord>(
                File.ReadAllText(_path),
                JsonOptions);
            if (placement is null || placement.SchemaVersion != 1)
                return false;

            var monitors = NativeMonitorCatalog.Enumerate();
            if (monitors.Count == 0) return false;
            var target = WindowPlacementPolicy.Restore(
                placement,
                monitors,
                window.MinWidth,
                window.MinHeight);
            if (target.Bounds.Width <= 0 || target.Bounds.Height <= 0)
                return false;

            var handle = new WindowInteropHelper(window).Handle;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            if (!SetWindowPos(
                    handle,
                    nint.Zero,
                    target.Bounds.Left,
                    target.Bounds.Top,
                    target.Bounds.Width,
                    target.Bounds.Height,
                    SwpNoActivate | SwpNoZOrder | SwpNoOwnerZOrder))
                return false;

            if (placement.IsMaximized)
            {
                window.Dispatcher.BeginInvoke(
                    () => window.WindowState = WindowState.Maximized,
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public void Save(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == nint.Zero) return;
            var monitor = NativeMonitorCatalog.ForWindow(handle);
            if (monitor is null) return;

            var logical = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.Width, window.Height)
                : window.RestoreBounds;
            if (!IsFiniteUsable(logical)) return;

            var source = HwndSource.FromHwnd(handle);
            var toDevice = source?.CompositionTarget?.TransformToDevice
                           ?? System.Windows.Media.Matrix.Identity;
            var topLeft = toDevice.Transform(logical.TopLeft);
            var bottomRight = toDevice.Transform(logical.BottomRight);
            var physical = PixelRect.FromEdges(
                (int)Math.Round(topLeft.X),
                (int)Math.Round(topLeft.Y),
                (int)Math.Round(bottomRight.X),
                (int)Math.Round(bottomRight.Y));
            if (physical.Width <= 0 || physical.Height <= 0) return;

            var record = new WindowPlacementRecord
            {
                SchemaVersion = 1,
                Bounds = physical,
                MonitorDeviceName = monitor.Value.DeviceName,
                MonitorWorkArea = monitor.Value.WorkArea,
                DpiX = monitor.Value.DpiX,
                DpiY = monitor.Value.DpiY,
                IsMaximized = window.WindowState == WindowState.Maximized
            };
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(record, JsonOptions));
            File.Move(temporary, _path, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
    }

    /// <summary>
    /// Recovers a normal window after a monitor, taskbar work area, or DPI
    /// configuration changes while the process is running.
    /// </summary>
    public void EnsureVisible(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.WindowState != WindowState.Normal) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero || !GetWindowRect(handle, out var rect)) return;
        var bounds = PixelRect.FromEdges(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var monitors = NativeMonitorCatalog.Enumerate();
        if (monitors.Count == 0
            || WindowPlacementPolicy.HasUsableTitleBar(bounds, monitors))
            return;

        var monitor = WindowPlacementPolicy.NearestMonitor(bounds, monitors);
        var clamped = WindowPlacementPolicy.ClampToWorkArea(
            bounds,
            monitor.WorkArea,
            window.MinWidth,
            window.MinHeight,
            monitor.DpiX,
            monitor.DpiY);
        SetWindowPos(
            handle,
            nint.Zero,
            clamped.Left,
            clamped.Top,
            clamped.Width,
            clamped.Height,
            SwpNoActivate | SwpNoZOrder | SwpNoOwnerZOrder);
    }

    private static bool IsFiniteUsable(Rect value) =>
        !value.IsEmpty
        && double.IsFinite(value.Left)
        && double.IsFinite(value.Top)
        && double.IsFinite(value.Width)
        && double.IsFinite(value.Height)
        && value.Width > 0
        && value.Height > 0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}

internal sealed record WindowPlacementRecord
{
    public int SchemaVersion { get; init; } = 1;
    public PixelRect Bounds { get; init; }
    public string MonitorDeviceName { get; init; } = string.Empty;
    public PixelRect MonitorWorkArea { get; init; }
    public int DpiX { get; init; } = 96;
    public int DpiY { get; init; } = 96;
    public bool IsMaximized { get; init; }
}

internal readonly record struct WindowRestoreTarget(
    PixelRect Bounds,
    MonitorWorkArea Monitor);

internal readonly record struct MonitorWorkArea(
    string DeviceName,
    PixelRect WorkArea,
    int DpiX,
    int DpiY,
    bool IsPrimary);

internal readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public long Area => Math.Max(0L, Width) * Math.Max(0L, Height);

    public static PixelRect FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public long IntersectionArea(PixelRect other)
    {
        var width = Math.Max(0, Math.Min(Right, other.Right) - Math.Max(Left, other.Left));
        var height = Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top));
        return (long)width * height;
    }
}

internal static class WindowPlacementPolicy
{
    private const int TitleBarVisibleHeight = 32;
    private const int TitleBarVisibleWidth = 96;

    public static WindowRestoreTarget Restore(
        WindowPlacementRecord placement,
        IReadOnlyList<MonitorWorkArea> monitors,
        double minimumWidthDip,
        double minimumHeightDip)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
            throw new ArgumentException("At least one monitor is required.", nameof(monitors));

        var monitor = monitors.FirstOrDefault(value =>
            !string.IsNullOrWhiteSpace(placement.MonitorDeviceName)
            && value.DeviceName.Equals(
                placement.MonitorDeviceName,
                StringComparison.OrdinalIgnoreCase));
        if (monitor == default)
            monitor = NearestMonitor(placement.Bounds, monitors);

        var sourceWork = placement.MonitorWorkArea.Width > 0
                         && placement.MonitorWorkArea.Height > 0
            ? placement.MonitorWorkArea
            : placement.Bounds;
        var scaleX = monitor.DpiX / (double)Math.Max(1, placement.DpiX);
        var scaleY = monitor.DpiY / (double)Math.Max(1, placement.DpiY);
        var width = Math.Max(1, (int)Math.Round(placement.Bounds.Width * scaleX));
        var height = Math.Max(1, (int)Math.Round(placement.Bounds.Height * scaleY));
        var xFraction = RelativePosition(
            placement.Bounds.Left - sourceWork.Left,
            sourceWork.Width - placement.Bounds.Width);
        var yFraction = RelativePosition(
            placement.Bounds.Top - sourceWork.Top,
            sourceWork.Height - placement.Bounds.Height);
        var left = monitor.WorkArea.Left + (int)Math.Round(
            xFraction * Math.Max(0, monitor.WorkArea.Width - width));
        var top = monitor.WorkArea.Top + (int)Math.Round(
            yFraction * Math.Max(0, monitor.WorkArea.Height - height));
        var clamped = ClampToWorkArea(
            new PixelRect(left, top, width, height),
            monitor.WorkArea,
            minimumWidthDip,
            minimumHeightDip,
            monitor.DpiX,
            monitor.DpiY);
        return new WindowRestoreTarget(clamped, monitor);
    }

    public static PixelRect ClampToWorkArea(
        PixelRect bounds,
        PixelRect workArea,
        double minimumWidthDip,
        double minimumHeightDip,
        int dpiX,
        int dpiY)
    {
        var minimumWidth = Math.Max(1, (int)Math.Ceiling(
            minimumWidthDip * Math.Max(96, dpiX) / 96d));
        var minimumHeight = Math.Max(1, (int)Math.Ceiling(
            minimumHeightDip * Math.Max(96, dpiY) / 96d));
        // On a display smaller than the documented minimum, fit the available
        // work area rather than placing controls behind the taskbar.
        var width = Math.Clamp(
            bounds.Width,
            Math.Min(minimumWidth, workArea.Width),
            workArea.Width);
        var height = Math.Clamp(
            bounds.Height,
            Math.Min(minimumHeight, workArea.Height),
            workArea.Height);
        var left = Math.Clamp(
            bounds.Left,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - width));
        var top = Math.Clamp(
            bounds.Top,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - height));
        return new PixelRect(left, top, width, height);
    }

    public static bool HasUsableTitleBar(
        PixelRect bounds,
        IReadOnlyList<MonitorWorkArea> monitors) =>
        monitors.Any(monitor =>
        {
            var title = new PixelRect(
                bounds.Left,
                bounds.Top,
                bounds.Width,
                Math.Min(TitleBarVisibleHeight, bounds.Height));
            var intersectionWidth = Math.Max(
                0,
                Math.Min(title.Right, monitor.WorkArea.Right)
                - Math.Max(title.Left, monitor.WorkArea.Left));
            var intersectionHeight = Math.Max(
                0,
                Math.Min(title.Bottom, monitor.WorkArea.Bottom)
                - Math.Max(title.Top, monitor.WorkArea.Top));
            return intersectionWidth >= Math.Min(TitleBarVisibleWidth, title.Width)
                   && intersectionHeight >= Math.Min(TitleBarVisibleHeight, title.Height);
        });

    public static MonitorWorkArea NearestMonitor(
        PixelRect bounds,
        IReadOnlyList<MonitorWorkArea> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
            throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        return monitors
            .OrderByDescending(value => bounds.IntersectionArea(value.WorkArea))
            .ThenBy(value => CenterDistanceSquared(bounds, value.WorkArea))
            .ThenByDescending(value => value.IsPrimary)
            .First();
    }

    private static double RelativePosition(int offset, int available) =>
        available <= 0 ? 0.5 : Math.Clamp(offset / (double)available, 0, 1);

    private static long CenterDistanceSquared(PixelRect left, PixelRect right)
    {
        var dx = ((long)left.Left * 2 + left.Width)
                 - ((long)right.Left * 2 + right.Width);
        var dy = ((long)left.Top * 2 + left.Height)
                 - ((long)right.Top * 2 + right.Height);
        return (dx * dx) + (dy * dy);
    }
}

internal static class NativeMonitorCatalog
{
    private const int MonitorInfoPrimary = 0x00000001;
    private const uint MonitorDefaultToNearest = 0x00000002;

    public static IReadOnlyList<MonitorWorkArea> Enumerate()
    {
        var result = new List<MonitorWorkArea>();
        EnumDisplayMonitors(nint.Zero, nint.Zero, (monitor, _, _, _) =>
        {
            if (TryRead(monitor, out var value)) result.Add(value);
            return true;
        }, nint.Zero);
        return result;
    }

    public static MonitorWorkArea? ForWindow(nint windowHandle)
    {
        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        return monitor != nint.Zero && TryRead(monitor, out var value)
            ? value
            : null;
    }

    private static bool TryRead(nint monitor, out MonitorWorkArea value)
    {
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            value = default;
            return false;
        }
        var work = PixelRect.FromEdges(
            info.Work.Left,
            info.Work.Top,
            info.Work.Right,
            info.Work.Bottom);
        var dpiX = 96u;
        var dpiY = 96u;
        try
        {
            _ = GetDpiForMonitor(monitor, 0, out dpiX, out dpiY);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        value = new MonitorWorkArea(
            info.DeviceName ?? string.Empty,
            work,
            (int)Math.Max(96, dpiX),
            (int)Math.Max(96, dpiY),
            (info.Flags & MonitorInfoPrimary) != 0);
        return true;
    }

    private delegate bool MonitorEnumProc(
        nint monitor,
        nint deviceContext,
        nint monitorRect,
        nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? DeviceName;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRect,
        MonitorEnumProc callback,
        nint data);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);
}
