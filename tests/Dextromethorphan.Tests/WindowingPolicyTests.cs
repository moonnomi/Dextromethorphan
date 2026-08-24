using Dextromethorphan.App.UI;

namespace Dextromethorphan.Tests;

public sealed class WindowingPolicyTests
{
    [Theory]
    [InlineData(800, "Compact", false, false, 190, 160)]
    [InlineData(1024, "Medium", true, false, 230, 190)]
    [InlineData(1366, "Expanded", true, true, 310, 250)]
    [InlineData(1920, "Expanded", true, true, 310, 250)]
    [InlineData(3440, "Expanded", true, true, 310, 250)]
    public void ResponsiveShellPreservesPrimaryPlaybackSpace(
        double width,
        string expectedClass,
        bool fullSearch,
        bool secondaryUtilities,
        double identityWidth,
        double utilityWidth)
    {
        var metrics = ResponsiveShellMetrics.ForWidth(width);

        Assert.Equal(expectedClass, metrics.WidthClass.ToString());
        Assert.Equal(fullSearch, metrics.ShowFullSearch);
        Assert.Equal(secondaryUtilities, metrics.ShowSecondaryUtilities);
        Assert.Equal(identityWidth, metrics.PlayerIdentityWidth);
        Assert.Equal(utilityWidth, metrics.PlayerUtilitiesWidth);
        Assert.True(width - identityWidth - utilityWidth >= 300);
    }

    [Fact]
    public void CompactShellCapsQueueWithoutChangingTheSavedPreference()
    {
        var metrics = ResponsiveShellMetrics.ForWidth(800);

        Assert.Equal(272, metrics.QueueMaximumWidth, precision: 3);
        Assert.False(metrics.ShowFullSearch);
        Assert.Equal(0, metrics.SearchWidth);
        Assert.Equal(0, metrics.VisualizerWidth);
    }

    [Theory]
    [InlineData(96, 800, 600, 100, 100)]
    [InlineData(120, 1000, 750, 125, 125)]
    [InlineData(144, 1200, 900, 150, 150)]
    [InlineData(192, 1600, 1200, 200, 200)]
    public void RestoreScalesPhysicalBoundsForPerMonitorDpi(
        int dpi,
        int expectedWidth,
        int expectedHeight,
        int expectedLeft,
        int expectedTop)
    {
        var scale = dpi / 96d;
        var placement = Placement(
            new PixelRect(100, 100, 800, 600),
            new PixelRect(0, 0, 1920, 1040),
            "DISPLAY-A",
            96);
        var monitor = new MonitorWorkArea(
            "DISPLAY-A",
            new PixelRect(
                0,
                0,
                (int)Math.Round(1920 * scale),
                (int)Math.Round(1040 * scale)),
            dpi,
            dpi,
            true);

        var restored = WindowPlacementPolicy.Restore(
            placement,
            [monitor],
            800,
            600);

        Assert.Equal(expectedWidth, restored.Bounds.Width);
        Assert.Equal(expectedHeight, restored.Bounds.Height);
        Assert.Equal(expectedLeft, restored.Bounds.Left);
        Assert.Equal(expectedTop, restored.Bounds.Top);
    }

    [Fact]
    public void MissingMonitorFallsBackToNearestAvailableWorkArea()
    {
        var placement = Placement(
            new PixelRect(3000, 200, 1200, 800),
            new PixelRect(2560, 0, 2560, 1400),
            "REMOVED-DISPLAY",
            144);
        var primary = new MonitorWorkArea(
            "PRIMARY",
            new PixelRect(0, 0, 1920, 1040),
            96,
            96,
            true);

        var restored = WindowPlacementPolicy.Restore(
            placement,
            [primary],
            800,
            600);

        Assert.Equal(primary, restored.Monitor);
        Assert.True(restored.Bounds.Left >= primary.WorkArea.Left);
        Assert.True(restored.Bounds.Top >= primary.WorkArea.Top);
        Assert.True(restored.Bounds.Right <= primary.WorkArea.Right);
        Assert.True(restored.Bounds.Bottom <= primary.WorkArea.Bottom);
    }

    [Fact]
    public void ResolutionReductionClampsWindowToTheNewWorkArea()
    {
        var placement = Placement(
            new PixelRect(840, 360, 1600, 1000),
            new PixelRect(0, 0, 2560, 1400),
            "DISPLAY-A",
            96);
        var monitor = new MonitorWorkArea(
            "DISPLAY-A",
            new PixelRect(0, 0, 1366, 728),
            96,
            96,
            true);

        var restored = WindowPlacementPolicy.Restore(
            placement,
            [monitor],
            800,
            600);

        Assert.Equal(new PixelRect(0, 0, 1366, 728), restored.Bounds);
    }

