# Artwork and virtualization optimization — 2026-07-25

This pass addresses the dominant causes identified by the PERF-002 baseline and PERF-003 trace:

- synchronous artwork file access and decode on the UI thread;
- weak-reference bitmap caching that caused decode/GC/decode churn;
- a non-virtualized album gallery;
- a non-virtualized folder sidebar;
- replacing the entire gallery collection whenever another page was loaded.

## Implementation

- Artwork now loads through one asynchronous service.
- File access and WPF bitmap decoding run on worker threads.
- Decoded bitmaps are frozen before returning to the UI.
- Concurrent requests for the same path and pixel width share one decode.
- A strong 96 MB LRU cache bounds decoded artwork memory.
- Recycled or unloaded image controls cancel their stale waiters and release their sources.
- The gallery uses a virtualizing wrap panel with generator-backed container recycling. Cleanup walks realized generator positions from the end, recycles only positions outside the overscan window, and the performance workload validates the original item/data-context mapping after returning to the top.
- Pixel offsets that remain inside the current realization row invalidate arrangement only; measure, cleanup, and generation run when the overscan row actually changes.
- Folder/sidebar lists use recycling `VirtualizingStackPanel` containers.
- Gallery pagination appends to a stable `ObservableCollection` instead of replacing its `ItemsSource`.

## 10k-track result

Release build, deterministic 10k fixture, seed `20260725`, two fresh processes:

| Metric | Before | After |
|---|---:|---:|
| Album-scroll p95 frame | 465.3 ms | 17.0 ms |
| Album-scroll worst frame | 1,076.4 ms | 38.3 ms |
| Scroll frames over 50 ms | 48 | 0 |
| Cached tab switch, median | 138.2 ms | 66.6 ms |
| Cached tab switch, maximum | 1,650.3 ms | 138.0 ms |
| Cold process to interactive | 1,632.9 ms | 1,569.8 ms |
| Peak working set | 461.0 MB | 327.5 MB |
| Idle CPU, median | 0.827% | 4.752% |

The before figures come from [PERF-002](BASELINE-2026-07-25.md). The after report is stored locally under `performance-results/perf-artwork-virtualization-final-20260725`.

All 40 tests pass, the Release build has zero warnings, the benchmark recorded zero errors, and all recorded `thumbnail.decode-off-thread` operations ran outside the UI thread.

## 50k recycling follow-up â€” 2026-07-28

The generator-backed recycling and row-aware layout pass was measured in two fresh-process runs against the deterministic 50k fixture:

| Metric | Standard cleanup | Recycling + row-aware layout |
|---|---:|---:|
| Album-scroll p95 frame | 24.402 ms | 13.166 ms |
| Album-scroll worst frame | 45.935 ms | 22.376 ms |
| Frames over 50 ms | 0 | 0 |
| Cached tab-switch maximum | 104.611 ms | 87.477 ms |
| Settled working set | 288.8 MiB | 267.2 MiB |

The workload materialized 500 cards, traversed the virtualized gallery, returned to the top, and verified the item/data-context mapping for every expected top-row container. The previous disappearing-card failure did not reproduce.

## Remaining work

The Songs view is now the slowest cached tab at about 131 ms median because its full track projection is still applied at once. The next performance work should focus on view-state reuse and paging/windowing the Songs projection, followed by the opt-in live performance overlay.
