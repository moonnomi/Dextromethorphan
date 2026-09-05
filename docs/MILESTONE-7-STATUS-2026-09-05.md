# Milestone 7 follow-up — 2026-09-05

Milestone 7 is still open. This follow-up records completed work and current evidence without replacing the outstanding qualification matrix in [the original status report](MILESTONE-7-STATUS-2026-08-24.md).

## Delivered

- Settings category templates now live in `UI/Styles/SettingsControls.xaml`. The category rail scrolls within its available height, keeping lower categories reachable in short windows. An STA regression test forces a 320-DIP viewport and checks that the rail scrolls.
- Performance and audio diagnostics are separate reusable views. They inherit the existing data context, commands, and theme resources. Their icon buttons have explicit automation names, and the performance shortcut text uses the theme's muted foreground.
- Settings search describes the current startup-only position restore behavior.
- Windowing qualification now includes the queue docked left at minimum size, restoring the original dock side afterward.

## Evidence

- Release suite: 486 passed, zero failed. Existing test-only compiler/analyzer warnings remain.
- Settings consumer automation: all 11 categories, search, Light/Amoled/Dark selection, and clean exit passed with isolated app data.
- Windowing harness: seven cases passed their control-boundary checks at 125% DPI. Minimum size with queue hidden/right/left and laptop sizes were realized. Desktop and ultrawide requests were clamped by the active display and are not physical-size qualification passes.
- Native maximize hit test returned 9; maximize/restore clicks and taskbar-safe fullscreen control visibility passed.
- Minimum-size queue-left/hidden and laptop screenshots were inspected. The run used an empty isolated library and does not qualify populated library or lyric layouts.
- The design detector reported no findings on the extracted views and styles.
- Local report and screenshots: `artifacts/qualification/milestone-7-20260905/windowing/`.

## Remaining work

UX-002 remains partial: the diagnostic panels and Settings navigation are extracted, but other shell and Settings content still contains inline presentation. UX-007/008 retain their populated-view, large-display, and other-DPI verification gaps. A11Y-001 through A11Y-004 require the complete keyboard, automation, Narrator, and contrast sweep. A11Y-008 retains the physical input matrix. Localization/RTL and floating panels remain as recorded in the master roadmap.

These checks cannot be substituted with a passing unit-test count. No physical DAC is needed for this milestone. The separate eight-hour PCM soak can use an onboard output; physical DoP qualification still needs a compatible DAC.
