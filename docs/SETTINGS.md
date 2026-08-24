# Settings and customization

Dextromethorphan keeps its configuration locally and does not require an
account. Settings are stored as validated JSON under the active app-data root;
portable builds keep the same data beside the application. Invalid values are
normalized before they reach playback, scanning, or the interface.

Settings is a reusable, non-modal window. Use its search field to find a
setting by name or purpose, then choose a result to open the relevant section.
Audio and diagnostic controls include contextual explanations so device and
signal-path choices can be understood without editing JSON.

## Appearance

Dark, Light, and AMOLED are live themes. Theme, accent, interface font, font
size, background opacity, queue visibility and width, fullscreen navigation,
animations, the spectrum visualizer, cover size, and Now Playing modules can
all be changed from the interface.

Accent colors are accepted as hexadecimal colors. If a requested color does
not meet the 4.5:1 contrast target against the selected theme, the application
preserves its hue and saturation while moving its lightness to the closest
accessible value. A contrasting foreground is selected for accent-filled
controls. Visual preferences preview immediately and are saved after a short
debounce, avoiding a disk write for every slider tick.

The animation toggle also respects the Windows reduced-motion preference.
Disabling animation removes decorative transitions without changing playback,
lyrics timing, or keyboard behavior.

## Audio output and playback

Each Windows output endpoint has its own profile. A profile can configure:

- shared or exclusive WASAPI;
- event-buffer size and recovery policy;
- source-matched or fixed sample rate and bit depth;
- channel handling and fallback behavior;
- software, hardware, or fixed volume;
- disabled, DoP, or native DSD mode;
- direct bit-perfect preference and per-output crossfade.

Output-profile changes remain a draft until **Save profile** is chosen. This
prevents an incomplete buffer, device, or format edit from interrupting the
active output.

The Playback section stages its changes and provides Apply and Revert. It
includes startup and bookmark resume, Stop after current, Stop after queue,
gapless/crossfade transitions, fade-in/out, ReplayGain mode and preamp,
clipping prevention, speed, pitch, pitch preservation, and keyboard seek and
volume increments. Stop modes remain mutually exclusive. Track-specific speed
and pitch overrides continue to live in the track playback menu.

Settings can expose and validate these device options without a specialist
DAC. Physical exclusive-mode, DoP, and native-DSD qualification remains a
separate hardware test and is not implied by changing a profile.

## Library and metadata

The Library section manages local, removable, mounted, and SMB/UNC sources.
Sources can be enabled, watched, rescanned, or removed independently. Folder
exclusions apply recursively. Scan progress, pause/resume, cancellation,
failure details, scheduling safeguards, and artwork-cache maintenance are
available in the same section. Removing a source or clearing artwork never
deletes music files.

Metadata preferences are staged until Apply. They cover multi-value tag
separators, the default database-only or write-to-file policy, metadata-cache
retention, and the MusicBrainz and Discogs providers. Online metadata is off by
default. Enabling a provider does not silently edit tags: matches are reviewed
before an edit, and file writes still use the guarded metadata workflow.

## Lyrics

Lyrics settings control automatic, synced, or static presentation, typography,
alignment, line spacing, artwork blur, karaoke word animation, and the optional
manual LRCLIB provider. Per-track timing offsets and selected lyric sources are
managed from Now Playing because they belong to the current track rather than
to a global preference.

## Keyboard shortcuts

The shortcut editor supports in-app and global bindings, enable/disable,
keyboard capture, validation, conflict reporting, removal, default reset, and
JSON preset import/export. Invalid or conflicting gestures stay visible for
correction instead of being silently discarded. Windows can still reject a
valid global shortcut when another application owns it; the registration
result is shown next to that binding.

Mouse4 and Mouse5 remain fixed browser-style Back and Forward navigation
inputs. Standard media keys continue through Windows media integration.

## Per-view preferences

Every library view can keep its own sort field and direction, quick filter,
density, cover size, visible columns, column order, and column widths. A single
view can be reset without disturbing the others, or every view profile can be
returned to its defaults. Applying a view profile refreshes the presentation;
it does not rescan or modify the library.

## Apply, revert, import, and reset

Safe visual changes preview live. Changes that can rebuild playback, alter tag
behavior, re-register shortcuts, or reshape a library view remain drafts until
Apply. Revert restores the persisted values. Closing Settings with unapplied
drafts prompts before discarding them.

The Data section can export or import validated settings JSON, create or
restore a complete `.dexbackup`, and reset individual areas. Scoped resets do
not erase unrelated preferences. User-data restore creates a safety backup and
never embeds or overwrites music files.

The following values are internal session state rather than settings controls:
search history, queue/session restoration, per-track lyric choices and offsets,
per-track playback overrides, and the legacy library-folder migration field.
They are still normalized, persisted, and covered by recovery tests.

## Diagnostics, About, and privacy

Diagnostics exports a local, redacted support bundle and can check installed
decoder paths with embedded synthetic audio. The About section shows the app
and runtime versions, build commit when available, app-data and log paths,
third-party license location, privacy summary, and update policy.

Dextromethorphan performs no telemetry or automatic background update check.
Updates are manual. Lyrics and metadata providers are independently opt-in and
make requests only after the corresponding feature is enabled and used.

