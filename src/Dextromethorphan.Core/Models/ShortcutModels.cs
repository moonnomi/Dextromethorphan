using System.Globalization;

namespace Dextromethorphan.Core.Models;

[Flags]
public enum ShortcutModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Windows = 8 }

public static class ShortcutActions
{
    public const string TogglePlayback = "Playback.Toggle";
    public const string Play = "Playback.Play";
    public const string Pause = "Playback.Pause";
    public const string Stop = "Playback.Stop";
    public const string Next = "Playback.Next";
    public const string Previous = "Playback.Previous";
    public const string SeekForward = "Playback.SeekForward";
    public const string SeekBackward = "Playback.SeekBackward";
    public const string VolumeUp = "Playback.VolumeUp";
    public const string VolumeDown = "Playback.VolumeDown";
    public const string RatingUp = "Track.RatingUp";
    public const string RatingDown = "Track.RatingDown";
    public const string Love = "Track.Love";
    public const string Search = "Library.Search";
    public const string UndoQueue = "Queue.Undo";
}

public sealed class ShortcutBinding
{
    public string Action { get; set; } = "";
    public string Gesture { get; set; } = "";
    public bool Global { get; set; }
    public bool Enabled { get; set; } = true;
}

public readonly record struct ShortcutGesture(ShortcutModifiers Modifiers, int VirtualKey)
{
    private static readonly IReadOnlyDictionary<string, int> NamedKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Escape"] = 0x1B, ["Space"] = 0x20,
        ["Pause"] = 0x13, ["CapsLock"] = 0x14,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["PrintScreen"] = 0x2C, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["NumPad0"] = 0x60, ["NumPad1"] = 0x61, ["NumPad2"] = 0x62, ["NumPad3"] = 0x63, ["NumPad4"] = 0x64,
        ["NumPad5"] = 0x65, ["NumPad6"] = 0x66, ["NumPad7"] = 0x67, ["NumPad8"] = 0x68, ["NumPad9"] = 0x69,
        ["Multiply"] = 0x6A, ["Add"] = 0x6B, ["Subtract"] = 0x6D, ["Decimal"] = 0x6E, ["Divide"] = 0x6F,
        ["Semicolon"] = 0xBA, ["Plus"] = 0xBB, ["Comma"] = 0xBC, ["Minus"] = 0xBD, ["Period"] = 0xBE,
        ["Slash"] = 0xBF, ["Tilde"] = 0xC0, ["OpenBracket"] = 0xDB, ["Backslash"] = 0xDC, ["CloseBracket"] = 0xDD, ["Quote"] = 0xDE,
        ["VolumeMute"] = 0xAD, ["VolumeDown"] = 0xAE, ["VolumeUp"] = 0xAF,
        ["MediaNextTrack"] = 0xB0, ["MediaPreviousTrack"] = 0xB1, ["MediaStop"] = 0xB2, ["MediaPlayPause"] = 0xB3
    };

    public static bool TryParse(string? text, out ShortcutGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = ShortcutModifiers.None;
        int? key = null;
        foreach (var token in tokens)
        {
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || token.Equals("Control", StringComparison.OrdinalIgnoreCase)) modifiers |= ShortcutModifiers.Control;
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= ShortcutModifiers.Alt;
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= ShortcutModifiers.Shift;
            else if (token.Equals("Win", StringComparison.OrdinalIgnoreCase) || token.Equals("Windows", StringComparison.OrdinalIgnoreCase)) modifiers |= ShortcutModifiers.Windows;
            else if (key is null && TryParseKey(token, out var parsed)) key = parsed;
            else return false;
        }
        if (key is null) return false;
        gesture = new ShortcutGesture(modifiers, key.Value);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ShortcutModifiers.Windows)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    private static bool TryParseKey(string token, out int key)
    {
        if (NamedKeys.TryGetValue(token, out key)) return true;
        if (token.Length == 1)
        {
            var character = char.ToUpperInvariant(token[0]);
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9') { key = character; return true; }
        }
        if (token.Length is 2 or 3 && token[0] is 'F' or 'f' && int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var function) && function is >= 1 and <= 24)
        {
            key = 0x6F + function;
            return true;
        }
        if (token.StartsWith("VK_", StringComparison.OrdinalIgnoreCase) && int.TryParse(token.AsSpan(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw) && raw is >= 1 and <= 0xFF)
        {
            key = raw;
            return true;
        }
        key = 0;
        return false;
    }

    private static string KeyName(int key)
    {
        var named = NamedKeys.FirstOrDefault(x => x.Value == key);
        if (!string.IsNullOrEmpty(named.Key)) return named.Key;
        if (key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)key).ToString();
        if (key is >= 0x70 and <= 0x87) return $"F{key - 0x6F}";
        return $"VK_{key:X2}";
    }
}

public sealed record ShortcutRegistrationResult(string Action, string Gesture, bool Global, bool Registered, string? Error = null);

public enum MediaTransportCommand { Play, Pause, Stop, Next, Previous, Seek }
public sealed record MediaTransportCommandEventArgs(MediaTransportCommand Command, TimeSpan? Position = null);
