# Windows integration

## Rebindable shortcuts

Shortcut bindings are stored in `%APPDATA%\Dextromethorphan\settings.json` under `Shortcuts`. Each entry has an action, gesture, global scope, and enabled state. Schema version 1 `KeyBindings` dictionaries migrate automatically to the typed version 2 list; customized gestures are retained and the original property is removed on save.

Global shortcuts are registered against the main HWND with `RegisterHotKey` and `MOD_NOREPEAT`. `WM_HOTKEY` is routed to the same action dispatcher used by in-app gestures and SMTC commands. Registration is non-fatal: invalid gestures, duplicate bindings, Windows-reserved combinations, and keys owned by another application appear in `IShortcutService.Registrations` with an error description.

Supported gesture names include letters, digits, F1-F24, navigation/editing keys, numpad keys, common punctuation, volume/media keys, and arbitrary hexadecimal `VK_XX` values. Modifiers are `Ctrl`, `Alt`, `Shift`, and `Win`.

Default global bindings:

- `Ctrl+Alt+Space`: play/pause
- `Ctrl+Alt+Right`: next
- `Ctrl+Alt+Left`: previous
- `Ctrl+Alt+Up`: volume up
- `Ctrl+Alt+Down`: volume down

Actions currently routed by the backend include explicit play, pause, stop, previous/next, seek, volume, rating, love, queue undo, and play/pause toggle. Seek and volume increments are configurable with `SeekStepSeconds` and `VolumeStep`.

## System Media Transport Controls

WPF is a desktop application, so the service obtains `SystemMediaTransportControls` for the top-level HWND through `ISystemMediaTransportControlsInterop.GetForWindow`; it does not call the unsupported desktop `GetForCurrentView` method.

The service publishes playback state, enabled-button state, title, artist, album, position, and duration. Timeline updates are throttled to two per second. Windows play, pause, stop, next, previous, and seek requests are marshalled back to the WPF dispatcher before touching playback state. This also provides the normal Windows media-key and system flyout path while an audio session is active.

SMTC activation or update failures are isolated and exposed through `ISystemMediaTransportService.Error`; audio playback continues independently.
