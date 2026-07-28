# Paged folder and playlist sidebars â€” 2026-07-28

The Folders tab previously assigned every folder card to the WPF sidebar whenever the tab was opened. The deterministic 50k fixture contains 2,500 folders, so even a virtualized list still paid collection-view and binding setup costs for far more rows than it could display.

## Implementation

- Folders and Playlists now keep separate stable presentation caches.
- A tab exposes an initial 32-card window instead of swapping the full source into WPF.
- Near-bottom paging adds another 32 cards only after mouse capture and smooth scrolling have gone idle.
- Scroll state stores both the pixel offset and materialized-card count, so Back/Forward and tab restoration can rebuild the same window before restoring the offset.
- Artwork planning sees only the materialized sidebar window; it no longer considers thousands of invisible folder cards.
- Selection and playback still resolve against the complete source index.

## 50k verification

Two fresh-process Release runs passed all 10 applicable release gates:

| Metric | Result |
|---|---:|
| Cached tab-switch maximum | 97.870 ms |
| Concurrent scan/playback/navigation maximum | 84.897 ms |
| Album-scroll p95 | 12.901 ms |
| Album-scroll worst frame | 21.754 ms |
| Frames over 50 ms | 0 |
| Settled working set | 282.2 MiB |
| Idle CPU | 5.402% |

The focused report is stored locally under `performance-results/sidebar-paging-check-50k-20260728`.
