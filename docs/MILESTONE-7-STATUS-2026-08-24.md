# Milestone 7 qualification status — 2026-08-24

Milestone 7 establishes the shared visual system, responsive window behavior, and the accessibility baseline for the native WPF shell. This document deliberately separates code-level and automated evidence from checks that require a real Windows desktop, assistive technology, or physical input hardware.

Status terms:

- **Implemented** — the behavior is present in the current working tree and has focused automated policy or resource coverage where practical.
- **Partial** — useful implementation exists, but the roadmap item is broader than the delivered coverage.
- **Pending manual** — requires visual, assistive-technology, monitor, or interaction testing and is not claimed as passed.
- **Dependency-gated** — intentionally not started until its prerequisite is reliable.

## Delivery summary

| Roadmap item | Status | Evidence and remaining gate |
| --- | --- | --- |
| UX-001 shared tokens | Implemented | `UI/Styles/DesignTokens.xaml` defines spacing, radii, type, interaction, motion, and elevation primitives. `DesignSystemResourceTests` materializes the semantic resources. |
| UX-002 reusable themed controls | Partial | Reusable window/dialog surfaces, standard-control styles, `StatePresenter`, and `TrackMetadataToolTip` are in place. The main shell still contains substantial inline layout and view-specific styling, so the broader decomposition/removal-of-one-offs goal is not complete. |
| UX-003 themed native surfaces | Implemented; visual sweep pending | Implicit styles cover windows, popups, tooltips, context menus/menu items, buttons, combo boxes/items, checkboxes, radio buttons, progress bars, text/password boxes, sliders, and scrollbars. Every reachable popup/dialog still needs the manual theme sweep below. |
| UX-004 consistent states | Implemented | The reusable presenter supports loading, empty, offline, error, disabled, and success semantics. Library, lyrics, and queue paths use the same visual and automation treatment. |
| UX-005 themed toasts | Implemented | Severity-specific information/success/warning/error toasts have a non-color glyph, automation live text, explicit dismissal, and longer dwell time for errors. Queue, playlist, scan/artwork, Settings apply, playback, rating/bookmark, and failure paths publish feedback. |
| UX-006 metadata tooltips | Implemented | Track and queue surfaces use a reusable tooltip with full title, artist, album, quality, bitrate, channels, duration, and source path. Manual clipping and long-path checks remain. |
| UX-007 layout sizes | Partially qualified | The live 125% DPI harness passed and screenshots were reviewed at 800×600, 1366×768, and 1536×864 with queue-visible/hidden variants. The active display could not physically realize 1920×1080 or 3440×1440, so those real-display rows remain pending. |
| UX-008 PerMonitorV2 DPI | Policy implemented; 125% passed | The manifest declares `PerMonitorV2,PerMonitor`; placement math and monitor-change hooks operate in physical pixels. The live 125% run passed. Real 100/150/200% and mixed-DPI monitor movement remain pending. |
| UX-009 safe placement restore | Implemented | Placement persists physical bounds, monitor identity/work area, DPI, and maximized state. Restore chooses an existing/nearest monitor and clamps to the current work area; display, DPI, and setting changes re-check title-bar visibility. |
| UX-010 snap layouts | Implemented and probed | The custom maximize button returned `HTMAXBUTTON` (`9`) on this Windows 11 run at 125% DPI. A human hover/click sweep remains useful, while Win+Arrow remains the equivalent on supported Windows versions. |
| UX-011 configurable panels | Implemented for dock/collapse | Queue visibility, compact state, width, and left/right dock side persist. Now Playing can show/hide lyrics and metadata modules and switch the inspector between them. |
| UX-012 floating panels | Dependency-gated | Deliberately deferred until state synchronization, keyboard focus restoration, ownership, shutdown, and multi-window DPI behavior are proven reliable. |
| A11Y-001 keyboard reachability | Implemented baseline; pending manual | Logical tab navigation, keyboard list navigation, access keys, and keyboard commands are wired. End-to-end focus order must still be verified in every view and dialog. |
| A11Y-002 focus and automation | Implemented baseline; pending manual | Named controls, help/item status text, list item metadata, and visible focus borders are present. Inspect/Narrator review remains pending. |
| A11Y-003 Narrator | Pending manual | No Narrator pass is claimed. Use the matrix below for navigation, metadata, transport, sliders, queue, dialogs, states, and toast announcements. |
| A11Y-004 contrast/non-color state | Automated baseline; pending visual audit | Theme accent correction and semantic status-pair tests enforce at least 4.5:1 in the tested palettes. Toasts and playback states also use glyph/text or shape, not color alone. Full text/control-state contrast needs a visual audit. |
| A11Y-005 High Contrast/reduced motion | Implemented; visual sweep pending | Motion requires both the app toggle and Windows `ClientAreaAnimation`. High Contrast follows `SystemParameters.HighContrast`, maps semantic resources to `SystemColors`, reacts live, and restores the requested app theme when disabled. Automated policy tests pass; each installed Windows contrast scheme still needs a visual sweep. |
| A11Y-006 keyboard menus/multi-select | Implemented baseline; pending manual | Track and queue lists expose multi-selection help, native Shift+F10 context menus, access-key labels, Ctrl+A, and modifier selection paths. Verify all lists and dialogs manually. |
| A11Y-007 slider semantics | Implemented | Seek and volume expose automation names, current status, help text, fine arrow increments, coarse Ctrl+Arrow/Page increments, and Home/End boundaries. Playback seek commits keyboard changes to the engine. Narrator announcement quality remains pending. |
| A11Y-008 input audit | Pending hardware/manual | Mouse4/Mouse5 navigation and Windows media-control routing exist, but touch, pen, precision touchpad, media keys, and remote-control behavior are not qualified by code alone. |
| A11Y-009 localization/RTL | Not delivered | User-facing strings remain embedded throughout XAML/C#. Resource extraction, layout mirroring, truncation expansion, and an RTL locale pass belong to a future localization milestone. |

