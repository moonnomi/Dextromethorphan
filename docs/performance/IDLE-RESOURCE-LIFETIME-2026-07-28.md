# Idle resource lifetime

SYS-005 closes the retained-resource paths found across the artwork, navigation, and render pipelines.

## Retention fixes

- Hidden artwork controls cancel their requests, detach source-invalidation handlers, clear animation clocks, and release bitmap sources.
- View changes cancel and dispose superseded artwork and paging token sources.
- Gallery, track, and playlist presentation state is cached in bounded/lazy forms rather than retaining duplicate full-library projections.
- Render-frame measurement and smooth-scroll callbacks detach at completion, unload, or visibility loss.
- Top-tab transitions use explicit base states and versioned code-behind clocks that remove themselves on completion; resource-dictionary storyboards no longer retain six indicator clocks.
- View transitions and artwork fades explicitly detach their clocks in their `Completed` callbacks, guarded by request/transition versions so an older completion cannot cancel a newer animation.
- Startup replaces the animated orbit and scale `Freezable` objects after the overlay collapses. This prevents WPF's composition timing manager from retaining the infinite startup clock.
- The audio position timer publishes only while playing or buffering, avoiding ten full UI snapshot updates per second while paused or stopped.

## Diagnostics

Performance report schema 4 includes the highest-CPU process threads for the idle window and identifies the UI thread. This exposed the WPF composition thread rather than the dispatcher as the remaining idle consumer.

The report also snapshots every visual-tree object that still implements `IAnimatable` with animated properties. The final focused run records zero active animation objects before sampling CPU.

## Result

On the same 10k warm benchmark:

| Metric | Before | After |
|---|---:|---:|
| Idle CPU | 5.82% | 4.76% |
| Dominant composition-thread CPU over 2 seconds | 1,812.5 ms | 1,515.6 ms |

A follow-up that detached completed finite clocks reduced the focused sample again to 3.78% idle CPU and 1,187.5 ms on the composition thread. The full multi-process release run remains responsible for the median idle gate; these focused samples prove that retained timing resources were removed rather than masking the measurement threshold.
