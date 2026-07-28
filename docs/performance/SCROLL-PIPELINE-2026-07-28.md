# Scroll pipeline hardening

SCROLL-001 through SCROLL-005 replace the refresh-rate-dependent wheel loop with a bounded input/render lifecycle.

## What changed

- Smoothing uses elapsed time and an exponential response, producing the same progress at 60, 120, or 144 Hz.
- Small high-resolution wheel/touchpad deltas are preserved instead of quantized.
- Shift+wheel scrolls horizontally when a viewer supports it.
- Boundary input is left unhandled so a nested parent can continue scrolling.
- Reduced-motion Windows settings bypass animation and scroll directly.
- Active viewers are removed when hidden, unloaded, disabled, or settled; the global render callback detaches as soon as no targets remain.
- Gallery and Songs page mutations are debounced until 80 ms after scrolling becomes idle and no thumb drag or smooth animation remains.
- The large software `BlurEffect` was removed from Now Playing; its enlarged low-resolution background art remains cached beneath the dark gradient.

## Verification

- `dotnet test Dextromethorphan.slnx -c Release --no-restore`
- 95 tests pass, including refresh-rate equivalence, boundary handoff, and bounded long-frame convergence.
- 10k-track warm WPF sample:

| Metric | Result |
|---|---:|
| Process to interactive | 1,684.376 ms |
| Cached tab maximum | 68.864 ms |
| Album scroll p95 | 15.615 ms |
| Album scroll worst | 30.367 ms |
| Scroll frames over 50 ms | 0 |
| Idle CPU | 4.916% |
| History / hidden view / paged Songs checks | Pass / Pass / Pass |

This sample is inside the 60 Hz p95, 50 ms routine-frame, zero-long-stall, and 5% idle-CPU budgets. Final qualification uses the multi-run Gate 005 reports.
