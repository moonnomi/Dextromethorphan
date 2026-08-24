using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Tests;

public sealed class SettingsRuntimeApplyPolicyTests
{
    [Fact]
    public async Task ImportWaitsForInFlightLibraryInitialization()
    {
        var initialization = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var wait = SettingsRuntimeApplyPolicy.EnsureLibraryReadyAsync(
            isLibraryReady: false,
            () =>
            {
                calls++;
                return initialization.Task;
            });

        Assert.Equal(1, calls);
        Assert.False(wait.IsCompleted);

        initialization.SetResult();
        await wait;
    }

    [Fact]
    public async Task ReadyLibraryDoesNotRestartInitialization()
    {
        var calls = 0;

        await SettingsRuntimeApplyPolicy.EnsureLibraryReadyAsync(
            isLibraryReady: true,
            () =>
            {
                calls++;
                return Task.CompletedTask;
            });

        Assert.Equal(0, calls);
    }

    [Fact]
    public void ImportedActiveProfileSelectsTheMatchingAvailableDevice()
    {
        var settings = SettingsWithProfiles(
            activeDeviceId: "device-b",
            ("device-a", 80),
            ("device-b", 35));
        var devices = new[]
        {
            Device("device-a", "Speakers"),
            Device("device-b", "USB DAC")
        };

        var selection = SettingsRuntimeApplyPolicy.ResolveOutputSelection(
            settings,
            devices);

        Assert.Equal("device-b", selection.Device?.Id);
        Assert.Equal("device-b", selection.Profile.DeviceId);
        Assert.Equal(35, selection.Profile.BufferMilliseconds);
    }

    [Fact]
    public void MissingImportedDeviceFallsBackToOneConsistentDeviceAndProfile()
    {
        var settings = SettingsWithProfiles(
            activeDeviceId: "disconnected-dac",
            ("speakers", 90),
            ("disconnected-dac", 25));
        var devices = new[] { Device("speakers", "Laptop speakers") };

        var selection = SettingsRuntimeApplyPolicy.ResolveOutputSelection(
            settings,
            devices);

        Assert.Equal("speakers", selection.Device?.Id);
        Assert.Equal(selection.Device?.Id, selection.Profile.DeviceId);
        Assert.Equal(90, selection.Profile.BufferMilliseconds);
    }

    [Fact]
    public void GeneratedDeviceDefaultsAreTheRevertBaselineNotUnsavedEdits()
    {
        var settings = SettingsWithProfiles(
            activeDeviceId: "default",
            ("default", 100));
        var device = Device("new-device", "New USB output");
        var draft = AudioOutputProfileDefaults.For(device);

        Assert.False(SettingsRuntimeApplyPolicy.HasOutputProfileChanges(
            settings,
            device,
            draft));

        draft.BufferMilliseconds++;

        Assert.True(SettingsRuntimeApplyPolicy.HasOutputProfileChanges(
            settings,
            device,
            draft));
    }

    private static AppSettings SettingsWithProfiles(
        string activeDeviceId,
        params (string DeviceId, int BufferMilliseconds)[] profiles) =>
        new()
        {
            ActiveOutputDeviceId = activeDeviceId,
            OutputProfiles = profiles.Select(profile => new AudioOutputProfile
            {
                DeviceId = profile.DeviceId,
                Name = profile.DeviceId,
                BufferMilliseconds = profile.BufferMilliseconds
            }).ToList()
        };

    private static AudioDeviceInfo Device(string id, string name) =>
        new(id, name, IsDefault: false, State: "Active");
}
