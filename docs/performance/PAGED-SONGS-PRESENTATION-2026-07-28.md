# Paged Songs presentation

VIEW-008 keeps the full ordered library as a lightweight source while exposing Songs to WPF in 500-track pages.

## What changed

- Songs initially binds 500 tracks, then adds the next page near the list's scroll boundary.
- Each tab retains its materialized page count and scroll offset independently.
- History restoration materializes the saved count before applying the prior offset, avoiding position clamping.
- Playing a selected song still builds the queue from the full ordered source, not only the visible page.
- Cached tab returns reuse the same paged presentation collection from VIEW-003.
- Collection details, playlists, Favorites, and smaller lists remain fully available unless they independently require paging later.

## Verification

- `dotnet test Dextromethorphan.slnx -c Release --no-restore`
- 92 tests pass.
- Automated 50k WPF check:

| Check | Result |
|---|---:|
| Full Songs source | 50,000 tracks |
| Initially materialized | 500 tracks |
| After next page | 1,000 tracks |
| Paging verification | Pass |
| Cached tab maximum | 72.894 ms |
| Album scroll p95 | 15.302 ms |
| Album scroll worst | 36.251 ms |
| Scroll frames over 50 ms | 0 |

The performance report and gate script now fail if a large Songs source is accidentally fully materialized again.
