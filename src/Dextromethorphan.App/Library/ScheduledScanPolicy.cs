using System.Runtime.InteropServices;
using Dextromethorphan.Core.Models;
using Windows.Networking.Connectivity;

namespace Dextromethorphan.App.Library;

public readonly record struct ScanEnvironment(bool OnBattery, bool MeteredNetwork);

public readonly record struct ScheduledScanDecision(bool CanScan, string Reason)
{
    public static ScheduledScanDecision Evaluate(AppSettings settings, ScanEnvironment environment)
    {
        if (!settings.ScheduledLibraryScanEnabled) return new(false, "Scheduled scans are off");
        if (environment.OnBattery && !settings.AllowScheduledScanOnBattery) return new(false, "Waiting for AC power");
        if (environment.MeteredNetwork && !settings.AllowScheduledScanOnMeteredNetwork) return new(false, "Waiting for an unmetered network");
        return new(true, "Ready for scheduled scan");
    }
}

internal static class ScheduledScanEnvironment
{
    public static ScanEnvironment Capture()
    {
        var onBattery = GetSystemPowerStatus(out var power) && power.ACLineStatus == 0;
        var metered = false;
        try
        {
            var cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
            metered = cost?.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable
                || cost?.Roaming == true
                || cost?.OverDataLimit == true;
        }
        catch { }
        return new ScanEnvironment(onBattery, metered);
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}
