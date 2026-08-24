using Dextromethorphan.Core.Models;

namespace Dextromethorphan.App.ViewModels;

internal static class SettingsRuntimeApplyPolicy
{
    public static Task EnsureLibraryReadyAsync(
        bool isLibraryReady,
        Func<Task> initializeLibraryAsync)
    {
        ArgumentNullException.ThrowIfNull(initializeLibraryAsync);
        return isLibraryReady
            ? Task.CompletedTask
            : initializeLibraryAsync();
    }

    public static OutputSettingsSelection ResolveOutputSelection(
        AppSettings settings,
        IReadOnlyList<AudioDeviceInfo> availableDevices)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(availableDevices);

        var activeProfile = settings.OutputProfiles.FirstOrDefault(profile =>
                                profile.DeviceId.Equals(
                                    settings.ActiveOutputDeviceId,
                                    StringComparison.OrdinalIgnoreCase))
                            ?? settings.OutputProfiles.FirstOrDefault()
                            ?? new AudioOutputProfile();
        var selectedDevice = availableDevices.FirstOrDefault(device =>
                                 device.Id.Equals(
                                     settings.ActiveOutputDeviceId,
                                     StringComparison.OrdinalIgnoreCase))
                             ?? availableDevices.FirstOrDefault();
        if (selectedDevice is null)
            return new OutputSettingsSelection(null, activeProfile);

        var selectedProfile = settings.OutputProfiles.FirstOrDefault(profile =>
                                  profile.DeviceId.Equals(
                                      selectedDevice.Id,
                                      StringComparison.OrdinalIgnoreCase))
                              ?? AudioOutputProfileDefaults.For(selectedDevice);
        return new OutputSettingsSelection(selectedDevice, selectedProfile);
    }

    public static bool HasOutputProfileChanges(
        AppSettings settings,
        AudioDeviceInfo? selectedDevice,
        AudioOutputProfile draft)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(draft);
        if (selectedDevice is null) return false;

        var baseline = settings.OutputProfiles.FirstOrDefault(profile =>
                           profile.DeviceId.Equals(
                               selectedDevice.Id,
                               StringComparison.OrdinalIgnoreCase))
                       ?? AudioOutputProfileDefaults.For(selectedDevice);
        return !ProfilesEqual(draft, baseline);
    }

    private static bool ProfilesEqual(
        AudioOutputProfile first,
        AudioOutputProfile second) =>
        first.Mode == second.Mode
        && first.BufferMilliseconds == second.BufferMilliseconds
        && first.SampleRatePolicy == second.SampleRatePolicy
        && first.PreferredSampleRate == second.PreferredSampleRate
        && first.BitDepthPolicy == second.BitDepthPolicy
        && first.PreferredBitDepth == second.PreferredBitDepth
        && first.ChannelPolicy == second.ChannelPolicy
        && first.DsdMode == second.DsdMode
        && Math.Abs(first.CrossfadeSeconds - second.CrossfadeSeconds) < .001
        && first.VolumeControl == second.VolumeControl
        && first.PreferBitPerfect == second.PreferBitPerfect
        && first.FallbackPolicy == second.FallbackPolicy
        && first.RecoveryMaximumAttempts == second.RecoveryMaximumAttempts
        && first.RecoveryInitialDelayMilliseconds
        == second.RecoveryInitialDelayMilliseconds;
}

internal readonly record struct OutputSettingsSelection(
    AudioDeviceInfo? Device,
    AudioOutputProfile Profile);
