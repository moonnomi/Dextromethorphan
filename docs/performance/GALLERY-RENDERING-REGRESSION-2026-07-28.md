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
