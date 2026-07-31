# Milestone 1 completion — 2026-07-29

Milestone 1 is complete and covered by the release-gate harness.

## Final implementation

- Library presentation is split into reusable controls rather than one monolithic window tree.
- A stable `LibraryGroupingIndex` owns lightweight track slots. File-watcher changes update only affected album, artist, genre, and folder cards.
- Startup exposes the shell before background library work; scan notifications are coalesced and scanning supports cancellation, pause/resume, checkpoints, and bounded per-source concurrency.
- Windows reduced-motion preferences are respected. Home, End, Page Up, Page Down, scrollbar, wheel, touchpad, and nested scrolling follow one policy.
- Idle cleanup runs only when the scanner and artwork queue are inactive. It trims thumbnail memory, stale presentations, navigation history, and completed jobs.

## Scale validation

The same gallery traversal used for release gates was run at 10k, 50k, and 100k tracks. The 100k fixture contained 5,000 albums and 1,031 artists:

- all 5,000 album cards remained present after top/middle/bottom traversal;
- all 424 expected visible artwork sources rendered;
- zero missing cards and zero card/artwork mapping failures;
- cached Albums application: 42.809 ms;
- scroll-frame p95: 19.864 ms;
- library ready: 6.191 s on the development machine;
- settled working set: 356,061,184 bytes.

The 100k result is a scale qualification, not the release budget. The formal consumer release gates remain the calibrated 10k and 50k runs documented in [RELEASE-GATES.md](RELEASE-GATES.md).

## Real-library safety regression

The user library is tested only through an isolated app-data clone selected with `DEXTROMETHORPHAN_DATA_ROOT`. The application never opens the live database for writing during the test. The 531-track, 302-album clone rendered all 302 cards through repeated top/middle/bottom traversal with no missing or mismatched artwork.
