# Batched collection application

VIEW-004 removes UI notification storms caused by clearing observable collections and adding every result individually.

## What changed

- Library card results now replace Albums, Artists, Genres, Folders, and Playlists with one reset notification per collection.
- Queue rebuilds publish one reset even when a large playing list changes.
- Synced and unsynced lyric loads publish one reset instead of one event per line.
- Audio-device discovery is applied as a batch.
- Track and gallery presentations created by VIEW-003 are constructed before they are bound, avoiding observable add loops entirely on first activation.
- `ObservableRangeCollection.ReplaceRange` materializes a streaming source once and mutates its backing items before notifying WPF.

This result originally retained incremental gallery paging. The later gallery rendering hardening replaced it with one complete lightweight reference collection so input timing cannot strand the view on a partial library; visual containers and image decoding remain virtualized.

## Verification

- `dotnet test Dextromethorphan.slnx -c Release --no-restore`
- 90 tests pass.
- A 10,000-item replacement emits exactly one `Reset` event in the automated test.
- 10k-track warm WPF sample:

| Metric | Result |
|---|---:|
| Process to interactive | 1,616.142 ms |
| Cached tab maximum | 84.028 ms |
| Mouse history state verification | Pass |
| Album scroll p95 | 19.979 ms |
| Album scroll worst | 32.473 ms |
| Scroll frames over 50 ms | 0 |
| Peak working set | 276.9 MB |

The one-run p95 is machine-noise sensitive and is not treated as the final PERF-GATE-002 report; the bounded worst frame and zero frames over 50 ms remained intact.
