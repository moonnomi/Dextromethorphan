# Hidden-view resource lifetime

VIEW-007 ensures collapsed views retain navigation data, not rendered bitmap surfaces.

## What changed

- `AsyncArtwork` now cancels pending work and clears `Image.Source` whenever effective WPF visibility becomes false.
- Hidden images unsubscribe from source-invalidation events and resubscribe only when visible again.
- Returning to a view reloads from the bounded strong artwork cache or persistent thumbnail, so navigation state remains intact without holding UI bitmap references.
- This applies to gallery cards, collection details, folder/playlist sidebars, queue covers, and both foreground/background Now Playing art.
- The live performance overlay now shows the count of image controls currently holding artwork sources.
- Raw performance reports verify a loaded gallery has artwork before hiding it and zero retained sources afterward.

Collapsed blur/shadow hosts have no bitmap input after this release and therefore do not keep a rendered artwork surface alive.

## Verification

- `dotnet test Dextromethorphan.slnx -c Release --no-restore`
- 92 tests pass.
- Automated 50k WPF hidden-view check: 1 loaded source before hide, 0 after hide, Pass.
- 50k-track warm sample:

| Metric | Result |
|---|---:|
| Process to interactive | 2,267.676 ms |
| Cached tab maximum | 78.739 ms |
| Album scroll p95 | 13.741 ms |
| Album scroll worst | 33.466 ms |
| Scroll frames over 50 ms | 0 |
| Peak working set | 337.9 MB |

The artwork service's bounded RAM cache is intentionally independent of view lifetime; reducing its large-library budget is tracked with the final memory-gate cleanup.
