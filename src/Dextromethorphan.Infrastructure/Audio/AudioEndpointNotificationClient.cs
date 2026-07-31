using Dextromethorphan.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Dextromethorphan.Infrastructure.Audio;

internal sealed class AudioEndpointNotificationClient(
    Action<AudioEndpointChangedEventArgs> changed)
    : IMMNotificationClient
{
    public void OnDeviceStateChanged(
        string deviceId,
        DeviceState newState) =>
        changed(new(
            AudioEndpointChangeKind.StateChanged,
            deviceId,
            newState.ToString()));

    public void OnDeviceAdded(string pwstrDeviceId) =>
        changed(new(AudioEndpointChangeKind.Added, pwstrDeviceId));

    public void OnDeviceRemoved(string deviceId) =>
        changed(new(AudioEndpointChangeKind.Removed, deviceId));

    public void OnDefaultDeviceChanged(
        DataFlow flow,
        Role role,
        string defaultDeviceId)
    {
        if (flow is DataFlow.Render or DataFlow.All
            && role == Role.Multimedia)
            changed(new(
                AudioEndpointChangeKind.DefaultChanged,
                defaultDeviceId,
                role.ToString()));
    }

    public void OnPropertyValueChanged(
        string pwstrDeviceId,
        PropertyKey key) =>
        changed(new(
            AudioEndpointChangeKind.StateChanged,
            pwstrDeviceId,
            key.ToString()));
}
