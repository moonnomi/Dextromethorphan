using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Dextromethorphan.App.UI;

namespace Dextromethorphan.Tests;

public sealed class HighContrastThemeTests
{
    [Fact]
    public void SemanticResourcesUseOnlyTheProvidedSystemPalette()
    {
        var resources = new ResourceDictionary();
        var theme = new ResourceDictionary
        {
            ["BackgroundColor"] = Colors.Magenta,
            ["WarningBrush"] = new SolidColorBrush(Colors.Magenta),
            ["AccentGradient"] = new LinearGradientBrush(),
            ["PlayerGradient"] = new LinearGradientBrush(),
            ["MotionFast"] = new Duration(TimeSpan.FromMilliseconds(140))
        };
        resources.MergedDictionaries.Add(theme);
        var palette = new HighContrastThemePalette(
            Color.FromRgb(1, 2, 3),
            Color.FromRgb(251, 252, 253),
            Color.FromRgb(20, 40, 80),
            Color.FromRgb(255, 255, 255),
            Color.FromRgb(128, 129, 130));

        ThemeManager.ApplyHighContrast(
            resources,
            palette,
            "Segoe UI",
            18);

        Assert.Equal(palette.Window, resources["BackgroundColor"]);
        Assert.Equal(palette.Window, BrushColor(resources, "BackgroundBrush"));
        Assert.Equal(palette.Window, BrushColor(resources, "AppBackgroundBrush"));
        Assert.Equal(palette.Window, BrushColor(resources, "SurfaceBrush"));
        Assert.Equal(palette.Window, BrushColor(resources, "SurfaceRaisedBrush"));
        Assert.Equal(palette.Highlight, BrushColor(resources, "SurfaceHoverBrush"));
        Assert.Equal(palette.WindowText, BrushColor(resources, "BorderBrush"));
        Assert.Equal(palette.WindowText, BrushColor(resources, "TextBrush"));
        Assert.Equal(palette.GrayText, BrushColor(resources, "TextMutedBrush"));
        Assert.Equal(palette.Highlight, BrushColor(resources, "AccentBrush"));
        Assert.Equal(palette.Highlight, BrushColor(resources, "AccentSoftBrush"));
        Assert.Equal(palette.HighlightText, BrushColor(resources, "AccentForegroundBrush"));

        foreach (var key in new[]
                 {
                     "WarningBrush", "ErrorBrush", "SuccessBrush", "DangerBrush"
                 })
            Assert.Equal(palette.WindowText, BrushColor(resources, key));
        foreach (var key in new[]
                 {
                     "WarningSoftBrush", "ErrorSoftBrush", "SuccessSoftBrush"
                 })
            Assert.Equal(palette.Window, BrushColor(resources, key));

        Assert.Equal(
            palette.Highlight,
            Assert.IsType<SolidColorBrush>(theme["AccentGradient"]).Color);
        Assert.Equal(
            palette.Window,
            Assert.IsType<SolidColorBrush>(theme["PlayerGradient"]).Color);
        Assert.Equal(1d, resources[ThemeManager.BackgroundOpacityResourceKey]);
        Assert.Equal(18d, resources[ThemeManager.FontSizeResourceKey]);

        // Theme synchronization is color-only and must not change the app's
        // reduced-motion token or policy.
        Assert.Equal(
            TimeSpan.FromMilliseconds(140),
            Assert.IsType<Duration>(theme["MotionFast"]).TimeSpan);

        ThemeManager.Apply(
            resources,
            new ThemeConfiguration(
                "Light",
                "#123456",
                "Segoe UI",
                14));
        Assert.Equal(
            Color.FromRgb(95, 59, 0),
            BrushColor(resources, "WarningBrush"));
        Assert.Equal(
            Color.FromRgb(255, 241, 194),
            BrushColor(resources, "WarningSoftBrush"));
        Assert.NotEqual(
            palette.Window,
            BrushColor(resources, "BackgroundBrush"));
    }

    [Fact]
    public void CoordinatorReappliesSystemColorsAndRestoresOnlyOnExit()
    {
        var highContrast = false;
        var applied = 0;
        var restored = 0;
        using var coordinator = new HighContrastThemeCoordinator(
            Dispatcher.CurrentDispatcher,
            () => highContrast,
            () => applied++,
            () => restored++);

        coordinator.Start();
        Assert.Equal(0, applied);
        Assert.Equal(0, restored);

        highContrast = true;
        coordinator.RefreshNow();
        coordinator.RefreshNow();
        Assert.Equal(2, applied);
        Assert.Equal(0, restored);

        highContrast = false;
        coordinator.RefreshNow();
        coordinator.RefreshNow();
        Assert.Equal(2, applied);
        Assert.Equal(1, restored);
    }

    [Fact]
    public void SystemPaletteIsSourcedFromWindowsColors()
    {
        var palette = HighContrastThemePalette.FromSystemColors();

        Assert.Equal(SystemColors.WindowColor, palette.Window);
        Assert.Equal(SystemColors.WindowTextColor, palette.WindowText);
        Assert.Equal(SystemColors.HighlightColor, palette.Highlight);
        Assert.Equal(SystemColors.HighlightTextColor, palette.HighlightText);
        Assert.Equal(SystemColors.GrayTextColor, palette.GrayText);
    }

    private static Color BrushColor(
        ResourceDictionary resources,
        string key) =>
        Assert.IsType<SolidColorBrush>(resources[key]).Color;
}
