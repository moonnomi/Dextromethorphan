# Artwork cache management

ART-012 makes artwork storage measurable and user-controlled.

## Behavior

- Settings → Library reports total disk usage plus original, thumbnail, and temporary-file counts.
- The disk limit is configurable from 64 MB to 4 GB in deterministic 64 MB increments.
- Pruning orders files by most recent use and then stable full path, counts originals and variants in the same budget, and removes expired temporary files first.
- Clear cache removes originals, thumbnails, temporary files, decoded RAM entries, and suppressed failure state. Visible artwork can repopulate naturally afterward.
- Rebuild cache performs a full reconstruction rather than an unchanged-file scan:
  - clears disk and RAM state;
  - reads embedded covers from every available indexed track with at most four workers;
  - stores format-aware originals;
  - updates artwork paths in SQLite in one bounded batch;
  - refreshes the active library view and reports progress.
- Cache commands disable while another cache operation is active.
- Clear, stats, prune, and runtime-memory clearing are included in developer diagnostics.

## Verification

- A focused cache-management test creates deterministic 40 MB, 40 MB, and 1 MB layers under a 64 MB limit.
- The test proves the newest original and thumbnail survive, the older original is removed, expired temporary state is removed, reported byte counts are exact, and Clear leaves zero files and bytes.
- The complete Release suite passes: 82 tests.
- The Settings XAML and dependency graph compile with zero warnings.

Cache invalidation when the underlying media or external artwork changes remains ART-013.
