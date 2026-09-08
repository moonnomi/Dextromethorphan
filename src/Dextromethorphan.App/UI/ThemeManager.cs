using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.App.UI;

/// <summary>
/// A normalized, UI-independent description of the user's theme choices.
/// </summary>
public sealed record ThemeConfiguration(
    string Theme,
    string AccentColor,
    string FontFamily,
    double FontSize,
    double BackgroundOpacity = 1,
    string? AmbienceColor = null,
    bool AnimateTransition = false)
{
    public static ThemeConfiguration FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new ThemeConfiguration(
            settings.Theme,
            settings.AccentColor,
            settings.FontFamily,
            settings.FontSize,
            settings.BackgroundOpacity);
    }
}

/// <summary>
/// The result of changing a foreground until it meets a requested WCAG
/// contrast ratio. Color alpha is deliberately ignored for contrast math;
/// accessibility contrast is evaluated against the theme's opaque base.
/// </summary>
public sealed record ThemeContrastResult(
    Color Requested,
    Color Adjusted,
    Color Background,
    double Ratio,
    bool WasAdjusted);

/// <summary>
/// Pure color output produced by <see cref="ThemeManager.CreatePalette"/>.
/// Background contains the requested opacity while OpaqueBackground is the
/// color used for contrast calculations.
/// </summary>
public sealed record ThemePalette(
    string Theme,
    Color OpaqueBackground,
    Color Background,
    Color Surface,
    Color SurfaceRaised,
    Color SurfaceHover,
    Color Border,
    Color Text,
    Color TextMuted,
    Color Accent,
    Color AccentForeground,
    Color AccentSoft,
    double BackgroundOpacity,
    ThemeContrastResult AccentContrast,
    double AccentForegroundContrastRatio)
{
    public string NormalizedAccent => ThemeManager.ToHex(Accent);
}

/// <summary>
/// Builds and applies the application palette. Palette construction and
/// contrast functions are pure, making the accessibility policy testable
/// without creating a WPF Application.
/// </summary>
public static class ThemeManager
{
    private static readonly object RequestedThemeConfigurationKey = new();

    public const double MinimumAccentContrast = 4.5;
    public const string DefaultTheme = "Dark";
    public const string DefaultAccent = "#8290FF";
    public const string DefaultFontFamily = "Segoe UI Variable Text";
    public const double DefaultFontSize = 14;
    public const double MinimumBackgroundOpacity = 0.72;

    public const string FontFamilyResourceKey = "AppFontFamily";
    public const string FontSizeResourceKey = "AppFontSize";
    public const string BackgroundOpacityResourceKey = "AppBackgroundOpacity";
    public const string AccentForegroundColorResourceKey = "AccentForegroundColor";
    public const string AccentForegroundBrushResourceKey = "AccentForegroundBrush";

    /// <summary>Returns Dark, Light, or Amoled using canonical casing.</summary>
    public static string NormalizeTheme(string? theme) =>
        theme?.Trim().ToUpperInvariant() switch
        {
            "LIGHT" => "Light",
            "AMOLED" => "Amoled",
            _ => DefaultTheme
        };

    /// <summary>
    /// Parses #RGB, #ARGB, #RRGGBB, or #AARRGGBB and returns canonical opaque
    /// #RRGGBB. Invalid input returns the normalized fallback.
    /// </summary>
    public static string NormalizeAccent(
        string? accent,
        string fallback = DefaultAccent) =>
        ToHex(TryParseAccent(accent, out var parsed)
            ? Opaque(parsed)
            : TryParseAccent(fallback, out var parsedFallback)
                ? Opaque(parsedFallback)
                : Color.FromRgb(130, 144, 255));

    public static bool TryParseAccent(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var hex = value.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length is 3 or 4)
            hex = string.Concat(hex.Select(character => new string(character, 2)));
        if (hex.Length is not (6 or 8)
            || !hex.All(Uri.IsHexDigit))
            return false;

        if (!uint.TryParse(
                hex,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var packed))
            return false;

