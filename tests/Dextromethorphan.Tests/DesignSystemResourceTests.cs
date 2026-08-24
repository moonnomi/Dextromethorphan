using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Dextromethorphan.App.UI;

namespace Dextromethorphan.Tests;

public sealed class DesignSystemResourceTests
{
    [Fact]
    public async Task SharedThemeMaterializesSemanticTokensAndReusableStyles()
    {
        var error = await RunOnStaAsync(() =>
        {
            var theme = LoadTheme();
            var tokenKeys = new[]
            {
                "Space4", "Space8", "Space12", "Space16", "Space24",
                "ControlPadding", "PopupPadding", "DialogPadding",
                "RadiusSmall", "RadiusControl", "RadiusPopup", "RadiusSurface",
                "TypeCaptionSize", "TypeBodySize", "TypeTitleSize",
                "StateHoverOpacity", "StateDisabledOpacity", "FocusRingThickness",
                "MotionFast", "MotionStandard", "MotionEmphasized",
                "ElevationSubtle", "ElevationPopup", "ElevationFloating"
            };
            foreach (var key in tokenKeys)
                Assert.NotNull(theme[key]);

            var reusableStyles = new[]
            {
                "AppWindowStyle", "DialogWindowStyle", "DialogSurfaceStyle",
                "ThemedPopupStyle", "PopupSurfaceStyle", "ThemedToolTipStyle",
                "ThemedContextMenuStyle", "ThemedMenuItemStyle",
                "ThemedButtonStyle", "ThemedComboBoxStyle",
                "ThemedComboBoxItemStyle", "ThemedCheckBoxStyle",
                "ThemedRadioButtonStyle", "ThemedProgressBarStyle",
                "ThemedTextBoxStyle", "ThemedPasswordBoxStyle",
                "ThemedSliderStyle", "ThemedScrollBarStyle",
                "MetadataLabelText", "MetadataValueText"
            };
            foreach (var key in reusableStyles)
            {
                try
                {
                    Assert.IsType<Style>(theme[key]);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"The reusable style '{key}' could not materialize.",
                        exception);
                }
            }
        });

        Assert.Null(error);
    }

    [Fact]
    public async Task StandardControlsUseTheReusableThemeStylesByDefault()
    {
        var error = await RunOnStaAsync(() =>
        {
            var theme = LoadTheme();
            var implicitStyles = new (Type TargetType, string NamedStyle)[]
            {
                (typeof(Window), "AppWindowStyle"),
                (typeof(Popup), "ThemedPopupStyle"),
                (typeof(ToolTip), "ThemedToolTipStyle"),
                (typeof(ContextMenu), "ThemedContextMenuStyle"),
                (typeof(MenuItem), "ThemedMenuItemStyle"),
                (typeof(Button), "ThemedButtonStyle"),
                (typeof(ComboBox), "ThemedComboBoxStyle"),
                (typeof(ComboBoxItem), "ThemedComboBoxItemStyle"),
                (typeof(CheckBox), "ThemedCheckBoxStyle"),
                (typeof(RadioButton), "ThemedRadioButtonStyle"),
                (typeof(ProgressBar), "ThemedProgressBarStyle"),
                (typeof(TextBox), "ThemedTextBoxStyle"),
                (typeof(PasswordBox), "ThemedPasswordBoxStyle"),
                (typeof(Slider), "ThemedSliderStyle"),
                (typeof(ScrollBar), "ThemedScrollBarStyle")
            };

            foreach (var (targetType, namedStyle) in implicitStyles)
            {
                var implicitStyle = Assert.IsType<Style>(theme[targetType]);
                var reusableStyle = Assert.IsType<Style>(theme[namedStyle]);
                Assert.Equal(targetType, reusableStyle.TargetType);
                Assert.Same(reusableStyle, implicitStyle.BasedOn);
            }
        });

        Assert.Null(error);
    }

    [Fact]
    public async Task SemanticStatusPairsRemainReadableAcrossApplicationThemes()
    {
        var error = await RunOnStaAsync(() =>
        {
            var theme = LoadTheme();
            var pairs = new[]
            {
                (Foreground: "WarningBrush", Background: "WarningSoftBrush"),
                (Foreground: "ErrorBrush", Background: "ErrorSoftBrush"),
                (Foreground: "SuccessBrush", Background: "SuccessSoftBrush")
            };

            foreach (var pair in pairs)
            {
                var foreground = Assert.IsType<SolidColorBrush>(theme[pair.Foreground]);
                var background = Assert.IsType<SolidColorBrush>(theme[pair.Background]);
                Assert.True(
                    ThemeManager.ContrastRatio(foreground.Color, background.Color) >= 4.5,
                    $"{pair.Foreground} must remain readable on {pair.Background}.");
            }
        });

        Assert.Null(error);
    }

    private static ResourceDictionary LoadTheme()
    {
        return Assert.IsType<ResourceDictionary>(
            Application.LoadComponent(
                new Uri(
                    "/Dextromethorphan;component/UI/Styles/Theme.xaml",
                    UriKind.Relative)));
    }

    private static async Task<Exception?> RunOnStaAsync(Action action)
    {
        var completion = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult(null);
            }
            catch (Exception exception)
            {
                completion.SetResult(exception);
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await completion.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
    }
}
