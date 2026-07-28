# Scroll pipeline hardening

SCROLL-001 through SCROLL-005 replace the refresh-rate-dependent wheel loop with a bounded input/render lifecycle.

## What changed

- Smoothing uses elapsed time and an exponential response, producing the same progress at 60, 120, or 144 Hz.
- Small high-resolution wheel/touchpad deltas are preserved instead of quantized.
- Shift+wheel scrolls horizontally when a viewer supports it.
- Boundary input is left unhandled so a nested parent can continue scrolling.
- Reduced-motion Windows settings bypass animation and scroll directly.
- Active viewers are removed when hidden, unloaded, disabled, or settled; the global render callback detaches as soon as no targets remain.
- Gallery, sidebar, and Songs page mutations wait until scrolling becomes idle and no thumb drag or smooth animation remains. The wait persists for the full animation rather than abandoning the request after a fixed number of polls.
- The large software `BlurEffect` was removed from Now Playing; its enlarged low-resolution background art remains cached beneath the dark gradient.

## Verification

- `dotnet test Dextromethorphan.slnx -c Release --no-restore`
- 103 tests pass, including refresh-rate equivalence, boundary handoff, bounded long-frame convergence, and long-running deferred-page waits.
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

## Paging regression â€” 2026-07-28

The first idle-paging implementation waited for two 80 ms intervals. The exponential smooth-scroll response commonly remains active for 300â€“500 ms, so the pending request could return without adding a page. The header continued to report the full library while the gallery remained capped at its initial 28 cards; scrolling those cards off-screen made the library appear to disappear.

The idle gate now polls until the latest scroll animation or pointer capture actually ends, unless a newer scroll request cancels it or the view becomes hidden. The performance workload additionally checks realized album/container mappings at the top, quarter, midpoint, and bottom of its 500-card traversal before verifying the return-to-top window.