        color = hex.Length == 8
            ? Color.FromArgb(
                (byte)(packed >> 24),
                (byte)(packed >> 16),
                (byte)(packed >> 8),
                (byte)packed)
            : Color.FromRgb(
                (byte)(packed >> 16),
                (byte)(packed >> 8),
                (byte)packed);
        return true;
    }

    public static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static ThemePalette CreatePalette(
        string? theme,
        string? accentColor,
        double backgroundOpacity = 1) =>
        CreatePalette(new ThemeConfiguration(
            theme ?? DefaultTheme,
            accentColor ?? DefaultAccent,
            DefaultFontFamily,
            DefaultFontSize,
            backgroundOpacity));

    public static ThemePalette CreatePalette(ThemeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var theme = NormalizeTheme(configuration.Theme);
        var baseColors = theme switch
        {
            "Light" => new BasePalette(
                Color.FromRgb(247, 247, 250),
                Color.FromRgb(255, 255, 255),
                Color.FromRgb(240, 241, 245),
                Color.FromRgb(229, 231, 237),
                Color.FromRgb(205, 209, 219),
                Color.FromRgb(23, 25, 31),
                Color.FromRgb(91, 98, 113)),
            "Amoled" => new BasePalette(
                Colors.Black,
                Color.FromRgb(5, 5, 6),
                Color.FromRgb(13, 14, 17),
                Color.FromRgb(23, 25, 30),
                Color.FromRgb(36, 38, 44),
                Colors.White,
                Color.FromRgb(153, 158, 170)),
            _ => new BasePalette(
                Color.FromRgb(11, 12, 16),
                Color.FromRgb(17, 19, 25),
                Color.FromRgb(24, 27, 35),
                Color.FromRgb(34, 38, 49),
                Color.FromRgb(41, 46, 58),
                Color.FromRgb(244, 246, 250),
                Color.FromRgb(146, 153, 168))
        };

        if (TryParseAccent(configuration.AmbienceColor, out var ambience))
        {
            var hsl = ToHsl(ambience);
            Color Tint(double lightness) => FromHsl(hsl.Hue, Math.Min(hsl.Saturation, 0.45), lightness);
            var light = theme == "Light";
            baseColors = baseColors with
            {
                Background = Tint(light ? 0.96 : 0.055),
                Surface = Tint(light ? 0.99 : 0.08),
                SurfaceRaised = Tint(light ? 0.94 : 0.115),
                SurfaceHover = Tint(light ? 0.90 : 0.15)
            };
        }

        TryParseAccent(
            NormalizeAccent(configuration.AccentColor),
            out var requestedAccent);
        var contrast = EnsureContrast(
            requestedAccent,
            baseColors.Background,
            MinimumAccentContrast);
        var accentForeground = BestContrastingForeground(contrast.Adjusted);
        var opacity = NormalizeOpacity(configuration.BackgroundOpacity);
        var background = Color.FromArgb(
            (byte)Math.Round(255 * opacity, MidpointRounding.AwayFromZero),
            baseColors.Background.R,
            baseColors.Background.G,
            baseColors.Background.B);
        var softAlpha = theme == "Light" ? (byte)38 : (byte)48;
        var accentSoft = Color.FromArgb(
            softAlpha,
            contrast.Adjusted.R,
            contrast.Adjusted.G,
            contrast.Adjusted.B);

        return new ThemePalette(
            theme,
            baseColors.Background,
            background,
            baseColors.Surface,
            baseColors.SurfaceRaised,
            baseColors.SurfaceHover,
            baseColors.Border,
            baseColors.Text,
            baseColors.TextMuted,
            contrast.Adjusted,
            accentForeground,
            accentSoft,
            opacity,
            contrast,
            ContrastRatio(accentForeground, contrast.Adjusted));
    }

    /// <summary>
    /// Finds the smallest HSL-lightness movement toward the higher-contrast
    /// endpoint. Hue and saturation are held constant until the endpoint.
    /// </summary>
    public static ThemeContrastResult EnsureContrast(
        Color foreground,
        Color background,
        double minimumRatio = MinimumAccentContrast)
    {
        if (!double.IsFinite(minimumRatio) || minimumRatio < 1 || minimumRatio > 21)
            throw new ArgumentOutOfRangeException(nameof(minimumRatio));

        foreground = Opaque(foreground);
        background = Opaque(background);
        var initialRatio = ContrastRatio(foreground, background);
        if (initialRatio >= minimumRatio)
            return new ThemeContrastResult(
                foreground,
                foreground,
                background,
                initialRatio,
                false);

        var hsl = ToHsl(foreground);
        var whiteRatio = ContrastRatio(Colors.White, background);
        var blackRatio = ContrastRatio(Colors.Black, background);
        var lighten = whiteRatio >= blackRatio;
        var endpoint = lighten ? Colors.White : Colors.Black;
        if (ContrastRatio(endpoint, background) < minimumRatio)
            return new ThemeContrastResult(
                foreground,
                endpoint,
                background,
                ContrastRatio(endpoint, background),
                true);

        double passing;
        if (lighten)
        {
            var failing = hsl.Lightness;
            passing = 1;
            for (var iteration = 0; iteration < 32; iteration++)
            {
                var candidate = (failing + passing) / 2;
                if (ContrastRatio(FromHsl(hsl.Hue, hsl.Saturation, candidate), background) >= minimumRatio)
                    passing = candidate;
                else
                    failing = candidate;
            }
        }
        else
        {
            passing = 0;
            var failing = hsl.Lightness;
            for (var iteration = 0; iteration < 32; iteration++)
            {
                var candidate = (passing + failing) / 2;
                if (ContrastRatio(FromHsl(hsl.Hue, hsl.Saturation, candidate), background) >= minimumRatio)
                    passing = candidate;
                else
                    failing = candidate;
            }
        }

        var adjusted = FromHsl(hsl.Hue, hsl.Saturation, passing);
        var ratio = ContrastRatio(adjusted, background);
        if (ratio < minimumRatio)
        {
            // Byte rounding can land just below the binary-search boundary.
            // Move in tiny increments in the already-selected direction.
            for (var step = 1; step <= 1024 && ratio < minimumRatio; step++)
            {
                var lightness = Math.Clamp(
                    passing + (lighten ? step : -step) / 1024d,
                    0,
                    1);
                adjusted = FromHsl(hsl.Hue, hsl.Saturation, lightness);
                ratio = ContrastRatio(adjusted, background);
            }
        }

        return new ThemeContrastResult(
            foreground,
            adjusted,
            background,
            ratio,
            true);
    }

    public static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(Opaque(first));
        var secondLuminance = RelativeLuminance(Opaque(second));
        var lighter = Math.Max(firstLuminance, secondLuminance);
        var darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static ThemePalette Apply(
        ResourceDictionary resources,
        ThemeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(configuration);
        var palette = CreatePalette(configuration);
        Apply(resources, palette, configuration.FontFamily, configuration.FontSize);
        return palette;
    }

    public static ThemePalette Apply(
        ResourceDictionary resources,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Apply(resources, ThemeConfiguration.FromSettings(settings));
    }

    public static void Apply(
        ResourceDictionary resources,
        ThemePalette palette,
        string? fontFamily = DefaultFontFamily,
        double fontSize = DefaultFontSize)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(palette);
        var normalizedFont = NormalizeFontFamily(fontFamily);
        var normalizedSize = NormalizeFontSize(fontSize);
        ApplyRecursive(
            resources,
            palette,
            normalizedFont,
            normalizedSize,
            new HashSet<ResourceDictionary>(ReferenceEqualityComparer.Instance),
            ensureAllResources: true);
    }

    public static ThemePalette ApplyToApplication(
        Application application,
        ThemeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!application.Dispatcher.CheckAccess())
            return application.Dispatcher.Invoke(
                () => ApplyToApplication(application, configuration));

        // Keep the user's most recent selection even while Windows owns the
        // palette. This lets High Contrast remain authoritative without losing
        // live changes made in Settings, and gives the coordinator an exact
        // theme to restore when High Contrast is turned off.
        application.Properties[RequestedThemeConfigurationKey] = configuration;
        if (SystemParameters.HighContrast)
        {
            var requestedPalette = CreatePalette(configuration);
            ApplyHighContrastToApplication(application, configuration);
            return requestedPalette;
        }

        return ApplyStandardToApplication(application, configuration);
    }

    internal static void ApplyHighContrastToApplication(
        Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.Invoke(
                () => ApplyHighContrastToApplication(application));
            return;
        }

        var configuration = application.Properties[RequestedThemeConfigurationKey]
            as ThemeConfiguration
            ?? new ThemeConfiguration(
                DefaultTheme,
                DefaultAccent,
                DefaultFontFamily,
                DefaultFontSize);
        ApplyHighContrastToApplication(application, configuration);
    }

    internal static void RestoreRequestedTheme(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.Invoke(
                () => RestoreRequestedTheme(application));
            return;
        }

        if (SystemParameters.HighContrast) return;
        var configuration = application.Properties[RequestedThemeConfigurationKey]
            as ThemeConfiguration;
        if (configuration is not null)
            ApplyStandardToApplication(application, configuration);
    }

    internal static void ApplyHighContrast(
        ResourceDictionary resources,
        HighContrastThemePalette palette,
        string? fontFamily = DefaultFontFamily,
        double fontSize = DefaultFontSize)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(palette);
        ApplyHighContrastRecursive(
            resources,
            palette,
            NormalizeFontFamily(fontFamily),
            NormalizeFontSize(fontSize),
            new HashSet<ResourceDictionary>(
                ReferenceEqualityComparer.Instance),
            ensureAllResources: true);
    }

    private static ThemePalette ApplyStandardToApplication(
        Application application,
        ThemeConfiguration configuration)
    {
        var animate = configuration.AnimateTransition && SystemParameters.ClientAreaAnimation
            && !SystemParameters.HighContrast;
        var snapshots = new List<(ResourceDictionary Resources, object Key, Brush Previous)>();
        var visited = new HashSet<ResourceDictionary>(ReferenceEqualityComparer.Instance);
        void Capture(ResourceDictionary resources)
        {
            if (!visited.Add(resources)) return;
            foreach (var merged in resources.MergedDictionaries) Capture(merged);
            foreach (var key in resources.Keys)
                if (resources[key] is Brush brush)
                    snapshots.Add((resources, key, brush.CloneCurrentValue()));
        }
        if (animate)
        {
            Capture(application.Resources);
            foreach (Window window in application.Windows) Capture(window.Resources);
        }
        var palette = Apply(application.Resources, configuration);
        var font = new FontFamily(NormalizeFontFamily(configuration.FontFamily));
        var size = NormalizeFontSize(configuration.FontSize);
        foreach (Window window in application.Windows)
        {
            var previousBackground = animate ? window.Background?.CloneCurrentValue() : null;
            Apply(window.Resources, palette, font.Source, size);
            window.Background = NewBrush(palette.Background);
            if (previousBackground is not null) TransitionBrush(window.Background, previousBackground);
            window.Foreground = NewBrush(palette.Text);
            window.FontFamily = font;
            window.FontSize = size;
            window.InvalidateVisual();
        }
        foreach (var snapshot in snapshots)
            if (snapshot.Resources[snapshot.Key] is Brush target)
                TransitionBrush(target, snapshot.Previous);
        return palette;
    }

    private static void TransitionBrush(Brush target, Brush previous)
    {
        if (target.IsFrozen) return;
        static ColorAnimation Animation(Color from, Color to) => new(from, to,
            new Duration(TimeSpan.FromMilliseconds(1600)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop
        };
        if (target is SolidColorBrush solid && previous is SolidColorBrush oldSolid)
        {
            var destination = (Color)solid.GetAnimationBaseValue(SolidColorBrush.ColorProperty);
            solid.BeginAnimation(SolidColorBrush.ColorProperty,
                Animation(oldSolid.Color, destination), HandoffBehavior.SnapshotAndReplace);
        }
        else if (target is GradientBrush gradient && previous is GradientBrush oldGradient
            && gradient.GradientStops.Count == oldGradient.GradientStops.Count)
        {
            for (var i = 0; i < gradient.GradientStops.Count; i++)
            {
                var stop = gradient.GradientStops[i];
                stop.BeginAnimation(GradientStop.ColorProperty,
                    Animation(oldGradient.GradientStops[i].Color, (Color)stop.GetAnimationBaseValue(GradientStop.ColorProperty)), HandoffBehavior.SnapshotAndReplace);
            }
        }
    }

    private static void ApplyHighContrastToApplication(
        Application application,
        ThemeConfiguration configuration)
    {
        var palette = HighContrastThemePalette.FromSystemColors();
        var font = new FontFamily(NormalizeFontFamily(configuration.FontFamily));
        var size = NormalizeFontSize(configuration.FontSize);
        ApplyHighContrast(
            application.Resources,
            palette,
            font.Source,
            size);
        foreach (Window window in application.Windows)
        {
            ApplyHighContrast(window.Resources, palette, font.Source, size);
            window.Background = SystemColors.WindowBrush;
            window.Foreground = SystemColors.WindowTextBrush;
            window.FontFamily = font;
            window.FontSize = size;
            window.InvalidateVisual();
        }
    }

    public static ThemePalette ApplyToApplication(
        Application application,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ApplyToApplication(
            application,
            ThemeConfiguration.FromSettings(settings));
    }

    public static ThemePalette ApplyToCurrentApplication(
        ThemeConfiguration configuration) =>
        ApplyToApplication(
            Application.Current
                ?? throw new InvalidOperationException(
                    "A WPF Application must exist before applying a theme."),
            configuration);

    public static ThemePalette ApplyToCurrentApplication(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ApplyToCurrentApplication(ThemeConfiguration.FromSettings(settings));
    }

    private static void ApplyRecursive(
        ResourceDictionary resources,
        ThemePalette palette,
        string fontFamily,
        double fontSize,
        HashSet<ResourceDictionary> visited,
        bool ensureAllResources)
    {
        if (!visited.Add(resources)) return;
        foreach (var merged in resources.MergedDictionaries)
            ApplyRecursive(
                merged,
                palette,
                fontFamily,
                fontSize,
                visited,
                ensureAllResources: false);

        var colors = ColorResources(palette);
        foreach (var pair in colors)
            if (ensureAllResources || resources.Contains(pair.Key))
                resources[pair.Key] = pair.Value;

        foreach (var pair in BrushResources(palette))
            if (ensureAllResources || resources.Contains(pair.Key))
                SetBrush(resources, pair.Key, pair.Value);

        if (ensureAllResources || resources.Contains(FontFamilyResourceKey))
            resources[FontFamilyResourceKey] = new FontFamily(fontFamily);
        if (ensureAllResources || resources.Contains(FontSizeResourceKey))
            resources[FontSizeResourceKey] = fontSize;
        if (ensureAllResources || resources.Contains(BackgroundOpacityResourceKey))
            resources[BackgroundOpacityResourceKey] = palette.BackgroundOpacity;

        if (resources.Contains("AccentGradient"))
            resources["AccentGradient"] = CreateAccentGradient(palette.Accent);
        if (resources.Contains("PlayerGradient"))
            resources["PlayerGradient"] = CreatePlayerGradient(palette);
    }

    private static void ApplyHighContrastRecursive(
        ResourceDictionary resources,
        HighContrastThemePalette palette,
        string fontFamily,
        double fontSize,
        HashSet<ResourceDictionary> visited,
        bool ensureAllResources)
    {
        if (!visited.Add(resources)) return;
        foreach (var merged in resources.MergedDictionaries)
            ApplyHighContrastRecursive(
                merged,
                palette,
                fontFamily,
                fontSize,
                visited,
                ensureAllResources: false);

        foreach (var pair in HighContrastColorResources(palette))
            if (ensureAllResources || resources.Contains(pair.Key))
                resources[pair.Key] = pair.Value;

        foreach (var pair in HighContrastBrushResources(palette))
            if (ensureAllResources || resources.Contains(pair.Key))
                resources[pair.Key] = new SolidColorBrush(pair.Value);

        if (ensureAllResources || resources.Contains(FontFamilyResourceKey))
            resources[FontFamilyResourceKey] = new FontFamily(fontFamily);
        if (ensureAllResources || resources.Contains(FontSizeResourceKey))
            resources[FontSizeResourceKey] = fontSize;
        if (ensureAllResources || resources.Contains(BackgroundOpacityResourceKey))
            resources[BackgroundOpacityResourceKey] = 1d;

        // Decorative gradients can obscure system-selected foregrounds. A
        // single system color keeps the same resource contract while leaving
        // selection and player surfaces legible.
        if (resources.Contains("AccentGradient"))
            resources["AccentGradient"] = new SolidColorBrush(palette.Highlight);
        if (resources.Contains("PlayerGradient"))
            resources["PlayerGradient"] = new SolidColorBrush(palette.Window);
    }

    private static IReadOnlyDictionary<string, Color> ColorResources(
        ThemePalette palette) => new Dictionary<string, Color>
        {
            ["BackgroundColor"] = palette.Background,
            ["SurfaceColor"] = palette.Surface,
            ["SurfaceRaisedColor"] = palette.SurfaceRaised,
            ["SurfaceHoverColor"] = palette.SurfaceHover,
            ["BorderColor"] = palette.Border,
            ["TextColor"] = palette.Text,
            ["TextMutedColor"] = palette.TextMuted,
            ["AccentColor"] = palette.Accent,
            [AccentForegroundColorResourceKey] = palette.AccentForeground
        };

    private static IReadOnlyDictionary<string, Color> BrushResources(
        ThemePalette palette) => new Dictionary<string, Color>
        {
            ["BackgroundBrush"] = palette.Background,
            ["AppBackgroundBrush"] = palette.Background,
            ["SurfaceBrush"] = palette.Surface,
            ["SurfaceRaisedBrush"] = palette.SurfaceRaised,
            ["SurfaceHoverBrush"] = palette.SurfaceHover,
            ["BorderBrush"] = palette.Border,
            ["TextBrush"] = palette.Text,
            ["TextMutedBrush"] = palette.TextMuted,
            ["AccentBrush"] = palette.Accent,
            ["AccentSoftBrush"] = palette.AccentSoft,
            [AccentForegroundBrushResourceKey] = palette.AccentForeground,
            ["WarningBrush"] = Color.FromRgb(95, 59, 0),
            ["WarningSoftBrush"] = Color.FromRgb(255, 241, 194),
            ["ErrorBrush"] = Color.FromRgb(123, 30, 43),
            ["ErrorSoftBrush"] = Color.FromRgb(255, 228, 232),
            ["SuccessBrush"] = Color.FromRgb(20, 91, 60),
            ["SuccessSoftBrush"] = Color.FromRgb(221, 247, 234),
            ["DangerBrush"] = Color.FromRgb(215, 53, 69)
        };

    private static IReadOnlyDictionary<string, Color> HighContrastColorResources(
        HighContrastThemePalette palette) => new Dictionary<string, Color>
        {
            ["BackgroundColor"] = palette.Window,
            ["SurfaceColor"] = palette.Window,
            ["SurfaceRaisedColor"] = palette.Window,
            ["SurfaceHoverColor"] = palette.Highlight,
            ["BorderColor"] = palette.WindowText,
            ["TextColor"] = palette.WindowText,
            ["TextMutedColor"] = palette.GrayText,
            ["AccentColor"] = palette.Highlight,
            [AccentForegroundColorResourceKey] = palette.HighlightText
        };

    private static IReadOnlyDictionary<string, Color> HighContrastBrushResources(
        HighContrastThemePalette palette) => new Dictionary<string, Color>
        {
            ["BackgroundBrush"] = palette.Window,
            ["AppBackgroundBrush"] = palette.Window,
            ["SurfaceBrush"] = palette.Window,
            ["SurfaceRaisedBrush"] = palette.Window,
            ["SurfaceHoverBrush"] = palette.Highlight,
            ["BorderBrush"] = palette.WindowText,
            ["TextBrush"] = palette.WindowText,
            ["TextMutedBrush"] = palette.GrayText,
            ["AccentBrush"] = palette.Highlight,
            ["AccentSoftBrush"] = palette.Highlight,
            [AccentForegroundBrushResourceKey] = palette.HighlightText,
            ["WarningBrush"] = palette.WindowText,
            ["WarningSoftBrush"] = palette.Window,
            ["ErrorBrush"] = palette.WindowText,
            ["ErrorSoftBrush"] = palette.Window,
            ["SuccessBrush"] = palette.WindowText,
            ["SuccessSoftBrush"] = palette.Window,
            ["DangerBrush"] = palette.WindowText
        };

    private static void SetBrush(
        ResourceDictionary resources,
        string key,
        Color color)
    {
        if (resources[key] is SolidColorBrush { IsFrozen: false } brush)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = color;
            brush.Opacity = 1;
            return;
        }
        resources[key] = NewBrush(color);
    }

    private static SolidColorBrush NewBrush(Color color) => new(color);

    private static LinearGradientBrush CreateAccentGradient(Color accent)
    {
        var hsl = ToHsl(accent);
        return new LinearGradientBrush(
            FromHsl(hsl.Hue, Math.Clamp(hsl.Saturation * 0.9, 0, 1), Math.Clamp(hsl.Lightness + 0.12, 0, 1)),
            FromHsl((hsl.Hue + 28) % 360, hsl.Saturation, Math.Clamp(hsl.Lightness - 0.08, 0, 1)),
            new Point(0, 0),
            new Point(1, 1));
    }

    private static LinearGradientBrush CreatePlayerGradient(ThemePalette palette)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0)
        };
        brush.GradientStops.Add(new GradientStop(WithAlpha(palette.Surface, 245), 0));
        brush.GradientStops.Add(new GradientStop(WithAlpha(palette.SurfaceRaised, 248), 0.5));
        brush.GradientStops.Add(new GradientStop(WithAlpha(palette.Surface, 245), 1));
        return brush;
    }

    private static Color BestContrastingForeground(Color background) =>
        ContrastRatio(Colors.Black, background)
            >= ContrastRatio(Colors.White, background)
            ? Colors.Black
            : Colors.White;

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte component)
        {
            var value = component / 255d;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R)
             + 0.7152 * Linear(color.G)
             + 0.0722 * Linear(color.B);
    }

    private static Hsl ToHsl(Color color)
    {
        var red = color.R / 255d;
        var green = color.G / 255d;
        var blue = color.B / 255d;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var lightness = (maximum + minimum) / 2;
        if (Math.Abs(maximum - minimum) < double.Epsilon)
            return new Hsl(0, 0, lightness);

        var difference = maximum - minimum;
        var saturation = lightness > 0.5
            ? difference / (2 - maximum - minimum)
            : difference / (maximum + minimum);
        var hue = maximum == red
            ? (green - blue) / difference + (green < blue ? 6 : 0)
            : maximum == green
                ? (blue - red) / difference + 2
                : (red - green) / difference + 4;
        return new Hsl(hue * 60, saturation, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        hue = ((hue % 360) + 360) % 360 / 360;
        saturation = Math.Clamp(saturation, 0, 1);
        lightness = Math.Clamp(lightness, 0, 1);
        if (saturation <= double.Epsilon)
        {
            var component = ToByte(lightness);
            return Color.FromRgb(component, component, component);
        }

        var q = lightness < 0.5
            ? lightness * (1 + saturation)
            : lightness + saturation - lightness * saturation;
        var p = 2 * lightness - q;
        static double HueToRgb(double p, double q, double value)
        {
            if (value < 0) value += 1;
            if (value > 1) value -= 1;
            if (value < 1d / 6) return p + (q - p) * 6 * value;
            if (value < 1d / 2) return q;
            if (value < 2d / 3) return p + (q - p) * (2d / 3 - value) * 6;
            return p;
        }

        return Color.FromRgb(
            ToByte(HueToRgb(p, q, hue + 1d / 3)),
            ToByte(HueToRgb(p, q, hue)),
            ToByte(HueToRgb(p, q, hue - 1d / 3)));
    }

    private static byte ToByte(double value) =>
        (byte)Math.Round(
            Math.Clamp(value, 0, 1) * 255,
            MidpointRounding.AwayFromZero);

    private static Color Opaque(Color color) =>
        Color.FromRgb(color.R, color.G, color.B);

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static double NormalizeOpacity(double opacity) =>
        double.IsFinite(opacity)
            ? Math.Clamp(opacity, MinimumBackgroundOpacity, 1)
            : 1;

    private static double NormalizeFontSize(double fontSize) =>
        double.IsFinite(fontSize)
            ? Math.Clamp(fontSize, 9, 32)
            : DefaultFontSize;

    private static string NormalizeFontFamily(string? fontFamily)
    {
        var normalized = fontFamily?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Length > 128
            || normalized.Any(char.IsControl))
            return DefaultFontFamily;
        try
        {
            _ = new FontFamily(normalized);
            return normalized;
        }
        catch (ArgumentException)
        {
            return DefaultFontFamily;
        }
    }

    private sealed record BasePalette(
        Color Background,
        Color Surface,
        Color SurfaceRaised,
        Color SurfaceHover,
        Color Border,
        Color Text,
        Color TextMuted);

    private readonly record struct Hsl(
        double Hue,
        double Saturation,
        double Lightness);
}
