namespace Dextromethorphan.App.UI;

internal enum ShellWidthClass
{
    Compact,
    Medium,
    Expanded
}

internal readonly record struct ResponsiveShellMetrics(
    ShellWidthClass WidthClass,
    bool ShowFullSearch,
    bool ShowSecondaryUtilities,
    double SearchWidth,
    double QueueMaximumWidth,
    double PlayerIdentityWidth,
    double PlayerUtilitiesWidth,
    double VisualizerWidth)
{
    public static ResponsiveShellMetrics ForWidth(double width)
    {
        width = double.IsFinite(width) ? Math.Max(0, width) : 0;
        if (width < 960)
        {
            return new ResponsiveShellMetrics(
                ShellWidthClass.Compact,
                ShowFullSearch: false,
                ShowSecondaryUtilities: false,
                SearchWidth: 0,
                QueueMaximumWidth: Math.Clamp(width * 0.34, 240, 280),
                PlayerIdentityWidth: 190,
                PlayerUtilitiesWidth: 160,
                VisualizerWidth: 0);
        }

        if (width < 1220)
        {
            return new ResponsiveShellMetrics(
                ShellWidthClass.Medium,
                ShowFullSearch: true,
                ShowSecondaryUtilities: false,
                SearchWidth: 170,
                QueueMaximumWidth: 300,
                PlayerIdentityWidth: 230,
                PlayerUtilitiesWidth: 190,
                VisualizerWidth: 0);
        }

        return new ResponsiveShellMetrics(
            ShellWidthClass.Expanded,
            ShowFullSearch: true,
            ShowSecondaryUtilities: true,
            SearchWidth: 230,
            QueueMaximumWidth: double.PositiveInfinity,
            PlayerIdentityWidth: 310,
            PlayerUtilitiesWidth: 250,
            VisualizerWidth: 58);
    }
}
