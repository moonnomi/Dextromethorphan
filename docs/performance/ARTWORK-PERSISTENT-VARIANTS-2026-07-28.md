# Persistent artwork variants

ART-009 adds a disk-backed thumbnail layer between extracted artwork and WPF image decoding.

## Behavior

- Artwork requests map to five stable sizes: 64, 192, 256, 640, and 1024 pixels. The 192-pixel card variant keeps the common album-grid working set smaller, while 1024 pixels remains the Now Playing variant.
- A missing variant is generated off the UI thread at the requested size and encoded as PNG with an atomic temporary-file move.
- Generation is lazy per size. Album scrolling does not pay to create detail and Now Playing files that have not been requested.
- A source-version identity determines the file name. Managed `.art` files already contain the media version in their name; external sources also include file length and modification time.
- Concurrent generation for the same source is serialized, while the existing artwork scheduler continues to deduplicate identical path-and-size requests.
- A fresh process reads the prepared variant instead of decoding the original embedded artwork again.
- The existing disk budget now includes persistent variants and recursively removes abandoned temporary files.
- The performance overlay reports RAM-cache hits, persistent variant count, disk hit rate, and original-cover decodes separately.

The persistent files live under `%APPDATA%\Dextromethorphan\artwork\thumbnails`. Cache management controls remain tracked by ART-012.

## Verification

- Mapping cases cover every boundary between the five fixed sizes.
- A cross-process test generates all five variants, verifies their pixel widths, and proves a fresh store reuses the 1024-pixel file with zero source decodes.
- The complete Release suite passes: 66 tests.
- A clean 10k-track fixture recorded:
  - 1,631.7 ms cold process-to-interactive;
  - 23.7 ms median scroll p95 and 39.7 ms worst frame;
  - zero scroll frames over 50 ms;
  - 4.99% median idle CPU.
- First-time requested-variant generation ran off-thread at 9.2 ms average and 16.9 ms p95.
- The published warmed binary recorded 1,478.3 ms process-to-interactive, 15.5 ms scroll p95, 38.6 ms worst frame, and zero frames over 50 ms.

ART-009 deliberately does not claim corrupt-image validation, format preservation, cache controls, or full invalidation behavior; those remain ART-010 through ART-013.
