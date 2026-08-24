using System.Windows;
using System.Windows.Media;
using Dextromethorphan.App.UI;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Tests;

public sealed class ThemeManagerTests
{
    [Theory]
    [InlineData("dark", "Dark")]
    [InlineData(" LIGHT ", "Light")]
    [InlineData("AMOLED", "Amoled")]
    [InlineData("unknown", "Dark")]
    [InlineData(null, "Dark")]
    public void ThemeNamesAreNormalized(string? value, string expected) =>
        Assert.Equal(expected, ThemeManager.NormalizeTheme(value));

    [Theory]
    [InlineData("#f83", "#FF8833")]
    [InlineData("#8f83", "#FF8833")]
    [InlineData("#ff8a3d", "#FF8A3D")]
    [InlineData("#80ff8a3d", "#FF8A3D")]
    [InlineData("not-a-color", "#8290FF")]
    [InlineData(null, "#8290FF")]
    public void AccentValuesAreParsedAndCanonicalized(
        string? value,
        string expected) =>
        Assert.Equal(expected, ThemeManager.NormalizeAccent(value));

    [Fact]
    public void EveryThemeProducesAnAccessibleAccentAndForeground()
    {
        foreach (var theme in new[] { "Dark", "Light", "Amoled" })
        {
            foreach (var accent in new[]
                     {
                         "#080808",
                         "#777777",
                         "#FFF766",
                         "#2C66FF",
                         "#FF8A3D"
                     })
            {
                var palette = ThemeManager.CreatePalette(theme, accent);

                Assert.True(
                    palette.AccentContrast.Ratio
                    >= ThemeManager.MinimumAccentContrast,
                    $"{theme}/{accent} produced {palette.AccentContrast.Ratio:0.###}:1");
                Assert.True(
                    palette.AccentForegroundContrastRatio
                    >= ThemeManager.MinimumAccentContrast,
                    $"{theme}/{accent} foreground produced {palette.AccentForegroundContrastRatio:0.###}:1");
                Assert.Equal(
                    palette.AccentContrast.Ratio,
                    ThemeManager.ContrastRatio(
                        palette.Accent,
                        palette.OpaqueBackground),
                    precision: 10);
            }
        }
    }

    [Fact]
    public void ContrastCorrectionMovesTowardTheBestEndpointWithoutChangingHueDirection()
    {
        var onDark = ThemeManager.EnsureContrast(
            Color.FromRgb(10, 12, 50),
            Color.FromRgb(5, 5, 6));
        var onLight = ThemeManager.EnsureContrast(
            Color.FromRgb(255, 246, 102),
            Color.FromRgb(247, 247, 250));

        Assert.True(onDark.WasAdjusted);
        Assert.True(onDark.Adjusted.B > onDark.Adjusted.R);
        Assert.True(onDark.Adjusted.B > onDark.Requested.B);
        Assert.True(onLight.WasAdjusted);
        Assert.True(onLight.Adjusted.R >= onLight.Adjusted.B);
        Assert.True(onLight.Adjusted.R < onLight.Requested.R);
        Assert.True(onDark.Ratio >= 4.5);
        Assert.True(onLight.Ratio >= 4.5);
    }

    [Fact]
    public void BackgroundOpacityAffectsOnlyTheBackgroundAlphaAndIsNormalized()
    {
        var palette = ThemeManager.CreatePalette("Dark", "#FF8A3D", 0.5);

        Assert.Equal(184, palette.Background.A);
        Assert.Equal(255, palette.OpaqueBackground.A);
        Assert.Equal(255, palette.Surface.A);
        Assert.Equal(ThemeManager.MinimumBackgroundOpacity, palette.BackgroundOpacity);
    }

    [Fact]
    public void SettingsConfigurationUsesExistingFieldsAndDefaultsFutureOpacity()
    {
        var settings = new AppSettings
        {
            Theme = "light",
            AccentColor = "#123456",
            FontFamily = "Segoe UI",
            FontSize = 16
        };

        var configuration = ThemeConfiguration.FromSettings(settings);

        Assert.Equal("light", configuration.Theme);
        Assert.Equal("#123456", configuration.AccentColor);
        Assert.Equal("Segoe UI", configuration.FontFamily);
        Assert.Equal(16, configuration.FontSize);
        Assert.Equal(1, configuration.BackgroundOpacity);
    }

    [Fact]
    public void ResourceApplicationUpdatesNestedThemeResourcesAndRootAliases()
    {
        var resources = new ResourceDictionary();
        var themeResources = new ResourceDictionary
        {
            ["BackgroundColor"] = Colors.Magenta,
            ["AccentBrush"] = new SolidColorBrush(Colors.Magenta),
            ["AccentGradient"] = new LinearGradientBrush()
        };
        resources.MergedDictionaries.Add(themeResources);
        var configuration = new ThemeConfiguration(
            "Amoled",
            "#101010",
            "Segoe UI",
            17,
            0.75);

        var palette = ThemeManager.Apply(resources, configuration);

        Assert.Equal(palette.Background, themeResources["BackgroundColor"]);
        Assert.Equal(
            palette.Accent,
            Assert.IsType<SolidColorBrush>(
                themeResources["AccentBrush"]).Color);
        Assert.IsType<LinearGradientBrush>(
            themeResources["AccentGradient"]);
        Assert.Equal(
            palette.AccentForeground,
            Assert.IsType<Color>(
                resources[ThemeManager.AccentForegroundColorResourceKey]));
        Assert.Equal(
            "Segoe UI",
            Assert.IsType<FontFamily>(
                resources[ThemeManager.FontFamilyResourceKey]).Source);
        Assert.Equal(17d, resources[ThemeManager.FontSizeResourceKey]);
        Assert.Equal(
            0.75d,
            resources[ThemeManager.BackgroundOpacityResourceKey]);
    }
}
