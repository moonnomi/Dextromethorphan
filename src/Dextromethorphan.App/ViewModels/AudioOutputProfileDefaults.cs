using Dextromethorphan.Core.Models;

namespace Dextromethorphan.App.ViewModels;

internal static class AudioOutputProfileDefaults
{
    public static AudioOutputProfile For(AudioDeviceInfo device)
    {
        var name = device.Name;
        var wireless =
            name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Headset", StringComparison.OrdinalIgnoreCase);
        var display =
            name.Contains("HDMI", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Display Audio", StringComparison.OrdinalIgnoreCase)
            || name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || name.Contains("AMD High Definition", StringComparison.OrdinalIgnoreCase);

        if (wireless)
            return SharedProfile(device, 180);
        if (display)
            return SharedProfile(device, 100);
        if (device.Id.Equals("default", StringComparison.OrdinalIgnoreCase))
            return SharedProfile(device, 100);
        return new AudioOutputProfile
        {
            DeviceId = device.Id,
            Name = device.Name,
            Mode = WasapiMode.Exclusive,
            BufferMilliseconds = 50,
            SampleRatePolicy = SampleRatePolicy.MatchSource,
            BitDepthPolicy = BitDepthPolicy.MatchSource,
            ChannelPolicy = ChannelPolicy.DownmixToStereo,
            FallbackPolicy = OutputFallbackPolicy.SharedMode,
            VolumeControl = VolumeControlMode.Software,
            DsdMode = DsdMode.Disabled,
            PreferBitPerfect = true
        };
    }

    private static AudioOutputProfile SharedProfile(
        AudioDeviceInfo device,
        int bufferMilliseconds) => new()
    {
        DeviceId = device.Id,
        Name = device.Name,
        Mode = WasapiMode.Shared,
        BufferMilliseconds = bufferMilliseconds,
        SampleRatePolicy = SampleRatePolicy.EndpointMixFormat,
        BitDepthPolicy = BitDepthPolicy.MatchSource,
        ChannelPolicy = ChannelPolicy.DownmixToStereo,
        FallbackPolicy = OutputFallbackPolicy.SystemDefaultShared,
        VolumeControl = VolumeControlMode.Software,
        DsdMode = DsdMode.Disabled,
        PreferBitPerfect = false
    };
}
