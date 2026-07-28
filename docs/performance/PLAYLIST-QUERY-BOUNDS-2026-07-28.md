# Bounded playlist queries

VIEW-006 removes the startup-time playlist N+1 pattern and stops retaining every playlist's full track list.

## What changed

- Manual playlist count and representative-track summaries are loaded with one joined SQLite query.
- Smart playlist summaries compile their independent rules through a maximum concurrency of four.
- Playlist cards retain only summary metadata at library load.
- A playlist's full ordered tracks are loaded lazily when that playlist is selected or played.
- Concurrent requests for the same playlist share one `Lazy<Task<...>>`; failures are evicted so a later selection can retry.
- Loaded playlist tracks are generation-scoped and cleared after a library/search refresh.
- Diagnostics distinguish `playlist.get-summaries` from lazy `playlist.get-tracks`.

## Verification

- `dotnet test Dextromethorphan.slnx -c Release --no-restore`
- 92 tests pass.
- Integration coverage verifies manual counts/order/representative art, smart-rule counts/order, and generated fixture summaries.
- The 50k benchmark trace contains one `playlist.get-summaries` operation and one `playlist.get-tracks` operation for the single default playlist activated by the benchmark—not one track query per playlist.
- 50k-track warm WPF sample:

| Metric | VIEW-005 sample | VIEW-006 sample |
|---|---:|---:|
| Process to interactive | 3,120.351 ms | 2,333.819 ms |
| Cached tab maximum | 77.664 ms | 74.028 ms |
| Working set after startup | 299.4 MB | 255.2 MB |
| Working set after navigation | 318.8 MB | 284.5 MB |
| Peak working set | 351.4 MB | 332.0 MB |
| Managed heap | 110.5 MB | 82.8 MB |
| Scroll frames over 50 ms | 0 | 0 |

The peak still includes the deliberately aggressive gallery-scroll workload and remains assigned to hidden-view/artwork lifetime and cleanup work.
