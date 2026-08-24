using System.IO;

namespace Dextromethorphan.App.UI;

internal sealed record WindowingSmokeCase(
    string Name,
    double RequestedWidth,
    double RequestedHeight,
    double ActualWidth,
    double ActualHeight,
    bool RequestedPhysicalSizeAvailable,
    bool QueueVisible,
    ShellWidthClass WidthClass,
    double SeekWidth,
    double QueueWidth,
    bool CriticalControlsInsideWindow,
    string Screenshot);

internal sealed record WindowingSmokeReport(
    int SchemaVersion,
    DateTimeOffset CapturedAt,
    double DpiScaleX,
    double DpiScaleY,
    bool NativeSnapLayoutAvailable,
    SnapLayoutProbe MaximizeHitTest,
    bool NativeMaximizeClickPassed,
    bool TaskbarSafeFullScreenPassed,
    bool Passed,
    IReadOnlyList<WindowingSmokeCase> Cases);

internal static class WindowingSmokeOptions
{
    public static string? ParseOutputDirectory(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!arguments[index].Equals(
                    "--windowing-smoke",
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (index + 1 >= arguments.Count
                || string.IsNullOrWhiteSpace(arguments[index + 1]))
                throw new ArgumentException("--windowing-smoke requires an output directory.");
            return Path.GetFullPath(arguments[index + 1]);
        }
        return null;
    }
}
