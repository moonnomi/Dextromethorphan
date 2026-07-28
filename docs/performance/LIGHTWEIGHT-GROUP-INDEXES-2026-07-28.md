# Lightweight group indexes

VIEW-005 removes the full `Track[]` projection previously retained by every album, artist, genre, and folder card.

## What changed

- Library grouping creates compact ordered integer membership indexes into the one `_allTracks` source.
- Cards retain only their count, representative track, metadata, and membership indexes.
- Opening or playing a collection creates an `IndexedReadOnlyList<Track>` view; it resolves source tracks lazily and does not copy references into another full array.
- Playlist materialization remains explicit and is handled separately by VIEW-006.
- Existing ordering is preserved for album disc/track order, artist discography, genres, and filesystem folders.

## Verification

- `dotnet test Dextromethorphan.slnx -c Release --no-restore`
- 92 tests pass, including indexed ordering, object identity, and source-slot update behavior.
- 50k-track warm WPF sample:

| Metric | Result |
|---|---:|
| Process to interactive | 3,120.351 ms |
| Cached tab maximum | 77.664 ms |
| Mouse history state verification | Pass |
| Album scroll p95 | 15.836 ms |
| Album scroll worst | 30.570 ms |
| Scroll frames over 50 ms | 0 |
| Working set after startup | 299.4 MB |
| Working set after navigation | 318.8 MB |
| Peak working set | 351.4 MB |

For context, the prior stored 50k PERF-006 reference runs peaked between 372.8 and 393.5 MB. This single sample is below that range, but the final under-300 MB gate still requires the later hidden-view, artwork-lifetime, and cleanup work.