    [Fact]
    public void MixedDpiFallbackChoosesTheDisplayContainingSavedBounds()
    {
        var placement = Placement(
            new PixelRect(2300, 180, 1000, 700),
            new PixelRect(1920, 0, 2560, 1400),
            "OLD-RIGHT",
            144);
        var primary = new MonitorWorkArea(
            "PRIMARY",
            new PixelRect(0, 0, 1920, 1040),
            96,
            96,
            true);
        var right = new MonitorWorkArea(
            "NEW-RIGHT",
            new PixelRect(1920, 0, 2560, 1400),
            144,
            144,
            false);

        var restored = WindowPlacementPolicy.Restore(
            placement,
            [primary, right],
            800,
            600);

        Assert.Equal(right, restored.Monitor);
        Assert.True(restored.Bounds.Left >= right.WorkArea.Left);
        Assert.True(restored.Bounds.Right <= right.WorkArea.Right);
    }

    [Fact]
    public void UsableTitleBarRequiresARecoverableDragSurface()
    {
        var monitor = new MonitorWorkArea(
            "PRIMARY",
            new PixelRect(0, 0, 1920, 1040),
            96,
            96,
            true);

        Assert.True(WindowPlacementPolicy.HasUsableTitleBar(
            new PixelRect(-700, 10, 800, 600),
            [monitor]));
        Assert.False(WindowPlacementPolicy.HasUsableTitleBar(
            new PixelRect(-900, 10, 800, 600),
            [monitor]));
    }

    [Fact]
    public void ManifestDeclaresPerMonitorV2BeforeLegacyFallback()
    {
        var root = FindRepositoryRoot();
        var manifest = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Dextromethorphan.App",
            "app.manifest"));

        Assert.Contains("PerMonitorV2", manifest, StringComparison.Ordinal);
        Assert.Contains("PerMonitorV2,PerMonitor", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeCaptionMaximizeClickInvokesExactlyOnceOnRelease()
    {
        var pressed = CaptionButtonInteractionPolicy.Apply(
            false,
            CaptionButtonPointerEvent.Press,
            pointerOverButton: true);
        var released = CaptionButtonInteractionPolicy.Apply(
            pressed.IsPressed,
            CaptionButtonPointerEvent.Release,
            pointerOverButton: true);

        Assert.True(pressed.Consume);
        Assert.False(pressed.Invoke);
        Assert.True(released.Consume);
        Assert.True(released.Invoke);
        Assert.False(released.IsPressed);
    }

    [Fact]
    public void NativeCaptionMaximizeReleaseOutsideButtonCancelsAction()
    {
        var pressed = CaptionButtonInteractionPolicy.Apply(
            false,
            CaptionButtonPointerEvent.Press,
            pointerOverButton: true);
        var released = CaptionButtonInteractionPolicy.Apply(
            pressed.IsPressed,
            CaptionButtonPointerEvent.Release,
            pointerOverButton: false);

        Assert.True(released.Consume);
        Assert.False(released.Invoke);
        Assert.False(released.IsPressed);
    }

    [Fact]
    public void NativeCaptionMaximizeCaptureLossClearsPendingPress()
    {
        var cancelled = CaptionButtonInteractionPolicy.Apply(
            true,
            CaptionButtonPointerEvent.Cancel,
            pointerOverButton: false);

        Assert.False(cancelled.IsPressed);
        Assert.False(cancelled.Invoke);
    }

    [Fact]
    public void FullScreenUsesTaskbarSafeWorkAreaAndRetainsKeyboardExit()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Dextromethorphan.App",
            "MainWindow.xaml.cs"));

        Assert.Contains(
            "WindowMaximizeHelper.GetMonitorWorkArea(this)",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains("pressedKey == Key.F11", mainWindow, StringComparison.Ordinal);
        Assert.Contains(
            "pressedKey == Key.Escape && _isFullScreen",
            mainWindow,
            StringComparison.Ordinal);
    }

    private static WindowPlacementRecord Placement(
        PixelRect bounds,
        PixelRect workArea,
        string device,
        int dpi) => new()
    {
        Bounds = bounds,
        MonitorWorkArea = workArea,
        MonitorDeviceName = device,
        DpiX = dpi,
        DpiY = dpi
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Dextromethorphan.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
