# Presentation collection reuse

VIEW-003 removes repeated presentation rebuilding when returning to an already-indexed tab or collection.

## What changed

- Each primary gallery owns a cached `ObservableCollection` instead of sharing one collection that is cleared and repopulated.
- Materialized gallery pages remain attached to that tab, so returning to a deep Albums or Artists position does not reconstruct earlier pages.
- Songs, Favorites, folders, playlists, and collection details reuse their track presentation by navigation-state key.
- Source factories are lazy on a cache hit; a cached switch does not enumerate or project the source again.
- A library/search generation invalidates presentation caches before applying new repository results, preventing stale cards or tracks.
- Diagnostics now mark gallery and track-list application with `cacheHit` and materialized counts.

## Verification

- `dotnet test Dextromethorphan.slnx -c Release --no-restore`
- 88 tests pass, including proof that a cached presentation keeps the same collection identity and invokes its source factory once.
- 10k-track warm fixture, working-tree sample:

| Metric | Result |
|---|---:|
| Cached Albums switch | 52.334 ms |
| Cached Artists switch | 46.574 ms |
| Cached Genres switch | 56.951 ms |
| Cached Songs switch | 37.057 ms |
| Cached Folders switch | 87.362 ms |
| Cached Playlists switch | 92.796 ms |
| Album scroll p95 | 19.745 ms |
| Album scroll worst | 37.570 ms |
| Scroll frames over 50 ms | 0 |

All cached primary switches stayed below the 100 ms PERF-005 budget in this sample. VIEW-004 remains responsible for replacing the remaining collection mutation loops used when a new library generation is applied.
