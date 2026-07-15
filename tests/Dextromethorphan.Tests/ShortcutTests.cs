using System.Text.Json;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Settings;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Tests;

public sealed class ShortcutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Dextromethorphan.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("ctrl+alt+space", ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x20, "Ctrl+Alt+Space")]
    [InlineData("Shift+F12", ShortcutModifiers.Shift, 0x7B, "Shift+F12")]
    [InlineData("MediaPlayPause", ShortcutModifiers.None, 0xB3, "MediaPlayPause")]
    [InlineData("Win+VK_E2", ShortcutModifiers.Windows, 0xE2, "Win+VK_E2")]
    [InlineData("Ctrl+Plus", ShortcutModifiers.Control, 0xBB, "Ctrl+Plus")]
    public void GestureParserCanonicalizesSupportedKeys(string text, ShortcutModifiers modifiers, int virtualKey, string canonical)
    {
        Assert.True(ShortcutGesture.TryParse(text, out var gesture));
        Assert.Equal(modifiers, gesture.Modifiers);
        Assert.Equal(virtualKey, gesture.VirtualKey);
        Assert.Equal(canonical, gesture.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+A+B")]
    [InlineData("F25")]
    [InlineData("VK_00")]
    public void GestureParserRejectsInvalidInput(string text) => Assert.False(ShortcutGesture.TryParse(text, out _));

    [Fact]
    public async Task VersionOneKeyBindingsMigrateWithoutLosingCustomization()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsFile, """
            {
              "SchemaVersion": 1,
              "Volume": 0.4,
              "KeyBindings": {
                "Playback.Toggle": "Ctrl+P",
                "Track.Love": "Alt+L"
              }
            }
            """, cancellationToken);

        var settings = new JsonSettingsService(paths);
        await settings.InitializeAsync(cancellationToken);

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.Current.SchemaVersion);
        Assert.Contains(settings.Current.Shortcuts, x => !x.Global && x.Action == ShortcutActions.TogglePlayback && x.Gesture == "Ctrl+P");
        Assert.Contains(settings.Current.Shortcuts, x => !x.Global && x.Action == ShortcutActions.Love && x.Gesture == "Alt+L");
        Assert.Contains(settings.Current.Shortcuts, x => x.Global && x.Action == ShortcutActions.Next);
        Assert.Null(settings.Current.KeyBindings);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsFile, cancellationToken));
        Assert.False(document.RootElement.TryGetProperty("KeyBindings", out _));
    }

    [Fact]
    public async Task SettingsPreserveConflictsAndInvalidBindingsForRegistrationFeedback()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var settings = new JsonSettingsService(new AppPaths(_root));
        await settings.InitializeAsync(cancellationToken);
        await settings.UpdateAsync(x =>
        {
            x.SeekStepSeconds = 500;
            x.VolumeStep = 0;
            x.Shortcuts =
            [
                new ShortcutBinding { Action = ShortcutActions.Play, Gesture = " ctrl+p " },
                new ShortcutBinding { Action = ShortcutActions.Pause, Gesture = "Ctrl+P" },
                new ShortcutBinding { Action = ShortcutActions.Stop, Gesture = "NotAKey" }
            ];
        }, cancellationToken);

        Assert.Equal(60, settings.Current.SeekStepSeconds);
        Assert.Equal(0.01, settings.Current.VolumeStep);
        Assert.Equal(3, settings.Current.Shortcuts.Count);
        Assert.Equal("Ctrl+P", settings.Current.Shortcuts[0].Gesture);
        Assert.Equal("NotAKey", settings.Current.Shortcuts[2].Gesture);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