## Automated evidence

The following focused tests are part of the Milestone 7 implementation:

- `DesignSystemResourceTests`: semantic token materialization, reusable/implicit control styles, and 4.5:1 semantic status-pair contrast.
- `ThemeManagerTests`: accent normalization and contrast correction across Dark, Light, and Amoled palettes.
- `ToastNotificationTests`: non-color severity glyphs, automation text, and longer error dwell time.
- `SliderAccessibilityTests`: fine/coarse keyboard increments, range clamping, Home/End, and unrelated-key pass-through.
- `LiveRegionBehaviorTests`: polite/assertive live announcements, visibility gating, and normalized announcement text.
- `HighContrastThemeTests`: semantic SystemColors mapping, runtime enable/disable, and requested-theme restoration.
- `MilestoneSevenShellContractTests`: queue dock/collapse persistence and Now Playing lyrics/metadata inspector contracts.
- `InteractionPolicyTests`: Windows/app reduced-motion arbitration and list navigation-key policy.
- `WindowingPolicyTests`: responsive width classes, 800 px compact queue cap, physical-pixel restore at 100/125/150/200%, removed-monitor fallback, resolution clamping, mixed-DPI monitor choice, recoverable title-bar bounds, and PerMonitorV2 manifest declaration.
- `SingleInstanceCoordinatorTests`: qualification-only `--windowing-smoke` arguments are not mistaken for media launch targets.

Run the integrated suite with:

```powershell
dotnet test Dextromethorphan.slnx -c Release --no-restore
```

Run the visual windowing harness against an isolated data root so qualification cannot alter a normal user library:

```powershell
$env:DEXTROMETHORPHAN_DATA_ROOT = Join-Path $env:TEMP 'Dextromethorphan-M7-Qualification'
& .\src\Dextromethorphan.App\bin\latest\Dextromethorphan.exe `
  --windowing-smoke (Join-Path $env:TEMP 'Dextromethorphan-M7-Windowing')
