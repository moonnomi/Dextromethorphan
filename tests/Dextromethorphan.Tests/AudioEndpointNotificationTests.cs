using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Audio;
using NAudio.CoreAudioApi;

namespace Dextromethorphan.Tests;

public sealed class AudioEndpointNotificationTests
{
    [Fact]
    public void RemovalAndDefaultChangesAreForwardedWithIdentity()
    {
        var changes = new List<AudioEndpointChangedEventArgs>();
        var notifications =
            new AudioEndpointNotificationClient(changes.Add);

        notifications.OnDeviceRemoved("removed");
        notifications.OnDefaultDeviceChanged(
            DataFlow.Render,
            Role.Multimedia,
            "new-default");

        Assert.Collection(
            changes,
            change =>
            {
                Assert.Equal(
                    AudioEndpointChangeKind.Removed,
                    change.Kind);
                Assert.Equal("removed", change.DeviceId);
            },
            change =>
            {
                Assert.Equal(
                    AudioEndpointChangeKind.DefaultChanged,
                    change.Kind);
                Assert.Equal("new-default", change.DeviceId);
            });
    }
}
