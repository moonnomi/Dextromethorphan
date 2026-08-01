# Gallery rendering regression — 2026-07-28

The Albums view could report the correct 302-album total while showing only a few cards. After scrolling farther, even previously visible cards could disappear.

## Cause

The database, artwork paths, and thumbnail files were intact. Two independent presentation problems produced the same blank-library symptom:

- incremental card paging depended on a near-bottom `ScrollChanged` event after smooth scrolling became idle, so normal mouse scrolling could leave the gallery stranded on its first 28-card page;
- the custom virtualizing wrap panel used WPF container recycling across disjoint pixel-scroll ranges. A recycled `ListBoxItem` could be returned before its image template had completed its unload/rebind/load lifecycle.

Item-count and data-context checks did not catch the image-lifecycle failure because it lived inside the attached asynchronous image state. The performance runner now has an optional visual gallery regression mode that:

- requires the complete lightweight card source to be exposed from the first frame;
- inspects realized card-to-item mappings;
- requires every realized card with an existing artwork file to have a rendered `Image.Source`;
- traverses the full extent in 10% increments, then revisits distant ranges in reverse directions;
- saves PNG captures for visual review.

## Corrective change

The gallery receives its complete, already-grouped card-reference list when the view opens. This makes its scroll extent independent of input timing and removes the pagination failure mode. It remains virtualized: only buffered visible rows exist in the WPF visual tree, and artwork is decoded only for live `Image` controls.

Off-screen gallery containers are also removed instead of recycled. A newly visible card therefore receives a fresh prepared container and artwork load lifecycle. Track, queue, lyrics, and sidebar lists keep their normal paging/recycling behavior.

## Real-library verification

The application was run against an isolated copy of the user's actual application database, settings, and artwork cache. `DEXTROMETHORPHAN_DATA_ROOT` redirected every database, settings, session, log, and thumbnail write to that copy. The live `%APPDATA%\Dextromethorphan` data and music files were not used as write targets.

| Check | Result |
|---|---:|
| Source album cards | 302 |
| Initially exposed cards | 302 |
| Final materialized cards | 302 |
| Incremental page advances | 0 |
| Scroll/artwork checkpoints | 16 |
| Realized artwork instances checked | 304 |
| Rendered artwork instances | 304 |
| Blank expected artwork instances | 0 |
| Container-to-album mapping failures | 0 |

Top, middle, and bottom PNGs all contained the expected album grid. The same run recorded a 10.035 ms album-scroll p95, 21.682 ms worst frame, and zero frames over 33 ms. Correctness is restored without removing virtualization or returning to eager full-library image creation.

A separate deterministic 50,000-track / 2,500-album image fixture exposed all 2,500 cards immediately and rendered 310/310 inspected artwork instances across the same 16 checkpoints. Its album-scroll p95 was 8.610 ms, cached Albums switch was 24.518 ms, and settled working set was 292.1 MiB.

## Recurrence hardening — 2026-07-31

A later user report showed the same visible symptom after rapid scrolling: the header still reported 302 albums while a realized range could appear empty, and returning through older ranges could leave cards absent. The complete card collection and image-request pipeline were intact. The remaining intermittent risk was in arrangement: the custom panel asked WPF's item generator to translate every realized child position during `ArrangeOverride`. During removal and recreation of a distant range, that lookup can temporarily return no item index; the valid container was then skipped for that layout pass.

Each realized container now retains the authoritative source index assigned by the generation loop. Measure, cleanup, and arrange use that stable index, with the generator lookup retained only as a compatibility fallback. This does not retain off-screen containers or images and does not disable virtualization.

The visual regression now adds four rapid, non-monotonic top/bottom recycling cycles after the settled traversal. It verifies both endpoints only after artwork/property queues settle and captures the final return-to-top state.

The current production build was qualified against an isolated copy of the live 531-track / 302-album database and artwork cache:

| Check | Result |
|---|---:|
| Source / final materialized cards | 302 / 302 |
| Inspection checkpoints | 24 |
| Realized cards inspected | 762 |
| Expected / rendered artwork | 762 / 762 |
| Container mapping failures | 0 |
| Missing expected images | 0 |
| Live app-data files hashed before/after | 747 |
| Live files changed | 0 |

The qualification process used `DEXTROMETHORPHAN_DATA_ROOT` to select the copied app-data directory. It never opened the live database as its writable database, and the SHA-256/length/timestamp snapshot confirmed that the live database, settings, artwork, and sidecars were unchanged.

## Custom-panel removal — 2026-08-01

A further real-world recurrence showed that retaining an authoritative index on each custom-panel child reduced the failure rate but did not eliminate it. The album count remained correct while a partial row could lose its remaining controls, and later scrolling could leave an empty realized range. The remaining common dependency was the hand-written per-card `ItemContainerGenerator` bookkeeping.

The gallery no longer uses a custom virtualizing panel. The complete lightweight card list is packed into responsive rows, and WPF's built-in `VirtualizingStackPanel` virtualizes those rows. Each realized row contains only the small number of cards that fit the current viewport. Standard container removal, a bounded one-page-behind/two-page-ahead cache, and batched row replacement preserve bounded visual-tree size without custom generator positions or recycled artwork state.

The regression harness now resolves the outer row container and every inner card container, verifies the flattened card identity, and checks each expected artwork source. Pure layout tests also repack 302- and 2,500-card inputs at multiple column counts and require every original reference exactly once.

Verification uses an isolated copy of the 531-track / 302-album library database and real artwork cache. The benchmark redirects all writable application state to that copy. A separate SHA-256, length, and timestamp snapshot covered every file in the live app-data directory before and after the run; no live file changed. Top, middle, bottom, and return-to-top captures contained complete rows and real covers.

| Real-library check | Result |
|---|---:|
| Source / initially exposed / final cards | 302 / 302 / 302 |
| Inspection checkpoints | 24 |
| Expected / rendered artwork | 1,378 / 1,378 |
| Container mapping failures | 0 |
| Missing expected images | 0 |
| Visual-anchor navigation restore | Passed |
| Album-scroll p95 / maximum | 16.389 ms / 35.546 ms |
| Frames over 50 ms | 0 |
| Live app-data files before / after | 781 / 781 |
| Live app-data files changed | 0 |

The independent 50,000-track / 2,500-album fixture also retained all 2,500 cards through the same 24 checkpoints. It rendered all 875 expected artwork instances with zero mapping failures or missing images; visual-anchor navigation restoration passed, album-scroll p95 was 11.980 ms, the maximum was 27.139 ms, and no frame exceeded 50 ms.

WPF can revise its estimated raw pixel extent after a distant virtualized row is realized. Navigation history therefore captures the first visible row and its within-row displacement, then restores that visual anchor after layout settles. This keeps the same albums in view even when the equivalent raw `VerticalOffset` changes.