```

The harness writes `windowing-smoke.json` and one PNG per size/queue combination. A generated report is evidence of geometry and hit testing at the machine's active DPI only; a person must still inspect the screenshots for clipping, overlap, contrast, and visual hierarchy.

### Current integration record

- Full Release suite: **passed 467/467** on 2026-08-24; the application project builds Release with zero warnings and zero errors. Test-only analyzer warnings remain in pre-existing fixtures.
- Impeccable final UI detector: **passed with no findings** across the changed shell, Settings, design system, dialogs, reusable states/tooltips, and track-list XAML.
- Windowing smoke: **passed 6/6** at 125% DPI. All critical controls remained inside the shell, the 800×600 visible queue was capped at 272 px, and the Windows 11 maximize hit test returned `HTMAXBUTTON` (`9`). Requested 1920×1080 and 3440×1440 cases were clamped by the active monitor and are not claimed as physical-size passes.
- Real-library gallery regression: **passed** against an isolated clone containing 315 albums. All 315 cards remained materialized through 24 top/middle/bottom/rapid-recycling checkpoints; 1,512/1,512 expected artwork sources rendered with zero missing sources and zero card mappings errors.
- Settings consumer smoke: **passed**, including a second pass against the packaged `bin/latest`. It opened Settings, visited all 11 categories, exercised search, switched Light/Amoled/Dark live, closed Settings, and exited cleanly.
- Library safety: SHA-256, byte length, and modification time for the live `library-v2.db` and `settings.json` matched before and after qualification. Every application write was redirected to the isolated clone.
- Local evidence folder: `artifacts/qualification/milestone-7-20260824-205547` (gitignored).
- No automated result substitutes for the remaining assistive-technology, display-topology, or physical-input rows below.

## Manual verification matrix

Create a qualification folder containing `windowing-smoke.json`, screenshots, Windows build, GPU, display topology, input devices, and brief pass/fail notes. Keep the test library isolated from normal user data.

### Window sizes and responsive layout

| Case | Queue | Status | Verify |
| --- | --- | --- | --- |
| 800×600 | Hidden | Pending manual | Primary tabs or compact navigation remain reachable; compact search is available; title buttons, mini-player, seek, volume, and transport remain fully visible; no taskbar overlap. |
| 800×600 | Visible, left and right | Pending manual | Queue is width-capped, content is still usable, queue collapse/expand and actions remain reachable, and the player never disappears below the work area. |
| 1366×768 | Visible | Pending manual | Laptop layout preserves search, library controls, queue, track rows, and bottom transport without overlap or clipped menus. |
| 1536×864 | Hidden | Pending manual | Main content uses the available width without leaving orphaned controls; reopening the queue does not shift focus unexpectedly. |
| 1920×1080 | Visible | Pending manual | Expanded title utilities, tooltips, inspector, queue, and player align cleanly. |
| 3440×1440 | Visible, left and right | Pending manual | Content does not stretch into unreadable lines; queue/inspector widths remain useful; menus and toasts appear near their owning window. |
| Maximize/restore/full screen | Both | Pending manual | Work-area maximization never covers the taskbar; full screen preserves transport; restoring returns to valid bounds and focus. |

For every size, open Albums, Artists, Genres, Songs, Folders, Playlists, a collection detail tab, Now Playing lyrics, Now Playing metadata, Settings, all context menus, a tooltip with a long path, and an error dialog.

### DPI and monitor topology

| Case | Status | Verify |
| --- | --- | --- |
| Single monitor at 100% | Pending manual | Crisp text/icons/artwork; expected 800×600 minimum; correct pointer and maximize hit targets. |
| Single monitor at 125% | Pending manual | No fractional clipping, blurry re-render, misplaced popup, or resized seek/volume thumb. |
| Single monitor at 150% | Pending manual | Same checks; close/reopen preserves physical placement. |
| Single monitor at 200% | Pending manual | Minimum layout remains operable; dialogs and Settings fit the work area. |
| Mixed 100% → 150%/200% | Pending manual; multi-monitor hardware required | Drag the normal, maximized, Settings, context-menu, and tooltip surfaces between monitors; verify re-scaling and hit testing after `WM_DPICHANGED`. |
| Remove saved monitor | Pending manual; topology change required | Close on the secondary monitor, disconnect it, relaunch, and verify the title bar and entire window are recoverable on an available work area. |
| Resolution/taskbar work-area change | Pending manual | Change resolution and taskbar position/auto-hide; verify restore and maximize do not cover controls or the taskbar. |
| Windows 11 Snap Layout | Pending manual; Windows 11 required | Hover the custom maximize button and confirm the native Snap Layout flyout; test click maximize/restore and Win+Arrow snapping. |

### Accessibility display and motion

| Case | Status | Verify |
| --- | --- | --- |
| High Contrast — each available Windows scheme | Pending manual | Text, focus, selection, disabled state, separators, sliders, scrollbars, menus, dialogs, toasts, and artwork fallbacks remain distinguishable. Record any hard-coded color that loses meaning. |
| Windows Animation effects off, app animations on | Pending manual | Startup, navigation, scrolling, artwork reveal, lyrics, queue drag, and full-screen transitions snap immediately without lost state. |
| Windows Animation effects on, app animations off | Pending manual | Same result; no lingering animation clocks or delayed focus. |
| Both animation switches on | Pending manual | Motion is brief, does not move focus, and does not make controls unresponsive. |
| Contrast/non-color state | Pending manual | Shuffle/repeat/current queue item/toast/error/success/focus remain identifiable without relying on hue alone. |

### Narrator and keyboard-only

| Case | Status | Verify |
| --- | --- | --- |
| Narrator landmarks and tabs | Pending manual | Window name, primary library destinations, collection tab, search, queue, Now Playing, and Settings are announced once with useful names. |
| Track metadata | Pending manual | A track announces title, artist, album, duration, selection/current state, and missing/failed state without reading decorative glyphs. |
| Transport | Pending manual | Play/pause state, previous restart behavior, next, shuffle, repeat mode, visualizer, rating, and love state are understandable. |
| Seek and volume | Pending manual | Current values and boundary changes are announced; Arrow, Ctrl+Arrow, Page Up/Down, Home, and End change the intended amount once. |
| Queue | Pending manual | Current/next/past status, multi-selection, keyboard reorder, remove, undo/redo, empty state, compact/expand, and dock side are announced. |
| States, dialogs, and toasts | Pending manual | Loading/error/offline/empty text, dialog title/body/actions, and transient feedback are announced without trapping focus or excessive repetition. |
| Tab/Shift+Tab focus order | Pending manual | Every interactive control is reachable in a logical visual order; focus remains visible and returns to the invoker after closing a popup/dialog. |
| Access keys and global navigation | Pending manual | Alt+B/R/G/S/F/P, Ctrl+F, F11, Escape, browser back/forward shortcuts, and window buttons work without a pointer. |
| Lists and context menus | Pending manual | Arrow/Home/End/Page keys, Ctrl+A, Shift/Ctrl multi-selection, Enter, Delete, Ctrl+Up/Down reorder, Shift+F10, and Escape work in track, queue, folder, playlist, and lyrics lists. |

### Pointer and hardware input

| Input | Status | Verify |
| --- | --- | --- |
| Mouse | Pending manual | Hover/focus parity, drag-seek and drag-volume capture, queue drag preview/drop, resize borders, context menus, and wheel scrolling. |
| Mouse4 / Mouse5 | Pending hardware/manual | Back/forward traverses library and temporary collection history without changing playback or losing selection. |
| Precision touchpad | Pending hardware/manual | Two-finger scrolling is smooth and bounded in galleries, tracks, queue, lyrics, folders, playlists, and Settings; horizontal gestures do not activate unintended controls. |
| Touch | Pending touch-display/manual | Tap targets are large enough, pan scrolling works, sliders can be scrubbed, and queue/list actions have a non-hover path. |
| Pen | Pending pen-hardware/manual | Tap, barrel-button context menu, scrolling, slider scrubbing, and drag interactions behave consistently. |
| Media keys / headset controls | Pending hardware/manual | Play/pause/next/previous/stop reach the active session, state updates in both directions, and actions are not duplicated. |
| Remote control | Pending hardware/manual | Supported directional/select/media commands reach the intended focused control or media session; unsupported keys fail safely. |

## Exit criteria

Milestone 7 should not be declared fully qualified until:

1. the full Release suite passes on the integrated working tree;
2. the windowing smoke report passes and all generated screenshots are reviewed;
3. every manual row above has a recorded Windows build/environment and pass/fail result;
4. any failure affecting playback controls, window recovery, keyboard access, Narrator comprehension, or taskbar safety is fixed or explicitly accepted;
5. UX-012 remains marked dependency-gated rather than being treated as a missing regression.
