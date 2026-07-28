# Artwork source invalidation

ART-013 prevents stale RAM and persistent thumbnails after source changes.

## Behavior

- Managed original filenames include both media-version identity and SHA-256 artwork-content identity.
- A tag editor that changes embedded artwork while preserving media timestamp and file size therefore produces a new cache path.
- Existing media timestamp changes continue to produce a new managed cache prefix during scanner updates.
- External artwork request keys include full path, file length, and modification time.
- A same-path external cover change bypasses both the decoded RAM cache and the persistent variant cache.
- Library file watchers now observe common image extensions. Created, changed, deleted, and renamed cover events:
  - remove matching decoded images and failure suppression;
  - emit an artwork-source invalidation diagnostic;
  - restart visible asynchronous image controls bound to that path.
- Old versioned files remain available until deterministic ART-012 pruning, avoiding delete/read races.

## Verification

- A storage test changes embedded bytes while preserving the exact media version and proves two distinct managed cache paths are produced.
- A service test changes only an external cover's modification time and proves a second request performs a new decode instead of returning the stale RAM object.
- The complete Release suite passes: 84 tests.
- A warmed 10k-track runtime sample recorded 1,656.5 ms process-to-interactive, 15.0 ms scroll p95, 46.6 ms worst frame, and zero frames over 50 ms.

Preferred external-cover discovery and precedence are intentionally separate in ART-014.
