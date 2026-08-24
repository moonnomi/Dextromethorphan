# Milestone 6 status — 2026-08-24

Milestone 6 is complete. Every consumer-facing `AppSettings` value is now
reachable from the application, while transient session and per-track state is
explicitly classified as internal. Settings no longer requires manual JSON
editing for normal use.

## Delivered

- **SET-001:** A coverage inventory classifies every public `AppSettings`
  property as user-facing or internal so new options cannot silently become
  JSON-only.
- **SET-002:** Dark, Light, and AMOLED apply live. Accent input is normalized,
  checked against a 4.5:1 contrast target, and adjusted to the nearest usable
  lightness when needed.
- **SET-003:** Interface font and size, background opacity, animation,
  visualizer, queue visibility and width, fullscreen navigation, cover size,
  and dashboard modules are configurable.
- **SET-004:** Playback settings include resume behavior, stop modes,
  gapless/crossfade, fades, ReplayGain, clipping prevention, speed, pitch,
  pitch preservation, and seek/volume step sizes. Output profiles retain their
  existing explicit Save behavior.
- **SET-005:** Source, exclusion, watcher, scan, scheduling, artwork cache,
  multi-value tag parsing, default tag-write policy, cache retention, and
  metadata-provider controls are exposed.
- **SET-006:** The shortcut editor supports capture, validation, conflict and
  registration feedback, global/in-app scope, enable/disable, removal, reset,
  and preset import/export.
- **SET-007:** Per-view sorting, filtering, cover size, density, columns,
  ordering, widths, and scoped reset are editable without changing the library.
- **SET-008:** Safe appearance changes preview live with debounced persistence;
  playback, metadata, shortcut, output, and view changes use explicit
  Apply/Revert or Save semantics.
- **SET-009:** Settings search opens matching sections, and contextual links
  connect audio, diagnostics, help, and data-safety information.
- **SET-010:** About reports application/runtime/build information, app-data
  locations, packaged licenses, the offline privacy policy, and the manual
  update status.

## Data safety and recovery

Settings schema 9 adds background opacity, queue width, fullscreen navigation,
and a default metadata-write policy. Older accent defaults migrate to the
current visual system, and all new numeric and enum values are normalized.
Appearance reset now includes the visualizer and panel behavior. Metadata has a
dedicated reset scope, so provider credentials and tag policy can be cleared
without resetting the library.

Imported or reset settings are reapplied to the active theme, playback engine,
shortcuts, scanner, source lists, scheduling controls, and output-profile
editor. Settings writes remain atomic and retain the previous file as a
recovery backup.

## Verification

- The settings coverage test compares the public `AppSettings` surface with
  the user-facing/internal classification.
- Theme tests cover preset normalization, accepted color forms, Dark/Light/
  AMOLED contrast, foreground selection, background opacity, and live resource
  replacement.
- Workspace-component tests cover the complete shortcut action catalog,
  editable-binding round trips, and per-view persistence/reset behavior.
- Shortcut-service tests prove unrelated settings writes do not churn Windows
  registrations while real gesture/scope changes do.
- The STA Settings regression test verifies the selected-content template and
  materializes every section. The isolated consumer smoke opens the published
  app, visits all 11 sections, exercises search, switches through Light,
  AMOLED, and Dark, closes Settings, and confirms a clean app shutdown without
  using the normal app-data root.
- Settings persistence, atomic recovery, import/export, scoped reset, output
  profile, scheduled scan, diagnostics-redaction, and user-data backup tests
  remain part of the Release solution gate.
- The release build is produced through `scripts/build-release.ps1` into
  `src/Dextromethorphan.App/bin/latest` and the Windows archive under
  `artifacts`.
- The final Release gate passed all 403 automated tests.

Physical DAC evidence is not part of this milestone. Exclusive-WASAPI, DoP,
native-DSD, and extended underrun qualification remain accurately marked as
hardware-gated in Milestone 3; the Settings implementation does not claim that
changing a device profile proves the hardware path.

## Next roadmap area

Milestone 7 covers the remaining cross-application visual consistency,
accessibility, small-window and DPI verification, High Contrast behavior, and
advanced panel docking. Secure automated updates remain a later engineering and
distribution milestone; About deliberately reports a manual, offline update
policy today.
